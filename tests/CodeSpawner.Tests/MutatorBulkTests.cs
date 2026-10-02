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
            .EnumerateArray().Select(e => e.GetString()!).ToList();
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
}
