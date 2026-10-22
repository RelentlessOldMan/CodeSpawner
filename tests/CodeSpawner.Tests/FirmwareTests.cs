using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeSpawner.Cli;
using CodeSpawner.Firmware;
using CodeSpawner.Verify;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The firmware corpus (docs/firmware-design.md): its knobs and refusals, that it is off by default, that gen's
/// truth agrees with every name's spelling (Z…Z must drop, Y…Y open, the rest must keep) on each case cases.tsv
/// lists, the boundary files, determinism and _meta.gen replay, that verify recomputes the truth and FAILs tampering,
/// the reference carve, and the truth rules on a hand-written tree. Compiling the units (and the carve) with clang is
/// checked by hand and in bench.
/// </summary>
[Collection("CapturesConsole")]   // swaps the process-wide Console: never alongside another class that does
public class FirmwareTests : IClassFixture<FirmwareTests.Corpus>
{
    private static string _lastErr = "";
    private static int Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var err = new StringWriter();
        Console.SetOut(TextWriter.Null);
        Console.SetError(err);
        try { return CodeSpawner.Program.Main(args); }
        finally { _lastErr = err.ToString(); Console.SetOut(o); Console.SetError(e); }
    }

    private static readonly string[] Tiny =
    [
        "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
        "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2",
    ];

    // The smallest firmware corpus that still holds every kind and trap: 3 big headers, 30 units.
    private static readonly string[] Fw =
    [
        "--firmware", "--fw-big-headers", "3", "--fw-files", "40", "--fw-families", "400", "--fw-pasters", "2000",
        "--fw-dense-headers", "2", "--fw-big-mb", "20",
    ];

    private static string Gen(string dir, string name, params string[] extra)
    {
        string corpus = Path.Combine(dir, name);
        int rc = Cli([.. new[] { "gen", "--out", corpus }, .. Tiny, .. extra]);
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        return corpus;
    }

    /// <summary>One corpus shared by the read-only tests (it is ~110 MB); compile db paths relative.</summary>
    public sealed class Corpus : IDisposable
    {
        private readonly TempDir _tmp = new();
        public readonly string Path;
        public Corpus() => Path = Gen(_tmp.Path, "fw", [.. Fw, "--fw-db-paths", "relative"]);
        public void Dispose() => _tmp.Dispose();
    }

    private readonly Corpus _c;
    public FirmwareTests(Corpus c) => _c = c;

    private static (int code, string err) Verify(string corpus, string? manifest = null, string? carve = null)
    {
        var err = new StringWriter();
        int code = ManifestVerifier.Run(new VerifyOptions { Corpus = corpus, Manifest = manifest ?? corpus + "-manifest.json", ReferenceCarve = carve }, err);
        return (code, err.ToString());
    }

    private static JsonObject Manifest(string corpus) => JsonNode.Parse(File.ReadAllText(corpus + "-manifest.json"))!.AsObject();

    private static HashSet<string> List(string corpus, string build, string name)
    {
        string rel = Manifest(corpus)["firmware"]!["builds"]![build]![name]!["path"]!.GetValue<string>();
        return File.ReadAllLines(Path.Combine(corpus, rel)).ToHashSet(StringComparer.Ordinal);
    }

    private static string Spelled(string name) =>
        name.Length > 1 && name[0] == 'Z' && name[^1] == 'Z' ? "drop" : name.Length > 1 && name[0] == 'Y' && name[^1] == 'Y' ? "open" : "keep";

    // ---------- knobs ----------

    [Theory]
    [InlineData("--fw-files", "10")]                                       // a firmware knob with it off
    [InlineData("--firmware", "--lang", "cpp")]
    [InlineData("--firmware", "--manifest", "false")]                     // the truth lives in the manifest
    [InlineData("--firmware", "--fw-big-mb", "19")]                       // a big header must pass maxParseBytes
    [InlineData("--firmware", "--fw-big-headers", "0")]
    [InlineData("--firmware", "--fw-chain-max", "1")]
    [InlineData("--firmware", "--fw-db-paths", "sideways")]
    public void BadKnobs_AreRefused(params string[] args) =>
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(["--out", "x", .. args]));

    [Fact]
    public void Preset_IsBarePlusFirmware()
    {
        var o = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "firmware"])]);
        Assert.True(o.Firmware);
        Assert.Equal((400, 38_000, 1_000_000, 6_000_000, 200, 24, 40, "absolute"),
            (o.FwBigHeaders, o.FwFiles, o.FwFamilies, o.FwPasters, o.FwDenseHeaders, o.FwBigMb, o.FwChainMax, o.FwDbPaths));
    }

    [Fact]
    public void Off_NoDirs_NoManifestKey_NoRecordedKnobs()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "c");
        Assert.All(FirmwareEmitter.Dirs, d => Assert.False(Directory.Exists(Path.Combine(c, d))));
        var m = Manifest(c);
        Assert.False(m.ContainsKey("firmware"));
        Assert.DoesNotContain(m["_meta"]!["gen"]!.AsObject(), kv => kv.Key.StartsWith("fw", StringComparison.Ordinal) || kv.Key == "firmware");
    }

    // ---------- the shared corpus ----------

    [Fact]
    public void Verify_Passes()
    {
        var (code, err) = Verify(_c.Path);
        Assert.True(code == 0, err);
    }

    [Fact]
    public void EveryCase_IsClassedAsSpelled()
    {
        var keep = List(_c.Path, "main", "mustKeepDefines");
        var drop = List(_c.Path, "main", "mustDropDefines");
        var open = List(_c.Path, "main", "openDefines");
        var rows = File.ReadAllLines(Path.Combine(_c.Path, FirmwareEmitter.CasesPath));
        Assert.Equal("name\tcase\tsite", rows[0]);
        var kinds = new HashSet<string>();
        foreach (var row in rows.Skip(1))
        {
            var cols = row.Split('\t');
            Assert.Equal(3, cols.Length);
            string got = keep.Contains(cols[0]) ? "keep" : drop.Contains(cols[0]) ? "drop" : open.Contains(cols[0]) ? "open" : "none";
            Assert.True(Spelled(cols[0]) == got, $"{cols[0]} ({cols[1]}): spelled {Spelled(cols[0])}, classed {got}");
            kinds.Add(cols[1].Split(' ')[0]);
        }
        Assert.True(kinds.Count >= 40, string.Join(", ", kinds));   // 18 family kinds, the traps, the hostile files
        // a #define inside a comment is no define
        Assert.DoesNotContain("KCMT0X", keep.Concat(drop).Concat(open));
        // every class is populated; the union build of one build is the same lists
        Assert.True(keep.Count > 0 && drop.Count > 0 && open.Count > 0);
        Assert.Equal(keep, List(_c.Path, "union", "mustKeepDefines"));
    }

    [Fact]
    public void Boundaries_AndDensity()
    {
        string fw = Path.Combine(_c.Path, FwSpec.FwDir);
        Assert.Equal(FwSpec.MaxParseBytes + 1, new FileInfo(Path.Combine(fw, "big/regs_0.h")).Length);
        Assert.Equal(FwSpec.MaxParseBytes, new FileInfo(Path.Combine(fw, "src/table_big.c")).Length);
        Assert.Equal(FwLexer.DenseMinBytes - 1, new FileInfo(Path.Combine(fw, "dense/dense_small.h")).Length);
        Assert.Equal(20L << 20, new FileInfo(Path.Combine(fw, "big/regs_1.h")).Length);
        Assert.Equal((true, false), FwLexer.Density(Path.Combine(fw, "dense/dense_70.h")));
        Assert.Equal((true, true), FwLexer.Density(Path.Combine(fw, "dense/dense_60.h")));
        Assert.Equal((false, true), FwLexer.Density(Path.Combine(fw, "dense/dense_5999.h")));
        Assert.Equal((false, false), FwLexer.Density(Path.Combine(fw, "dense/dense_50.h")));
        Assert.True(File.GetAttributes(Path.Combine(fw, "misc/.fwhidden")).HasFlag(FileAttributes.Hidden));

        var f = Manifest(_c.Path)["firmware"]!;
        var big = f["bigHeaders"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("big/regs_0.h", big);
        Assert.Contains("src/bigsrc_0.c", big);
        Assert.DoesNotContain("src/table_big.c", big);   // exactly maxParseBytes is parsed
        Assert.Equal(["dense/shared_0.h"], f["wholeIncludedFromOutside"]!.AsArray().Select(x => x!.GetValue<string>()));
        var c = f["counts"]!;
        Assert.True(c["definesDroppedMin"]!.GetValue<long>() <= c["definesDroppedMax"]!.GetValue<long>());
        Assert.True(c["bytesAfterMin"]!.GetValue<long>() <= c["bytesAfterMax"]!.GetValue<long>());
        Assert.Equal(".", JsonNode.Parse(File.ReadAllText(Path.Combine(_c.Path, FwSpec.DbPath)))![0]!["directory"]!.GetValue<string>());
    }

    [Fact]
    public void Verify_FailsATamperedManifest()
    {
        using var tmp = new TempDir();
        foreach (var (path, value) in new (string[] Path, JsonNode Value)[]
        {
            (["counts", "mustKeep"], 1),
            (["treeSha256"], "00"),
            (["builds", "union", "openDefines", "sha256"], "00"),
            (["bigHeaders"], new JsonArray("big/regs_0.h")),
        })
        {
            var m = Manifest(_c.Path);
            JsonNode n = m["firmware"]!;
            foreach (var k in path[..^1]) n = n[k]!;
            n[path[^1]] = value;
            string mp = Path.Combine(tmp.Path, "m.json");
            File.WriteAllText(mp, m.ToJsonString());
            var (code, err) = Verify(_c.Path, mp);
            Assert.True(code == 1, $"{string.Join(".", path)}: exit {code}");
            Assert.Contains("FAIL firmware", err);
        }
    }

    [Fact]
    public void ReferenceCarve_KeepsMustKeep_DropsTheRest()
    {
        using var tmp = new TempDir();
        string carve = Path.Combine(tmp.Path, "carve");
        var (code, err) = Verify(_c.Path, carve: carve);
        Assert.True(code == 0, err);
        string regs = File.ReadAllText(Path.Combine(carve, "fw/big/regs_0.h"));
        Assert.True(regs.Length < 2_000_000);
        Assert.Contains("#define KDV_VAL 9", regs);
        Assert.DoesNotContain("#define ZBIN0Z", regs);
        Assert.DoesNotContain("#define ZPA0Z", regs);
        // files that are not carved are copied as they are
        Assert.Equal(File.ReadAllBytes(Path.Combine(_c.Path, "fw/dense/shared_0.h")), File.ReadAllBytes(Path.Combine(carve, "fw/dense/shared_0.h")));
        Assert.True(File.Exists(Path.Combine(carve, FwSpec.DbPath)));
    }

    // ---------- determinism, replay, tree tampering ----------

    private static SortedDictionary<string, string> Snap(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (!f.EndsWith("compile_commands.json", StringComparison.Ordinal))   // names the tree's absolute path
                map[Path.GetRelativePath(root, f).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return map;
    }

    [Fact]
    public void SameSeed_SameBytes_MetaGenReplays_AndATreeEditFails()
    {
        using var tmp = new TempDir();
        string a = Gen(tmp.Path, "a", Fw);
        var gen = Manifest(a)["_meta"]!["gen"]!.AsObject();
        Assert.Equal(3, gen["fwBigHeaders"]!.GetValue<long>());
        var replay = new List<string> { "gen", "--out", Path.Combine(tmp.Path, "r") };
        foreach (var (k, v) in gen)
        {
            string knob = k == "maxHeaderMB" ? "max-header-mb" : System.Text.RegularExpressions.Regex.Replace(k, "[A-Z]", x => "-" + char.ToLowerInvariant(x.Value[0]));
            replay.Add($"--{knob}={(v!.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString())}");
        }
        Assert.True(Cli([.. replay]) == 0, _lastErr);
        string r = Path.Combine(tmp.Path, "r");
        Assert.Equal(Snap(a), Snap(r));
        Assert.Equal(Manifest(a)["firmware"]!["treeSha256"]!.ToJsonString(), Manifest(r)["firmware"]!["treeSha256"]!.ToJsonString());
        Directory.Delete(r, true);

        // one byte of one unit changed: the tree is not the one the truth describes
        string unit = Directory.EnumerateFiles(Path.Combine(a, "fw/src/m0"), "u*.c").First();
        File.AppendAllText(unit, " ");
        var (code, err) = Verify(a);
        Assert.Equal(1, code);
        Assert.Contains("treeSha256", err);
    }

    // ---------- the rules, on a hand-written tree ----------

    // A dense header (density 1.0, past the 1 MB floor) holding `defines`, and one unit that includes it.
    private static FwTruth Mini(TempDir tmp, string defines, string unit)
    {
        string root = tmp.Path, fw = Path.Combine(root, "fw");
        Directory.CreateDirectory(fw);
        Directory.CreateDirectory(Path.Combine(root, "fw-build"));
        var sb = new StringBuilder("#define CAT_(a, b) a##b\n#define CAT(a, b) CAT_(a, b)\n").Append(defines);
        for (int i = 0; sb.Length < 1_100_000; i++) sb.Append($"#define ZFILL{i}Z {i}\n");
        File.WriteAllText(Path.Combine(fw, "d.h"), sb.ToString());
        File.WriteAllText(Path.Combine(fw, "u.c"), "#include \"d.h\"\n" + unit);
        File.WriteAllText(Path.Combine(root, FwSpec.DbPath),
            """[{"directory": ".", "arguments": ["cc", "-I", "fw", "-c", "fw/u.c"], "file": "fw/u.c"}]""");
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        return FwSpec.Compute(root, Path.Combine(root, "lists"), checkAlphabet: true);
    }

    [Fact]
    public void Rules_BranchesRestart_CommentsAndStringsAndEofAreOpen_DefinesInCommentsAreNot()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            """
            #define KA_1 1
            #define KB_1 1
            #define KC_1 1
            #define YCM_1Y 1
            #define YST_1Y 1
            #define YEO_1Y 1
            #define ZNEVER_1Z 1
            /*
            #define KFAKE 1
            */

            """,
            """
            int a = CAT(KA_,
            #ifdef ALT
            	KB_);
            #else
            	KC_);
            #endif
            /* CAT(YCM_, 1) */
            const char *s = "CAT(YST_, 1)";
            int e = CAT(YEO_, 1

            """);
        Assert.Empty(t.AlphabetErrors);
        var keep = File.ReadAllLines(Path.Combine(tmp.Path, "lists", "main.mustKeepDefines.txt"));
        var open = File.ReadAllLines(Path.Combine(tmp.Path, "lists", "main.openDefines.txt"));
        Assert.Contains("KB_1", keep);
        Assert.Contains("KC_1", keep);
        Assert.Equal(["YCM_1Y", "YEO_1Y", "YST_1Y"], open);
        Assert.DoesNotContain("KFAKE", keep);
    }

    [Fact]
    public void Rules_AliasingTheSpellingCheckCatchesAWrongClass()
    {
        // ZUSED_1Z is spelled must-drop but is reached: gen would refuse to ship this as truth
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define ZUSED_1Z 1\n", "int a = CAT(ZUSED_, 1);\n");
        Assert.Contains(t.AlphabetErrors, e => e.Contains("ZUSED_1Z"));
    }
}
