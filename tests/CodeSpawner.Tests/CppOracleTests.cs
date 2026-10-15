using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// An independent recomputation of the C++ truth from the emitted program text alone (no generator model): the
/// class hierarchy from the `class` lines, every body's calls / constructions / template uses / member pointers
/// from its statements, then cha, rta (the live-class fixpoint), implicit ctor/dtor edges, refs, and both
/// reachability closures. Each must equal what the manifest says. A second pass checks ref completeness by name:
/// every whole-word occurrence of a symbol's simple name is a def or ref of some symbol with that name (outside the
/// documented exclusions). These catch a wrong truth that every per-fact verifier check would still accept.
/// </summary>
public class CppOracleTests
{
    private sealed class Cls
    {
        public required string Name;
        public List<string> Bases = new();
        public Dictionary<string, (bool Virtual, bool Pure)> Methods = new(StringComparer.Ordinal);
        public List<string> Members = new();
    }

    private sealed class Body
    {
        public List<string> Edges = new(), Implicit = new(), Cons = new();
        public List<(string Q, string N)> Virt = new();
        public List<(string Target, string Via, bool Dispatched)> Mp = new();
        public List<(string Sym, string Site)> Refs = new();
        public List<(string Tmpl, string Arg)> TCalls = new();
    }

    private sealed class Oracle
    {
        public readonly Dictionary<string, string[]> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Cls> Classes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Children = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<(string Rel, int Line, string Text)>> Bodies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Body> All = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Imp = new(StringComparer.Ordinal);
        public readonly List<string> Statics = new(), SRoots = new(), Roots = new();
        public readonly HashSet<string> Live = new(StringComparer.Ordinal);
        public HashSet<string> Precise = new(), Sound = new();
        public int Rounds;
        public readonly List<string> Unparsed = new();

        private static readonly Regex ClsRe = new(@"^class (\w+)( final)?(?: : (.*))? \{$");

        public Oracle(string root)
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Files[Path.GetRelativePath(root, f).Replace('\\', '/')] = File.ReadAllText(f).Split('\n');
            ParseClasses();
            ParseBodies();
            Analyze();
            Solve();
        }

        private void ParseClasses()
        {
            foreach (var (rel, ls) in Files)
            {
                if (!rel.EndsWith(".hpp", StringComparison.Ordinal) || rel.Contains("/rt/") || rel.Contains("/tmpl/")) continue;
                string ns = rel.Split('/')[1];
                Cls? cur = null;
                foreach (var l in ls)
                {
                    var m = ClsRe.Match(l);
                    if (m.Success)
                    {
                        cur = Classes[$"{ns}::{m.Groups[1].Value}"] = new Cls { Name = m.Groups[1].Value };
                        if (m.Groups[3].Success) cur.Bases = m.Groups[3].Value.Split(',').Select(b => b.Replace("public ", "").Trim()).ToList();
                        continue;
                    }
                    if (cur is null) continue;
                    if (l == "};") { cur = null; continue; }
                    var mm = Regex.Match(l, @"^    (virtual )?void (\w+)\(\)( override)?( final)?( = 0)?");
                    if (mm.Success) cur.Methods[mm.Groups[2].Value] = (mm.Groups[1].Success || mm.Groups[3].Success, mm.Groups[5].Success);
                    mm = Regex.Match(l, @"^    ([\w:]+) mem_\d+;");
                    if (mm.Success) cur.Members.Add(mm.Groups[1].Value);
                }
            }
            foreach (var q in Classes.Keys) Children[q] = new();
            foreach (var (q, c) in Classes) foreach (var b in c.Bases) Children[b].Add(q);
        }

        public string? Resolve(string q, string n)
        {
            if (Classes[q].Methods.ContainsKey(n)) return q;
            foreach (var b in Classes[q].Bases) if (Resolve(b, n) is { } r) return r;
            return null;
        }

        private bool IsVirtual(string q, string n) => Resolve(q, n) is { } r && Classes[r].Methods[n].Virtual;

