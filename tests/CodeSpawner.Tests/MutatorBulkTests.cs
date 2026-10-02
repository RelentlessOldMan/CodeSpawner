using System.Security.Cryptography;
using System.Text.Json;
using CodeSpawner.Cli;
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
        // The honesty contract: every recorded file has oldSha != newSha AND at least one hunk (never a
        // "modified" record with an empty diff).
        using var doc = JsonDocument.Parse(File.ReadAllText(delta));
        foreach (var rec in doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray())
        {
            Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());
            Assert.True(rec.GetProperty("hunks").GetArrayLength() >= 1);
        }
    }

    // --- step 2: edit-kind / reason classes ---

    private static JsonElement FirstModified(string deltaPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray().First();
    }

    [Fact]
    public void Kind_Default_IsContent()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 2, lines: 30);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.5, Seed = 1 });
        Assert.Equal("content", FirstModified(delta).GetProperty("reason").GetString());
    }

    [Fact]
    public void Kind_Eol_FlipsTerminators_ReasonEol_NoHunks()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 2, lines: 20);
        string before = File.ReadAllText(Path.Combine(corpus, "src_0.c"));
        Assert.DoesNotContain("\r\n", before);                 // corpus is LF

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "eol", Seed = 1 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("eol", rec.GetProperty("reason").GetString());
        Assert.Equal(0, rec.GetProperty("hunks").GetArrayLength());       // bytes differ, zero textual hunks
        Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());
        string after = File.ReadAllText(Path.Combine(corpus, "src_0.c"));
        Assert.Contains("\r\n", after);                        // now CRLF
        Assert.Equal(before.Replace("\n", "\r\n"), after);     // same text, flipped terminators only
    }

    [Fact]
    public void Kind_Whitespace_ReasonWhitespace_HunksPresent()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 40);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "whitespace", EditDensity = 1.0, Seed = 2 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("whitespace", rec.GetProperty("reason").GetString());
        Assert.True(rec.GetProperty("hunks").GetArrayLength() >= 1);      // whitespace changes ARE textual hunks
        // every line now carries trailing spaces, no comment marker
        var lines = File.ReadAllLines(Path.Combine(corpus, "src_0.c"));
        Assert.All(lines, l => Assert.EndsWith("   ", l));
        Assert.DoesNotContain("/*mut:", File.ReadAllText(Path.Combine(corpus, "src_0.c")));
    }

    [Fact]
    public void Kind_LineInsert_ReasonContent_InsertHunks_RenumberAndGrow()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 30);
        int beforeCount = File.ReadAllLines(Path.Combine(corpus, "src_0.c")).Length;

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "line-insert", EditDensity = 0.2, Seed = 3 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("content", rec.GetProperty("reason").GetString());  // line-insert classifies as content
        var hunks = rec.GetProperty("hunks").EnumerateArray().ToList();
        Assert.NotEmpty(hunks);
        Assert.All(hunks, h => { Assert.Equal("insert", h.GetProperty("op").GetString()); Assert.Equal(0, h.GetProperty("oldLines").GetInt32()); });
        // file grew by exactly the sum of inserted lines, and each hunk's newStart points at real filler
        var after = File.ReadAllLines(Path.Combine(corpus, "src_0.c"));
        int inserted = hunks.Sum(h => h.GetProperty("newLines").GetInt32());
        Assert.Equal(beforeCount + inserted, after.Length);
        foreach (var h in hunks)
        {
            int ns = h.GetProperty("newStart").GetInt32(), n = h.GetProperty("newLines").GetInt32();
            for (int i = 0; i < n; i++) Assert.StartsWith("// mutate inserted", after[ns - 1 + i]);
        }
    }

    [Fact]
    public void Kind_LineDelete_ReasonContent_DeleteHunks_Shrink()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 40);
        int beforeCount = File.ReadAllLines(Path.Combine(corpus, "src_0.c")).Length;

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "line-delete", EditDensity = 0.3, Seed = 4 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("content", rec.GetProperty("reason").GetString());
        var hunks = rec.GetProperty("hunks").EnumerateArray().ToList();
        Assert.NotEmpty(hunks);
        Assert.All(hunks, h => { Assert.Equal("delete", h.GetProperty("op").GetString()); Assert.Equal(0, h.GetProperty("newLines").GetInt32()); });
        int deleted = hunks.Sum(h => h.GetProperty("oldLines").GetInt32());
        Assert.Equal(beforeCount - deleted, File.ReadAllLines(Path.Combine(corpus, "src_0.c")).Length);
    }

    [Fact]
    public void Kind_Encoding_ReasonEncoding_TextIdenticalBytesDiffer()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 25);
        string before = File.ReadAllText(Path.Combine(corpus, "src_0.c"));

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "encoding", Seed = 5 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("encoding", rec.GetProperty("reason").GetString());
        Assert.Equal(0, rec.GetProperty("hunks").GetArrayLength());
        Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());
        // File.ReadAllText auto-detects the UTF-16 BOM ⇒ decoded text is unchanged
        Assert.Equal(before, File.ReadAllText(Path.Combine(corpus, "src_0.c")));
    }

    [Fact]
    public void Kind_Binary_ReasonBinary_BytesDiffer_NoHunks()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 50);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "binary", EditDensity = 0.1, Seed = 6 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("binary", rec.GetProperty("reason").GetString());
        Assert.Equal(0, rec.GetProperty("hunks").GetArrayLength());
        Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());
    }

    [Fact]
    public void Kind_Metadata_ContentIdentical_ReasonMetadata_WithField()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 20);
        byte[] before = File.ReadAllBytes(Path.Combine(corpus, "src_0.c"));

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", FilesChanged = 1, EditKind = "metadata", Seed = 7 });

        var rec = ModifiedRecord(delta, "src_0.c");
        Assert.Equal("metadata", rec.GetProperty("reason").GetString());
        Assert.Equal(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());  // content identical
        Assert.Equal(0, rec.GetProperty("hunks").GetArrayLength());
        Assert.Equal("mode", rec.GetProperty("metadata").GetProperty("field").GetString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(corpus, "src_0.c")));                     // bytes untouched on disk
    }

    // --- step 3: rename / move + decoys ---

    private static List<JsonElement> Renamed(string deltaPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("renamed").EnumerateArray().ToList();
    }

    private static List<string> Added(string deltaPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("added").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public void Rename_GradedRenames_PureIdenticalBytes_EditKeyedByToPath()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 8, lines: 20);
        // capture each source file's original bytes by stem (src_0, src_1, ...)
        var origin = Directory.GetFiles(corpus, "src_*.c")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllBytes(f));

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", DecoyFraction = 0, Seed = 5 });

        var renamed = Renamed(delta);
        Assert.Equal(8, renamed.Count);
        Assert.Empty(Added(delta));
        var modifiedByPath = JsonDocument.Parse(File.ReadAllText(delta)).RootElement
            .GetProperty("fileOps").GetProperty("modified").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!);

        int pure = 0, edited = 0;
        foreach (var r in renamed)
        {
            string from = r.GetProperty("from").GetString()!, to = r.GetProperty("to").GetString()!;
            int sim = r.GetProperty("similarityMilli").GetInt32();
            Assert.InRange(sim, 0, 1000);
            Assert.False(File.Exists(Path.Combine(corpus, from.Replace('/', Path.DirectorySeparatorChar))));  // from gone
            string toAbs = Path.Combine(corpus, to.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(toAbs));                                                                  // to exists
            string stem = Path.GetFileNameWithoutExtension(from);
            if (sim >= 1000)
            {
                pure++;
                Assert.False(modifiedByPath.ContainsKey(to));                       // pure rename ⇒ no modified record
                Assert.Equal(origin[stem], File.ReadAllBytes(toAbs));              // identical bytes
            }
            else
            {
                edited++;
                Assert.True(modifiedByPath.ContainsKey(to));                        // rename+edit hunks keyed by `to`
                var rec = modifiedByPath[to];
                Assert.Equal("content", rec.GetProperty("reason").GetString());
                Assert.True(rec.GetProperty("hunks").GetArrayLength() >= 1);
            }
        }
        Assert.True(pure >= 1 && edited >= 1);   // band cycles {1000,900,600,300} ⇒ both kinds present
    }

    [Fact]
    public void Rename_SimilarityMilli_MatchesRealizedLineOverlap()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 6, lines: 40);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", Seed = 5 });

        // For each rename+edit, (lines - changed)/lines * 1000 (rounded) must equal the emitted similarityMilli.
        var modifiedByPath = JsonDocument.Parse(File.ReadAllText(delta)).RootElement
            .GetProperty("fileOps").GetProperty("modified").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!);
        foreach (var r in Renamed(delta))
        {
            int sim = r.GetProperty("similarityMilli").GetInt32();
            if (sim >= 1000) continue;
            string to = r.GetProperty("to").GetString()!;
            var lines = File.ReadAllLines(Path.Combine(corpus, to.Replace('/', Path.DirectorySeparatorChar)));
            int changed = lines.Count(l => l.Contains("/*ren:"));
            int expected = (int)Math.Round((lines.Length - changed) * 1000.0 / lines.Length);
            Assert.Equal(expected, sim);
        }
    }

    [Fact]
    public void Rename_Decoys_AreAddsNotRenames_OriginalKept()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 6, lines: 20);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", DecoyFraction = 1.0, Seed = 5 });

        var added = Added(delta);
        Assert.Empty(Renamed(delta));                 // decoy-fraction 1.0 ⇒ no renames
        Assert.Equal(6, added.Count);
        var renamedTo = Renamed(delta).Select(r => r.GetProperty("to").GetString()).ToHashSet();
        foreach (var dup in added)
        {
            Assert.Contains("_dup", dup);
            Assert.DoesNotContain(dup, renamedTo);     // a decoy is an ADD, never a rename target
            Assert.True(File.Exists(Path.Combine(corpus, dup.Replace('/', Path.DirectorySeparatorChar))));
        }
        // every original source file is still present (a decoy copies, it does not move)
        Assert.Equal(6, Directory.GetFiles(corpus, "src_*.c").Count(f => !Path.GetFileName(f).Contains("_dup")));
    }

    [Fact]
    public void Rename_IsDeterministic()
    {
        string Fingerprint()
        {
            using var tmp = new TempDir();
            var (corpus, delta) = BuildCorpus(tmp, nSource: 8, lines: 25);
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", DecoyFraction = 0.3, Seed = 5 });
            return JsonDocument.Parse(File.ReadAllText(delta)).RootElement.GetProperty("_meta").GetProperty("diffTruthSha").GetString()!;
        }
        Assert.Equal(Fingerprint(), Fingerprint());
    }

    // --- step 4: native 3-way ---

    private static JsonElement ConflictRoot(string corpus) =>
        JsonDocument.Parse(File.ReadAllText(corpus + "-conflict.json")).RootElement;

    [Fact]
    public void ThreeWay_EmitsVariantsDeltasConflict_BasePristine()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 6, lines: 40);
        var baseBytes = Directory.GetFiles(corpus, "src_*.c").ToDictionary(f => f, File.ReadAllBytes);

        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = 0.5, EditDensity = 0.8, Seed = 9 });

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(corpus + "_v1"));
        Assert.True(Directory.Exists(corpus + "_v2"));
        Assert.True(File.Exists(corpus + "-delta-v1.json"));
        Assert.True(File.Exists(corpus + "-delta-v2.json"));
        Assert.True(File.Exists(corpus + "-conflict.json"));
        // B is left pristine (3-way never edits the base tree in place)
        foreach (var (f, bytes) in baseBytes) Assert.Equal(bytes, File.ReadAllBytes(f));
        Assert.Equal("conflict-3way", ConflictRoot(corpus).GetProperty("_meta").GetProperty("deltaKind").GetString());
    }

    [Fact]
    public void ThreeWay_StableSeparator_AllCoordsOdd()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 4, lines: 60);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = 0.5, EditDensity = 0.9, Seed = 3 });

        var root = ConflictRoot(corpus);
        foreach (var c in root.GetProperty("conflicts").EnumerateArray())
            Assert.True(c.GetProperty("baseStart").GetInt32() % 2 == 1);   // edits only on odd base lines
        foreach (var m in root.GetProperty("mergedClean").EnumerateArray())
            Assert.True(m.GetProperty("newStart").GetInt32() % 2 == 1);
    }

    [Fact]
    public void ThreeWay_ConflictsEditedBothSides_CleanEditedOneSide()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 5, lines: 50);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = 0.5, EditDensity = 0.9, Seed = 11 });

        string v1 = corpus + "_v1", v2 = corpus + "_v2";
        string[] V1(string p) => File.ReadAllLines(Path.Combine(v1, p.Replace('/', Path.DirectorySeparatorChar)));
        string[] V2(string p) => File.ReadAllLines(Path.Combine(v2, p.Replace('/', Path.DirectorySeparatorChar)));
        var root = ConflictRoot(corpus);

        foreach (var c in root.GetProperty("conflicts").EnumerateArray())
        {
            string path = c.GetProperty("path").GetString()!; int ln = c.GetProperty("baseStart").GetInt32();
            Assert.Contains("/*v1:", V1(path)[ln - 1]);     // a conflict line is edited in BOTH trees
            Assert.Contains("/*v2:", V2(path)[ln - 1]);
        }
        foreach (var m in root.GetProperty("mergedClean").EnumerateArray())
        {
            string path = m.GetProperty("path").GetString()!; int ln = m.GetProperty("newStart").GetInt32();
            if (m.GetProperty("side").GetString() == "v1")
            {
                Assert.Contains("/*v1:", V1(path)[ln - 1]);  // v1-only: present in v1, absent in v2
                Assert.DoesNotContain("/*v2:", V2(path)[ln - 1]);
            }
            else
            {
                Assert.Contains("/*v2:", V2(path)[ln - 1]);
                Assert.DoesNotContain("/*v1:", V1(path)[ln - 1]);
            }
        }
    }

    [Theory]
    [InlineData(0.0)]   // every V2 edit lands on a stable (non-V1) line ⇒ all clean-merge
    [InlineData(1.0)]   // every V2 edit coincides with V1 ⇒ all conflict, no v2-only clean
    public void ThreeWay_OverlapFraction_DrivesConflictShare(double f)
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 5, lines: 60);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = f, EditDensity = 0.8, Seed = 7 });

        var root = ConflictRoot(corpus);
        int conflicts = root.GetProperty("conflicts").GetArrayLength();
        int v2clean = root.GetProperty("mergedClean").EnumerateArray().Count(m => m.GetProperty("side").GetString() == "v2");
        if (f == 0.0) Assert.Equal(0, conflicts);
        else Assert.Equal(0, v2clean);     // f=1 ⇒ no V2 edit is one-sided
    }

    [Fact]
    public void ThreeWay_IsDeterministic()
    {
        string Sha()
        {
            using var tmp = new TempDir();
            var (corpus, _) = BuildCorpus(tmp, nSource: 6, lines: 40);
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = 0.5, EditDensity = 0.7, Seed = 4 });
            return ConflictRoot(corpus).GetProperty("_meta").GetProperty("conflictTruthSha").GetString()!;
        }
        Assert.Equal(Sha(), Sha());
    }

    [Fact]
    public void Bulk_PreservesEolAndMissingFinalNewline_NoSpuriousEmptyDiff()
    {
        using var tmp = new TempDir();
        // A CRLF file with NO trailing newline: a naive ReadLine+write-LF rewrite would flip every \r\n and add
        // a final \n — bytes changed with zero marked lines, i.e. a content record with an empty diff.
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 10, extra: c =>
            File.WriteAllBytes(Path.Combine(c, "crlf.c"),
                System.Text.Encoding.UTF8.GetBytes("int a(void){return 0;}\r\nint b(void){return 1;}\r\nint c(void){return 2;}")));

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 1.0, Seed = 8 });

        byte[] after = File.ReadAllBytes(Path.Combine(corpus, "crlf.c"));
        string s = System.Text.Encoding.UTF8.GetString(after);
        Assert.Contains("\r\n", s);                    // CRLF terminators preserved
        Assert.False(s.EndsWith("\n"));                // no trailing newline manufactured
        Assert.Equal(3, s.Split("\r\n").Length);       // still three lines, each marked (density 1.0)
        Assert.All(s.Split("\r\n"), line => Assert.Contains("/*mut:", line));
        // and every record in the delta carries real hunks
        var rec = ModifiedRecord(delta, "crlf.c");
        Assert.True(rec.GetProperty("hunks").GetArrayLength() >= 1);
    }

    // --- coverage: guards, target selection, nested trees, and the two honesty/faithfulness fixes ---

    [Fact]
    public void Bulk_RefusesDirWithoutCodeSpawnerMarker()
    {
        using var tmp = new TempDir();
        string dir = Path.Combine(tmp.Path, "notcorpus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "src_0.c"), "int x(void){return 0;}\n");
        // No .codespawner marker ⇒ refuse to edit (the marker check runs before anything touches disk).
        var ex = Assert.Throws<ArgException>(() =>
            Mutator.Run(new MutateOptions { Corpus = dir, Target = "source", EditDensity = 0.5, Seed = 1 }));
        Assert.Contains(".codespawner", ex.Message);
    }

    [Fact]
    public void Bulk_TargetHeaders_SelectsHeaderFamilyOnly()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 2, lines: 20, extra: c =>
        {
            File.WriteAllText(Path.Combine(c, "api.h"),
                string.Join("\n", Enumerable.Range(0, 20).Select(i => $"#define A{i} {i}")));
            File.WriteAllText(Path.Combine(c, "util.hpp"),
                string.Join("\n", Enumerable.Range(0, 20).Select(i => $"inline int g{i}(void){{return {i};}}")));
        });

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "headers", EditDensity = 1.0, Seed = 1 });

        var modified = ModifiedFiles(delta);
        Assert.NotEmpty(modified);
        Assert.All(modified, p => Assert.Matches(@"\.(h|hpp|hh|hxx)$", p));   // header family only
        Assert.Contains(modified, p => p.EndsWith("api.h"));
        Assert.Contains(modified, p => p.EndsWith("util.hpp"));
        Assert.DoesNotContain(modified, p => p.EndsWith(".c"));               // .c files left alone
    }

    [Fact]
    public void Bulk_TargetAll_SelectsEveryNonMarkerFile()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildCorpus(tmp, nSource: 3, lines: 15, extra: c =>
            File.WriteAllText(Path.Combine(c, "readme.txt"),
                string.Join("\n", Enumerable.Range(0, 15).Select(i => $"line {i}"))));

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "all", EditDensity = 1.0, Seed = 2 });

        var modified = ModifiedFiles(delta);
        Assert.Equal(4, modified.Count);                                     // 3 .c + 1 .txt
        Assert.Contains(modified, p => p.EndsWith("readme.txt"));
        Assert.DoesNotContain(modified, p => p.EndsWith(".codespawner"));    // the marker is never a target
    }

    [Fact]
    public void ThreeWay_CopiesNestedSubdirectoriesIntoBothVariants()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 2, lines: 30, extra: c =>
        {
            string sub = Path.Combine(c, "sub", "deep");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "nested.c"),
                string.Join("\n", Enumerable.Range(0, 30).Select(l => $"int n_{l}(void){{return {l};}}")));
        });

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, OverlapFraction = 0.5, EditDensity = 0.8, Seed = 9 });

        // CopyTree must recreate the subdir structure in both variant trees (real repos are nested, not flat).
        Assert.True(File.Exists(Path.Combine(corpus + "_v1", "sub", "deep", "nested.c")));
        Assert.True(File.Exists(Path.Combine(corpus + "_v2", "sub", "deep", "nested.c")));
        // base tree stays pristine, nested file included
        Assert.True(File.Exists(Path.Combine(corpus, "sub", "deep", "nested.c")));
    }

    [Fact]
    public void Rename_EmptyFile_IsPureRename_NotAnEmptyDiffRecord()
    {
        using var tmp = new TempDir();
        // Files named so ordinal order puts the empty file at index 1 ⇒ band 900 (a non-pure similarity target).
        // A non-pure band on a 0-line file changes ceil(0.1*0)=0 lines ⇒ byte-identical. The honesty guard must
        // record it as a PURE rename, never a `modified` record with equal shas and an empty diff.
        var (corpus, delta) = BuildCorpus(tmp, nSource: 0, lines: 0, extra: c =>
        {
            File.WriteAllText(Path.Combine(c, "f0.c"), "int a(void){return 0;}\n");
            File.WriteAllText(Path.Combine(c, "f1.c"), "");                                   // empty ⇒ index 1, band 900
            File.WriteAllText(Path.Combine(c, "f2.c"), "int b(void){return 1;}\nint c(void){return 2;}\n");
            File.WriteAllText(Path.Combine(c, "f3.c"), "int d(void){return 3;}\n");
        });

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", DecoyFraction = 0, Seed = 5 });

        using var doc = JsonDocument.Parse(File.ReadAllText(delta));
        foreach (var rec in doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray())
        {
            Assert.NotEqual(rec.GetProperty("oldSha").GetString(), rec.GetProperty("newSha").GetString());  // honesty
            Assert.True(rec.GetProperty("hunks").GetArrayLength() >= 1);
        }
        // the empty file still shows up as a rename — a pure one (similarity 1000, identical bytes)
        var r = Renamed(delta).Single(x => x.GetProperty("from").GetString()!.EndsWith("f1.c"));
        Assert.Equal(1000, r.GetProperty("similarityMilli").GetInt32());
    }

    [Fact]
    public void Bulk_Giant_PreservesCrlfTerminators_AndMissingFinalNewline()
    {
        using var tmp = new TempDir();
        // A >=1 MB "giant" with CRLF terminators and NO trailing newline. The giant path must preserve both
        // (same faithfulness as the normal-file path) rather than flattening every terminator to LF.
        var (corpus, delta) = BuildCorpus(tmp, nSource: 1, lines: 10, extra: c =>
        {
            var g = new System.Text.StringBuilder();
            for (int i = 0; i < 60_000; i++) { g.Append($"#define MACRO_{i} {i}"); if (i < 59_999) g.Append("\r\n"); }
            File.WriteAllBytes(Path.Combine(c, "big.h"), System.Text.Encoding.UTF8.GetBytes(g.ToString()));
        });

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "giant", GiantMinMb = 1, EditDensity = 0.1, Seed = 1 });

        string s = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(corpus, "big.h")));
        Assert.Contains("\r\n", s);            // CRLF preserved, not flattened to LF
        Assert.False(s.EndsWith("\n"));        // no trailing newline manufactured
        Assert.DoesNotContain("\r\n\n", s);    // and no stray LF injected after a CRLF

        // The run-rule still expands to exactly the stride-th lines marked on disk.
        var run = ModifiedRecord(delta, "big.h").GetProperty("hunks")[0];
        Assert.Equal("run", run.GetProperty("kind").GetString());
        int stride = run.GetProperty("stride").GetInt32(), start = run.GetProperty("rangeStart").GetInt32(),
            end = run.GetProperty("rangeEnd").GetInt32();
        var expanded = new SortedSet<int>();
        for (int ln = start; ln <= end; ln += stride) expanded.Add(ln);
        var fromDisk = new SortedSet<int>();
        string[] lines = s.Split("\r\n");
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains("/*mut:")) fromDisk.Add(i + 1);
        Assert.Equal(fromDisk, expanded);
    }

    // --- step 4b: 3-way edge kinds (adjacent / modify-delete / add-add / identical-overlap) ---

    private static List<JsonElement> Conflicts(string corpus) =>
        ConflictRoot(corpus).GetProperty("conflicts").EnumerateArray().ToList();
    private static List<JsonElement> Clean(string corpus) =>
        ConflictRoot(corpus).GetProperty("mergedClean").EnumerateArray().ToList();

    [Fact]
    public void ThreeWayEdges_EmitsEachConflictKind_IdenticalOverlapIsClean()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 20);
        var baseBytes = File.ReadAllBytes(Path.Combine(corpus, "src_0.c"));

        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, ConflictEdges = true, Seed = 9 });

        Assert.Equal(0, rc);
        Assert.True(File.Exists(corpus + "-delta-v1.json") && File.Exists(corpus + "-delta-v2.json") && File.Exists(corpus + "-conflict.json"));
        Assert.Equal(baseBytes, File.ReadAllBytes(Path.Combine(corpus, "src_0.c")));   // base pristine

        var conflicts = Conflicts(corpus);
        var clean = Clean(corpus);

        // single modify/modify: baseLines=1, both replace
        Assert.Contains(conflicts, c => c.GetProperty("baseLines").GetInt32() == 1
            && Op(c, "v1") == "replace" && Op(c, "v2") == "replace");
        // adjacent multi-line: ONE region, baseLines=3 (union span), both replace 3 lines
        Assert.Contains(conflicts, c => c.GetProperty("baseLines").GetInt32() == 3
            && Op(c, "v1") == "replace" && NL(c, "v1") == 3 && Op(c, "v2") == "replace" && NL(c, "v2") == 3);
        // modify/delete: one side deletes (newLines 0), other replaces
        Assert.Contains(conflicts, c => Op(c, "v1") == "replace" && Op(c, "v2") == "delete" && NL(c, "v2") == 0);
        // add/add: baseLines=0, both insert
        Assert.Contains(conflicts, c => c.GetProperty("baseLines").GetInt32() == 0
            && Op(c, "v1") == "insert" && Op(c, "v2") == "insert");

        // identical-overlap (base line 12): a CLEAN merge, side=v1 — and NOT any conflict's region
        Assert.Contains(clean, m => m.GetProperty("side").GetString() == "v1" && m.GetProperty("oldStart").GetInt32() == 12);
        Assert.DoesNotContain(conflicts, c =>
        {
            int s = c.GetProperty("baseStart").GetInt32(), n = c.GetProperty("baseLines").GetInt32();
            return n > 0 && 12 >= s && 12 <= s + n - 1;
        });
        // one-sided clean on each side
        Assert.Contains(clean, m => m.GetProperty("side").GetString() == "v1" && m.GetProperty("oldStart").GetInt32() == 14);
        Assert.Contains(clean, m => m.GetProperty("side").GetString() == "v2" && m.GetProperty("oldStart").GetInt32() == 16);

        static string Op(JsonElement c, string side) => c.GetProperty(side).GetProperty("op").GetString()!;
        static int NL(JsonElement c, string side) => c.GetProperty(side).GetProperty("newLines").GetInt32();
    }

    [Fact]
    public void ThreeWayEdges_TruthCoordsLocateContentInVariantTrees()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 20);
        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, ConflictEdges = true, Seed = 9 });

        var V1 = File.ReadAllLines(Path.Combine(corpus + "_v1", "src_0.c"));
        var V2 = File.ReadAllLines(Path.Combine(corpus + "_v2", "src_0.c"));

        // add/add: the conflict's insert coords point at the inserted lines in each tree.
        var add = Conflicts(corpus).Single(c => c.GetProperty("baseLines").GetInt32() == 0);
        int v1s = add.GetProperty("v1").GetProperty("newStart").GetInt32(), v1n = add.GetProperty("v1").GetProperty("newLines").GetInt32();
        int v2s = add.GetProperty("v2").GetProperty("newStart").GetInt32(), v2n = add.GetProperty("v2").GetProperty("newLines").GetInt32();
        for (int i = 0; i < v1n; i++) Assert.StartsWith("// v1 add", V1[v1s - 1 + i]);
        for (int i = 0; i < v2n; i++) Assert.StartsWith("// v2 add", V2[v2s - 1 + i]);

        // identical-overlap: at its (v1) newStart the line carries the shared marker, identically in both trees.
        var ident = Clean(corpus).Single(m => m.GetProperty("oldStart").GetInt32() == 12);
        int ins = ident.GetProperty("newStart").GetInt32();
        Assert.Contains("/*both:12*/", V1[ins - 1]);
        Assert.Contains(V2, line => line.Contains("/*both:12*/"));   // same agreed edit present in v2 too

        // one-sided v1 (base 14): present in v1 tree, absent from v2.
        var c14 = Clean(corpus).Single(m => m.GetProperty("oldStart").GetInt32() == 14 && m.GetProperty("side").GetString() == "v1");
        Assert.Contains("/*v1e:14*/", V1[c14.GetProperty("newStart").GetInt32() - 1]);
        Assert.DoesNotContain(V2, line => line.Contains("/*v1e:14*/"));
    }

    [Fact]
    public void ThreeWayEdges_IsDeterministic()
    {
        string Sha()
        {
            using var tmp = new TempDir();
            var (corpus, _) = BuildCorpus(tmp, nSource: 2, lines: 24);
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, ConflictEdges = true, Seed = 4 });
            return ConflictRoot(corpus).GetProperty("_meta").GetProperty("conflictTruthSha").GetString()!;
        }
        Assert.Equal(Sha(), Sha());
    }

    [Fact]
    public void ThreeWayEdges_SkipsFilesTooShortForLayout()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 5);   // < EdgeMinLines ⇒ nothing to lay out
        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, ConflictEdges = true, Seed = 1 });
        Assert.Equal(2, rc);                                         // no eligible file ⇒ error
    }

    [Fact]
    public void Bulk_Giant_PreservesLoneCrTerminators()
    {
        using var tmp = new TempDir();
        // A >=1 MB "giant" with classic-Mac lone-CR terminators (no LF anywhere). The streaming rewrite must
        // preserve each '\r' as its own terminator, never pairing or flattening it.
        var (corpus, _) = BuildCorpus(tmp, nSource: 1, lines: 10, extra: c =>
        {
            var g = new System.Text.StringBuilder();
            for (int i = 0; i < 60_000; i++) { g.Append($"#define MACRO_{i} {i}"); if (i < 59_999) g.Append('\r'); }
            File.WriteAllBytes(Path.Combine(c, "big.h"), System.Text.Encoding.UTF8.GetBytes(g.ToString()));
        });

        Mutator.Run(new MutateOptions { Corpus = corpus, Target = "giant", GiantMinMb = 1, EditDensity = 0.1, Seed = 1 });

        string s = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(corpus, "big.h")));
        Assert.Contains("\r", s);
        Assert.DoesNotContain("\n", s);                 // no LF introduced — every terminator stayed a lone CR
        Assert.Equal(60_000, s.Split('\r').Length);     // still 60k CR-separated lines
    }
}
