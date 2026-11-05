using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CodeSpawner.Cli;
using CodeSpawner.Firmware;
using CodeSpawner.Verify;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The multi-target firmware corpus (docs/firmware-design.md "Multi-target"): its knobs, the tree each target
/// builds, that every case is classed per target as its suffix spells and the union keeps what any target keeps,
/// that verify recomputes every build and FAILs tampering, and that two targets leave the third out. Linking each
/// target with ld.lld is checked by hand.
/// </summary>
[Collection("CapturesConsole")]   // swaps the process-wide Console: never alongside another class that does
public class FirmwareMultiTests : IClassFixture<FirmwareMultiTests.Corpus>
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

    private static readonly string[] Small =
    [
        "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
        "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2",
        "--firmware", "--fw-big-headers", "3", "--fw-files", "40", "--fw-families", "400", "--fw-pasters", "2000",
        "--fw-dense-headers", "2", "--fw-big-mb", "20", "--fw-db-paths", "relative",
    ];

    private static string Gen(string dir, string name, int targets)
    {
        string corpus = Path.Combine(dir, name);
        int rc = Cli([.. new[] { "gen", "--out", corpus }, .. Small, "--fw-targets", targets.ToString()]);
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        return corpus;
    }

    /// <summary>One three-target corpus shared by the read-only tests.</summary>
    public sealed class Corpus : IDisposable
    {
        private readonly TempDir _tmp = new();
        public readonly string Path;
        public Corpus() => Path = Gen(_tmp.Path, "fw", 3);
        public void Dispose() => _tmp.Dispose();
    }

    private readonly Corpus _c;
    public FirmwareMultiTests(Corpus c) => _c = c;

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

    // a name's class in one build: 0 keep, 1 drop, 2 open, -1 not listed
    private static Func<string, int> Classes(string corpus, string build, string kind)
    {
        var l = new[] { "mustKeep", "mustDrop", "open" }.Select(k => List(corpus, build, k + kind)).ToArray();
        return n => Array.FindIndex(l, s => s.Contains(n));
    }

    private static string F(string corpus, string rel) => Path.Combine(corpus, FwSpec.FwDir, rel);

    // ---------- knobs ----------

    [Theory]
    [InlineData("--fw-targets", "3")]                                     // firmware off
    [InlineData("--firmware", "--fw-targets", "1")]
    [InlineData("--firmware", "--fw-targets", "4")]
    [InlineData("--firmware", "--fw-bulk", "--fw-targets", "3")]
    public void BadKnobs_AreRefused(params string[] args) =>
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(["--out", "x", .. args]));

    [Fact]
    public void Preset_IsPlainFirmwareForThreeTargets()
    {
        var o = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "firmware-multi"])]);
        var p = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "firmware"])]);
        Assert.True(o.Firmware && !o.FwWork);
        Assert.Equal((3, 0), (o.FwTargets, p.FwTargets));
        Assert.Equal((p.FwBigHeaders, p.FwFiles, p.FwFamilies, p.FwPasters, p.FwDenseHeaders, p.FwBigMb),
            (o.FwBigHeaders, o.FwFiles, o.FwFamilies, o.FwPasters, o.FwDenseHeaders, o.FwBigMb));
    }

    // ---------- the shared corpus ----------

    [Fact]
    public void Verify_Passes()
    {
        var (code, err) = Verify(_c.Path);
        Assert.True(code == 0, err);
    }

    [Fact]
    public void Tree_EachTargetHasItsStartupScriptDbAndLog_NoTrapsNoWorld2()
    {
        foreach (var t in FwMulti.Names)
        {
            Assert.True(File.Exists(F(_c.Path, $"targets/{t}/startup.S")), t);
            Assert.True(File.Exists(F(_c.Path, $"targets/{t}/board.c")), t);
            Assert.True(File.Exists(F(_c.Path, $"include/board_{t}.h")), t);
            Assert.True(File.Exists(F(_c.Path, $"ld/{t}.ld")), t);
            Assert.True(File.Exists(Path.Combine(_c.Path, FwMulti.DbPath(t))), t);
            string log = File.ReadAllText(Path.Combine(_c.Path, FwMulti.LogPath(t)));
            Assert.Contains($"ld.lld --gc-sections -T fw/ld/{t}.ld", log);
            Assert.Contains($"ar rcs fw-build/{t}/libboard_{t}.a", log);
            Assert.Equal(t == "tb", log.Contains("fw/lib/tb_obj.o"));
            foreach (var u in FwMulti.Names.Where(u => u != t)) Assert.DoesNotContain($"targets/{u}/", log);
        }
        Assert.Contains(".fwtc", File.ReadAllText(F(_c.Path, "ld/tc.ld")));
        Assert.DoesNotContain(".fwtc", File.ReadAllText(F(_c.Path, "ld/ta.ld")));
        foreach (var gone in new[] { "src/traps", "src/world.c", "asm/startup.S", "ld/fw.ld", "include/fw_q3.h" })
            Assert.False(File.Exists(F(_c.Path, gone)) || Directory.Exists(F(_c.Path, gone)), gone);
        Assert.False(File.Exists(Path.Combine(_c.Path, FirmwareEmitter.DbWorld2Path)));

        var fw = Manifest(_c.Path)["firmware"]!;
        Assert.Equal(FwMulti.Names, fw["targets"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(FwMulti.Names.Select(FwMulti.Root), fw["roots"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(fw["compileDbUnion"]);
        Assert.Null(fw["builds"]!["main"]);
        Assert.Equal(0, fw["counts"]!["traps"]?.GetValue<int>() ?? 0);
        // nothing in the tree defines FW_TARGET: only each target's -D does
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_c.Path, FwSpec.FwDir), "*", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains("#define FW_TARGET"));
    }

    [Fact]
    public void EveryCase_IsClassedPerTargetAsItsSuffixSpells_TheUnionKeepsWhatAnyTargetKeeps()
    {
        foreach (var (kind, cases) in new[] { ("Defines", FirmwareEmitter.CasesPath), ("Functions", FirmwareEmitter.FunctionCasesPath) })
        {
            var per = FwMulti.Names.Select(t => Classes(_c.Path, t, kind)).ToArray();
            var union = Classes(_c.Path, "union", kind);
            foreach (var row in File.ReadAllLines(Path.Combine(_c.Path, cases)).Skip(1))
            {
                string name = row.Split('\t')[0];
                var got = per.Select(c => c(name)).ToArray();
                // a file no target builds is open everywhere: its names are spelled open
                if (got.All(g => g < 0)) Assert.True(FwSpec.Alphabet(System.Text.Encoding.Latin1.GetBytes(name)) == 2, $"{name}: no target grades it");
                for (int i = 0; i < 3; i++)
                    if (got[i] >= 0) Assert.True(got[i] == FwMulti.Expected(name, i, 3), $"{name} in {FwMulti.Names[i]}: classed {got[i]}");
                int want = got.Contains(0) ? 0 : got.Any(g => g >= 0) && got.Where(g => g >= 0).All(g => g == 1) ? 1 : 2;
                Assert.True(union(name) == want, $"{name} in union: classed {union(name)}, want {want}");
            }
        }
        // the target chain and the feature define are graded by every target
        foreach (var n in new[] { "KMT_1__KDD", "KMT_2__DKD", "KMT_3__DDK", "KMFB_1__DKD" })
            Assert.All(FwMulti.Names, t => Assert.True(Classes(_c.Path, t, "Defines")(n) >= 0, $"{n} in {t}"));
        // tb's prebuilt object keeps a function only in tb; a static's two copies are told apart by file
        var fk = FwMulti.Names.Select(t => List(_c.Path, t, "mustKeepFunctions")).ToArray();
        Assert.Equal([false, true, false], fk.Select(k => k.Contains("kfwtbneed__DKD")));
        Assert.Equal([false, false, true], fk.Select(k => k.Contains("kfwtckeep__DDK")));
        Assert.All(fk, k => Assert.Contains("src/handlers.c:kfwdup", k));
        Assert.All(fk, k => Assert.DoesNotContain("kfwdup", k));
        Assert.Contains("targets/ta/board.c:kboard", fk[0]);
        Assert.DoesNotContain("targets/tb/board.c:kboard", fk[0]);
    }

    [Fact]
    public void Verify_FailsATamperedManifest_AndRefusesAReferenceCarve()
    {
        using var tmp = new TempDir();
        foreach (var (path, value) in new (string[] Path, JsonNode Value)[]
        {
            (["builds", "tb", "counts", "mustKeep"], 1),
            (["builds", "tc", "counts", "bytesAfterMin"], 1),
            (["builds", "union", "openFunctions", "sha256"], "00"),
            (["builds", "ta", "buildLog"], "fw-build/tb/build.log"),
            (["roots"], new JsonArray(FwMulti.Root("ta"))),
            (["targets"], new JsonArray("ta", "tc")),
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
        var (rc, e) = Verify(_c.Path, carve: Path.Combine(tmp.Path, "carve"));
        Assert.NotEqual(0, rc);
        Assert.Contains("reference carve", e);
    }

    // ---------- two targets, determinism ----------

    private static SortedDictionary<string, string> Snap(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, f).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return map;
    }

    [Fact]
    public void TwoTargets_LeaveTheThirdOut_Verify_AndTheSameSeedGivesTheSameBytes()
    {
        using var tmp = new TempDir();
        string a = Gen(tmp.Path, "a", 2), b = Gen(tmp.Path, "b", 2);
        Assert.Equal(Snap(a), Snap(b));
        var (code, err) = Verify(a);
        Assert.True(code == 0, err);
        Assert.False(Directory.Exists(F(a, "targets/tc")));
        Assert.False(File.Exists(F(a, "ld/tc.ld")));
        Assert.DoesNotContain("kfwtckeep", File.ReadAllText(F(a, "src/fw_attr.c")));
        Assert.Contains("kfwtbneed__DK", File.ReadAllText(F(a, "src/handlers.c")));
        Assert.Equal(["ta", "tb"], Manifest(a)["firmware"]!["targets"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(Manifest(a)["firmware"]!["builds"]!["tc"]);
    }
}