        public List<string> Subtree(string q)
        {
            var out_ = new List<string>();
            var st = new Stack<string>();
            st.Push(q);
            while (st.Count > 0)
            {
                var x = st.Pop();
                if (out_.Contains(x)) continue;
                out_.Add(x);
                for (int i = Children[x].Count - 1; i >= 0; i--) st.Push(Children[x][i]);
            }
            return out_;
        }

        private IEnumerable<string> Ancestors(string q)
        {
            foreach (var b in Classes[q].Bases) { yield return b; foreach (var a in Ancestors(b)) yield return a; }
        }

        private void AddBody(string key, string rel, int start, string[] ls, int indent)
        {
            var stmts = new List<(string, int, string)>();
            string close = new string(' ', indent) + "}";
            for (int i = start + 1; ls[i] != close; i++) stmts.Add((rel, i + 1, ls[i].Trim()));
            Bodies[key] = stmts;
        }

        private void ParseBodies()
        {
            foreach (var (rel, ls) in Files)
            {
                var parts = rel.Split('/');
                if (rel.EndsWith("free.cpp", StringComparison.Ordinal))
                {
                    for (int i = 0; i < ls.Length; i++)
                        if (Regex.Match(ls[i], @"^void (free_\d+)\(\) \{$") is { Success: true } m) AddBody($"{parts[1]}::{m.Groups[1].Value}", rel, i, ls, 0);
                }
                else if (Regex.IsMatch(rel, @"^cpp/ns_\d+/cls_\d+\.cpp$"))
                {
                    for (int i = 0; i < ls.Length; i++)
                        if (Regex.Match(ls[i], @"^(?:void )?(Cls_\d+)::(~?\w+)\(\) \{$") is { Success: true } m)
                            AddBody($"{parts[1]}::{m.Groups[1].Value}::{m.Groups[2].Value}", rel, i, ls, 0);
                }
                else if (Regex.IsMatch(rel, @"^cpp/ns_\d+/cls_\d+\.hpp$"))
                {
                    string? cname = null;
                    for (int i = 0; i < ls.Length; i++)
                    {
                        if (ClsRe.Match(ls[i]) is { Success: true } c) cname = c.Groups[1].Value;
                        if (cname is not null && Regex.Match(ls[i], @"^    (?:virtual )?(?:void )?(~?\w+)\(\)(?: override)?(?: final)? \{$") is { Success: true } m)
                            AddBody($"{parts[1]}::{cname}::{m.Groups[1].Value}", rel, i, ls, 4);
                    }
                }
                else if (rel.StartsWith("cpp/tmpl/", StringComparison.Ordinal))
                {
                    string? ns = null, tname = null;
                    for (int i = 0; i < ls.Length; i++)
                    {
                        if (Regex.Match(ls[i], @"^namespace (\w+)") is { Success: true } n) ns = n.Groups[1].Value;
                        if (Regex.Match(ls[i], @"^struct (Tmpl_\d+)") is { Success: true } t) tname = t.Groups[1].Value;
                        if (Regex.Match(ls[i], @"^void (tf_\d+)\(\) \{$") is { Success: true } f) AddBody($"TMPL:{ns}::{f.Groups[1].Value}", rel, i, ls, 0);
                        if (ls[i] == "    void m_0() {") AddBody($"TMPL:{ns}::{tname}::m_0", rel, i, ls, 4);
                    }
                }
                else if (rel == "cpp/rt/rt.cpp")
                {
                    for (int i = 0; i < ls.Length; i++)
                        if (Regex.Match(ls[i], @"^void (mp_\w+)\(") is { Success: true } m) AddBody($"cs_rt::{m.Groups[1].Value}", rel, i, ls, 0);
                }
            }
        }

        private static string TmplInst(string t, string arg)
        {
            int i = t.IndexOf("::Tmpl_", StringComparison.Ordinal);
            return i >= 0 ? $"{t[..t.LastIndexOf("::m_0", StringComparison.Ordinal)]}<{arg}>::m_0" : $"{t}<{arg}>";
        }

        private string ClassBySimple(string simple) => Classes.First(kv => kv.Value.Name == simple).Key;

