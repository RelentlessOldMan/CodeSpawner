using CodeSpawner.Generation;
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

    // Files land per the measured files-per-dir histogram: one full directory among nine empty ones stays that way,
    // where a uniform pick spread the files over all ten.
    [Fact]
    public void FromProfile_FollowsTheFilesPerDirHistogram()
    {
        using var tmp = new TempDir();
        string tree = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(tree);
        for (int i = 0; i < 80; i++) File.WriteAllText(Path.Combine(tree, $"f{i}.c"), $"int f{i}(void) {{ return {i}; }}\n");
        for (int i = 0; i < 9; i++) Directory.CreateDirectory(Path.Combine(tree, $"empty{i}"));
        string prof = Path.Combine(tmp.Path, "prof.json");
        Assert.Equal(0, new Scanner(ArgParser.ParseScan(new[] { tree, "--out", prof })).Run());

        string o = Path.Combine(tmp.Path, "g");
        new ProfileGenerator(ArgParser.ParseGen(new[] { "--out", o, "--from-profile", prof })).Run();
        var full = Directory.EnumerateDirectories(o, "*", SearchOption.AllDirectories).Append(o)
            .Count(d => Directory.EnumerateFiles(d).Any(f => !f.EndsWith(".codespawner", StringComparison.Ordinal)));
        Assert.Equal(1, full);
    }

    [Fact]
    public void Scan_CountsFilesItCannotRead()
    {
        using var tmp = new TempDir();
        string tree = BuildTree(tmp);
        string prof = Path.Combine(tmp.Path, "prof.json");
        // Held open with no sharing, the file still lists with its size but can't be opened for sampling.
        using (new FileStream(Path.Combine(tree, "b.h"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scan = new Scanner(ArgParser.ParseScan(new[] { tree, "--out", prof }));
            Assert.Equal(0, scan.Run());
            Assert.Equal(1, scan.UnreadableFiles);
            Assert.Equal(0, scan.UnreadableDirs);
        }
    }

    [Fact]
    public void Scan_RefusesConflictingPostures_AndAnOutInsideTheTree()
    {
        using var tmp = new TempDir();
        string tree = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(tree);
        Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { tree, "--out", tmp.File("p.json"), "--structure-only", "--content-stats" }));
        Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { tree, "--out", Path.Combine(tree, "src", "f.c") }));
        Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { tree + Path.DirectorySeparatorChar, "--out", Path.Combine(tree, "p.json") }));
        ArgParser.ParseScan(new[] { tree, "--out", tree + "-profile.json", "--structure-only" });   // a sibling is fine
        Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { tree, "--out", tree + "-profile.json", "--structure-only", "--structure-only" }));
        Assert.Equal(1350, Scanner.Round3(1347));
        Assert.Equal(840000, Scanner.Round3(840043));
        Assert.Equal(999, Scanner.Round3(999));
        Assert.Equal(10000, Scanner.Round3(9996));
    }

    // Depth is measured from the tree root, so a trailing separator on the argument doesn't shift it by one.
    [Fact]
    public void Scan_DepthHistogram_IgnoresATrailingSeparator()
    {
        using var tmp = new TempDir();
        string tree = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
        File.WriteAllText(Path.Combine(tree, "a", "b", "f.c"), "int x;\n");
        string Depths(string arg)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ScanToText(arg, tmp.File(Guid.NewGuid() + ".json"), "--structure-only"));
            return doc.RootElement.GetProperty("dirs").GetProperty("depthHistogram").GetRawText();
        }
        string plain = Depths(tree);
        Assert.Equal(plain, Depths(tree + Path.DirectorySeparatorChar));
        Assert.Contains("\"2\"", plain);                                            // root 0, a 1, a/b 2
        Assert.DoesNotContain("\"-1\"", plain);
    }

    [Fact]
    public void Scan_MissingTree_ReturnsError()
    {
        using var tmp = new TempDir();
        string prof = Path.Combine(tmp.Path, "p.json");
        string missing = Path.Combine(tmp.Path, "no-such-tree");
        Assert.Equal(2, new Scanner(ArgParser.ParseScan(new[] { missing, "--out", prof })).Run());   // bad input
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

    // --- the privacy contract (docs/scan-design.md: "must be airtight") ---
    // A tree whose every name, identifier, macro, string and comment carries a canary. Two clusters big enough to
    // be real archetypes (6 .c + 6 .h of one size band, k = 5) plus two singletons that must fold into "other".

    private const string Canary = "ZQXCANARY";

    private static string BuildCanaryTree(TempDir tmp)
    {
        string root = Path.Combine(tmp.Path, Canary + "_tree");
        string dir = Path.Combine(root, Canary + "_dir", Canary + "_sub");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 6; i++)
        {
            string body = string.Concat(Enumerable.Range(0, 40).Select(l =>
                $"#define {Canary}_MACRO_{i}_{l} {l}\n" +
                $"/* {Canary} comment {l} */ static const char *{Canary}_str_{i}_{l} = \"{Canary} literal\";\n" +
                $"int {Canary}_fn_{i}_{l}(int {Canary}_arg) {{ return {Canary}_fn_{i}_{(l + 1) % 40}({Canary}_arg); }}\n"));
            File.WriteAllText(Path.Combine(dir, $"{Canary}_src_{i}.c"), $"#include \"{Canary}_hdr_{i}.h\"\n" + body);
            File.WriteAllText(Path.Combine(dir, $"{Canary}_hdr_{i}.h"), body);
        }
        File.WriteAllText(Path.Combine(root, $"{Canary}_lonely.py"), $"# {Canary}\n{Canary}_x = '{Canary}'\n");
        File.WriteAllText(Path.Combine(root, $"{Canary}_notes.txt"), $"{Canary} secret notes\n");
        // a dot inside a name is not an extension: neither a rare suffix nor one that isn't extension-shaped is kept
        File.WriteAllText(Path.Combine(root, $"board deck.{Canary}"), "x\n");
        File.WriteAllText(Path.Combine(root, $"backup.{Canary}@example.com.old copy"), "x\n");
        return root;
    }

    private static string ScanToText(string tree, string prof, params string[] posture)
    {
        int rc = new Scanner(ArgParser.ParseScan(new[] { tree, "--out", prof }.Concat(posture).ToArray())).Run();
        Assert.Equal(0, rc);
        return File.ReadAllText(prof);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--structure-only")]
    [InlineData("--content-stats")]
    public void Scan_Profile_LeaksNoNames_KAnonymizes_AndIsDeterministic(string postureFlag)
    {
        string[] posture = postureFlag == "" ? Array.Empty<string>() : new[] { postureFlag };
        using var tmp = new TempDir();
        string tree = BuildCanaryTree(tmp);
        string text = ScanToText(tree, Path.Combine(tmp.Path, "p1.json"), posture);

        // nothing name- or content-derived, in any case, nor any part of the scanned path
        Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tmp.Path.Replace("\\", "\\\\"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("literal", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("comment ", text, StringComparison.OrdinalIgnoreCase);

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var arch = doc.RootElement.GetProperty("archetypes").EnumerateArray().ToList();
        var real = arch.Where(a => a.GetProperty("label").GetString() != "other").ToList();
        var other = arch.Single(a => a.GetProperty("label").GetString() == "other");
        // the two 6-file clusters are real archetypes, labelled positionally; the singletons exist only in "other"
        Assert.Equal(new[] { ".c", ".h" }, real.Select(a => a.GetProperty("extension").GetString()).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(real, a => Assert.Matches("^a[0-9]+$", a.GetProperty("label").GetString()!));
        Assert.All(real, a => Assert.Equal(6, a.GetProperty("count").GetInt32()));
        Assert.Equal("*", other.GetProperty("extension").GetString());
        Assert.Equal(4, other.GetProperty("count").GetInt32());
        Assert.DoesNotContain(arch, a => a.GetProperty("extension").GetString() is ".py" or ".txt");
        // the extension histogram is k-anonymized too: the four lone suffixes are one (other) row
        var exts = doc.RootElement.GetProperty("extensions").EnumerateArray()
            .ToDictionary(e => e.GetProperty("ext").GetString()!, e => e.GetProperty("files").GetInt64());
        Assert.Equal(new Dictionary<string, long> { [".c"] = 6, [".h"] = 6, ["(other)"] = 4 }, exts);
        // p50/p90/max are rounded, never one file's exact size
        Assert.All(arch, a => Assert.All(new[] { "p50", "p90", "max" },
            k => { long v = a.GetProperty("sizeDistribution").GetProperty(k).GetInt64(); Assert.Equal(Scanner.Round3(v), v); }));

        // posture decides which blocks exist
        bool reads = posture.Length == 0 || posture[0] == "--content-stats", stats = posture.Length > 0 && posture[0] == "--content-stats";
        Assert.All(real, a => Assert.Equal(reads, a.TryGetProperty("class", out _)));
        Assert.All(real, a => Assert.Equal(reads, a.TryGetProperty("trigram", out _)));
        Assert.All(real, a => Assert.Equal(stats, a.TryGetProperty("content", out _)));
        Assert.Equal(reads, doc.RootElement.GetProperty("totals").TryGetProperty("distinctTrigramEstimate", out _));

        // byte-identical on a re-scan, once the timestamp is set aside
        string again = ScanToText(tree, Path.Combine(tmp.Path, "p2.json"), posture);
        static string NoClock(string t) => System.Text.RegularExpressions.Regex.Replace(t, "\"scannedAt\": \".*\"", "");
        Assert.Equal(NoClock(text), NoClock(again));
    }

    // A suffix common enough to pass --min-cluster can still be a name: a dotfile (".zqxcanary-acme", all name) in
    // many dirs, or a user's suffix ("notes.zqxcanaryjs") repeated in their own dir. Neither is recorded.
    [Theory]
    [InlineData("")]
    [InlineData("--structure-only")]
    public void Scan_NameLikeSuffixes_NeverRecorded(string postureFlag)
    {
        using var tmp = new TempDir();
        string root = Path.Combine(tmp.Path, "tree");
        for (int d = 0; d < 3; d++)
        {
            string dir = Path.Combine(root, $"d{d}");
            Directory.CreateDirectory(dir);
            for (int k = 0; k < 2; k++)
            {
                Directory.CreateDirectory(Path.Combine(dir, $"k{k}"));
                File.WriteAllText(Path.Combine(dir, $"k{k}", $".{Canary}-acme"), "x\n");
            }
        }
        string mine = Path.Combine(root, "home");
        Directory.CreateDirectory(mine);
        for (int i = 0; i < 6; i++) File.WriteAllText(Path.Combine(mine, $"notes{i}.{Canary}js"), "x\n");
        for (int i = 0; i < 6; i++) File.WriteAllText(Path.Combine(root, $"d{i % 3}", $"f{i}.c"), "int x;\n");

        string[] posture = postureFlag == "" ? Array.Empty<string>() : new[] { postureFlag };
        string text = ScanToText(root, Path.Combine(tmp.Path, "p.json"), posture);
        Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var exts = doc.RootElement.GetProperty("extensions").EnumerateArray()
            .ToDictionary(e => e.GetProperty("ext").GetString()!, e => e.GetProperty("files").GetInt64());
        Assert.Equal(new Dictionary<string, long> { [".c"] = 6, ["(dotfile)"] = 6, ["(other)"] = 6 }, exts);
    }

    // gen --from-profile honours the profile: one file per archetype count (named <label>_<i><ext>, "other" → .dat),
    // every size inside its archetype's drawn range (give or take the line the cut lands on), and the same profile + seed is byte-identical.
    [Fact]
    public void GenFromProfile_MatchesArchetypes_AndIsDeterministic()
    {
        using var tmp = new TempDir();
        string prof = Path.Combine(tmp.Path, "p.json");
        ScanToText(BuildCanaryTree(tmp), prof);
        var arch = CodeSpawner.Profile.ProfileReader.Load(prof).Archetypes;

        string a = Path.Combine(tmp.Path, "ga"), b = Path.Combine(tmp.Path, "gb");
        foreach (var o in new[] { a, b })
            Assert.Equal(0, CodeSpawner.Program.Main(new[] { "gen", "--out", o, "--from-profile", prof, "--seed", "3" }));

        var files = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != ".codespawner").ToList();
        Assert.Equal(arch.Sum(x => x.Count), files.Count);
        foreach (var x in arch)
        {
            string ext = x.Extension == "*" ? ".dat" : x.Extension;
            var mine = files.Where(f => Path.GetFileName(f).StartsWith(x.Label + "_") && f.EndsWith(ext)).ToList();
            Assert.Equal(x.Count, mine.Count);
            long lo = Math.Max(1, x.SizeDistribution.P50 / 2) - 128, hi = Math.Max(x.SizeDistribution.P90, x.SizeDistribution.Max) + 128;   // files end on a line boundary
            Assert.All(mine, f => Assert.InRange(new FileInfo(f).Length, lo, hi));
        }

        static SortedDictionary<string, string> Snap(string root) => new(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))),
            StringComparer.Ordinal);
        Assert.Equal(Snap(a), Snap(b));
    }
}
