using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Verify;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// gen --lang cpp (docs/cpp-profile-design.md): the knobs and their refusals, determinism, that every truth
/// quadrant the consumers score against is populated (cha minus rta, an instantiated-but-unreachable instance,
/// an unused template, pure / final / multiple inheritance, static init, member pointers called and not), and that
/// verify PASSes the real output and FAILs each kind of tampering. The program itself compiling is checked with
/// clang++ by hand / in bench, not here (no compiler on the test path).
/// </summary>
public class CppProfileTests
{
    private static GenOptions Opts(string outDir, int seed = 1337) => new() { Out = outDir, Seed = seed, Lang = SourceLang.Cpp };

    private static (CppProgram p, CppTruth t) Model(GenOptions o)
    {
        var p = CppProgram.Build(o);
        CppEmitter.Emit(p);
        return (p, CppTruth.Build(p));
    }

    private static (string corpus, string manifest) Gen(TempDir tmp, GenOptions? o = null, string name = "c")
    {
        string corpus = Path.Combine(tmp.Path, name);
        new CppGenerator(o ?? Opts(corpus)).Run();
        return (corpus, corpus + "-manifest.json");
    }

    private static int Verify(string corpus, string manifest) =>
        ManifestVerifier.Run(new VerifyOptions { Corpus = corpus, Manifest = manifest });

    // ---------- knobs ----------

    [Fact]
    public void Lang_ParsesAndDefaultsToC()
    {
        Assert.Equal(SourceLang.C, ArgParser.ParseGen(new[] { "--out", "x" }).Lang);
        Assert.Equal(SourceLang.Cpp, ArgParser.ParseGen(new[] { "--out", "x", "--lang", "cpp" }).Lang);
        Assert.Equal(SourceLang.Cpp, ArgParser.ParseGen(new[] { "--out", "x", "--lang", "C++" }).Lang);
        var o = ArgParser.ParseGen(new[] { "--out", "x", "--lang", "cpp", "--cpp-classes", "30", "--cpp-depth", "5",
            "--cpp-multi-inherit", "0.5", "--cpp-virtual-frac", "0.9", "--cpp-instantiated-frac", "0.25", "--cpp-templates", "7",
            "--cpp-instances", "3", "--cpp-static-init", "4", "--oracle-chain", "20", "--oracle-fanout", "2" });
        Assert.Equal((30, 5, 0.5, 0.9, 0.25, 7, 3, 4), (o.CppClasses, o.CppDepth, o.CppMultiInherit, o.CppVirtualFrac,
            o.CppInstantiatedFrac, o.CppTemplates, o.CppInstances, o.CppStaticInit));
    }