        private Body AnalyzeBody(List<(string Rel, int Line, string Text)> stmts, string? targ)
        {
            var r = new Body();
            var tv = new Dictionary<string, (string, string)>();
            string? tbl = null;
            var tblTargets = new List<string>();
            foreach (var (rel, ln, t) in stmts)
            {
                string site = $"{rel}:{ln}";
                Match m;
                if ((m = Regex.Match(t, @"^(ns_\d+)::(free_\d+)\(\);$")).Success)
                { string s = $"{m.Groups[1].Value}::{m.Groups[2].Value}"; r.Edges.Add(s); r.Refs.Add((s, site)); continue; }
                if ((m = Regex.Match(t, @"^gp_(\w+)->(\w+)\(\);$")).Success)
                {
                    string q = ClassBySimple(m.Groups[1].Value), n = m.Groups[2].Value;
                    string s = $"{Resolve(q, n)}::{n}";
                    if (IsVirtual(q, n)) r.Virt.Add((q, n)); else r.Edges.Add(s);
                    r.Refs.Add((s, site));
                    continue;
                }
                if ((m = Regex.Match(t, @"^(ns_\d+::(?:Cls|Mix)_\d+)::(\w+)\(\);$")).Success)
                { string s = $"{m.Groups[1].Value}::{m.Groups[2].Value}"; r.Edges.Add(s); r.Refs.Add((s, site)); continue; }
                if ((m = Regex.Match(t, @"^(ns_\d+)::(Cls_\d+) obj_\d+;$")).Success)
                {
                    string q = $"{m.Groups[1].Value}::{m.Groups[2].Value}", n = m.Groups[2].Value;
                    r.Edges.Add($"{q}::{n}"); r.Implicit.Add($"{q}::~{n}"); r.Cons.Add(q); r.Refs.Add(($"{q}::{n}", site));
                    continue;
                }
                if ((m = Regex.Match(t, @"^gp_\w+ = new (ns_\d+)::(Cls_\d+)\(\);$")).Success)
                {
                    string q = $"{m.Groups[1].Value}::{m.Groups[2].Value}", n = m.Groups[2].Value;
                    r.Edges.Add($"{q}::{n}"); r.Cons.Add(q); r.Refs.Add(($"{q}::{n}", site));
                    continue;
                }
                if ((m = Regex.Match(t, @"^(ns_\d+::tf_\d+)<(\w+)>\(\);$")).Success)
                {
                    string? arg = m.Groups[2].Value == "T" ? targ : m.Groups[2].Value;
                    r.TCalls.Add((m.Groups[1].Value, m.Groups[2].Value));
                    if (arg is not null) { string s = TmplInst(m.Groups[1].Value, arg); r.Edges.Add(s); r.Refs.Add((s, site)); }
                    else r.Refs.Add((m.Groups[1].Value, site));                 // unused template: a ref of the template
                    continue;
                }
                if ((m = Regex.Match(t, @"^(ns_\d+::Tmpl_\d+)<(\w+)> (tv_\d+);$")).Success)
                { tv[m.Groups[3].Value] = (m.Groups[1].Value + "::m_0", m.Groups[2].Value); r.TCalls.Add(tv[m.Groups[3].Value]); continue; }
                if ((m = Regex.Match(t, @"^(tv_\d+)\.m_0\(\);$")).Success)
                {
                    var (tq, a) = tv[m.Groups[1].Value];
                    string? arg = a == "T" ? targ : a;
                    if (arg is not null) { string s = TmplInst(tq, arg); r.Edges.Add(s); r.Refs.Add((s, site)); }
                    else r.Refs.Add((tq, site));
                    continue;
                }
                if ((m = Regex.Match(t, @"^static void \(([\w:]+)::\* const (tbl_\d+)\[\]\)\(\) = \{$")).Success)
                { tbl = m.Groups[2].Value; tblTargets = new(); continue; }
                if (tbl is not null && (m = Regex.Match(t, @"^&([\w:]+)::(\w+),$")).Success)
                { string s = $"{Resolve(m.Groups[1].Value, m.Groups[2].Value)}::{m.Groups[2].Value}"; tblTargets.Add(s); r.Refs.Add((s, site)); continue; }
                if (t == "};" && tbl is not null) continue;
                if (Regex.IsMatch(t, @"^for \(auto f : (tbl_\d+)\) \(gp_\w+->\*f\)\(\);$"))
                { r.Mp.AddRange(tblTargets.Select(x => (x, "table", true))); tbl = null; continue; }
                if (Regex.IsMatch(t, @"^\(void\)(tbl_\d+);$"))
                { r.Mp.AddRange(tblTargets.Select(x => (x, "table", false))); tbl = null; continue; }
                if ((m = Regex.Match(t, @"^cs_rt::(mp_(run|keep)_\w+)\(&([\w:]+)::(\w+)\);$")).Success)
                {
                    string h = "cs_rt::" + m.Groups[1].Value; r.Edges.Add(h); r.Refs.Add((h, site));
                    string s = $"{m.Groups[3].Value}::{m.Groups[4].Value}";
                    r.Mp.Add((s, "arg", m.Groups[2].Value == "run")); r.Refs.Add((s, site));
                    continue;
                }
                if (Regex.IsMatch(t, @"^\(gp_\w+->\*f\)\(\);$") || Regex.IsMatch(t, @"^kept_\w+ = f;$")) continue;
                Unparsed.Add($"{site} {t}");
            }
            return r;
        }

