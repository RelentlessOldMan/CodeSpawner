using System.Security.Cryptography;
using System.Text.Json;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// Bulk in-place mutation (the diff-oracle mode): pick a population, control how many files change and how
/// much of each, deterministically, and emit one base->variant delta. These guard the knobs a diff-tool test
/// harness leans on — exact file counts, density, size-targeting, and byte-for-byte determinism.
/// </summary>
public class MutatorBulkTests
{
    // Build a self-contained corpus under tmp\corpus so its sibling manifest/delta also land in tmp.
    private static (string corpus, string delta) BuildCorpus(TempDir tmp, int nSource, int lines, Action<string>? extra = null)
    {
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        for (int i = 0; i < nSource; i++)
        {
            string body = string.Join("\n", Enumerable.Range(0, lines).Select(l => $"int f{i}_{l}(void) {{ return {l}; }}"));
            File.WriteAllText(Path.Combine(corpus, $"src_{i}.c"), body);
        }
        extra?.Invoke(corpus);
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        m.Symbols["func_0"] = new SymbolEntry { Def = "src_0.c:1" };
        ManifestWriter.Write(m, Path.Combine(tmp.Path, "corpus-manifest.json"));
        return (corpus, Path.Combine(tmp.Path, "corpus-delta.json"));
    }

    private static List<string> ModifiedFiles(string deltaPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("modified")
            .EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    // The full modified-file record (path, reason, shas, sizes, hunks) for the one file ending in `suffix`.
    private static JsonElement ModifiedRecord(string deltaPath, string suffix)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray()
            .Single(e => e.GetProperty("path").GetString()!.EndsWith(suffix));
    }

    private static string DiffTruthSha(string deltaPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("_meta").GetProperty("diffTruthSha").GetString()!;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public void Bulk_Source_ChangesExactlyNFiles_AndRecordsThem()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 6, lines: 50);

        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", FilesChanged = 3, EditDensity = 0.5, Seed = 7 });

