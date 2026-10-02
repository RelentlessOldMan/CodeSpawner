using CodeSpawner.Cli;
using CodeSpawner.Scan;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The scanner (real-tree → shape profile) and its include aggregator. Scanner is the read side that feeds
/// profile-driven regeneration; these pin that a scan of a small tree produces a valid profile and that the
/// include fan-out/ambiguity stats are computed from the sampled targets.
/// </summary>
public class ScanTests
{
    private static string BuildTree(TempDir tmp)
    {
        string root = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.c"), "#include \"b.h\"\n#include \"missing.h\"\nint a(void){return 0;}\n");
        File.WriteAllText(Path.Combine(root, "b.h"), "#pragma once\nint b(void);\n");
        File.WriteAllText(Path.Combine(root, "sub", "c.c"), "#include \"b.h\"\nint c(void){return 1;}\nint d(void){return 2;}\n");
        File.WriteAllText(Path.Combine(root, "sub", "e.h"), "#pragma once\n");
        return root;
    }

    [Fact]
    public void Scan_ProducesProfile_WithFileTally()
    {
        using var tmp = new TempDir();
        string tree = BuildTree(tmp);
        string prof = Path.Combine(tmp.Path, "prof.json");
        int rc = new Scanner(ArgParser.ParseScan(new[] { tree, "--out", prof })).Run();

        Assert.Equal(0, rc);
        Assert.True(File.Exists(prof));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(prof));
        Assert.True(doc.RootElement.GetProperty("totals").GetProperty("files").GetInt32() >= 4);   // the 4 files we wrote
    }

    [Fact]
    public void Scan_MissingTree_ReturnsError()
    {
        using var tmp = new TempDir();
        string prof = Path.Combine(tmp.Path, "p.json");
        string missing = Path.Combine(tmp.Path, "no-such-tree");
        Assert.Equal(1, new Scanner(ArgParser.ParseScan(new[] { missing, "--out", prof })).Run());
    }

    [Fact]
    public void IncludeAggregate_ComputesUnresolvedAndAmbiguity()
    {
        var agg = new IncludeAggregate();
        // basename -> how many files in the tree carry that basename (2 ⇒ ambiguous resolution).
        var counts = new Dictionary<string, int> { ["b.h"] = 1, ["dup.h"] = 2 };
        agg.AddFile("a.c", new List<string> { "b.h", "missing.h", "dup.h" }, counts);
        var stats = agg.Build(hops: 2);

        Assert.Equal(2, stats.Hops);
        Assert.InRange(stats.UnresolvedIncludeRate, 0.0, 1.0);
        Assert.True(stats.UnresolvedIncludeRate > 0);              // "missing.h" resolves to nothing
        Assert.True(stats.DuplicateBasenameAmbiguity >= 1);       // "dup.h" matches 2 files
        Assert.NotNull(stats.FanoutHistogram);
    }
}