        private void Analyze()
        {
            foreach (var (k, v) in Bodies) if (!k.StartsWith("TMPL:", StringComparison.Ordinal)) All[k] = AnalyzeBody(v, null);
            // Template instances: seeded by the concrete uses in non-template bodies, then T forwarded transitively.
            var inst = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var work = new Stack<(string, string)>();
            void Seed(string t, string a)
            {
                if (!inst.TryGetValue(t, out var s)) inst[t] = s = new(StringComparer.Ordinal);
                if (s.Add(a)) work.Push((t, a));
            }
            foreach (var r in All.Values.ToList()) foreach (var (t, a) in r.TCalls) if (a != "T") Seed(t, a);
            while (work.Count > 0)
            {
                var (t, a) = work.Pop();
                foreach (var (tt, aa) in AnalyzeBody(Bodies["TMPL:" + t], a).TCalls) Seed(tt, aa == "T" ? a : aa);
            }
            foreach (var (t, args) in inst) foreach (var a in args) All[TmplInst(t, a)] = AnalyzeBody(Bodies["TMPL:" + t], a);
            foreach (var (k, v) in Bodies)
                if (k.StartsWith("TMPL:", StringComparison.Ordinal) && !inst.ContainsKey(k[5..]))
                    All["UNUSED:" + k] = new Body { Refs = AnalyzeBody(v, null).Refs };

            List<string> I(string k) { if (!Imp.TryGetValue(k, out var l)) Imp[k] = l = new(); return l; }
            foreach (var (q, c) in Classes)
            {
                if (c.Name.StartsWith("Mix_", StringComparison.Ordinal)) continue;
                string n = c.Name;
                var concrete = c.Bases.Where(b => !Classes[b].Name.StartsWith("Mix_", StringComparison.Ordinal)).ToList();
                foreach (var b in concrete) I($"{q}::{n}").Add($"{b}::{Classes[b].Name}");
                foreach (var mq in c.Members) I($"{q}::{n}").Add($"{mq}::{Classes[mq].Name}");
                foreach (var mq in Enumerable.Reverse(c.Members)) I($"{q}::~{n}").Add($"{mq}::~{Classes[mq].Name}");
                foreach (var b in concrete) I($"{q}::~{n}").Add($"{b}::~{Classes[b].Name}");
            }
            foreach (var (k, r) in All) I(k).AddRange(r.Implicit);

            foreach (var l in Files["cpp/rt/static_init.cpp"])
                if (Regex.Match(l, @"^([\w:]+) g_static_\d+;$") is { Success: true } m) Statics.Add(m.Groups[1].Value);
            foreach (var q in Statics) { string n = Classes[q].Name; SRoots.Add($"{q}::{n}"); SRoots.Add($"{q}::~{n}"); }
            Roots.Add(Regex.Match(Files["cpp/rt/main.cpp"][2], @"^    ([\w:]+)\(\);$").Groups[1].Value);
        }

