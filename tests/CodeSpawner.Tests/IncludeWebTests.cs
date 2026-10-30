using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Verify;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The include web (docs/include-web-design.md): its knobs and refusals, that it is off by default (no dirs, no
/// manifest keys, no recorded knobs), determinism and _meta.gen replay, that verify recomputes the closures and
/// instances from the text and FAILs each kind of tampering, that verify's preprocessor gets every trap right on a
/// hand-written web, and that mutate leaves the web alone. clang agreeing with the closures is checked in bench.
/// </summary>
[Collection("CapturesConsole")]   // swaps the process-wide Console: never alongside another class that does
public class IncludeWebTests
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

    // Every shape turned well up, so a small web holds each trap many times over.
    private static readonly string[] Web =
    [
        "--web-units", "30", "--web-headers", "120", "--web-depth", "8", "--web-window", "3-6", "--web-aggregators", "4",
        "--web-header-lines", "40", "--web-tail", "1", "--web-tail-mb", "1", "--web-templates", "8",
        "--web-template-sites", "3", "--web-template-heads", "6", "--web-template-reach-frac", "0.6",
        "--web-template-hidden-frac", "0.5", "--web-cycle-frac", "0.5", "--web-ifdef-frac", "0.5",
        "--web-continuation-frac", "0.5", "--web-comment-frac", "0.5", "--web-undef-frac", "0.5",
        "--web-computed-frac", "0.3", "--web-next-frac", "0.5", "--web-shadow", "4", "--web-relative-frac", "0.3",
        "--web-crlf-frac", "0.3", "--web-mixed-eol-frac", "0.2",
    ];

    private static string Gen(TempDir tmp, string name, params string[] extra)
    {
        string corpus = Path.Combine(tmp.Path, name);
        int rc = Cli([.. new[] { "gen", "--out", corpus }, .. Tiny, .. extra]);
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        return corpus;
    }

    private static (int code, string err) Verify(string corpus, string? manifest = null)
    {
        var err = new StringWriter();
        int code = ManifestVerifier.Run(new VerifyOptions { Corpus = corpus, Manifest = manifest ?? corpus + "-manifest.json" }, err);
        return (code, err.ToString());
    }

    private static JsonObject Manifest(string corpus) => JsonNode.Parse(File.ReadAllText(corpus + "-manifest.json"))!.AsObject();
    private static void Save(string corpus, JsonObject m) => File.WriteAllText(corpus + "-manifest.json", m.ToJsonString());

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, f).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return map;
    }

    private static IEnumerable<string> WebFiles(string corpus) =>
        WebEmitter.Dirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(corpus, d), "*", SearchOption.AllDirectories));

    // ---------- knobs ----------

    [Fact]
    public void Knobs_Parse_AndWindowTakesNOrRange()
    {
        var o = ArgParser.ParseGen(["--out", "x", .. Web]);
        Assert.Equal((30, 120, 8, "3-6", 4, 40, 1, 1), (o.WebUnits, o.WebHeaders, o.WebDepth, o.WebWindow, o.WebAggregators, o.WebHeaderLines, o.WebTail, o.WebTailMb));
        Assert.Equal((8, 3, 6, 0.6, 0.5), (o.WebTemplates, o.WebTemplateSites, o.WebTemplateHeads, o.WebTemplateReachFrac, o.WebTemplateHiddenFrac));
        Assert.Equal((0.5, 0.5, 0.5, 0.5, 0.5), (o.WebCycleFrac, o.WebIfdefFrac, o.WebContinuationFrac, o.WebCommentFrac, o.WebUndefFrac));
        Assert.Equal((0.3, 0.5, 4, 0.3, 0.3, 0.2), (o.WebComputedFrac, o.WebNextFrac, o.WebShadow, o.WebRelativeFrac, o.WebCrlfFrac, o.WebMixedEolFrac));
        Assert.Equal((7, 7), ArgParser.ParseWebWindow("7"));
        Assert.Equal((2, 9), ArgParser.ParseWebWindow("2-9"));
    }

    [Theory]
    [InlineData("--web-headers", "10")]                                   // a web knob with the web off
    [InlineData("--web-units", "5", "--web-window", "0-3")]
    [InlineData("--web-units", "5", "--web-window", "6-3")]
    [InlineData("--web-units", "5", "--web-window", "x")]
    [InlineData("--web-units", "5", "--web-cycle-frac", "1.5")]
    [InlineData("--web-units", "5", "--web-crlf-frac", "0.6", "--web-mixed-eol-frac", "0.6")]
    [InlineData("--web-units", "5", "--web-headers", "4", "--web-tail", "5")]
    [InlineData("--web-units", "5", "--web-headers", "4", "--web-shadow", "5")]
    [InlineData("--web-units", "-1")]
    [InlineData("--web-units", "5", "--lang", "cpp")]
    [InlineData("--web-units", "5", "--web-depth", "151")]                 // closures would pass the compilers' 200
    [InlineData("--web-units", "5", "--manifest", "false")]               // the closures are the web's truth
    [InlineData("--web-units", "5", "--web-templates", "0", "--web-template-sites", "4")]   // a knob with its parent off
    [InlineData("--web-units", "5", "--web-templates", "0", "--web-template-hidden-frac", "0.1")]
    [InlineData("--web-units", "5", "--web-tail-mb", "8")]               // --web-tail is 0 by default
    public void BadKnobs_AreRefused(params string[] args) =>
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(["--out", "x", .. args]));

    [Fact]
    public void Presets_HugeCarriesTheWeb_HugeLiteHasItsCountsWithSmallHeaders()
    {
        var huge = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "huge"])]);
        var lite = ArgParser.ParseGen(["--out", "x", .. Presets.Expand(["--preset", "huge-lite"])]);
        Assert.Equal(3000, huge.WebUnits);
        Assert.Equal((huge.WebUnits, huge.WebHeaders, huge.WebDepth, huge.WebWindow, huge.WebTemplates, huge.WebTail),
                     (lite.WebUnits, lite.WebHeaders, lite.WebDepth, lite.WebWindow, lite.WebTemplates, lite.WebTail));
        Assert.True(lite.WebHeaderLines < huge.WebHeaderLines && lite.WebTailMb < huge.WebTailMb);
        Assert.Equal(32, lite.MaxHeaderMB);
    }

    // ---------- off by default ----------

    [Fact]
    public void Off_NoDirs_NoManifestKeys_NoRecordedKnobs()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c");
        Assert.All(WebEmitter.Dirs, d => Assert.False(Directory.Exists(Path.Combine(c, d))));
        var m = Manifest(c);
        Assert.False(m.ContainsKey("closures"));
        Assert.False(m["_meta"]!.AsObject().ContainsKey("includeDirs"));
        Assert.DoesNotContain(m["_meta"]!["gen"]!.AsObject(), kv => kv.Key.StartsWith("web", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(c, "compile_commands.json")));   // --compile-db none, no web: none written
    }

    // ---------- gen + verify ----------

    [Fact]
    public void Web_VerifiesAndHoldsEveryTrap()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c", Web);
        var (code, err) = Verify(c);
        Assert.True(code == 0, err);

        var m = Manifest(c);
        Assert.Equal(["inc", "inc_vnd"], m["_meta"]!["includeDirs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(30, m["closures"]!.AsObject().Count);
        Assert.Contains(m["symbols"]!.AsObject(), kv => kv.Value!["templateHead"] is not null);

        string all = string.Concat(WebFiles(c).Select(File.ReadAllText));
        Assert.Contains("#include_next <vx_", all);
        Assert.Contains("#include WEB_HDR_", all);
        Assert.Contains("\"../inc/", all);
        Assert.Contains("\r\n", all);
        Assert.Contains("#error", all);                                      // inc_vnd shadows, never reached
        Assert.Matches(@"/\*[^*]*\n#include ""rm_\d+\.h""\n", all);           // a fake include in a block comment
        Assert.Contains("\\\n", all);                                         // continuation lines
        Assert.Contains("#undef ", all);
        Assert.Matches(@"#if(def)? CFG_\d+", all);
        Assert.Contains(WebFiles(c), f => Path.GetFileName(f).StartsWith("all_", StringComparison.Ordinal));   // aggregators
        Assert.Contains(WebFiles(c), f =>                                                                      // a cycle: a back include
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^rm_(\d+)\.h$");
            return m.Success && System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"#include ""rm_(\d+)\.h""")
                .Any(x => int.Parse(x.Groups[1].Value) < int.Parse(m.Groups[1].Value));
        });
        Assert.Contains(WebFiles(c), f => { string t = File.ReadAllText(f); return t.Contains("\r\n") && t.Replace("\r\n", "").Contains('\n'); });   // mixed EOL

        // compile_commands lists every unit with the web's -I order, though --compile-db is none.
        string db = File.ReadAllText(Path.Combine(c, "compile_commands.json"));
        Assert.Equal(30, System.Text.RegularExpressions.Regex.Matches(db, @"""file"": ""[^""]*u_\d+\.c""").Count);
    }

    [Fact]
    public void Web_ReachesTemplatesDeeperThan8()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c", Web);
        var m = Manifest(c);
        var units = m["closures"]!.AsObject().Select(kv => kv.Key).ToList();
        var walk = new IncludeClosure(c, ["inc", "inc_vnd"], units, new Containment(c)).Run(units);
        var inst = walk.Values.SelectMany(r => r.Instances).ToList();
        Assert.Contains(inst, i => i.Depth > 8);
        Assert.Contains(inst, i => i.Depth <= 2);
    }

    [Fact]
    public void SameSeed_SameBytes_AndMetaGenReplays()
    {
        using var tmp = new TempDir();
        string a = Gen(tmp, "a", Web), b = Gen(tmp, "b", Web);
        Assert.Equal(Snap(a), Snap(b));
        Assert.NotEqual(Snap(a), Snap(Gen(tmp, "s", [.. Web, "--seed", "7"])));

        // _meta.gen -> args (camelCase -> --kebab-case) regenerates the same tree.
        var gen = Manifest(a)["_meta"]!["gen"]!.AsObject();
        Assert.Equal("3-6", gen["webWindow"]!.GetValue<string>());
        var replay = new List<string> { "gen", "--out", Path.Combine(tmp.Path, "r") };
        foreach (var (k, v) in gen)
        {
            string knob = k == "maxHeaderMB" ? "max-header-mb" : System.Text.RegularExpressions.Regex.Replace(k, "[A-Z]", x => "-" + char.ToLowerInvariant(x.Value[0]));
            replay.Add($"--{knob}={(v!.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString())}");
        }
        Assert.True(Cli([.. replay]) == 0, _lastErr);
        Assert.Equal(Snap(a), Snap(Path.Combine(tmp.Path, "r")));

        // compile_commands.json holds absolute paths: compare everything else.
        static SortedDictionary<string, string> Snap(string root) { var s = Snapshot(root); s.Remove("compile_commands.json"); return s; }
    }

    // ---------- tampering ----------

    private static (int code, string err) Tampered(TempDir tmp, Action<string, JsonObject> edit)
    {
        string c = Gen(tmp, "c", Web);
        var m = Manifest(c);
        edit(c, m);
        Save(c, m);
        return Verify(c);
    }

    private static KeyValuePair<string, JsonNode?> AnInstance(JsonObject m) =>
        m["symbols"]!.AsObject().First(kv => kv.Value!["templateHead"] is not null);

    [Fact]
    public void Tamper_ClosureField_Fails()
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (_, m) =>
        {
            var u = m["closures"]!.AsObject().First().Value!.AsObject();
            u["readBytes"] = u["readBytes"]!.GetValue<long>() + 1;
        });
        Assert.Equal(1, code);
        Assert.Contains("readBytes", err);
    }

    [Fact]
    public void Tamper_HeaderText_ChangesTheRecomputedClosure()
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (c, _) =>
        {
            // One more #define inside cfg.h's guard: every unit reads it once.
            string cfg = Path.Combine(c, "inc", "cfg.h");
            string t = File.ReadAllText(cfg);
            int at = t.IndexOf("#define CAT_", StringComparison.Ordinal);
            File.WriteAllText(cfg, t[..at] + "#define WEB_EXTRA 1\n" + t[at..]);
        });
        Assert.Equal(1, code);
        Assert.Contains("defines", err);
        Assert.Contains("bytes", err);
    }

    [Fact]
    public void Tamper_InstanceDef_Fails()
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (_, m) =>
        {
            var e = AnInstance(m).Value!.AsObject();
            string d = e["def"]!.GetValue<string>();
            int line = int.Parse(d[(d.LastIndexOf(':') + 1)..]);
            e["def"] = $"{d[..d.LastIndexOf(':')]}:{line + 1}";   // the #undef TNAME line: not the include
        });
        Assert.Equal(1, code);
        Assert.Contains("FAIL def", err);
    }

    [Fact]
    public void Tamper_TemplateHead_Fails()
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (_, m) =>
        {
            var e = AnInstance(m).Value!.AsObject();
            string h = e["templateHead"]!.GetValue<string>();
            e["templateHead"] = h[..(h.LastIndexOf(':') + 1)] + (int.Parse(h[(h.LastIndexOf(':') + 1)..]) + 1);
        });
        Assert.Equal(1, code);
        Assert.Contains("templateHead", err);
    }

    [Fact]
    public void Tamper_IncludedVia_Fails()
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (_, m) => AnInstance(m).Value!["includedVia"] = "inc/cfg.h");
        Assert.Equal(1, code);
        Assert.Contains("includedVia inc/cfg.h", err);
    }

    [Fact]
    public void Instances_AreDefNotLiteral_AndTheFlagOnlyMarksInstances()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c", Web);
        var m = Manifest(c);
        var syms = m["symbols"]!.AsObject();
        Assert.All(syms.Where(kv => kv.Value!["templateHead"] is not null), kv => Assert.True(kv.Value!["defNotLiteral"]!.GetValue<bool>()));
        Assert.All(syms.Where(kv => kv.Value!["templateHead"] is null), kv => Assert.Null(kv.Value!["defNotLiteral"]));
        Assert.DoesNotContain(syms, kv => kv.Value!["expectedMiss"] is not null && kv.Value!["templateHead"] is not null);

        syms.First(kv => kv.Value!["templateHead"] is null).Value!["defNotLiteral"] = true;
        Save(c, m);
        var (code, err) = Verify(c);
        Assert.True(code == 2, err);
        Assert.Contains("defNotLiteral marks a template instance", err);
    }

    [Theory]
    [InlineData("swap-I")]
    [InlineData("drop-unit")]
    [InlineData("missing")]
    public void Tamper_CompileDb_Fails(string kind)
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (c, _) =>
        {
            string db = Path.Combine(c, "compile_commands.json");
            var arr = JsonNode.Parse(File.ReadAllText(db))!.AsArray();
            switch (kind)
            {
                case "swap-I":
                    var cmd = arr[0]!["command"]!.GetValue<string>();
                    string inc = $"-I\"{Path.Combine(c, "inc")}\"", vnd = $"-I\"{Path.Combine(c, "inc_vnd")}\"";
                    arr[0]!["command"] = cmd.Replace(inc, "@").Replace(vnd, inc).Replace("@", vnd);
                    File.WriteAllText(db, arr.ToJsonString());
                    break;
                case "drop-unit": arr.RemoveAt(arr.Count - 1); File.WriteAllText(db, arr.ToJsonString()); break;
                case "missing": File.Delete(db); break;
            }
        });
        Assert.Equal(1, code);
        Assert.Contains("FAIL compile_commands.json", err);
    }

    // A db that still names the tree it was generated at: a NOTE for a moved corpus, a FAIL for a 3-way variant
    // whose db names its base (what 1.3.1's mutate left).
    [Fact]
    public void CompileDb_NamingAnotherTree_IsANote_OrAFailForAVariantsBase()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c", Web);
        foreach (var (dst, fails) in new[] { ("moved", false), ("c_v1", true) })
        {
            string to = Path.Combine(tmp.Path, dst);
            CopyDir(c, to);
            File.Copy(c + "-manifest.json", to + "-manifest.json");
            var (code, err) = Verify(to);
            if (fails) { Assert.Equal(1, code); Assert.Contains("this variant's base", err); }
            else Assert.True(code == 0, err);
        }

        static void CopyDir(string from, string to)
        {
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string d = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.Copy(f, d);
            }
        }
    }

    [Fact]
    public void Tamper_RemovedOrInventedInstance_Fails()
    {
        using var tmp = new TempDir();
        string removed = "";
        var (code, err) = Tampered(tmp, (_, m) =>
        {
            var (name, node) = AnInstance(m);
            removed = name;
            var syms = m["symbols"]!.AsObject();
            syms.Remove(name);
            var fake = node!.DeepClone().AsObject();
            fake["refs"] = new JsonArray();
            syms["u0_r9_t999"] = fake;
        });
        Assert.Equal(1, code);
        Assert.Contains($"FAIL instance {removed}: the walk finds it", err);
        Assert.Contains("FAIL instance u0_r9_t999: no unit's closure defines it", err);
    }

    [Theory]
    [InlineData("includeDirs-not-array")]
    [InlineData("negative-field")]
    [InlineData("missing-field")]
    [InlineData("head-without-via")]
    [InlineData("instances-without-closures")]
    public void MalformedWebShape_IsBadInput(string kind)
    {
        using var tmp = new TempDir();
        var (code, err) = Tampered(tmp, (_, m) =>
        {
            var u = m["closures"]!.AsObject().First().Value!.AsObject();
            switch (kind)
            {
                case "includeDirs-not-array": m["_meta"]!["includeDirs"] = "inc"; break;
                case "negative-field": u["depth"] = -1; break;
                case "missing-field": u.Remove("templateInstances"); break;
                case "head-without-via": AnInstance(m).Value!.AsObject().Remove("includedVia"); break;
                case "instances-without-closures": m.Remove("closures"); break;
            }
        });
        Assert.True(code == 2, err);
        Assert.Contains("malformed manifest", err);
    }

    // ---------- mutate ----------

    [Fact]
    public void Mutate_NeverEditsTheWeb()
    {
        using var tmp = new TempDir();
        string c = Gen(tmp, "c", Web);
        var before = WebFiles(c).ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        foreach (var target in new[] { "source", "headers" })
            Assert.True(Cli("mutate", "--corpus", c, "--target", target, "--files-changed", "1000") is 0 or 1, _lastErr);
        Assert.All(before, kv => Assert.Equal(kv.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kv.Key)))));
    }

    // ---------- gen --from-profile ----------

    // The web is added beside a profile's tree: that tree, its oracle symbols and indirectTruthSha are untouched,
    // the web knobs are recorded, verify recomputes the closures, and a three-way mutate leaves the web in both variants.
    [Fact]
    public void FromProfile_AddsTheWeb_LeavingTheProfileTreeAndOracleAlone()
    {
        using var tmp = new TempDir();
        string tree = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(tree);
        for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(tree, $"f{i}.c"), $"int f{i}(void) {{ return {i}; }}\n");
        string prof = Path.Combine(tmp.Path, "prof.json");
        Assert.Equal(0, Cli("scan", tree, "--out", prof));

        string[] oracle = ["--with-oracle", "--oracle-fanout", "3", "--oracle-chain", "40", "--oracle-indirect", "5", "--oracle-bytes"];
        string plain = Path.Combine(tmp.Path, "p"), web = Path.Combine(tmp.Path, "w");
        Assert.True(Cli(["gen", "--out", plain, "--from-profile", prof, .. oracle]) == 0, _lastErr);
        Assert.True(Cli(["gen", "--out", web, "--from-profile", prof, .. oracle, .. Web]) == 0, _lastErr);

        var p = Snapshot(plain);
        var w = Snapshot(web);
        Assert.Equal(p, new SortedDictionary<string, string>(w.Where(kv => !IsWeb(kv.Key)).ToDictionary(), StringComparer.Ordinal));
        Assert.Contains(w.Keys, k => k.StartsWith("web/", StringComparison.Ordinal));

        var (pm, wm) = (Manifest(plain), Manifest(web));
        Assert.Equal(pm["_meta"]!["indirectTruthSha"]!.ToJsonString(), wm["_meta"]!["indirectTruthSha"]!.ToJsonString());
        foreach (var kv in pm["symbols"]!.AsObject()) Assert.Equal(kv.Value!.ToJsonString(), wm["symbols"]![kv.Key]!.ToJsonString());
        Assert.Equal(30, wm["closures"]!.AsObject().Count);
        Assert.Contains("webUnits", wm["_meta"]!["gen"]!.ToJsonString());
        var (code, err) = Verify(web);
        Assert.True(code == 0, err);

        Assert.True(Cli("mutate", "--corpus", web, "--target", "source", "--files-changed", "20", "--three-way", "--overlap-fraction", "0.5") == 0, _lastErr);
        string db = File.ReadAllText(Path.Combine(web, "compile_commands.json"));
        foreach (var v in new[] { web + "_v1", web + "_v2" })
        {
            var vs = Snapshot(v);
            Assert.All(w.Where(kv => IsWeb(kv.Key) && kv.Key != "compile_commands.json"), kv => Assert.Equal(kv.Value, vs[kv.Key]));
            // The variant's db compiles the variant: B's db with only the tree's path changed.
            string vdb = File.ReadAllText(Path.Combine(v, "compile_commands.json"));
            Assert.Equal(db.Replace(JsonEncodedText.Encode(web).ToString(), JsonEncodedText.Encode(v).ToString()), vdb);
            Assert.DoesNotContain(JsonEncodedText.Encode(web).ToString() + "\\\\", vdb);
        }
        // ... and every 3-way manifest says that file is bookkeeping, not an edit (manifestVersion 2).
        foreach (var m in new[] { "-delta-v1.json", "-delta-v2.json", "-conflict.json" })
        {
            var meta = JsonNode.Parse(File.ReadAllText(web + m))!["_meta"]!;
            Assert.Equal(2, (int)meta["manifestVersion"]!);
            Assert.Equal("[\"compile_commands.json\"]", meta["bookkeeping"]!.ToJsonString());
        }

        static bool IsWeb(string rel) => rel == "compile_commands.json" || rel.StartsWith("web/", StringComparison.Ordinal) || rel.StartsWith("inc/", StringComparison.Ordinal) || rel.StartsWith("inc_vnd/", StringComparison.Ordinal);
    }

    [Fact]
    public void FromProfile_WebWithoutOracle_IsRefused() =>
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(["--out", "x", "--from-profile", "p", "--web-units", "3"]));

    // ---------- verify's preprocessor on a hand-written web ----------

    private static void Put(string root, string rel, string text) { string p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }

    [Fact]
    public void Preprocessor_HonoursEveryTrap()
    {
        using var tmp = new TempDir();
        string r = tmp.Path;
        Put(r, "inc/cfg.h", "#ifndef CFG_H\n#define CFG_H\n#define CAT_(a, b) a##_##b\n#define CAT(a, b) CAT_(a, b)\n#define ON 1\n#define OFF 0\n#define HDR \"b.h\"\n#endif\n");
        // a.h: guarded; a block comment with a fake include spanning lines; a CRLF continuation; a computed include;
        // #if on a macro; an inactive include of a missing file; #include_next via vx.h.
        Put(r, "inc/a.h", "#ifndef A_H\r\n#define A_H\r\n/* old:\r\n#include \"missing.h\"\r\n*/ #define A1 1\r\n#define LONG 1 + \\\r\n  2\r\n#include HDR\r\n#if ON && !OFF\r\n#include <vx.h>\r\n#elif 1\r\n#include \"missing.h\"\r\n#endif\r\n#if defined(NOPE) || OFF\r\n#include \"missing.h\"\r\n#endif\r\n#include \"a.h\"\r\n#endif\r\n");
        Put(r, "inc/b.h", "#ifndef B_H\n#define B_H\n#define B1 1 // #include \"missing.h\"\n#endif\n");
        Put(r, "inc/vx.h", "#define VX_TOP 1\n#include_next <vx.h>\n");
        Put(r, "vnd/vx.h", "#define VX_VND 1\n");
        Put(r, "vnd/a.h", "#error shadow, never reached\n");
        // t.h: unguarded template, entered twice under different TNAMEs, its head through an alias.
        Put(r, "inc/t.h", "#ifdef TNAME\n#define ALIAS CAT(TNAME, t0_h1)\nint CAT(TNAME, t0)(void) { return 0; }\nint ALIAS(void) { return 1; }\n#undef ALIAS\n#endif\n");
        Put(r, "web/u.c", "#include \"cfg.h\"\n#include \"../inc/a.h\"\n#include \"a.h\"\n#define TNAME CAT(U, r0)\n#include \"t.h\"\n#undef TNAME\n#define TNAME CAT(U, r1)\n#include \"t.h\"\n#undef TNAME\n#include \"t.h\"\n");

        var res = new IncludeClosure(r, ["inc", "vnd"], ["web/u.c"], new Containment(r)).Run(["web/u.c"])["web/u.c"];
        Assert.Null(res.Error);
        // files: u.c cfg.h a.h b.h vx.h vnd/vx.h t.h = 7; reads: + t.h twice more (unguarded) = 9, a.h's guarded re-reads not counted.
        Assert.Equal(7, res.Files);
        Assert.Equal(9, res.Reads);
        long size(string p) => new FileInfo(Path.Combine(r, p)).Length;
        Assert.Equal(new[] { "web/u.c", "inc/cfg.h", "inc/a.h", "inc/b.h", "inc/vx.h", "vnd/vx.h", "inc/t.h" }.Sum(size), res.Bytes);
        Assert.Equal(res.Bytes + 2 * size("inc/t.h"), res.ReadBytes);
        Assert.Equal(3, res.Depth);   // u.c -> a.h -> vx.h -> vnd/vx.h
        // defines: cfg 6, a 3 (A_H A1 LONG), b 2, vx 1, vnd 1, u.c 2 (TNAME x2), t.h's ALIAS in its 2 entries with TNAME = 17.
        Assert.Equal(17, res.Defines);
        Assert.Equal(2, res.TemplateInstances);   // the third entry has no TNAME
        Assert.Equal(new[] { ("U_r0_t0", 5, 3), ("U_r1_t0", 8, 3) }, res.Instances.Select(i => (i.Name, i.UnitLine, i.HeadLine)));
    }

    private static IncludeClosure.Result Walk(string r, string unit = "web/u.c") =>
        new IncludeClosure(r, ["inc", "vnd"], [unit], new Containment(r)).Run([unit])[unit];

    // The includer's own dir comes before -I for a quoted include; angle brackets skip it.
    [Fact]
    public void Preprocessor_QuotedIncludeSearchesTheIncludersDirFirst()
    {
        using var tmp = new TempDir();
        string r = tmp.Path;
        Put(r, "inc/sub/a.h", "#include \"b.h\"\n#include <b.h>\n");
        Put(r, "inc/sub/b.h", "#define SUB_B 1\n");
        Put(r, "inc/b.h", "#define TOP_B 1\n");
        Put(r, "web/u.c", "#include \"sub/a.h\"\n");
        var res = Walk(r);
        Assert.Null(res.Error);
        Assert.Equal(4, res.Files);   // u.c, sub/a.h, sub/b.h (quoted, beside a.h), inc/b.h (angle)
    }

    [Theory]
    [InlineData("#if 010 == 8 && 0x10 == 16 && 0b11 == 3 && 10u == 10\n#include \"y.h\"\n#endif\n", 2)]
    [InlineData("#if UNDEFINED_NAME || 0\n#include \"y.h\"\n#endif\n", 1)]   // an undefined name is 0
    [InlineData("#if 0 || 1\n#include \"y.h\"\n#endif\n", 2)]
    [InlineData("#include \"chain.h\"\n#if TOP\n#include \"y.h\"\n#endif\n", 3)]   // TOP -> MID -> 1, defined in a header
    [InlineData("#pragma once\n#include \"once.h\"\n#include \"once.h\"\n", 2)]
    [InlineData("// a comment that runs on \\\n#include \"missing.h\"\n", 1)]   // the splice carries the comment
    [InlineData("#include \"d.h\"\n#include LATER\n", 3)]                    // computed include defined in a later file
    public void Preprocessor_Cases(string unit, int files)
    {
        using var tmp = new TempDir();
        string r = tmp.Path;
        Put(r, "inc/y.h", "#define Y 1\n");
        Put(r, "inc/chain.h", "#ifndef CHAIN_H\n#define CHAIN_H\n#define TOP MID\n#define MID 1\n#endif\n");
        Put(r, "inc/once.h", "#pragma once\n#define ONCE 1\n");
        Put(r, "inc/d.h", "#define LATER \"y.h\"\n");
        Put(r, "web/u.c", unit);
        var res = Walk(r);
        Assert.True(res.Error is null, res.Error);
        Assert.Equal(files, res.Files);
        if (unit.Contains("once.h")) Assert.Equal(2, res.Reads);   // once.h read once, though included twice
    }

    [Theory]
    [InlineData("#include \"self.h\"\n", "nested deeper than 200")]                 // no guard, no once: a cycle
    [InlineData("#if 1\n#error stop here\n#endif\n", "#error stop here")]
    [InlineData("#if 09\n#endif\n", "#if number 09")]                                 // not an octal number
    [InlineData("#if 99999999999999999999\n#endif\n", "#if number")]                // past 64 bits
    public void Preprocessor_FailsWhereACompilerWould(string unit, string why)
    {
        using var tmp = new TempDir();
        string r = tmp.Path;
        Put(r, "inc/self.h", "#define SELF 1\n#include \"self.h\"\n");
        Put(r, "web/u.c", unit);
        var res = Walk(r);
        Assert.NotNull(res.Error);
        Assert.Contains(why, res.Error);
    }
}