        Assert.Equal(0, rc);
        var modified = ModifiedFiles(delta);
        Assert.Equal(3, modified.Count);
        Assert.All(modified, p => Assert.EndsWith(".c", p));
        // exactly the recorded files carry markers; the others are untouched
        int withMarker = Directory.GetFiles(corpus, "src_*.c").Count(f => File.ReadAllText(f).Contains("/*mut:"));
        Assert.Equal(3, withMarker);
    }

    [Fact]
    public void Bulk_FilesChangedOmitted_ChangesAllMatching()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 5, lines: 20);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.5, Seed = 2 });
        Assert.Equal(5, ModifiedFiles(delta).Count);
    }

    [Fact]
    public void Bulk_IsDeterministic_SameSeedByteIdentical_DifferentSeedDiffers()
    {
        string Fingerprint(int seed)
        {
            using var tmp = new TempDir();
            var (corpus, _) = BuildCorpus(tmp, nSource: 8, lines: 40);
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", FilesChanged = 4, EditDensity = 0.3, Seed = seed });
            return string.Join("|", Directory.GetFiles(corpus, "src_*.c").OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetFileName(f) + ":" + Sha(File.ReadAllBytes(f))));
        }
        Assert.Equal(Fingerprint(7), Fingerprint(7));   // same seed -> byte-identical variant
        Assert.NotEqual(Fingerprint(7), Fingerprint(9)); // different seed -> different selection/markers
    }

    [Theory]
    [InlineData(0.02, 1, 70)]
    [InlineData(0.50, 400, 600)]
    public void Bulk_Density_ControlsLinesChanged(double density, int lo, int hi)
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 1000);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = density, Seed = 3 });
        int changed = File.ReadLines(Path.Combine(corpus, "src_0.c")).Count(l => l.Contains("/*mut:"));
        Assert.InRange(changed, lo, hi);
    }

    [Fact]
    public void Bulk_Giant_SelectsBySizeFloor_NotSmallFiles()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 3, lines: 10, extra: c =>
            File.WriteAllText(Path.Combine(c, "big.h"),
                string.Join("\n", Enumerable.Range(0, 100_000).Select(i => $"#define MACRO_{i} {i}"))));

        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Target = "giant", GiantMinMb = 1, EditDensity = 0.1, Seed = 1 });

        Assert.Equal(0, rc);
        var modified = ModifiedFiles(delta);
        Assert.Single(modified);                         // only the >=1 MB header, not the tiny .c files
        Assert.EndsWith("big.h", modified[0]);
    }

    [Fact]
    public void Bulk_PreservesLineCount_AndDefTokens()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 200);
        string before = File.ReadAllText(Path.Combine(corpus, "src_0.c"));
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 1.0, Seed = 5 });
        var after = File.ReadAllLines(Path.Combine(corpus, "src_0.c"));
        // every line changed (density 1.0) but tokens survive: the def of f0_0 is still present and findable
        Assert.Equal(200, after.Length);
        Assert.Contains(after, l => l.StartsWith("int f0_0(void)") && l.Contains("/*mut:"));
    }

    // --- step 1: hunk / sha / run-rule / diffTruthSha truth ---

    [Fact]
    public void Bulk_Record_CarriesReasonShasAndSizes()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 50);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.5, Seed = 7 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("content", rec.GetProperty("reason").GetString());
        // shas are lowercase-hex SHA-256, old != new (the file really changed), and match the on-disk bytes.
        string oldSha = rec.GetProperty("oldSha").GetString()!, newSha = rec.GetProperty("newSha").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", oldSha);
        Assert.Matches("^[0-9a-f]{64}$", newSha);
        Assert.NotEqual(oldSha, newSha);
        Assert.Equal(Sha(File.ReadAllBytes(Path.Combine(corpus, "src_0.c"))).ToLowerInvariant(), newSha);
        Assert.Equal(new FileInfo(Path.Combine(corpus, "src_0.c")).Length, rec.GetProperty("newSize").GetInt64());
    }

    [Fact]
    public void Bulk_Hunks_CoalesceContiguousChangedLines()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 40);
        // density 1.0 ⇒ every line changes ⇒ exactly ONE coalesced replace hunk spanning all 40 lines.
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 1.0, Seed = 5 });

        var hunks = ModifiedRecord(delta, "src_0.c").GetProperty("hunks");
        Assert.Equal(1, hunks.GetArrayLength());
        var h = hunks[0];
        Assert.Equal("replace", h.GetProperty("op").GetString());
        Assert.Equal(1, h.GetProperty("oldStart").GetInt32());
        Assert.Equal(40, h.GetProperty("oldLines").GetInt32());
        Assert.Equal(1, h.GetProperty("newStart").GetInt32());
        Assert.Equal(40, h.GetProperty("newLines").GetInt32());   // in-place edit ⇒ old==new coords/length
    }

    [Fact]
    public void Bulk_Hunks_MatchTheActualMarkedLines()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 60);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.3, Seed = 11 });

        // The union of hunk line-ranges must equal exactly the set of lines carrying a marker on disk.
        var fromHunks = new SortedSet<int>();
        foreach (var h in ModifiedRecord(delta, "src_0.c").GetProperty("hunks").EnumerateArray())
        {
            int start = h.GetProperty("newStart").GetInt32(), n = h.GetProperty("newLines").GetInt32();
            for (int i = 0; i < n; i++) fromHunks.Add(start + i);
        }
        var fromDisk = new SortedSet<int>();
        var lines = File.ReadAllLines(Path.Combine(corpus, "src_0.c"));
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains("/*mut:")) fromDisk.Add(i + 1);
        Assert.Equal(fromDisk, fromHunks);
    }

    [Fact]
    public void Bulk_Giant_EmitsRunRuleThatExpandsToTheMarkedLines()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 10, extra: c =>
            File.WriteAllText(Path.Combine(c, "big.h"),
                string.Join("\n", Enumerable.Range(0, 100_000).Select(i => $"#define MACRO_{i} {i}"))));
        // density 0.1 ⇒ stride 10. Giant ⇒ compact run-rule, not an explicit hunk list.
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "giant", GiantMinMb = 1, EditDensity = 0.1, Seed = 1 });

        var hunks = ModifiedRecord(delta, "big.h").GetProperty("hunks");
        Assert.Equal(1, hunks.GetArrayLength());
        var run = hunks[0];
        Assert.Equal("run", run.GetProperty("kind").GetString());
        Assert.Equal("replace", run.GetProperty("op").GetString());
        int stride = run.GetProperty("stride").GetInt32(), start = run.GetProperty("rangeStart").GetInt32(),
            end = run.GetProperty("rangeEnd").GetInt32();
        Assert.Equal(10, stride);
        Assert.Equal(1, start);

        // Expand the run-rule exactly as CodeDiffer will and assert it equals the lines marked on disk.
        var expanded = new SortedSet<int>();
        for (int ln = start; ln <= end; ln += stride) expanded.Add(ln);
        var fromDisk = new SortedSet<int>();
        var lines = File.ReadAllLines(Path.Combine(corpus, "big.h"));
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains("/*mut:")) fromDisk.Add(i + 1);
        Assert.Equal(fromDisk, expanded);
    }

    [Fact]
    public void Bulk_DiffTruthSha_IsStableForSameSeed_AndSensitiveToContent()
    {
        string Sha1(int seed, double density)
        {
            using var tmp = new TempDir();
            var (corpus, delta) = BuildCorpus(tmp, nSource: 4, lines: 40);
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", FilesChanged = 2, EditDensity = density, Seed = seed });
            return DiffTruthSha(delta);
        }
        Assert.Equal(Sha1(7, 0.3), Sha1(7, 0.3));       // deterministic
        Assert.NotEqual(Sha1(7, 0.3), Sha1(9, 0.3));    // different selection/markers ⇒ different shas/hunks
        Assert.NotEqual(Sha1(7, 0.3), Sha1(7, 0.6));    // more lines changed ⇒ different hunks
    }

    [Fact]
    public void Bulk_NeverRecordsAFileWithNoActualChange()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 10, lines: 60);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.2, Seed = 4 });
        // every recorded file must have oldSha != newSha — the honesty contract (no "modified" with empty diff).
        using var doc = JsonDocument.Parse(File.ReadAllText(delta));
        foreach (var rec in doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray())
            Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());
    }
}