        public List<string> Cha(string q, string n, Func<string, bool>? keep = null)
        {
            var out_ = new List<string>();
            foreach (var c in Subtree(q))
            {
                if (keep is not null && !keep(c)) continue;
                if (Resolve(c, n) is { } r && !Classes[r].Methods[n].Pure && !out_.Contains($"{r}::{n}")) out_.Add($"{r}::{n}");
            }
            return out_;
        }

        public List<string> Rta(string q, string n) => Cha(q, n, Live.Contains);

        private HashSet<string> Closure(bool precise)
        {
            var seen = new HashSet<string>(Roots.Concat(SRoots), StringComparer.Ordinal);
            var st = new Stack<string>(seen);
            while (st.Count > 0)
            {
                string k = st.Pop();
                var nxt = new List<string>(Imp.GetValueOrDefault(k) ?? new());
                if (All.TryGetValue(k, out var r))
                {
                    nxt.AddRange(r.Edges);
                    foreach (var (q, n) in r.Virt) nxt.AddRange(precise ? Rta(q, n) : Cha(q, n));
                    nxt.AddRange(r.Mp.Where(x => x.Dispatched || !precise).Select(x => x.Target));
                }
                foreach (var x in nxt) if (seen.Add(x)) st.Push(x);
            }
            return seen;
        }

        private void MakeLive(string q)
        {
            if (!Live.Add(q)) return;
            foreach (var x in Ancestors(q).Prepend(q)) foreach (var mq in Classes[x].Members) MakeLive(mq);
        }