    [Theory]
    [InlineData("--lang", "rust")]                                     // unknown language
    [InlineData("--cpp-classes", "5")]                                 // a cpp knob without --lang cpp
    [InlineData("--lang", "cpp", "--scale", "0.01")]                   // a C population knob with --lang cpp
    [InlineData("--lang", "cpp", "--with-oracle")]
    [InlineData("--lang", "cpp", "--cpp-classes", "0")]
    [InlineData("--lang", "cpp", "--cpp-depth", "17")]
    [InlineData("--lang", "cpp", "--cpp-instances", "6")]
    [InlineData("--lang", "cpp", "--cpp-instances", "0")]
    [InlineData("--lang", "cpp", "--cpp-virtual-frac", "1.5")]
    [InlineData("--lang", "cpp", "--cpp-multi-inherit", "-0.1")]
    [InlineData("--lang", "cpp", "--cpp-templates", "-1")]
    [InlineData("--lang", "cpp", "--oracle-chain", "1")]
    [InlineData("--lang", "cpp", "--oracle-depth", "3")]                // shapes the fanout DAG only
    [InlineData("--lang", "cpp", "--oracle-reachable-frac", "0.001")]
    [InlineData("--lang", "cpp", "--cpp-instantiated-frac", "1.5")]
    [InlineData("--lang", "cpp", "--cpp-static-init", "-1")]
    [InlineData("--lang", "cpp", "--cpp-depth", "-1")]
    [InlineData("--lang", "cpp", "--cpp-classes", "100001")]
    [InlineData("--lang", "cpp", "--cpp-templates", "10001")]
    [InlineData("--lang", "cpp", "--oracle-fanout", "2", "--oracle-shared-leaves", "1000001")]
    [InlineData("--lang", "cpp", "--oracle-chain", "1000000", "--oracle-reachable-frac", "0.01")]   // ~100M free functions
    [InlineData("--lang", "cpp", "--oracle-chain", "1900000")]                                     // over the range, and the cap
    public void BadKnobs_AreRefused(params string[] args)
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "x" }.Concat(args).ToArray()));

    // The edges of each range are accepted.
    [Theory]
    [InlineData("--cpp-depth", "0")]
    [InlineData("--cpp-depth", "16")]
    [InlineData("--cpp-instances", "5")]
    [InlineData("--cpp-classes", "1")]
    [InlineData("--cpp-classes", "100000")]
    [InlineData("--cpp-instantiated-frac", "0")]
    [InlineData("--cpp-static-init", "0")]
    [InlineData("--oracle-fanout", "3", "--oracle-shared-leaves", "10")]
    [InlineData("--oracle-chain", "1000000")]
    [InlineData("--oracle-chain", "1000000", "--oracle-reachable-frac", "0.5")]
    public void EdgeKnobs_AreAccepted(params string[] args)
        => ArgParser.ParseGen(new[] { "--out", "x", "--lang", "cpp" }.Concat(args).ToArray());

    // ---------- determinism ----------

    [Fact]
    public void SameSeed_SameBytes_DifferentSeed_DifferentProgram()
    {
        using var tmp = new TempDir();
        var (a, am) = Gen(tmp, Opts(Path.Combine(tmp.Path, "a")), "a");
        var (b, bm) = Gen(tmp, Opts(Path.Combine(tmp.Path, "b")), "b");
        var (c, _) = Gen(tmp, Opts(Path.Combine(tmp.Path, "c"), seed: 7), "c");
        static Dictionary<string, string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".codespawner", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);
        Assert.Equal(Files(a), Files(b));
        Assert.NotEqual(Files(a), Files(c));
        static string NoRoot(string p) => string.Join("\n", File.ReadAllLines(p).Where(l => !l.Contains("\"corpusRoot\"")));
        Assert.Equal(NoRoot(am), NoRoot(bm));
    }

    // ---------- truth quadrants, across seeds ----------

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(1337)]
    public void EveryQuadrant_IsPopulated(int seed)
    {
        var (p, t) = Model(Opts("x", seed));
        var m = t.ToManifest(Opts("x", seed), "root");
        var syms = m.Symbols;
        var virt = syms.Values.SelectMany(s => s.VirtualEdges).ToList();
        var mp = syms.Values.SelectMany(s => s.MemberPtrEdges).Select(e => (e.Via, e.Dispatched)).ToHashSet();

        Assert.Contains(virt, v => v.Cha.Except(v.Rta).Any());                       // the sound-vs-precise tax
        Assert.Contains(virt, v => v.Rta.Count > 0);
        Assert.Contains(p.Classes.SelectMany(c => c.Methods), f => f.Pure);           // abstract class
        Assert.Contains(syms.Values, s => s.Pure && s.Kind == "method");               // ... marked in the manifest
        Assert.Contains(p.Classes, c => c.Final);
        Assert.Contains(p.Classes, c => c.Base is not null && c.Mixin is not null);  // multiple inheritance
        Assert.Contains(p.Classes, c => c.Depth >= 2);
        Assert.NotEmpty(m.StaticInitRoots);
        Assert.Contains(syms.Values, s => s.ImplicitEdges.Count > 0);
        foreach (var via in new[] { MemberPtrVia.Table, MemberPtrVia.Arg })
            foreach (bool d in new[] { true, false })
                Assert.Contains((via, d), mp);

        var inst = syms.Where(s => s.Value.Kind == "instance").ToList();
        Assert.Contains(inst, s => t.SoundReach.Contains(s.Key));                    // a live instance
        Assert.Contains(inst, s => !t.SoundReach.Contains(s.Key));                   // instantiated but unreachable
        Assert.Contains(syms, s => s.Value.Kind == "template" && !inst.Any(i => i.Value.InstanceOf == s.Key));   // unused
        Assert.Contains(syms.Keys, k => !t.SoundReach.Contains(k) && syms[k].Kind == "function");             // dead code
        Assert.True(t.PreciseReach.IsSubsetOf(t.SoundReach));
        Assert.True(t.PreciseReach.Count < t.SoundReach.Count);
    }

    // A class is live only if a static object or reachable code constructs it (or it is a member of a live class),
    // every rta target is what some live class dispatches to, and rta is within cha. (scratchpad-independent exact
    // recomputation lives in the Python cross-check; these are the invariants a regression would break first.)
    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(1337)]
    public void Rta_IsDrivenByLiveClasses(int seed)
    {
        var (p, t) = Model(Opts("x", seed));
        var memberOfLive = t.Live.SelectMany(c => c.Members).ToHashSet();
        foreach (var c in t.Live)
            Assert.True(p.StaticObjs.Contains(c) || memberOfLive.Contains(c) || t.PreciseReach.Contains(c.Ctor!.Symbol), $"{c.Q} live but never constructed");
        Assert.DoesNotContain(t.Live, c => c.Abstract);
        var m = t.ToManifest(Opts("x", seed), "r");
        foreach (var v in m.Symbols.Values.SelectMany(s => s.VirtualEdges))
        {
            Assert.Subset(v.Cha.ToHashSet(), v.Rta.ToHashSet());
            foreach (var target in v.Rta)
                Assert.Contains(t.Live, c => c.VirtualNames().Any(n => c.Resolve(n)!.Symbol == target));
        }
    }

    [Fact]
    public void KnobExtremes_StillProduceAVerifiedProgram()
    {
        using var tmp = new TempDir();
        var cases = new (string name, Action<GenOptions> set)[]
        {
            ("flat", o => o.CppDepth = 0),
            ("one", o => { o.CppClasses = 1; o.CppTemplates = 0; o.CppStaticInit = 0; }),
            ("nomi", o => o.CppMultiInherit = 0),
            ("allinst", o => o.CppInstantiatedFrac = 1),
            ("noinst", o => o.CppInstantiatedFrac = 0),
            ("dag", o => { o.OracleFanout = 3; o.OracleChain = 30; o.OracleSharedLeaves = 4; o.OracleReachableFrac = 0.5; }),
            ("full", o => o.OracleReachableFrac = 1),
        };
        foreach (var (name, set) in cases)
        {
            var o = Opts(Path.Combine(tmp.Path, name));
            set(o);
            var (c, m) = Gen(tmp, o, name);
            Assert.True(Verify(c, m) == 0, name);
        }
    }

    // ---------- the manifest ----------

    [Fact]
    public void Manifest_IsV2_AndRoundTripsItsDigest()
    {
        using var tmp = new TempDir();
        var (_, mp) = Gen(tmp);
        var root = JsonNode.Parse(File.ReadAllText(mp))!;
        var meta = root["_meta"]!;
        Assert.Equal(2, (int)meta["manifestVersion"]!);
        Assert.Equal("cpp", (string)meta["lang"]!);
        var parsed = ManifestReader.Load(mp, maxVersion: 2);
        Assert.Equal((string)meta["cppTruthSha"]!, CppDigest.Compute(parsed.Symbols, parsed.StaticInitRoots));
        Assert.Throws<ManifestVersionException>(() => ManifestReader.Load(mp));      // a v1-only reader refuses it
    }

    // The default program's truth, pinned (bench 4h holds the same value). A generator change that moves any
    // C++ fact moves this: update it on purpose, with a CHANGELOG note, never to make the test pass.
    [Fact]
    public void TruthSha_IsPinned()
    {
        var (_, t) = Model(Opts("x"));
        var m = t.ToManifest(Opts("x"), "r");
        Assert.Equal("d3a38e31f260177f36337a7b475eba0fb266008cf56e22e620ced14a36f40ad5", CppDigest.Compute(m.Symbols, m.StaticInitRoots));
    }

    // --cpp-classes 1: the lone class is concrete (it is constructed, statically too) and takes no mixin.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void OneClass_IsConcrete_WithStaticObjects_AndNoMixin(int seed)
    {
        var o = Opts("x", seed);
        o.CppClasses = 1;
        o.CppStaticInit = 5;
        var (p, t) = Model(o);
        var c = Assert.Single(p.Classes);
        Assert.False(c.Abstract);
        Assert.Null(c.Mixin);
        Assert.Empty(p.Mixins);
        Assert.Equal(5, p.StaticObjs.Count);
        Assert.Contains(c, t.Live);
    }

    // With two or more classes, class 0 is abstract and class 1 (when multiple inheritance is on) takes a mixin.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void TwoClasses_FirstIsAbstract_SecondTakesTheMixin(int seed)
    {
        var o = Opts("x", seed);
        o.CppClasses = 2;
        o.CppMultiInherit = 0.01;
        var (p, _) = Model(o);
        Assert.True(p.Classes[0].Abstract);
        Assert.NotNull(p.Classes[1].Mixin);
    }

    [Fact]
    public void V1Manifest_CarriesNoV2Fields()
    {
        using var tmp = new TempDir();
        var m = new ManifestModel { GeneratorVersion = "x", Seed = 1, CorpusRoot = "r" };
        m.Roots.Add("func_0");
        m.Symbols["func_0"] = new SymbolEntry { Def = "a.c:1", Edges = { "func_1" } };
        m.Symbols["func_1"] = new SymbolEntry { Def = "a.c:2" };
        string p = tmp.File("m.json");
        ManifestWriter.Write(m, p);
        string text = File.ReadAllText(p);
        foreach (var k in new[] { "\"lang\"", "\"kind\"", "\"simpleName\"", "\"instanceOf\"", "\"implicitEdges\"", "\"virtualEdges\"",
                                  "\"memberPtrEdges\"", "\"staticInitRoots\"", "\"classes\"", "\"cppTruthSha\"" })
            Assert.DoesNotContain(k, text);
        Assert.Contains("\"manifestVersion\": 1", text);
    }

    // ---------- verify ----------

    [Fact]
    public void Verify_PassesRealOutput()
    {
        using var tmp = new TempDir();
        var (c, m) = Gen(tmp);
        Assert.Equal(0, Verify(c, m));
    }

    // A class symbol's def + refs are exactly the lines where its name is a whole word (CodeCompass's name search),
    // checked here by an independent scan of the emitted program; every class and mixin has one.
    [Theory]
    [InlineData(1337)]
    [InlineData(3)]
    [InlineData(11)]
    public void ClassSymbols_AreEveryWholeWordLine(int seed)
    {
        using var tmp = new TempDir();
        var (c, mp) = Gen(tmp, Opts(Path.Combine(tmp.Path, "c"), seed));
        var root = JsonNode.Parse(File.ReadAllText(mp))!.AsObject();
        var syms = root["symbols"]!.AsObject();
        var classes = syms.Where(kv => (string?)kv.Value!["kind"] == "class").ToList();
        Assert.Equal(root["_meta"]!["classes"]!.AsObject().Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal),
                     classes.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal));
        var files = Directory.GetFiles(Path.Combine(c, "cpp"), "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(c, f).Replace('\\', '/'), Lines: File.ReadAllLines(f))).ToList();
        foreach (var (name, sym) in classes)
        {
            string simple = (string)sym!["simpleName"]!;
            var word = new System.Text.RegularExpressions.Regex($@"\b{simple}\b");
            var expect = files.SelectMany(f => f.Lines.Select((l, i) => (l, site: $"{f.Rel}:{i + 1}")))
                .Where(x => !x.l.TrimStart().StartsWith("#") && word.IsMatch(x.l)).Select(x => x.site).ToHashSet();
            var got = sym["refs"]!.AsArray().Select(x => (string)x!).Append((string)sym["def"]!).ToHashSet();
            Assert.True(expect.SetEquals(got), $"{name}: missing {string.Join(",", expect.Except(got))}; extra {string.Join(",", got.Except(expect))}");
            Assert.Empty(sym["edges"]!.AsArray());
        }
    }

    private static string FirstWith(JsonObject syms, Func<JsonObject, bool> pred) =>
        syms.First(kv => pred(kv.Value!.AsObject())).Key;

    [Theory]
    [InlineData("chaUnrelated")]      // a cha target that does not override static
    [InlineData("rtaNotInCha")]
    [InlineData("staticNotMethod")]
    [InlineData("instanceOfNotTemplate")]
    [InlineData("badKind")]
    [InlineData("noSimpleName")]
    [InlineData("implicitMissing")]
    [InlineData("memberPtrMissing")]
    [InlineData("memberPtrVia")]
    [InlineData("staticInitMissing")]
    [InlineData("refWrongLine")]
    [InlineData("defWrongWord")]
    [InlineData("shaDrift")]
    [InlineData("shaMissing")]
    [InlineData("rtaDropped")]        // only the digest catches a quietly shrunk rta
    [InlineData("pureOnBody")]        // pure claimed for a method that has a body
    [InlineData("pureOnFunction")]
    [InlineData("classWithEdge")]     // a class symbol is a def + refs only
    [InlineData("classNotInMeta")]
    [InlineData("classRefWrongLine")]
    [InlineData("chaPure")]           // a pure method has no body to dispatch to
    [InlineData("chaIncomplete")]     // an override quietly dropped from cha (re-sealed: only the exact-cha check sees it)
    [InlineData("chaSameName")]       // an unrelated class's method of the same simple name
    [InlineData("classesFakeBase")]   // _meta.classes names a base the class line does not
    [InlineData("classesCycle")]      // must FAIL, not hang
    [InlineData("simpleNameRetarget")]
    [InlineData("defInRefs")]
    [InlineData("ctorDefAtClassLine")]
    [InlineData("ctorRefAtDtor")]     // `~Cls_N` does not name the ctor
    [InlineData("instanceOfOnFunction")]
    [InlineData("pureMissing")]
    [InlineData("staticInitMethod")]
    [InlineData("implicitMethod")]
    [InlineData("dupRef")]
    [InlineData("siteNotCanonical")]
    public void Verify_FailsTampering(string how)
    {
        using var tmp = new TempDir();
        var (c, mp) = Gen(tmp);
        var root = JsonNode.Parse(File.ReadAllText(mp))!.AsObject();
        var meta = root["_meta"]!.AsObject();
        var syms = root["symbols"]!.AsObject();
        JsonObject Ve() => syms.Select(kv => kv.Value!["virtualEdges"]).OfType<JsonArray>().SelectMany(a => a)
            .Select(v => v!.AsObject()).First(v => v["rta"]!.AsArray().Count > 0 && v["cha"]!.AsArray().Count > v["rta"]!.AsArray().Count);
        switch (how)
        {
            case "chaUnrelated":
            {
                var v = Ve();
                string st = (string)v["static"]!;
                string other = FirstWith(syms, s => (string?)s["kind"] == "method" && !syms[st]!["simpleName"]!.ToString().Equals((string?)s["simpleName"]));
                v["cha"]!.AsArray().Add(other);
                break;
            }
            case "rtaNotInCha": Ve()["rta"]!.AsArray().Add(FirstWith(syms, s => (string?)s["kind"] == "function")); break;
            case "staticNotMethod": Ve()["static"] = FirstWith(syms, s => (string?)s["kind"] == "function"); break;
            case "instanceOfNotTemplate": syms[FirstWith(syms, s => (string?)s["kind"] == "instance")]!["instanceOf"] = FirstWith(syms, s => (string?)s["kind"] == "method"); break;
            case "badKind": syms[FirstWith(syms, s => (string?)s["kind"] == "ctor")]!["kind"] = "constructor"; break;
            case "noSimpleName": syms[FirstWith(syms, s => (string?)s["kind"] == "dtor")]!.AsObject().Remove("simpleName"); break;
            case "implicitMissing": syms[FirstWith(syms, s => s["implicitEdges"] is JsonArray)]!["implicitEdges"]!.AsArray().Add("ns_0::Nope::~Nope"); break;
            case "memberPtrMissing": syms[FirstWith(syms, s => s["memberPtrEdges"] is JsonArray)]!["memberPtrEdges"]![0]!["target"] = "ns_0::Nope::m_0"; break;
            case "memberPtrVia": syms[FirstWith(syms, s => s["memberPtrEdges"] is JsonArray)]!["memberPtrEdges"]![0]!["via"] = "vtable"; break;
            case "staticInitMissing": meta["staticInitRoots"]!.AsArray().Add("ns_0::Nope::Nope"); break;
            case "refWrongLine":
            {
                var s = syms[FirstWith(syms, s => (string?)s["kind"] == "method" && s["refs"]!.AsArray().Count > 0)]!;
                string r = (string)s["refs"]![0]!;
                s["refs"]![0] = r[..(r.LastIndexOf(':') + 1)] + "1";       // line 1 is an #include
                break;
            }
            case "defWrongWord": syms[FirstWith(syms, s => (string?)s["kind"] == "method")]!["simpleName"] = "m_99"; break;
            case "shaDrift": meta["cppTruthSha"] = new string('0', 64); break;
            case "shaMissing": meta.Remove("cppTruthSha"); break;
            case "rtaDropped": Ve()["rta"]!.AsArray().RemoveAt(0); break;
            case "pureOnBody": syms[FirstWith(syms, s => (string?)s["kind"] == "method" && s["pure"] is null)]!["pure"] = true; break;
            case "pureOnFunction": syms[FirstWith(syms, s => (string?)s["kind"] == "function")]!["pure"] = true; break;
            case "classWithEdge": syms[FirstWith(syms, s => (string?)s["kind"] == "class")]!["edges"]!.AsArray().Add(FirstWith(syms, s => (string?)s["kind"] == "function")); break;
            case "classNotInMeta": meta["classes"]!.AsObject().Remove(FirstWith(syms, s => (string?)s["kind"] == "class")); break;
            case "classRefWrongLine":
            {
                var s = syms[FirstWith(syms, s => (string?)s["kind"] == "class" && s["refs"]!.AsArray().Count > 0)]!;
                string r = (string)s["refs"]![0]!;
                s["refs"]![0] = r[..(r.LastIndexOf(':') + 1)] + "1";
                break;
            }
            case "chaPure": Ve()["cha"]!.AsArray().Add(FirstWith(syms, s => (bool?)s["pure"] == true)); break;
            case "chaIncomplete":
            {
                var v = Ve();
                var rta = v["rta"]!.AsArray().Select(x => (string)x!).ToHashSet();
                var cha = v["cha"]!.AsArray();
                cha.RemoveAt(cha.Select(x => (string)x!).ToList().FindIndex(x => !rta.Contains(x)));
                break;
            }
            case "chaSameName":
            {
                // Some record, plus a non-pure method of the same simple name that is not in its cha.
                var (v, other) = syms.Select(kv => kv.Value!["virtualEdges"]).OfType<JsonArray>().SelectMany(a => a).Select(r => r!.AsObject())
                    .Select(r =>
                    {
                        string sn = (string)syms[(string)r["static"]!]!["simpleName"]!;
                        var cha = r["cha"]!.AsArray().Select(x => (string)x!).ToHashSet();
                        string? o = syms.Where(kv => (string?)kv.Value!["kind"] == "method" && (string?)kv.Value["simpleName"] == sn
                                                     && (bool?)kv.Value["pure"] != true && !cha.Contains(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                        return (r, o);
                    })
                    .First(x => x.o is not null);
                v["cha"]!.AsArray().Add(other);
                break;
            }
            case "classesFakeBase":
            {
                var cls = meta["classes"]!.AsObject();
                var leaf = cls.First(kv => kv.Value!.AsArray().Count == 0 && kv.Key.Contains("Cls_"));
                leaf.Value!.AsArray().Add(cls.First(kv => kv.Key != leaf.Key && kv.Key.Contains("Mix_")).Key);
                break;
            }
            case "classesCycle":
            {
                var cls = meta["classes"]!.AsObject();
                var child = cls.First(kv => kv.Value!.AsArray().Count > 0);
                cls[(string)child.Value![0]!]!.AsArray().Add(child.Key);
                break;
            }
            case "simpleNameRetarget":
            {
                // A function renamed to another function's simple name: the def line still names the old one.
                var fns = syms.Where(kv => (string?)kv.Value!["kind"] == "function").Select(kv => kv.Value!).Take(2).ToList();
                fns[0]["simpleName"] = (string)fns[1]["simpleName"]!;
                break;
            }
            case "defInRefs":
            {
                var s = syms[FirstWith(syms, s => (string?)s["kind"] == "function")]!;
                s["refs"]!.AsArray().Add((string)s["def"]!);
                break;
            }
            case "ctorDefAtClassLine":
            {
                var cls = FirstWith(syms, s => (string?)s["kind"] == "class" && !((string)s["simpleName"]!).StartsWith("Mix_"));
                string sn = (string)syms[cls]!["simpleName"]!;
                syms[$"{cls}::{sn}"]!["def"] = (string)syms[cls]!["def"]!;
                break;
            }
            case "ctorRefAtDtor":
            {
                var cls = FirstWith(syms, s => (string?)s["kind"] == "class" && !((string)s["simpleName"]!).StartsWith("Mix_"));
                string sn = (string)syms[cls]!["simpleName"]!;
                syms[$"{cls}::{sn}"]!["refs"]!.AsArray().Add((string)syms[$"{cls}::~{sn}"]!["def"]!);
                break;
            }
            case "instanceOfOnFunction": syms[FirstWith(syms, s => (string?)s["kind"] == "function")]!["instanceOf"] = FirstWith(syms, s => (string?)s["kind"] == "template"); break;
            case "pureMissing": syms[FirstWith(syms, s => (bool?)s["pure"] == true)]!.AsObject().Remove("pure"); break;
            case "staticInitMethod": meta["staticInitRoots"]![0] = FirstWith(syms, s => (string?)s["kind"] == "method"); break;
            case "implicitMethod": syms[FirstWith(syms, s => s["implicitEdges"] is JsonArray)]!["implicitEdges"]!.AsArray().Add(FirstWith(syms, s => (string?)s["kind"] == "method")); break;
            case "dupRef":
            {
                var s = syms[FirstWith(syms, s => (string?)s["kind"] == "function" && s["refs"]!.AsArray().Count > 0)]!;
                s["refs"]!.AsArray().Add((string)s["refs"]![0]!);
                break;
            }
            case "siteNotCanonical":
            {
                var s = syms[FirstWith(syms, s => (string?)s["kind"] == "function" && s["refs"]!.AsArray().Count > 0)]!;
                s["refs"]![0] = "./" + (string)s["refs"]![0]!;
                break;
            }
        }
        var jo = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        File.WriteAllText(mp, root.ToJsonString(jo));
        // Re-seal the digest over the tampered manifest, so the check under test (not the digest) must catch it.
        if (how is not ("shaDrift" or "shaMissing" or "rtaDropped" or "memberPtrVia"))
        {
            var parsed = ManifestReader.Load(mp, maxVersion: 2);
            meta["cppTruthSha"] = CppDigest.Compute(parsed.Symbols, parsed.StaticInitRoots);
            File.WriteAllText(mp, root.ToJsonString(jo));
        }
        Assert.NotEqual(0, Verify(c, mp));
    }

    // A v2 manifest of the wrong shape is bad input (exit 2), never read as if the field were empty.
    [Theory]
    [InlineData("chaString")]
    [InlineData("pureString")]
    [InlineData("refsString")]
    [InlineData("classesArray")]
    [InlineData("classBasesString")]
    [InlineData("v1Field")]
    [InlineData("mpNoDispatched")]
    [InlineData("staticInitString")]
    [InlineData("dupSymbolKey")]
    [InlineData("badVersion")]
    public void Verify_MalformedV2_Exits2(string how)
    {
        using var tmp = new TempDir();
        var (c, mp) = Gen(tmp);
        var root = JsonNode.Parse(File.ReadAllText(mp))!.AsObject();
        var meta = root["_meta"]!.AsObject();
        var syms = root["symbols"]!.AsObject();
        JsonObject First(Func<JsonObject, bool> pred) => syms.Select(kv => kv.Value!.AsObject()).First(pred);
        switch (how)
        {
            case "chaString": First(s => s["virtualEdges"] is JsonArray)["virtualEdges"]![0]!["cha"] = "x"; break;
            case "pureString": First(s => s["pure"] is not null)["pure"] = "true"; break;
            case "refsString": First(s => (string?)s["kind"] == "function")["refs"] = "a.cpp:1"; break;
            case "classesArray": meta["classes"] = new JsonArray(); break;
            case "classBasesString": meta["classes"]![meta["classes"]!.AsObject().First().Key] = "Base"; break;
            case "v1Field": First(s => (string?)s["kind"] == "function")["bytes"] = 1; break;
            case "mpNoDispatched": First(s => s["memberPtrEdges"] is JsonArray)["memberPtrEdges"]![0]!.AsObject().Remove("dispatched"); break;
            case "staticInitString": meta["staticInitRoots"] = "x"; break;
            case "badVersion": meta["manifestVersion"] = 3; break;
        }
        string text = root.ToJsonString();
        if (how == "dupSymbolKey")
        {
            // A second copy of the first symbol, same key (JsonNode cannot hold one, so splice the text).
            var first = syms.First();
            string entry = $"\"{first.Key}\":{first.Value!.ToJsonString()}";
            int at = text.IndexOf(entry, StringComparison.Ordinal);
            text = text.Insert(at, entry + ",");
        }
        File.WriteAllText(mp, text);
        Assert.Equal(2, Verify(c, mp));
    }

    // Whole-word matching: m_1 is not in m_10, and with notAfterTilde a ctor's name is not found inside `~Cls`.
    [Theory]
    [InlineData("gp_x->m_10();", "m_1", false, false)]
    [InlineData("gp_x->m_1();", "m_1", false, true)]
    [InlineData("x_m_1 = 0;", "m_1", false, false)]
    [InlineData("~Cls_3();", "~Cls_3", false, true)]
    [InlineData("~Cls_3();", "Cls_3", false, true)]           // the class's name, as a type, is there
    [InlineData("~Cls_3();", "Cls_3", true, false)]           // ... but not the ctor
    [InlineData("Cls_3::~Cls_3() {", "Cls_3", true, true)]    // the qualifier names it
    [InlineData("Cls_30 obj;", "Cls_3", true, false)]
    public void HasWord_IsWholeWord(string text, string word, bool notAfterTilde, bool want)
        => Assert.Equal(want, ManifestVerifier.HasWord(text, word, notAfterTilde));

    // mutate edits C corpora only: a v2 (C++) manifest is refused before anything is touched.
    [Fact]
    public void Mutate_RefusesCppCorpus()
    {
        using var tmp = new TempDir();
        var (c, _) = Gen(tmp);
        var ex = Record.Exception(() => CodeSpawner.Mutation.Mutator.Run(ArgParser.ParseMutate(new[] { "--corpus", c })));
        Assert.NotNull(ex);
        Assert.Contains("manifest version 2", ex!.Message);
    }
}
