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
        name.Length < 2 ? "keep"
        : (name[0] is 'Z' or 'Y') && name[^1] == 'Z' || name[0] == 'Z' && name[^1] == 'Y' ? "drop"
        : name[0] == 'Y' && name[^1] == 'Y' ? "open" : "keep";

    // ---------- knobs ----------

    [Theory]
    [InlineData("--fw-files", "10")]                                       // a firmware knob with it off
    [InlineData("--firmware", "--lang", "cpp")]
    [InlineData("--firmware", "--manifest", "false")]                     // the truth lives in the manifest
    [InlineData("--firmware", "--fw-big-mb", "19")]                       // a big header must pass maxParseBytes
    [InlineData("--firmware", "--fw-big-headers", "0")]
    [InlineData("--firmware", "--fw-chain-max", "1")]
    [InlineData("--firmware", "--fw-db-paths", "sideways")]
    [InlineData("--firmware", "--fw-work-defines", "10")]                 // a work knob with the work carve off
    [InlineData("--firmware", "--fw-work", "--fw-work-keep-pct", "50", "--fw-work-decoy-pct", "40")]   // past 100%
    [InlineData("--firmware", "--fw-work", "--fw-work-growth", "1")]
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
    public void Preset_FirmwareWork_IsASmallFixedCorrectnessPartPlusTheScaledWork()
    {
        var o = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "firmware-work"])]);
        Assert.True(o.Firmware && o.FwWork);
        Assert.Equal((1, 296, 192, 20_000, 0), (o.FwBigHeaders, o.FwFiles, o.FwFamilies, o.FwPasters, o.FwDenseHeaders));
        Assert.Equal(1, o.Eff("fw-big-headers", o.FwBigHeaders, keepOne: true));
        o.Scale = 0.1;
        Assert.Equal((12_230_000, 500, 1_500), (o.Eff("fw-work-defines", o.FwWorkDefines), o.Eff("fw-work-pasters", o.FwWorkPasters),
            o.Eff("fw-work-forwarders", o.FwWorkForwarders)));
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
        Assert.True(kinds.Count >= 59, string.Join(", ", kinds));   // 44 family kinds, the traps, the hostile files
        foreach (var k in new[] { "comment-hash", "empty-tail-alias", "param-tail-alias", "self-comma" }) Assert.Contains(k, kinds);
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
        Assert.True(File.GetAttributes(Path.Combine(_c.Path, "ext/include/fw_osh.h")).HasFlag(FileAttributes.Hidden));
        Assert.Contains("KOSH0X_VAL", List(_c.Path, "main", "mustKeepDefines"));   // seeded only by that hidden header
        Assert.Contains("YEXU0Y", List(_c.Path, "main", "openDefines"));          // only in an outside header nothing includes

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
            if (!Path.GetFileName(f).StartsWith("compile_commands.", StringComparison.Ordinal))   // both dbs name the tree's absolute path
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

    // ---------- the work carve ----------

    [Fact]
    public void Work_CountsAreRecountedByVerify_DecoysDrop_ExactAndMacrosKeep()
    {
        // the shared corpus has no work carve
        Assert.False(Directory.Exists(Path.Combine(_c.Path, FwSpec.FwDir, FwWork.Dir)));
        Assert.False(Manifest(_c.Path)["firmware"]!.AsObject().ContainsKey("work"));

        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "w", [
            "--firmware", "--fw-big-headers", "1", "--fw-files", "40", "--fw-families", "64", "--fw-pasters", "200",
            "--fw-dense-headers", "0", "--fw-work", "--fw-work-defines", "40000", "--fw-work-pasters", "60", "--fw-work-forwarders", "180", .. TreePcts, .. SmallWide]);
        var (code, err) = Verify(c);
        Assert.True(code == 0, err);

        var wk = Manifest(c)["firmware"]!["work"]!;
        var w = wk["tree"]!;
        long L(string k) => w[k]!.GetValue<long>();
        var levels = w["byLevel"]!.AsArray().Select(x => x!["uses"]!.GetValue<long>()).ToArray();
        Assert.Equal(6, L("levels"));
        Assert.Equal(7, L("expectedPasses"));
        Assert.Equal(8 * levels[0], levels[1]);                      // pieces x9 from pass 1 to pass 2
        Assert.Equal(levels.Sum(), L("exactKept"));
        Assert.Equal(L("exactKept") + L("crossDecoys"), L("keptUnderGlobalPools"));
        Assert.True(L("exactKept") + L("crossDecoys") + L("halfDecoys") + L("filler") >= L("defines"));   // + padding filler
        Assert.Equal(L("exactKept"), w["uses"]!.AsObject().Sum(kv => kv.Value!.GetValue<long>()));
        Assert.True(L("crossDecoys") > 0 && L("halfDecoys") > 0 && L("forwarders") > 0);

        var keep = List(c, "main", "mustKeepDefines");
        var drop = List(c, "main", "mustDropDefines");
        Assert.Equal(L("pasters"), keep.Count(n => n.StartsWith("WRP", StringComparison.Ordinal)));
        Assert.Equal(L("appliers"), keep.Count(n => n.StartsWith("WAP", StringComparison.Ordinal)));
        Assert.Equal(L("crossDecoys"), drop.Count(n => FwWork.Classify(n) == "cross"));
        // the wide population (1.4.11): kept pasters and roots, open matches and body names, dropped near misses
        long WL(string k) => wk[k]!.GetValue<long>();
        var open = List(c, "main", "openDefines");
        Assert.Equal(10, WL("patterns"));
        Assert.Equal(WL("patterns"), keep.Count(n => FwWide.Classify(n) == "widePaster"));
        Assert.Equal(WL("openByPattern"), open.Count(n => FwWide.Classify(n) == "matched"));
        Assert.Equal(WL("nearMissDropped"), drop.Count(n => FwWide.Classify(n) == "near"));
        Assert.True(WL("openByPattern") > 0 && WL("namedOnly") > 0 && WL("nearMissDropped") > 0 && WL("pasteMacrosUsed") > 0);
        Assert.Equal(12, keep.Count(n => FwWide.Classify(n) == "edge"));
        foreach (var row in File.ReadAllLines(Path.Combine(c, FirmwareEmitter.CasesPath)).Where(r => r.Contains("\twork-")))
        {
            string n = row.Split('\t')[0];
            Assert.True(row.Contains("work-exact") || row.Contains(", kept)") || row.Contains("work-wide-paster") ? keep.Contains(n)
                : row.Contains("open)") ? open.Contains(n) : drop.Contains(n), row);
        }

        // a wrong work count fails verify
        var m = Manifest(c);
        m["firmware"]!["work"]!["tree"]!["crossDecoys"] = L("crossDecoys") + 1;
        string mp = Path.Combine(tmp.Path, "m.json");
        File.WriteAllText(mp, m.ToJsonString());
        var (code2, err2) = Verify(c, mp);
        Assert.True(code2 == 1, err2);
        Assert.Contains("work.tree.crossDecoys", err2);
    }

    [Fact]
    public void Rules_BranchesRestart_CommentsAndStringsAndEofAreOpen_DefinesInCommentsAreNot()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            """
            #define KA_X1 1
            #define KA_X2 1
            #define ZB_X1Z 1
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
            	X1);
            #else
            	X2);
            #endif
            /* CAT(YCM_, 1Y) */
            const char *s = "CAT(YST_, 1Y)";
            int e = CAT(YEO_, 1Y

            """);
        Assert.Empty(t.AlphabetErrors);
        var keep = File.ReadAllLines(Path.Combine(tmp.Path, "lists", "main.mustKeepDefines.txt"));
        var open = File.ReadAllLines(Path.Combine(tmp.Path, "lists", "main.openDefines.txt"));
        Assert.Contains("KA_X1", keep);
        Assert.Contains("KA_X2", keep);
        Assert.Equal(["YCM_1Y", "YEO_1Y", "YST_1Y"], open);
        Assert.DoesNotContain("KFAKE", keep);
    }

    [Fact]
    public void Rules_AliasingTheSpellingCheckCatchesAWrongClass()
    {
        // ZUSED_1Z is spelled must-drop but is reached: gen would refuse to ship this as truth
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define ZUSED_1Z 1\n", "int a = CAT(ZUSED_, 1Z);\n");
        Assert.Contains(t.AlphabetErrors, e => e.Contains("ZUSED_1Z"));
    }

    private static string[] Lists(TempDir tmp, string which) => File.ReadAllLines(Path.Combine(tmp.Path, "lists", $"main.{which}Defines.txt"));

    [Fact]
    public void Rules_ANameNeedsAStartPieceAndAnEndPiece()
    {
        // ZS_ only starts a name and E1Z only ends one: a name with just one of them is dropped
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define ZS_E1 1\n#define ZS_QZ 1\n#define KT_E1Z 1\n#define ZQ_E1Z 1\n#define ZE1Z 1\n",
            "int a = CAT(ZS_, E1) + CAT(KT_, E1Z);\n");
        Assert.Empty(t.AlphabetErrors);
        Assert.Equal(["KT_E1Z", "ZS_E1"], Lists(tmp, "mustKeep").Where(n => !n.StartsWith("CAT", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void Rules_InteriorParamsAndDigraphs()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define KHP(b, n) KHB_##b##_r##n##_VAL\n#define KHB_Z_rZ_VAL 1\n#define ZIZ 1\n#define KDGQ_5 1\n",
            "%:define KDG(n) KDGQ_%:%:n\nint a = KHP(Z, Z) + KDG(5) + CAT(KDGQ_, 5);\n");
        Assert.Empty(t.AlphabetErrors);
        var keep = Lists(tmp, "mustKeep");
        Assert.Contains("KHB_Z_rZ_VAL", keep);
        Assert.Contains("KDGQ_5", keep);
    }

    [Theory]
    [InlineData("__LINE__")]
    [InlineData("__COUNTER__")]
    public void Rules_LineAndCounterEndAnyNameEndingInADigit(string wild)
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define ZW_31 1\n#define ZW_QZ 1\n", $"int a = CAT(ZW_, {wild});\n");
        Assert.Empty(t.AlphabetErrors);
        Assert.Contains("ZW_31", Lists(tmp, "mustKeep"));
    }

    [Fact]
    public void Rules_AnUndefinedReservedNameEndsAnyName()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define ZV_Q 1\n#define ZNOSTART_QZ 1\n", "int a = CAT(ZV_, __SIZEOF_INT__);\n");
        Assert.Empty(t.AlphabetErrors);
        Assert.Contains("ZV_Q", Lists(tmp, "mustKeep"));
    }

    [Fact]
    public void Rules_AHigherOrderForwarderIsAPasteMacro()
    {
        // MYCAT names CAT without calling it: it forwards to CAT, so its argument is a piece
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define KAP(m, a, b) m(a, b)\n#define MYCAT(a) KAP(CAT, a, _6)\n#define ZHG_6 1\n#define ZHG_REGZ 1\n",
            "int a = MYCAT(ZHG);\n");
        Assert.Empty(t.AlphabetErrors);
        Assert.Contains("ZHG_6", Lists(tmp, "mustKeep"));
        Assert.Contains("ZHG_REGZ", File.ReadAllLines(Path.Combine(tmp.Path, "lists", "main.mustDropDefines.txt")));
    }

    // ---------- rule 3: each use builds its own names ----------

    private static string[] Keep(TempDir tmp) => Lists(tmp, "mustKeep").Where(n => !n.StartsWith("CAT", StringComparison.Ordinal)).ToArray();

    // the alphabet errors, less the unused CAT and CAT_ every mini defines
    private static string[] Bad(FwTruth t) => t.AlphabetErrors.Where(e => !e.StartsWith("CAT", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Uses_PiecesOfDifferentUsesNeverPair()
    {
        // two uses of one chain build their own names; the crossed name is dropped
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define SUF2(a, b) a##SFX##b\n#define ZASFXB 1\n#define CSFXDZ 1\n#define ZASFXDZ 1\n",
            "int a = SUF2(ZA, B) + SUF2(C, DZ);\n");
        Assert.Empty(Bad(t));
        Assert.Contains("CSFXDZ", Keep(tmp));
        Assert.Contains("ZASFXB", Keep(tmp));
        Assert.Contains("ZASFXDZ", Lists(tmp, "mustDrop"));
    }

    [Fact]
    public void Uses_AnUncalledPasteMacroNamedInCodeIsAWildcardUse()
    {
        // H is named, never called: its slot is any text, so KHW_<anything>_OFF is built, never overlapping
        using var tmp = new TempDir();
        Mini(tmp, "#define G(a) KHW_##a##_OFF\n#define H(p) G(p)\n#define KHW_Q_OFF 1\n#define KHW__OFF 1\n#define KHW_OFF 1\n#define KHW_Q 1\n",
            "void *p = (void *)H;\n");
        var keep = Keep(tmp);
        Assert.Contains("KHW_Q_OFF", keep);
        Assert.Contains("KHW__OFF", keep);
        Assert.DoesNotContain("KHW_OFF", keep);
        Assert.DoesNotContain("KHW_Q", keep);
    }

    [Fact]
    public void Uses_AnUncalledPasteMacroCalledInDroppedTextGetsNoWildcardUse()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define G(a) ZHW_##a##_OFFZ\n#define ZHW_Q_OFFZ 1\n#define ZDEADZ G(Q)\n", "void *p = (void *)G;\n");
        Assert.Empty(Bad(t));
    }

    [Fact]
    public void Uses_AnExpandedArgumentGivesItsSpellingAndItsExpansion()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define FWD(a) CAT(KV_, a)\n#define U 3\n#define KV_U 1\n#define KV_3 1\n#define NOTHING\n#define KN_NOTHING 1\n#define KN_ 1\n" +
            "#define KE_ 1\n#define KWP_ CAT(KWP_, 5)\n#define KWB CAT(KWP_, 5)\n#define KWP_5 1\n#define KWP_5_X 1\n#define ZWB_XZ 1\n",
            "int a = FWD(U) + CAT(KN_, NOTHING) + CAT(KE_, ) + CAT(KWB, _X);\n");
        Assert.Empty(Bad(t));
        var keep = Keep(tmp);
        foreach (var n in new[] { "KV_U", "KV_3", "KN_NOTHING", "KN_", "KE_", "KWP_5", "KWP_5_X" }) Assert.Contains(n, keep);
    }

    [Fact]
    public void Uses_HigherOrderExactAndComputedCalleePool()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define KAP(m, a, b) m(a, b)\n#define KHX_1 1\n#define ZHX_2Z 1\n#define ZHX_1Z 1\n" +
            "#define KCP_9 1\n#define KCP_Q9 1\n#define ZCP_Z 1\n#define ZCP_9Z 1\n",
            "int a = KAP(CAT, KHX_, 1) + CAT(CA, T)(KCP_, 9);\n");
        // the pool (a start piece and an end piece, anything between) builds KCP_Q9 and no exact build does: open
        // since 1.4.10 (CodeCarver), though its K spelling says keep
        Assert.Equal(["KCP_Q9: open, alphabet says keep"], Bad(t));
        var keep = Keep(tmp);
        Assert.Contains("KHX_1", keep);
        Assert.Contains("KCP_9", keep);
        Assert.Contains("KCP_Q9", Lists(tmp, "open"));
    }


    // ---------- 1.4.8: shapes from review round 9 (each checked against gcc -E and clang -E) ----------

    [Fact]
    public void Uses_CommasFromAnObjectLikeExpansionResplitTheArguments_AnOpenBodyCallIsOpen()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define GPIO_PORT(p, n) p##_##n\n#define PORT(x) GPIO_PORT(x)\n#define PIN_LED KGPIOA, 5\n#define KGPIOA_5 1\n" +
            "#define CALL(m, ...) m(__VA_ARGS__)\n#define ARGS KQA, 7\n#define KQA7 1\n#define OPENF(x) CAT(x,\n#define YOPB_RY 1\n",
            "int a = PORT(PIN_LED);\nint b = CALL(CAT, ARGS);\nint c = OPENF(YOPB_) RY);\n");
        Assert.Empty(Bad(t));
        var keep = Keep(tmp);
        Assert.Contains("KGPIOA_5", keep);
        Assert.Contains("KQA7", keep);
        Assert.Contains("YOPB_RY", Lists(tmp, "open"));
    }

    [Fact]
    public void Uses_AMultiTokenInteriorArgumentSplitsTheChain()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define HX(b) KX_##b##_KY\n#define KX_KA 1\n#define KB_KY 1\n#define KX_KA_KY 1\n#define KX_KB_KY 1\n",
            "int a = HX(KA KB);\n");
        // the crossed names start with K, so the alphabet wants them kept
        Assert.DoesNotContain(Bad(t), e => !e.StartsWith("KX_K", StringComparison.Ordinal));
        Assert.Contains("KX_KA", Keep(tmp));
        Assert.Contains("KB_KY", Keep(tmp));
        Assert.Contains("KX_KA_KY", Lists(tmp, "mustDrop"));
        Assert.Contains("KX_KB_KY", Lists(tmp, "mustDrop"));
    }

    [Fact]
    public void Rules_AFunctionLikeBodyEndingInAPasteMacroIsAnAlias_CommentsAroundTheHashAreSpaces()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define PICK(x) CAT\n#define KAB_1 1\n/* lead */ #define PCAT(a, b) a##b\n# /*c*/ define QCAT(a, b) a##b\n#define KPC_1 1\n#define KQC_1 1\n",
            "int a = PICK(0)(KAB_, 1) + PCAT(KPC_, 1) + QCAT(KQC_, 1);\n");
        Assert.Empty(Bad(t));
        foreach (var n in new[] { "KAB_1", "KPC_1", "KQC_1" }) Assert.Contains(n, Keep(tmp));
    }

    [Fact]
    public void Uses_AParenAcrossADirectiveIsACallInArgumentsAndOpenAtTopLevel()
    {
        // gcc and clang look past a directive for a macro's ( only while reading a call's arguments
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define ID(x) x\n#define PLAIN(a, b) a\n#define KHW_7 1\n#define KHW_8 1\n#define KHW_9 1\n#define YHT_7Y 1\n#define YHT_8Y 1\n#define YHE_7Y 1\n",
            """
            int a = ID(CAT
            #if A
            (KHW_, 7)
            #else
            (KHW_, 8)
            #endif
            );
            int b = ID(
            #ifdef FW_ALT
            CAT
            #else
            PLAIN
            #endif
            (KHW_, 9));
            int c = CAT
            #if A
            (YHT_, 7Y)
            #else
            (YHT_, 8Y)
            #endif
            ;
            int d =
            #ifdef FW_ALT
            CAT
            #else
            PLAIN
            #endif
            (YHE_, 7Y);

            """);
        Assert.Empty(Bad(t));
        foreach (var n in new[] { "KHW_7", "KHW_8", "KHW_9" }) Assert.Contains(n, Keep(tmp));
        Assert.Equal(["YHE_7Y", "YHT_7Y", "YHT_8Y"], Lists(tmp, "open"));
    }

    [Fact]
    public void Uses_AnIfConditionANestedCalleeAndAComputedHeadChainAreExact()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp,
            "#define XCAT(a, b) a##b\n#define KCFG_1 1\n#define ZCFG_2Z 1\n#define APPLY(m, a, b) m(a, b)\n#define KHN_7 1\n#define ZHN_8Z 1\n" +
            "#define GETCAT(x) CAT\n#define KHD_7 1\n#define ZHD_8Z 1\n",
            """
            #if XCAT(KCFG_, 1)
            int a = APPLY(ID(CAT), KHN_, 7);
            #elif defined(XCAT) || defined CAT
            int b;
            #endif
            #define ID(x) x
            int c = APPLY(ID(CAT), KHN_, 7) + CAT(GET, CAT)(0)(KHD_, 7);

            """);
        Assert.Empty(Bad(t));
        foreach (var n in new[] { "KCFG_1", "KHN_7", "KHD_7" }) Assert.Contains(n, Keep(tmp));
    }
    [Fact]
    public void Uses_AProductPastTheCapIsTheUsesPool()
    {
        // 70 x 70 names from one chain is past 4096: the use's first slot starts names, its last slot ends them
        using var tmp = new TempDir();
        string first = string.Join(" ", Enumerable.Range(0, 70).Select(i => $"KP{i}"));
        var t = Mini(tmp,
            $"#define LEFT {first}\n#define RIGHT {first.Replace("KP", "Q")}\n#define P3(a, b) a##_##b\n#define DO(a, b) P3(a, b)\n" +
            "#define KP0_Q0 1\n#define KP3_XQ9 1\n#define ZP3_XZ 1\n",
            "int a = DO(LEFT, RIGHT);\n");
        // only the pool builds them: open since 1.4.10 (CodeCarver), though their K spelling says keep
        Assert.Equal(["KP0_Q0: open, alphabet says keep", "KP3_XQ9: open, alphabet says keep"], Bad(t));
        var open = Lists(tmp, "open");
        Assert.Contains("KP0_Q0", open);
        Assert.Contains("KP3_XQ9", open);
        Assert.Contains("ZP3_XZ", Lists(tmp, "mustDrop"));
    }

    // ---------- review round 9 ----------

    // a firmware corpus without the size of the shared one: one big header, no dense ones
    // the tree's pre-1.4.11 shares, so a small tree still has levels and decoys
    private static readonly string[] TreePcts = ["--fw-work-keep-pct", "4", "--fw-work-decoy-pct", "16", "--fw-work-half-pct", "20"];
    private static readonly string[] SmallWide =
    [
        "--fw-work-patterns", "10", "--fw-work-body-pasters", "100", "--fw-work-body-forwarders", "100", "--fw-work-body-uses", "100",
        "--fw-work-pieces", "40", "--fw-work-uncovered", "3", "--fw-work-asm", "2",
    ];

    private static readonly string[] SmallFw =
    [
        "--firmware", "--fw-big-headers", "1", "--fw-files", "40", "--fw-families", "64", "--fw-pasters", "200", "--fw-dense-headers", "0",
    ];

    private static readonly string[] SmallFwDense = [.. SmallFw[..^2], "--fw-dense-headers", "2"];

    private static (int code, string err) VerifyWith(string corpus, Action<JsonObject> edit, string dir)
    {
        var m = Manifest(corpus);
        edit(m["firmware"]!.AsObject());
        string mp = Path.Combine(dir, "m.json");
        File.WriteAllText(mp, m.ToJsonString());
        return Verify(corpus, mp);
    }

    [Fact]
    public void Verify_ComparesListsElementByElement_RecountsCounts_AndNeedsHostile()
    {
        // one verify per manifest (each recomputes the truth): every edit must show up as its own FAIL
        using var tmp = new TempDir();
        var big = Manifest(_c.Path)["firmware"]!["bigHeaders"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.True(big.Length > 1);
        var (code, err) = VerifyWith(_c.Path, f =>
        {
            f["bigHeaders"] = new JsonArray(JsonValue.Create(string.Join(", ", big)));                 // one string is not the list
            f["denseHeaders"]![0]!["edge"] = f["denseHeaders"]![0]!["edge"]!.ToJsonString();        // edge as a string
            f["hostile"]![0]!["path"] = "fw/misc/nothing.h";
            f["counts"]!["units"] = f["counts"]!["units"]!.GetValue<long>() + 1;
            f["counts"]!["traps"] = f["counts"]!["traps"]!.GetValue<long>() - 1;
            f["counts"]!["families"] = -1;
            f["counts"]!["cases"] = f["counts"]!["cases"]!.GetValue<long>() + 1;
        }, tmp.Path);
        Assert.True(code == 1, $"exit {code}: {err}");
        foreach (var what in new[] { "bigHeaders", "denseHeaders", "hostile", "counts.units", "counts.traps", "counts.families", "counts.cases" })
            Assert.Contains($"FAIL firmware {what}", err);
        (code, err) = VerifyWith(_c.Path, f => f.Remove("hostile"), tmp.Path);
        Assert.True(code == 1, $"exit {code}: {err}");
        Assert.Contains("FAIL firmware hostile: missing", err);
    }

    [Fact]
    public void ReferenceCarve_RefusesADestThatIsNotItsOwn_AndRecarvesItsOwn()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "c", SmallFwDense);
        string foreign = Path.Combine(tmp.Path, "foreign"), file = Path.Combine(tmp.Path, "afile");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");
        File.WriteAllText(file, "mine");
        foreach (var dest in new[]
        {
            c, Path.Combine(c, FwSpec.FwDir), Path.GetDirectoryName(c)!, foreign, file,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })
        {
            var (code, err) = Verify(c, carve: dest);
            Assert.True(code == 2, $"{dest}: exit {code}: {err}");
            Assert.Contains("--reference-carve", err);
        }
        Assert.Equal("mine", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
        Assert.Equal("mine", File.ReadAllText(file));
        Assert.True(Verify(c).code == 0);

        // a carve dir verify made itself is carved again, its old contents gone
        string carve = Path.Combine(tmp.Path, "carve");
        Assert.True(Verify(c, carve: carve).code == 0);
        Assert.True(File.Exists(Path.Combine(carve, FirmwareVerifier.CarveMarker)));
        File.WriteAllText(Path.Combine(carve, "stale.txt"), "x");
        var (code2, err2) = Verify(c, carve: carve);
        Assert.True(code2 == 0, err2);
        Assert.False(File.Exists(Path.Combine(carve, "stale.txt")));
    }

    [Fact]
    public void ListWriter_AFailedListLeavesNoTemp()
    {
        using var tmp = new TempDir();
        using (var w = new ListWriter(tmp.Path, "x")) w.Add("KA"u8);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tmp.Path));
    }

    [Fact]
    public void Lists_PastTheGzipSeam_AreGzipped_UnionIsACopy_AndVerifyPasses()
    {
        using var tmp = new TempDir();
        long was = ListWriter.GzipPast;
        string c;
        try { ListWriter.GzipPast = 1000; c = Gen(tmp.Path, "g", SmallFw); }
        finally { ListWriter.GzipPast = was; }
        var b = Manifest(c)["firmware"]!["builds"]!;
        foreach (var name in new[] { "mustKeepDefines", "mustDropDefines" })
        {
            string main = b["main"]![name]!["path"]!.GetValue<string>(), union = b["union"]![name]!["path"]!.GetValue<string>();
            Assert.EndsWith(".gz", main);
            Assert.EndsWith(".gz", union);
            Assert.Equal(File.ReadAllBytes(Path.Combine(c, main)), File.ReadAllBytes(Path.Combine(c, union)));
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(c, main)), System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            long lines = 0;
            while (r.ReadLine() is not null) lines++;
            Assert.Equal(b["main"]![name]!["count"]!.GetValue<long>(), lines);
        }
        Assert.Empty(Directory.EnumerateFiles(c, "*.tmp", SearchOption.AllDirectories));
        var (code, err) = Verify(c);
        Assert.True(code == 0, err);
    }

    [Fact]
    public void IoParallelism_SameBytes_AndVerifyFailsTamperedListsCasesAndDb()
    {
        using var tmp = new TempDir();
        string a = Gen(tmp.Path, "a", [.. SmallFw, "--io-parallelism", "1"]);
        string b = Gen(tmp.Path, "b", [.. SmallFw, "--io-parallelism", "8"]);
        Assert.Equal(Snap(a), Snap(b));
        // the union list is its own file with the main list's bytes
        string truth = Path.Combine(a, Path.GetDirectoryName(FirmwareEmitter.CasesPath)!);
        Assert.Equal(File.ReadAllBytes(Path.Combine(truth, "main.mustKeepDefines.txt")), File.ReadAllBytes(Path.Combine(truth, "union.mustKeepDefines.txt")));

        void Fails(string corpus, string what, string? manifest = null)
        {
            var (code, err) = Verify(corpus, manifest);
            Assert.True(code == 1, $"{what}: exit {code}: {err}");
            Assert.Contains(what, err);
        }
        // a list edited on disk
        string open = Path.Combine(truth, "main.openDefines.txt");
        byte[] keepOpen = File.ReadAllBytes(open);
        File.AppendAllText(open, "YNEWY\n");
        Fails(a, "openDefines");
        File.WriteAllBytes(open, keepOpen);
        // a cases row whose site is no file
        string cases = Path.Combine(a, FirmwareEmitter.CasesPath);
        byte[] keepCases = File.ReadAllBytes(cases);
        File.AppendAllText(cases, "KNOPE\tdefine\tsrc/nothing.c\n");
        Fails(a, "cases");
        File.WriteAllBytes(cases, keepCases);
        Assert.True(Verify(a).code == 0, "restored");
        // a malformed compile db: a FAIL, not a crash
        File.WriteAllText(Path.Combine(a, FwSpec.DbPath), "[{\"directory\": 3");
        Fails(a, "compileDb");
        // a moved corpus whose db names absolute paths
        string moved = Path.Combine(tmp.Path, "moved");
        Directory.Move(b, moved);
        Fails(moved, "compileDb", b + "-manifest.json");
    }

    [Fact]
    public void Work_MissingIsAFail_AndAppliersOnlyWithHigherOrderUses()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "w", [.. SmallFw, "--depth", "12",
            "--fw-work", "--fw-work-defines", "3000", "--fw-work-pasters", "60", "--fw-work-forwarders", "180", .. TreePcts, .. SmallWide]);
        var (code, err) = Verify(c);
        Assert.True(code == 0, err);
        var w = Manifest(c)["firmware"]!["work"]!["tree"]!;
        var keep = List(c, "main", "mustKeepDefines");
        Assert.Equal(w["appliers"]!.GetValue<long>(), keep.Count(n => n.StartsWith("WAP", StringComparison.Ordinal)));
        Assert.Equal(w["uses"]!["higherOrder"]!.GetValue<long>() > 0, w["appliers"]!.GetValue<long>() > 0);

        foreach (var edit in new Action<JsonObject>[]
        {
            f => f.Remove("work"),
            f => f["work"]!["tree"]!["byLevel"]![0]!["uses"] = f["work"]!["tree"]!["byLevel"]![0]!["uses"]!.GetValue<long>() + 1,
            f => f["work"]!["tree"]!["expectedPasses"] = f["work"]!["tree"]!["expectedPasses"]!.GetValue<long>() + 1,
            f => f["work"]!["openByPattern"] = f["work"]!["openByPattern"]!.GetValue<long>() + 1,
            f => f["work"]!["nearMissDropped"] = f["work"]!["nearMissDropped"]!.GetValue<long>() - 1,
            f => f["work"]!["asmFiles"] = f["work"]!["asmFiles"]!.GetValue<long>() + 1,
        })
        {
            var (code2, err2) = VerifyWith(c, edit, tmp.Path);
            Assert.True(code2 == 1, $"exit {code2}: {err2}");
            Assert.Contains("FAIL firmware work", err2);
        }
    }

    [Fact]
    public void Preset_FirmwareWork_CanBeSwitchedOff()
    {
        var args = Presets.Expand(["--preset", "firmware-work", "--out", "x", "--firmware", "false"], out int pt);
        Assert.False(ArgParser.ParseGen(args, pt).Firmware);
        // a firmware knob the user typed with it off is still refused
        args = Presets.Expand(["--preset", "firmware-work", "--out", "x", "--firmware", "false", "--fw-files", "9"], out pt);
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(args, pt));
    }

    // ---------- round 10 rulings ----------

    // kcat called once, so the minis' uncalled-wildcard use never builds the fillers
    private const string Kcat = "#define kcat(a, b) a##b\n#define KQX_1 1\n";
    private const string KcatUse = " + kcat(KQX_, 1)";

    [Fact]
    public void Alias_WalksPastATailThatMayBeEmpty_ANonEmptyNameStopsIt()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, Kcat + "#define KID(x) x\n#define KETE\n#define KETM() CAT KETE\n#define KETX_1 1\n" +
            "#define KEWN(a, b) , 3\n#define KEUM() kcat KETE KEWN\n#define ZEUX_2Z 1\n",
            "int a = KID(KETM()(KETX_, 1)) + ((void)KEUM()(ZEUX_, 2Z))" + KcatUse + ";\n");
        Assert.Empty(Bad(t));
        Assert.Contains("KETX_1", Keep(tmp));
        Assert.Contains("ZEUX_2Z", Lists(tmp, "mustDrop"));
    }

    [Fact]
    public void Alias_AParameterTailMayBeEmpty_AnArgumentOpeningWithParenCalls_AComputedCallPools()
    {
        // PICK2() leaves CAT before the (; PICK2((a, b)) gives CAT (a, b); PICK2(KPXN)(..) calls KPXN, but the
        // computed call's own pool still builds YPVX_3Y, open (only a pool builds it), while ZPVY_ is no piece of it
        using var tmp = new TempDir();
        var t = Mini(tmp, Kcat + "#define PICK2(x) CAT x\n#define KPKX_1 1\n#define KPKX_2 1\n" +
            "#define KPXN(a, b) , 4\n#define KPWM(x) kcat x\n#define YPVX_3Y 1\n#define ZPVY_3Z 1\n",
            "int a = PICK2()(KPKX_, 1) + PICK2((KPKX_, 2)) + ((void)KPWM(KPXN)(YPVX_, 3Y))" + KcatUse + ";\n");
        Assert.Empty(Bad(t));
        Assert.Contains("KPKX_1", Keep(tmp));
        Assert.Contains("KPKX_2", Keep(tmp));
        Assert.DoesNotContain("YPVX_3Y", Keep(tmp));
        Assert.DoesNotContain("YPVX_3Y", Lists(tmp, "mustDrop"));
        Assert.Contains("ZPVY_3Z", Lists(tmp, "mustDrop"));
    }

    [Fact]
    public void Resplit_ASelfReferentialCommaMacroEnds()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define KGP_(p, n) p##_##n\n#define KPORT(x) KGP_(x)\n#define KSRCX KSRCX, 1\n#define KSRCX_1 5\n",
            "int a = KPORT(KSRCX);\n");
        Assert.Empty(Bad(t));
        Assert.Contains("KSRCX_1", Keep(tmp));
    }

    [Fact]
    public void Branches_ElifdefIsABranch()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define KAX_1 1\n#define KBX_2 1\n#define KCX_3 1\n",
            "int a = CAT(\n#ifdef FW_ALT\nKAX_, 1\n#elifdef FW_B\nKBX_, 2\n#elifndef FW_C\nKCX_, 3\n#endif\n);\n");
        Assert.Empty(Bad(t));
        foreach (var n in new[] { "KAX_1", "KBX_2", "KCX_3" }) Assert.Contains(n, Keep(tmp));
    }

    [Fact]
    public void Branches_PastThePathCapAreOpen()
    {
        // 32 paths through one call: the first 16 are followed (the call crosses a directive, so it pools their
        // pieces); the 16 dropped paths are the last #else's, and what only they could build (an E) is open
        using var tmp = new TempDir();
        var defs = new StringBuilder("#define J5(a, b, c, d, e, f) a##b##c##d##e##f\n");
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            var n = "KCPX_" + string.Concat(Enumerable.Range(0, 4).Select(b => (i >> (4 - b) & 1) == 0 ? 'A' : 'B')) + ((i & 1) == 0 ? 'D' : 'E');
            names.Add(n);
            defs.Append($"#define {n} 1\n");
        }
        var unit = new StringBuilder("int a = J5(KCPX_\n");
        for (int i = 0; i < 4; i++) unit.Append($"#ifdef FW_P{i}\n, B\n#else\n, A\n#endif\n");
        unit.Append("#ifdef FW_P4\n, D\n#else\n, E\n#endif\n);\n");
        var t = Mini(tmp, defs.ToString(), unit.ToString());
        Assert.Equal(16, Tracker.MaxPaths);
        foreach (var n in names) Assert.Contains(n, n.EndsWith('D') ? Keep(tmp) : Lists(tmp, "open"));
        // the alphabet says keep for them all: only the open E names disagree
        Assert.Equal(16, Bad(t).Count(e => e.Contains("E: open, alphabet says keep")));
        Assert.Equal(16, Bad(t).Length);
    }

    [Fact]
    public void Defines_ACommentRunOnFromTheBodyIsSeeded_ACommentBeforeTheHashIsStillADefine()
    {
        using var tmp = new TempDir();
        var t = Mini(tmp, "#define KCOX 1 /* runs\n   on */ + 2\n/* a comment over\n   two lines */ # define ZHFZ 3\n# /* c */ define ZHEZ 2\n",
            "int a = 0;\n");
        Assert.Empty(Bad(t));
        Assert.Contains("KCOX", Keep(tmp));
        Assert.Contains("ZHFZ", Lists(tmp, "mustDrop"));
        Assert.Contains("ZHEZ", Lists(tmp, "mustDrop"));
    }

    [Fact]
    public void Gen_RefusesACaseRowNoCarvedFileDefines()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "lists"));
        File.WriteAllText(Path.Combine(tmp.Path, "lists", "cases.tsv"), "name\tcase\tsite\nKNOPEX\tdirect\tfw/u.c\nKAX\tdirect\tfw/u.c\n");
        var t = Mini(tmp, "#define KAX 1\n", "int a = KAX;\n");
        Assert.Equal(["KNOPEX: a case row, but no carved file defines it"], Bad(t));
    }

    [Fact]
    public void Scale_PastIntMax_IsRefused()
    {
        var o = ArgParser.ParseGen(["--out", "x", "--firmware"]);
        o.Scale = 1e6;
        Assert.Throws<ArgException>(() => o.Eff("fw-work-defines", 2_000_000_000));
    }

    // ---------- phase 2: function truth (1.5.0) ----------

    [Fact]
    public void Functions_EveryCaseIsClassedAsSpelled_World2IsKeptInTheUnionOnly()
    {
        var rows = File.ReadAllLines(Path.Combine(_c.Path, FirmwareEmitter.FunctionCasesPath));
        Assert.Equal("name\tcase\tsite", rows[0]);
        var keep = List(_c.Path, "union", "mustKeepFunctions");
        var drop = List(_c.Path, "union", "mustDropFunctions");
        var open = List(_c.Path, "union", "openFunctions");
        Assert.Empty(keep.Intersect(drop).Concat(keep.Intersect(open)).Concat(drop.Intersect(open)));
        foreach (var row in rows.Skip(1))
        {
            var cols = row.Split('\t');
            Assert.Equal(3, cols.Length);
            Assert.True(File.Exists(Path.Combine(_c.Path, FwSpec.FwDir, cols[2])), $"{cols[0]}: site {cols[2]}");
            string got = keep.Contains(cols[0]) ? "keep" : drop.Contains(cols[0]) ? "drop" : open.Contains(cols[0]) ? "open" : "none";
            Assert.True(Spelled(cols[0]) == got, $"{cols[0]} ({cols[1]}): spelled {Spelled(cols[0])}, classed {got}");
        }
        Assert.True(keep.Count > 0 && drop.Count > 0 && open.Count > 0);
        // every unit's fu is reached from Reset_Handler through main and m<k>_init; the dead fu-likes are not
        Assert.Contains("main", keep);
        Assert.Contains("m0_init", keep);
        Assert.Contains("fu0", keep);
        Assert.Contains("Zfu0deadZ", drop);
        // world 2 is only in the union's db: its function drops in main
        var mainKeep = List(_c.Path, "main", "mustKeepFunctions");
        Assert.Contains("kworld2", keep);
        Assert.Contains("kworld2", List(_c.Path, "main", "mustDropFunctions"));
        Assert.DoesNotContain("kworld2", mainKeep);
        Assert.Contains("kworld1", mainKeep);
        Assert.Equal(open, List(_c.Path, "main", "openFunctions"));
    }

    [Fact]
    public void Functions_ManifestNamesTheRoots_AndTheWorld2Db()
    {
        var fw = Manifest(_c.Path)["firmware"]!;
        Assert.Equal(["Reset_Handler"], fw["roots"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(FirmwareEmitter.DbWorld2Path, fw["compileDbUnion"]!.GetValue<string>());
        var w2 = JsonNode.Parse(File.ReadAllText(Path.Combine(_c.Path, FirmwareEmitter.DbWorld2Path)))!.AsArray();
        Assert.Single(w2);
        string args2 = w2[0]!.ToJsonString();
        Assert.Contains("world.c", args2);
        Assert.Contains("-DFW_WORLD=2", args2);
        string main = File.ReadAllText(Path.Combine(_c.Path, FwSpec.DbPath));
        Assert.Contains("-DFW_WORLD=1", main);
        Assert.DoesNotContain("-DFW_WORLD=2", main);
        Assert.Contains("--target=arm-none-eabi", main);
        Assert.Contains("startup.S", main);
        Assert.DoesNotContain("spare.s", main);   // asm no build names: its callee is open
        var files = List(_c.Path, "main", "mustKeepFiles");
        foreach (var f in new[] { "asm/spare.s", "ld/mem.ld", "ld/fw.icf", "ld/fw.sct", "ld/fw_ti.cmd", "lib/fwcrc.o", "lib/libfwx.a" })
            Assert.True(files.Any(x => x.EndsWith(f, StringComparison.Ordinal)), $"{f} not in mustKeepFiles");
        // nothing includes the density fixtures, so an unforced carve drops them
        foreach (var f in new[] { "dense/dense_60.h", "dense/dense_70.h" })
            Assert.DoesNotContain(f, files);
    }

    [Fact]
    public void Functions_HandWrittenObjectAndArchive_AreArmElf_WithTheUndefinedRef()
    {
        byte[] o = FirmwareEmitter.ArmObject("fw_objfn", "kfwobj");
        Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1 }, o[..6]);
        Assert.Equal(1, BitConverter.ToUInt16(o, 16));    // ET_REL
        Assert.Equal(40, BitConverter.ToUInt16(o, 18));   // EM_ARM
        var u = new HashSet<string>();
        FwSpec.ObjUndefined(o, u);
        Assert.Equal(["kfwobj"], u);
        byte[] a = FirmwareEmitter.Archive("fwx.o", FirmwareEmitter.ArmObject("fw_arfn", "kfwar"), "fw_arfn");
        Assert.Equal("!<arch>\n", Encoding.ASCII.GetString(a, 0, 8));
        u.Clear();
        FwSpec.ObjUndefined(a, u);
        Assert.Equal(["kfwar"], u);
        // what the corpus holds is what these write
        Assert.Equal(o, File.ReadAllBytes(Path.Combine(_c.Path, FwSpec.FwDir, "lib", "fwcrc.o")));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(_c.Path, FwSpec.FwDir, "lib", "libfwx.a")));
        // garbage and truncation read as no symbols, not a crash
        foreach (var bad in new[] { o[..60], a[..70], new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' } })
        {
            u.Clear();
            FwSpec.ObjUndefined(bad, u);
            Assert.Empty(u);
        }
    }

    [Fact]
    public void Functions_VerifyFailsATamperedTreeOrManifest()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "f", [.. SmallFw, "--fw-db-paths", "relative"]);
        var ok = Verify(c);
        Assert.True(ok.code == 0, ok.err);
        void Fails(string what)
        {
            var (code, err) = Verify(c);
            Assert.True(code == 1, $"{what}: exit {code}: {err}");
            Assert.Contains(what, err);
        }
        // main calls a Z-spelled dead function: it is reached, and its spelling says drop
        string main = Path.Combine(c, FwSpec.FwDir, "src", "main.c");
        string was = File.ReadAllText(main);
        File.WriteAllText(main, was.Replace("r += kworld();", "r += kworld(); r += ZFwNoKeepZ();"));
        Fails("ZFwNoKeepZ (function): keep, alphabet says drop");
        File.WriteAllText(main, was);
        // the asm stops calling the C function only it reaches: the alphabet still spells it keep
        string util = Path.Combine(c, FwSpec.FwDir, "asm", "fw_util.s");
        string u0 = File.ReadAllText(util);
        File.WriteAllText(util, u0.Replace("bl kfwasmcall", "nop"));
        Fails("kfwasmcall (function)");
        File.WriteAllText(util, u0);
        Assert.True(Verify(c).code == 0, "restored");
        // the manifest: no roots, a different union db, roots not names, a function list's count, a missing list
        foreach (var (edit, what) in new (Action<JsonObject>, string)[]
        {
            (f => f.Remove("roots"), "mustKeepFunctions"),
            (f => f["compileDbUnion"] = "fw-build/other.json", "compileDbUnion"),
            (f => f["roots"] = new JsonArray(1), "roots"),
            (f => f["builds"]!["union"]!["openFunctions"]!["count"] = 0, "builds.union.openFunctions.count"),
            (f => f["builds"]!["main"]!.AsObject().Remove("mustDropFunctions"), "builds.main.mustDropFunctions"),
        })
        {
            var (code, err) = VerifyWith(c, edit, tmp.Path);
            Assert.True(code == 1, $"{what}: exit {code}: {err}");
            Assert.Contains(what, err);
        }
    }

    [Fact]
    public void Functions_WorkPresetHasNone()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp.Path, "w", [.. SmallFw, "--depth", "12",
            "--fw-work", "--fw-work-defines", "3000", "--fw-work-pasters", "60", "--fw-work-forwarders", "180", .. TreePcts, .. SmallWide]);
        var fw = Manifest(c)["firmware"]!.AsObject();
        Assert.False(fw.ContainsKey("roots") || fw.ContainsKey("compileDbUnion") || fw.ContainsKey("functionCases"));
        Assert.False(fw["builds"]!["main"]!.AsObject().ContainsKey("mustDropFunctions"));
        Assert.False(File.Exists(Path.Combine(c, FirmwareEmitter.DbWorld2Path)));
        Assert.False(File.Exists(Path.Combine(c, FirmwareEmitter.FunctionCasesPath)));
        Assert.False(File.Exists(Path.Combine(c, FwSpec.FwDir, "asm", "startup.S")));
        Assert.False(File.Exists(Path.Combine(c, FwSpec.FwDir, "src", "main.c")));
    }
}