        private void Solve()
        {
            foreach (var q in Statics) MakeLive(q);
            while (true)
            {
                Rounds++;
                Precise = Closure(true);
                int before = Live.Count;
                foreach (var k in Precise) if (All.TryGetValue(k, out var r)) foreach (var q in r.Cons) MakeLive(q);
                if (Live.Count == before) break;
            }
            Sound = Closure(false);
        }
    }

    private static (string corpus, JsonObject root) Gen(TempDir tmp, GenOptions o)
    {
        new CppGenerator(o).Run();
        return (o.Out, JsonNode.Parse(File.ReadAllText(o.Out + "-manifest.json"))!.AsObject());
    }

    private static GenOptions Opts(string outDir, int seed) => new() { Out = outDir, Seed = seed, Lang = SourceLang.Cpp };

    private static List<string> Strs(JsonNode? n) => n is JsonArray a ? a.Select(x => (string)x!).ToList() : new();
    private static string Sorted(IEnumerable<string> xs) => string.Join(", ", xs.Distinct().Order(StringComparer.Ordinal));

    public static IEnumerable<object[]> Shapes()
    {
        yield return new object[] { "s1", (Action<GenOptions>)(o => o.Seed = 1) };
        yield return new object[] { "s3", (Action<GenOptions>)(o => o.Seed = 3) };
        yield return new object[] { "s1337", (Action<GenOptions>)(o => o.Seed = 1337) };
        yield return new object[] { "r5", (Action<GenOptions>)(o => { o.Seed = 8; o.CppDepth = 0; o.CppVirtualFrac = 0; o.CppInstantiatedFrac = 0; }) };
        yield return new object[] { "one", (Action<GenOptions>)(o => { o.Seed = 2; o.CppClasses = 1; o.CppStaticInit = 5; }) };
        yield return new object[] { "t1", (Action<GenOptions>)(o => o.CppTemplates = 1) };
        yield return new object[] { "big", (Action<GenOptions>)(o => { o.Seed = 9; o.CppClasses = 60; o.CppInstantiatedFrac = 1; o.OracleChain = 40; }) };
        yield return new object[] { "deep", (Action<GenOptions>)(o => { o.Seed = 4; o.CppClasses = 40; o.CppDepth = 6; o.CppMultiInherit = 0.8; }) };
        yield return new object[] { "dag", (Action<GenOptions>)(o => { o.Seed = 6; o.OracleFanout = 3; o.OracleChain = 30; o.OracleSharedLeaves = 4; o.OracleReachableFrac = 0.5; }) };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Manifest_EqualsRecomputationFromSource(string name, Action<GenOptions> set)
    {
        using var tmp = new TempDir();
        var o = Opts(Path.Combine(tmp.Path, name), 1337);
        set(o);
        var (corpus, root) = Gen(tmp, o);
        var meta = root["_meta"]!.AsObject();
        var syms = root["symbols"]!.AsObject();
        var or = new Oracle(corpus);
        var bad = new List<string>(or.Unparsed.Select(u => "UNPARSED " + u));

        // The symbol set: every body, pure declaration, class, ctor/dtor and template.
        var want = or.All.Keys.Where(k => !k.StartsWith("UNUSED:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        want.UnionWith(or.Bodies.Keys.Where(k => !k.StartsWith("TMPL:", StringComparison.Ordinal)));
        want.UnionWith(or.Bodies.Keys.Where(k => k.StartsWith("TMPL:", StringComparison.Ordinal)).Select(k => k[5..]));
        foreach (var (q, c) in or.Classes)
        {
            want.Add(q);
            want.UnionWith(c.Methods.Where(m => m.Value.Pure).Select(m => $"{q}::{m.Key}"));
            if (!c.Name.StartsWith("Mix_", StringComparison.Ordinal)) { want.Add($"{q}::{c.Name}"); want.Add($"{q}::~{c.Name}"); }
        }
        var got = syms.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (!want.SetEquals(got)) bad.Add($"SYMS missing [{Sorted(want.Except(got))}] extra [{Sorted(got.Except(want))}]");

        // Classes and their bases in order.
        var mc = meta["classes"]!.AsObject();
        foreach (var (q, c) in or.Classes)
            if (mc[q] is not JsonArray b || !Strs(b).SequenceEqual(c.Bases)) bad.Add($"CLASSES {q}: [{string.Join(", ", c.Bases)}] vs [{string.Join(", ", Strs(mc[q]))}]");

        // Refs from the bodies, the static objects and main.
        var refs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void R(string s, string site) { if (!refs.TryGetValue(s, out var h)) refs[s] = h = new(StringComparer.Ordinal); h.Add(site); }
        foreach (var r in or.All.Values) foreach (var (s, site) in r.Refs) R(s, site);
        for (int i = 0; i < or.Statics.Count; i++) R($"{or.Statics[i]}::{or.Classes[or.Statics[i]].Name}", $"cpp/rt/static_init.cpp:{i + 2}");
        R(or.Roots[0], "cpp/rt/main.cpp:3");

        foreach (var (k, node) in syms)
        {
            var s = node!.AsObject();
            or.All.TryGetValue(k, out var r);
            if (r is not null && Sorted(r.Edges) != Sorted(Strs(s["edges"]))) bad.Add($"EDGES {k}: [{Sorted(r.Edges)}] vs [{Sorted(Strs(s["edges"]))}]");
            if (Sorted(or.Imp.GetValueOrDefault(k) ?? new()) != Sorted(Strs(s["implicitEdges"])))
                bad.Add($"IMPLICIT {k}: [{Sorted(or.Imp.GetValueOrDefault(k) ?? new())}] vs [{Sorted(Strs(s["implicitEdges"]))}]");
            var mine = (r?.Virt ?? new()).Select(v => $"{or.Resolve(v.Q, v.N)}::{v.N} cha[{Sorted(or.Cha(v.Q, v.N))}] rta[{Sorted(or.Rta(v.Q, v.N))}]");
            var theirs = (s["virtualEdges"] as JsonArray ?? new()).Select(v => $"{v!["static"]} cha[{Sorted(Strs(v["cha"]))}] rta[{Sorted(Strs(v["rta"]))}]");
            if (Sorted(mine) != Sorted(theirs)) bad.Add($"VIRT {k}: {Sorted(mine)} vs {Sorted(theirs)}");
            var mmp = (r?.Mp ?? new()).Select(x => $"{x.Target}/{x.Via}/{x.Dispatched}");
            var tmp2 = (s["memberPtrEdges"] as JsonArray ?? new()).Select(x => $"{x!["target"]}/{x["via"]}/{(bool)x["dispatched"]!}");
            if (Sorted(mmp) != Sorted(tmp2)) bad.Add($"MP {k}: {Sorted(mmp)} vs {Sorted(tmp2)}");
            if ((string?)s["kind"] != "class")
            {
                var w = refs.GetValueOrDefault(k) ?? new();
                var g = Strs(s["refs"]);
                if (!w.SetEquals(g)) bad.Add($"REFS {k}: missing [{Sorted(w.Except(g))}] extra [{Sorted(g.Except(w))}]");
                if (g.Count != g.Distinct().Count()) bad.Add($"DUPREF {k}");
                if (g.Contains((string)s["def"]!)) bad.Add($"DEFINREFS {k}");
            }
        }
        if (Sorted(or.Roots) != Sorted(Strs(meta["roots"]))) bad.Add($"ROOTS [{Sorted(or.Roots)}] vs [{Sorted(Strs(meta["roots"]))}]");
        if (Sorted(or.SRoots) != Sorted(Strs(meta["staticInitRoots"]))) bad.Add($"SROOTS [{Sorted(or.SRoots)}] vs [{Sorted(Strs(meta["staticInitRoots"]))}]");

        // Both closures, recomputed over the manifest's own edges, against the source's.
        HashSet<string> MClosure(bool precise)
        {
            var seen = Strs(meta["roots"]).Concat(Strs(meta["staticInitRoots"])).ToHashSet(StringComparer.Ordinal);
            var st = new Stack<string>(seen);
            while (st.Count > 0)
            {
                var s = syms[st.Pop()]!;
                var nxt = Strs(s["edges"]).Concat(Strs(s["implicitEdges"])).ToList();
                foreach (var v in s["virtualEdges"] as JsonArray ?? new()) nxt.AddRange(Strs(v![precise ? "rta" : "cha"]));
                foreach (var x in s["memberPtrEdges"] as JsonArray ?? new()) if ((bool)x!["dispatched"]! || !precise) nxt.Add((string)x["target"]!);
                foreach (var x in nxt) if (seen.Add(x)) st.Push(x);
            }
            return seen;
        }
        foreach (var (what, mine, theirs) in new[] { ("sound", or.Sound, MClosure(false)), ("precise", or.Precise, MClosure(true)) })
            if (!mine.SetEquals(theirs)) bad.Add($"REACH {what}: missing [{Sorted(mine.Except(theirs))}] extra [{Sorted(theirs.Except(mine))}]");

        // The generator's own model (what the other tests reason with) agrees too.
        var p = CppProgram.Build(o);
        CppEmitter.Emit(p);
        var t = CppTruth.Build(p);
        foreach (var (what, mine, theirs) in new[] { ("model sound", or.Sound, t.SoundReach), ("model precise", or.Precise, t.PreciseReach),
                                                    ("model live", or.Live, t.Live.Select(c => c.Q).ToHashSet()) })
            if (!mine.SetEquals(theirs)) bad.Add($"{what}: missing [{Sorted(mine.Except(theirs))}] extra [{Sorted(theirs.Except(mine))}]");

        Assert.True(bad.Count == 0, $"{name}:\n" + string.Join("\n", bad.Take(30)));
    }

    // The RTA fixpoint really iterates: a construction only reachable through a dispatch to a class made live by an
    // earlier round. 1337 has one (the dispatch-only construction); the recompute agrees on the live set.
    [Fact]
    public void Rta_FixpointTakesMoreThanOneRound()
    {
        using var tmp = new TempDir();
        var (corpus, _) = Gen(tmp, Opts(Path.Combine(tmp.Path, "c"), 1337));
        var or = new Oracle(corpus);
        Assert.True(or.Rounds >= 3, $"rounds {or.Rounds}");
        var p = CppProgram.Build(Opts("x", 1337));
        CppEmitter.Emit(p);
        var t = CppTruth.Build(p);
        Assert.True(t.Rounds >= 3, $"generator rounds {t.Rounds}");
        Assert.Equal(or.Live.Order(StringComparer.Ordinal), t.Live.Select(c => c.Q).Order(StringComparer.Ordinal));
    }

    // Every whole-word occurrence of a symbol's simple name is a def or ref of some symbol with that name, and every
    // def/ref site names it. Excluded: #include lines, bodiless in-class declarations (not = 0), the prototypes in
    // rt/free.hpp and rt/helpers.hpp, and the Tmpl_k class-template name (not a symbol).
    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(8)] [InlineData(1337)]
    public void Refs_AreEveryOccurrenceOfTheName(int seed)
    {
        using var tmp = new TempDir();
        var (corpus, root) = Gen(tmp, Opts(Path.Combine(tmp.Path, "c"), seed));
        var syms = root["symbols"]!.AsObject();
        var lines = Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(corpus, f).Replace('\\', '/'), f => File.ReadAllText(f).Split('\n'));
        var decl = new Regex(@"^\s*((virtual )?void \w+\(\)( override( final)?)?|~?\w+\(\));$");
        // occ: the occurrences a symbol must cover; any: every occurrence (what a def or ref may point at).
        var occ = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var any = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (rel, ls) in lines)
            for (int i = 0; i < ls.Length; i++)
            {
                string l = ls[i];
                if (l.StartsWith("#include", StringComparison.Ordinal)) continue;
                bool excluded = rel is "cpp/rt/free.hpp" or "cpp/rt/helpers.hpp"
                                || (decl.IsMatch(l) && !l.TrimEnd().EndsWith("= 0;", StringComparison.Ordinal));
                foreach (Match w in Regex.Matches(l, @"~?[A-Za-z_]\w*").Concat(Regex.Matches(l, @"[A-Za-z_]\w*")))
                    foreach (var d in excluded ? new[] { any } : new[] { any, occ })
                    {
                        if (!d.TryGetValue(w.Value, out var h)) d[w.Value] = h = new(StringComparer.Ordinal);
                        h.Add($"{rel}:{i + 1}");
                    }
            }
        var man = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var bad = new List<string>();
        foreach (var (k, node) in syms)
        {
            string sn = (string)node!["simpleName"]!;
            if (!man.TryGetValue(sn, out var h)) man[sn] = h = new(StringComparer.Ordinal);
            foreach (var site in Strs(node["refs"]).Prepend((string)node["def"]!))
            {
                h.Add(site);
                int c = site.LastIndexOf(':');
                string l = lines[site[..c]][int.Parse(site[(c + 1)..]) - 1];
                // A class's name is a whole word even after a dtor's '~'; a ctor's (the same name) is not.
                string left = (string?)node["kind"] == "class" ? @"(?<!\w)" : @"(?<![\w~])";
                if (!Regex.IsMatch(l, $@"{left}{Regex.Escape(sn)}(?!\w)")) bad.Add($"BADLINE {k} {site}: {l.Trim()}");
            }
        }
        foreach (var (sn, h) in man)
        {
            var o = occ.GetValueOrDefault(sn) ?? new();
            var a = any.GetValueOrDefault(sn) ?? new();
            if (o.Except(h).Any()) bad.Add($"MISSING {sn}: {Sorted(o.Except(h))}");
            if (h.Except(a).Any()) bad.Add($"EXTRA {sn}: {Sorted(h.Except(a))}");
        }
        var names = man.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var w in occ.Keys.Where(w => Regex.IsMatch(w, "^(free_|m_|tf_|Cls_|Mix_|hook_|mp_|~Cls)") && !names.Contains(w)))
            bad.Add($"NOSYM {w}");
        Assert.True(bad.Count == 0, $"seed {seed}:\n" + string.Join("\n", bad.Take(30)));
    }
}
