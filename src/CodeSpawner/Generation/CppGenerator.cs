using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;

namespace CodeSpawner.Generation;

/// <summary>
/// <c>gen --lang cpp</c>: a self-contained C++ program (namespaces, single + multiple inheritance, virtual / pure /
/// final methods, ctors/dtors, static-storage objects, function and class templates, member-function pointers) with
/// a manifest v2 recording its known call graph: direct <c>edges</c>, compiler-added <c>implicitEdges</c>,
/// <c>virtualEdges</c> with the sound (cha) and precise (rta) target sets, and <c>memberPtrEdges</c>. The truth is
/// computed from the program model, never placed by hand. See docs/cpp-profile-design.md.
/// </summary>
public sealed class CppGenerator
{
    private readonly GenOptions _o;
    public CppGenerator(GenOptions o) => _o = o;

    public void Run()
    {
        var sw = Stopwatch.StartNew();
        string outFull = new DirectoryInfo(_o.Out).FullName;
        string mpath = CorpusGenerator.ManifestPath(outFull);
        CorpusGenerator.ProbeManifestWritable(mpath, "choose another --out");
        OutputDir.Prepare(_o.Out, _o.Force);
        Console.WriteLine($"Writing a C++ program at {outFull} (seed {_o.Seed}, {_o.CppClasses} classes, {_o.CppTemplates} templates) ...");

        var prog = CppProgram.Build(_o);
        var files = CppEmitter.Emit(prog);
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(outFull, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }

        var truth = CppTruth.Build(prog);
        var model = truth.ToManifest(_o, outFull);
        ManifestWriter.Write(model, mpath);
        Console.WriteLine($"  {files.Count} files, {model.Symbols.Count} symbols; reachable: {truth.SoundReach.Count} sound, " +
                          $"{truth.PreciseReach.Count} precise -> {mpath}");
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:N1}s.");
    }
}

internal enum FnKind { Free, Method, Ctor, Dtor, TmplFn, TmplMethod, Helper }

/// <summary>One body (or, for a pure virtual, its declaration): a free function, method, ctor, dtor, template body,
/// or a member-pointer helper.</summary>
internal sealed class CppFn
{
    public required string Name { get; init; }      // simple name
    public required FnKind Kind { get; init; }
    public CppClass? Owner { get; init; }
    public CppTemplate? Tmpl { get; init; }
    public string Ns { get; init; } = "";
    public bool Virtual, Pure, Final, Override;
    /// <summary>Free functions: designated live (graph-reachable) or dead.</summary>
    public bool Live;
    /// <summary>Helpers: the class whose member pointer they take, and whether they call through it.</summary>
    public CppClass? MpClass;
    public bool MpRun;
    public List<Stmt> Body { get; } = new();
    public string DefSite = "";

    public string Symbol => Kind switch
    {
        FnKind.Free => $"{Ns}::{Name}",
        FnKind.Helper => $"cs_rt::{Name}",
        FnKind.TmplFn or FnKind.TmplMethod => Tmpl!.SymbolName,
        _ => $"{Owner!.Q}::{Name}",
    };

    public string ManifestKind => Kind switch
    {
        FnKind.Free or FnKind.Helper => "function",
        FnKind.Method => "method",
        FnKind.Ctor => "ctor",
        FnKind.Dtor => "dtor",
        _ => "template",
    };
}

internal sealed class CppClass
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Ns { get; init; }
    public bool IsMixin { get; init; }
    public CppClass? Base, Mixin;
    public bool Final, Inline, Abstract, Instantiated;
    public int Depth;
    public List<CppFn> Methods { get; } = new();
    public List<CppClass> Members { get; } = new();
    public List<CppClass> Children { get; } = new();
    public CppFn? Ctor, Dtor;
    /// <summary>The <c>class Name … {</c> line: the def of the class symbol.</summary>
    public string DefSite = "";
    public string Q => $"{Ns}::{Name}";
    public string Stem => IsMixin ? $"mix_{Index}" : $"cls_{Index}";
    public string Header => $"cpp/{Ns}/{Stem}.hpp";

    /// <summary>The method <paramref name="name"/> resolves to from this class: own declaration, then the primary
    /// base chain, then the mixin. Primary-chain and mixin names never overlap, so the order is unambiguous.</summary>
    public CppFn? Resolve(string name)
    {
        foreach (var m in Methods) if (m.Name == name) return m;
        return Base?.Resolve(name) ?? Mixin?.Resolve(name);
    }

    public IEnumerable<CppClass> Ancestors()
    {
        if (Base is not null) { yield return Base; foreach (var a in Base.Ancestors()) yield return a; }
        if (Mixin is not null) { yield return Mixin; foreach (var a in Mixin.Ancestors()) yield return a; }
    }

    /// <summary>This class and every class below it (through primary or mixin bases), in index order of discovery.</summary>
    public List<CppClass> Subtree()
    {
        var seen = new HashSet<CppClass>();
        var order = new List<CppClass>();
        var stack = new Stack<CppClass>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c)) continue;
            order.Add(c);
            for (int i = c.Children.Count - 1; i >= 0; i--) stack.Push(c.Children[i]);
        }
        return order;
    }

    /// <summary>Every virtual method name visible from this class.</summary>
    public List<string> VirtualNames()
    {
        var names = new List<string>();
        void Add(CppClass? c)
        {
            if (c is null) return;
            Add(c.Base);
            Add(c.Mixin);
            foreach (var m in c.Methods) if (m.Virtual && !names.Contains(m.Name)) names.Add(m.Name);
        }
        Add(this);
        return names;
    }
}

internal sealed class CppTemplate
{
    public required int Index { get; init; }
    public required bool IsClass { get; init; }
    public required string Ns { get; init; }
    public required string Name { get; init; }      // tf_k or Tmpl_k
    public CppFn Body = null!;
    public string File => $"cpp/tmpl/tmpl_{Index}.hpp";
    public string SymbolName => IsClass ? $"{Ns}::{Name}::m_0" : $"{Ns}::{Name}";
    public string InstanceName(string t) => IsClass ? $"{Ns}::{Name}<{t}>::m_0" : $"{Ns}::{Name}<{t}>";
}

internal enum StmtKind { CallFree, CallMethod, CallVirtual, SuperCall, ConstructLocal, ConstructNew, CallTmpl, MemberPtrTable, MemberPtrArg }

internal sealed class Stmt
{
    public required StmtKind Kind { get; init; }
    public CppFn? Target { get; init; }            // CallFree / CallMethod / SuperCall; the helper for MemberPtrArg
    public CppClass? Cls { get; init; }            // receiver static type / constructed class / member-pointer class
    public string? Method { get; init; }           // CallVirtual
    public CppTemplate? Tmpl { get; init; }
    public string? TypeArg { get; init; }          // CallTmpl; "T" inside a template body
    public List<CppFn> MpTargets { get; init; } = new();
    public bool Dispatched { get; init; }
    // Filled by the emitter:
    public string File = "";
    public int Line;                               // the line naming the referenced symbol
    public List<int> MpLines { get; } = new();     // MemberPtrTable: one line per target
}

/// <summary>The program model. Built from one RNG stream, so the same knobs + seed give the same program.</summary>
internal sealed class CppProgram
{
    /// <summary>Type arguments, in order; the last ("bool") is reserved for the dead-only instance of template 0.</summary>
    public static readonly string[] TypeArgs = { "int", "long", "short", "char", "unsigned", "bool" };
    private const string DeadOnlyArg = "bool";

    public List<string> Namespaces { get; } = new();
    public List<CppClass> Classes { get; } = new();
    public List<CppClass> Mixins { get; } = new();
    public List<CppFn> Free { get; } = new();
    public CppFn Root = null!;
    public List<CppTemplate> Templates { get; } = new();
    public List<CppFn> Helpers { get; } = new();
    public List<CppClass> StaticObjs { get; } = new();
    /// <summary>Filled by the emitter: the line of each static object in static_init.cpp, and main's call to the root.</summary>
    public List<int> StaticInitLines { get; } = new();
    /// <summary>The emitted files (set by <see cref="CppEmitter.Emit"/>); class refs are read from them.</summary>
    public List<(string Rel, string Text)> Files = new();
    public int MainLine;

    private Rng _rng;
    private double U() => (_rng.NextULong() >> 11) * (1.0 / (1UL << 53));
    private T Pick<T>(List<T> xs) => xs[_rng.Next(xs.Count)];

    public static CppProgram Build(GenOptions o)
    {
        var p = new CppProgram { _rng = Rng.For(o.Seed, Category.CppGen, 0) };
        int ns = Math.Min(3, Math.Max(1, o.CppClasses / 4));
        for (int i = 0; i < ns; i++) p.Namespaces.Add($"ns_{i}");
        p.BuildFree(o);
        p.BuildClasses(o);
        p.BuildTemplates(o);
        p.Wire(o);
        return p;
    }

    private string NsOf(int i) => Namespaces[i % Namespaces.Count];
    private List<CppFn> LiveFree => Free.Where(f => f.Live).ToList();
    private List<CppFn> DeadFree => Free.Where(f => !f.Live).ToList();

    // Free functions: the oracle graph (linear chain or fanout DAG) is the live part; a dead block of short chains
    // follows, sized by --oracle-reachable-frac (default: a quarter of the live count, at least 2).
    private void BuildFree(GenOptions o)
    {
        var go = new GenOptions
        {
            Out = "", Seed = o.Seed, OracleChain = o.OracleChain, OracleFanout = o.OracleFanout,
            OracleDepth = o.OracleDepth, OracleSharedLeaves = o.OracleSharedLeaves,
        };
        var g = OracleGraph.Build(go);
        int live = g.Count;
        double frac = o.OracleReachableFrac;
        int dead = frac == 0 ? Math.Max(2, live / 4)
            : frac >= 1 ? 0 : Math.Max(0, (int)Math.Round(live / frac, MidpointRounding.AwayFromZero) - live);
        for (int i = 0; i < live + dead; i++)
            Free.Add(new CppFn { Name = $"free_{i}", Kind = FnKind.Free, Ns = NsOf(i), Live = i < live });
        for (int i = 0; i < live; i++)
            foreach (int c in g.Children[i]) Free[i].Body.Add(new Stmt { Kind = StmtKind.CallFree, Target = Free[c] });
        for (int j = 1; j < dead; j++)
            if (j % 4 != 0) Free[live + j].Body.Add(new Stmt { Kind = StmtKind.CallFree, Target = Free[live + j - 1] });
        Root = Free[g.Roots[0]];
    }

    private void BuildClasses(GenOptions o)
    {
        int nMix = o.CppMultiInherit > 0 ? Math.Max(1, (int)Math.Round(o.CppClasses * o.CppMultiInherit / 3)) : 0;
        for (int j = 0; j < nMix; j++)
        {
            var mx = new CppClass { Index = j, Name = $"Mix_{j}", Ns = NsOf(j + 1), IsMixin = true, Abstract = true };
            mx.Methods.Add(new CppFn { Name = $"hook_{j}", Kind = FnKind.Method, Owner = mx, Virtual = true, Pure = true });
            Mixins.Add(mx);
        }

        for (int i = 0; i < o.CppClasses; i++)
        {
            var c = new CppClass { Index = i, Name = $"Cls_{i}", Ns = NsOf(i), Inline = i % 3 == 0 };
            // Base: the first --cpp-depth classes form one guaranteed chain; the rest root 30% of the time.
            var eligible = Classes.Where(x => x.Depth < o.CppDepth).ToList();
            if (eligible.Count > 0)
            {
                if (i <= o.CppDepth) c.Base = Classes[i - 1];
                else if (U() >= 0.3) c.Base = Pick(eligible);
            }
            if (c.Base is not null) { c.Depth = c.Base.Depth + 1; c.Base.Children.Add(c); }
            bool forceMix = nMix > 0 && i == Math.Min(1, o.CppClasses - 1);
            // Class 0 never takes a mixin (it would block the whole chain below it); class 1 always does, so a
            // primary base + mixin (multiple inheritance) exists whenever there are two classes.
            if (nMix > 0 && i > 0 && !c.Ancestors().Any(a => a.IsMixin) && (forceMix || U() < o.CppMultiInherit))
            {
                c.Mixin = Pick(Mixins);
                c.Mixin.Children.Add(c);
            }

            var taken = new HashSet<string>(c.Ancestors().SelectMany(a => a.Methods).Select(m => m.Name));
            int hi = taken.Where(n => n.StartsWith("m_")).Select(n => int.Parse(n[2..])).DefaultIfEmpty(-1).Max();

            // Overrides of inherited virtuals (never past a final one; a newly added mixin's hook always).
            foreach (var n in VirtualNamesOf(c))
            {
                var cur = (c.Base?.Resolve(n) ?? c.Mixin?.Resolve(n))!;
                if (c.Ancestors().SelectMany(a => a.Methods).Any(m => m.Name == n && m.Final)) continue;
                bool must = c.Mixin is not null && n == c.Mixin.Methods[0].Name;
                double pr = cur.Pure ? Math.Max(o.CppVirtualFrac, 0.7) : o.CppVirtualFrac;
                if (must || U() < pr)
                    c.Methods.Add(new CppFn { Name = n, Kind = FnKind.Method, Owner = c, Virtual = true, Override = true, Final = !must && U() < 0.2 });
            }
            // New virtuals: a root introduces two (the first pure 30% of the time); a derived class one, 40% of the time.
            int fresh = c.Base is null ? 2 : U() < 0.4 ? 1 : 0;
            for (int k = 0; k < fresh; k++)
                c.Methods.Add(new CppFn { Name = $"m_{++hi}", Kind = FnKind.Method, Owner = c, Virtual = true, Pure = c.Base is null && k == 0 && ((i == 0 && o.CppClasses > 1) || U() < 0.3) });   // class 0: always abstract
            // Non-virtual methods (one, sometimes two) from a small shared name pool, so unrelated classes reuse names.
            int nv = U() < 0.4 ? 2 : 1;
            for (int k = 0; k < nv; k++)
            {
                var free = Enumerable.Range(0, 8).Select(x => $"m_{x}").Where(x => !taken.Contains(x) && c.Methods.All(m => m.Name != x)).ToList();
                string name = free.Count > 0 ? Pick(free) : $"m_{++hi}";
                while (c.Methods.Any(m => m.Name == name)) name = $"m_{++hi}";   // the pool pick may already hold hi+1
                c.Methods.Add(new CppFn { Name = name, Kind = FnKind.Method, Owner = c });
            }

            c.Abstract = c.VirtualNames().Any(n => c.Resolve(n)!.Pure);
            c.Instantiated = !c.Abstract && U() < o.CppInstantiatedFrac;
            c.Ctor = new CppFn { Name = c.Name, Kind = FnKind.Ctor, Owner = c };
            c.Dtor = new CppFn { Name = "~" + c.Name, Kind = FnKind.Dtor, Owner = c };
            if (U() < 0.3)
            {
                var mem = Classes.Where(x => x.Instantiated && !c.Ancestors().Contains(x)).ToList();
                if (mem.Count > 0) c.Members.Add(Pick(mem));
            }
            Classes.Add(c);
        }

        // Every quadrant at least once: one class constructed, and (below 1.0) one concrete class that is not.
        var concrete = Classes.Where(c => !c.Abstract).ToList();
        if (concrete.Count > 0 && !concrete.Any(c => c.Instantiated) && o.CppInstantiatedFrac > 0) concrete[0].Instantiated = true;
        var memberTypes = Classes.SelectMany(c => c.Members).ToHashSet();
        if (o.CppInstantiatedFrac < 1 && concrete.Count >= 2 && concrete.All(c => c.Instantiated)
            && concrete.LastOrDefault(c => !memberTypes.Contains(c)) is { } spare)
            spare.Instantiated = false;

        // final classes: concrete leaves (nothing derives from them) that add no virtual of their own, 30% each, at least
        // one when any qualifies. (An abstract final class, or a new virtual in one, is legal C++ that compilers flag.)
        bool CanBeFinal(CppClass c) => c.Children.Count == 0 && !c.Abstract && c.Methods.All(m => !m.Virtual || m.Override);
        foreach (var c in Classes) if (CanBeFinal(c) && U() < 0.3) c.Final = true;
        if (!Classes.Any(c => c.Final) && Classes.LastOrDefault(CanBeFinal) is { } leaf) leaf.Final = true;
    }

    private static List<string> VirtualNamesOf(CppClass c)
    {
        var names = new List<string>();
        foreach (var b in new[] { c.Base, c.Mixin })
            if (b is not null)
                foreach (var n in b.VirtualNames()) if (!names.Contains(n)) names.Add(n);
        return names;
    }

    private void BuildTemplates(GenOptions o)
    {
        for (int t = 0; t < o.CppTemplates; t++)
        {
            bool isClass = t % 2 == 1;
            var tp = new CppTemplate { Index = t, IsClass = isClass, Ns = NsOf(t), Name = isClass ? $"Tmpl_{t}" : $"tf_{t}" };
            tp.Body = new CppFn { Name = isClass ? "m_0" : tp.Name, Kind = isClass ? FnKind.TmplMethod : FnKind.TmplFn, Tmpl = tp, Ns = tp.Ns };
            if (U() < 0.7) tp.Body.Body.Add(new Stmt { Kind = StmtKind.CallFree, Target = Pick(LiveFree) });
            if (t > 0 && U() < 0.5) tp.Body.Body.Add(new Stmt { Kind = StmtKind.CallTmpl, Tmpl = Templates[_rng.Next(t)], TypeArg = "T" });
            Templates.Add(tp);
        }
    }

    // Call sites: construction, virtual / non-virtual / super calls, template instances, member pointers, static objects.
    private void Wire(GenOptions o)
    {
        var liveFree = LiveFree;
        var deadFree = DeadFree;
        var outOfClass = Classes.Where(c => !c.Inline)
            .SelectMany(c => c.Methods.Where(m => !m.Pure).Append(c.Ctor!).Append(c.Dtor!)).ToList();
        CppFn? Host(bool live)
        {
            if (!live) return deadFree.Count > 0 ? Pick(deadFree) : null;
            return outOfClass.Count > 0 && U() < 0.2 ? Pick(outOfClass) : Pick(liveFree);
        }
        void Add(CppFn? host, Stmt s) => host?.Body.Add(s);

        // Construction: each instantiated class in live code (local or new/delete); half the others in dead code.
        foreach (var c in Classes.Where(c => !c.Abstract))
        {
            var kind = c.Index % 2 == 0 ? StmtKind.ConstructLocal : StmtKind.ConstructNew;
            if (c.Instantiated) Pick(liveFree).Body.Add(new Stmt { Kind = kind, Cls = c });
            else if (U() < 0.6) Add(Host(false), new Stmt { Kind = kind, Cls = c });
        }

        // Virtual calls: each introduced virtual through its introducing class (mostly live); some derived classes
        // call an inherited one through their own static type.
        foreach (var c in Mixins.Concat(Classes))
        {
            foreach (var m in c.Methods.Where(m => m.Virtual && !m.Override))
                Add(Host(U() >= 0.15), new Stmt { Kind = StmtKind.CallVirtual, Cls = c, Method = m.Name });
            if (c.Base is not null && U() < 0.35)
                Add(Host(true), new Stmt { Kind = StmtKind.CallVirtual, Cls = c, Method = Pick(c.VirtualNames()) });
        }

        // Non-virtual calls, and super calls from overrides to the implementation they replace.
        foreach (var c in Classes)
            foreach (var m in c.Methods)
            {
                if (!m.Virtual && U() < 0.75)
                {
                    var h = Host(U() >= 0.2);
                    if (h == m) h = Pick(liveFree);       // a body calling itself unconditionally never returns
                    Add(h, new Stmt { Kind = StmtKind.CallMethod, Cls = c, Target = m });
                }
                if (m.Override && !m.Pure && U() < 0.3 && (c.Base?.Resolve(m.Name) ?? c.Mixin?.Resolve(m.Name)) is { Pure: false } up)
                    m.Body.Add(new Stmt { Kind = StmtKind.SuperCall, Target = up });
            }

        // Bodies of methods, ctors and dtors call live free functions only, so the dead block stays dead.
        foreach (var c in Classes)
            foreach (var f in c.Methods.Where(m => !m.Pure).Append(c.Ctor!).Append(c.Dtor!))
                if (U() < 0.5) f.Body.Add(new Stmt { Kind = StmtKind.CallFree, Target = Pick(liveFree) });

        // Template instances: the last template is never used (droppable); each other gets 1..--cpp-instances type
        // arguments, the first from live code, later ones from dead code half the time (instantiated, unreachable).
        for (int t = 0; t < Templates.Count; t++)
        {
            if (Templates.Count >= 2 && t == Templates.Count - 1) break;
            int n = t == 0 ? o.CppInstances : 1 + _rng.Next(o.CppInstances);
            for (int k = 0; k < n; k++)
            {
                bool live = k == 0 || deadFree.Count == 0 || U() >= 0.5;
                var host = live ? Pick(liveFree) : Pick(deadFree);
                host.Body.Add(new Stmt { Kind = StmtKind.CallTmpl, Tmpl = Templates[t], TypeArg = TypeArgs[k] });
            }
        }
        // One instance that is instantiated but unreachable whatever the graph: template 0 with a type argument no
        // other call uses (template 0's body calls no lower template, so nothing propagates from it either).
        if (Templates.Count > 0 && deadFree.Count > 0)
            Pick(deadFree).Body.Add(new Stmt { Kind = StmtKind.CallTmpl, Tmpl = Templates[0], TypeArg = DeadOnlyArg });

        // Member-function pointers (non-virtual targets): table+called, arg+called, arg+kept, table+never called.
        int nMp = Math.Max(4, Classes.Count / 4);
        for (int k = 0; k < nMp; k++)
        {
            var c = Pick(Classes);
            var targets = c.Methods.Where(m => !m.Virtual).ToList();
            var host = Pick(liveFree);
            switch (k % 4)
            {
                case 0: host.Body.Add(new Stmt { Kind = StmtKind.MemberPtrTable, Cls = c, MpTargets = targets, Dispatched = true }); break;
                case 3: host.Body.Add(new Stmt { Kind = StmtKind.MemberPtrTable, Cls = c, MpTargets = targets, Dispatched = false }); break;
                default:
                    bool run = k % 4 == 1;
                    host.Body.Add(new Stmt { Kind = StmtKind.MemberPtrArg, Cls = c, MpTargets = { targets[0] }, Dispatched = run, Target = HelperFor(c, run) });
                    break;
            }
        }

        // Static-storage objects of constructed classes.
        var pool = Classes.Where(c => c.Instantiated).ToList();
        if (pool.Count == 0) pool = Classes.Where(c => !c.Abstract).ToList();
        for (int k = 0; k < o.CppStaticInit && pool.Count > 0; k++) StaticObjs.Add(pool[k % pool.Count]);
    }

    private CppFn HelperFor(CppClass c, bool run)
    {
        string name = $"{(run ? "mp_run" : "mp_keep")}_{c.Name}";
        var h = Helpers.FirstOrDefault(x => x.Name == name);
        if (h is null) Helpers.Add(h = new CppFn { Name = name, Kind = FnKind.Helper, Ns = "cs_rt", MpClass = c, MpRun = run });
        return h;
    }
}

/// <summary>Writes the program as text, recording every def and call-site line on the model.</summary>
internal static class CppEmitter
{
    private sealed class Buf
    {
        private readonly StringBuilder _sb = new();
        public int Lines { get; private set; }
        public int W(string s) { _sb.Append(s).Append('\n'); return ++Lines; }
        public override string ToString() => _sb.ToString();
    }

    public static List<(string Rel, string Text)> Emit(CppProgram p)
    {
        var files = new List<(string, string)>();

        // rt/free.hpp: every free function's prototype.
        var fh = new Buf();
        fh.W("#pragma once");
        foreach (var ns in p.Namespaces)
        {
            fh.W($"namespace {ns} {{");
            foreach (var f in p.Free.Where(f => f.Ns == ns)) fh.W($"void {f.Name}();");
            fh.W("}");
        }
        files.Add(("cpp/rt/free.hpp", fh.ToString()));

        // rt/globals.hpp: forward declarations + the receiver pointers virtual and member calls go through.
        var gh = new Buf();
        gh.W("#pragma once");
        foreach (var ns in p.Namespaces)
        {
            gh.W($"namespace {ns} {{");
            foreach (var c in p.Mixins.Concat(p.Classes).Where(c => c.Ns == ns)) gh.W($"class {c.Name};");
            gh.W("}");
        }
        foreach (var c in p.Mixins.Concat(p.Classes)) gh.W($"extern {c.Q}* gp_{c.Name};");
        files.Add(("cpp/rt/globals.hpp", gh.ToString()));

        foreach (var mx in p.Mixins)
        {
            var b = new Buf();
            b.W("#pragma once");
            b.W($"namespace {mx.Ns} {{");
            mx.DefSite = Site(mx.Header, b.W($"class {mx.Name} {{"));
            b.W("public:");
            var hook = mx.Methods[0];
            hook.DefSite = Site(mx.Header, b.W($"    virtual void {hook.Name}() = 0;"));
            b.W("};");
            b.W("}");
            files.Add((mx.Header, b.ToString()));
        }

        foreach (var c in p.Classes) EmitClass(c, files);
        foreach (var t in p.Templates) EmitTemplate(t, files);

        // rt/helpers.hpp: member-pointer helpers (declared after the classes are complete).
        var hh = new Buf();
        hh.W("#pragma once");
        foreach (var c in p.Helpers.Select(h => h.MpClass!).Distinct()) hh.W($"#include \"../{c.Ns}/{c.Stem}.hpp\"");
        hh.W("namespace cs_rt {");
        foreach (var h in p.Helpers) hh.W($"void {h.Name}(void ({h.MpClass!.Q}::*f)());");
        hh.W("}");
        files.Add(("cpp/rt/helpers.hpp", hh.ToString()));

        var all = new Buf();
        all.W("#pragma once");
        all.W("#include \"free.hpp\"");
        all.W("#include \"globals.hpp\"");
        foreach (var c in p.Mixins.Concat(p.Classes)) all.W($"#include \"../{c.Ns}/{c.Stem}.hpp\"");
        foreach (var t in p.Templates) all.W($"#include \"../tmpl/tmpl_{t.Index}.hpp\"");
        all.W("#include \"helpers.hpp\"");
        files.Add(("cpp/rt/all.hpp", all.ToString()));

        // Free functions, one file per namespace.
        foreach (var ns in p.Namespaces)
        {
            string rel = $"cpp/{ns}/free.cpp";
            var b = new Buf();
            b.W("#include \"../rt/all.hpp\"");
            b.W($"namespace {ns} {{");
            foreach (var f in p.Free.Where(f => f.Ns == ns))
            {
                f.DefSite = Site(rel, b.W($"void {f.Name}() {{"));
                EmitBody(b, rel, f, "    ");
                b.W("}");
            }
            b.W("}");
            files.Add((rel, b.ToString()));
        }

        // rt/rt.cpp: receiver pointers and helper bodies.
        {
            string rel = "cpp/rt/rt.cpp";
            var b = new Buf();
            b.W("#include \"all.hpp\"");
            foreach (var c in p.Mixins.Concat(p.Classes)) b.W($"{c.Q}* gp_{c.Name} = nullptr;");
            b.W("namespace cs_rt {");
            foreach (var h in p.Helpers)
            {
                var c = h.MpClass!;
                if (!h.MpRun) b.W($"void ({c.Q}::*kept_{c.Name})() = nullptr;");
                h.DefSite = Site(rel, b.W($"void {h.Name}(void ({c.Q}::*f)()) {{"));
                b.W(h.MpRun ? $"    (gp_{c.Name}->*f)();" : $"    kept_{c.Name} = f;");
                b.W("}");
            }
            b.W("}");
            files.Add((rel, b.ToString()));
        }

        // rt/static_init.cpp: static-storage objects; rt/main.cpp: calls the root.
        {
            string rel = "cpp/rt/static_init.cpp";
            var b = new Buf();
            b.W("#include \"all.hpp\"");
            for (int k = 0; k < p.StaticObjs.Count; k++) p.StaticInitLines.Add(b.W($"{p.StaticObjs[k].Q} g_static_{k};"));
            files.Add((rel, b.ToString()));
        }
        {
            string rel = "cpp/rt/main.cpp";
            var b = new Buf();
            b.W("#include \"all.hpp\"");
            b.W("int main() {");
            p.MainLine = b.W($"    {p.Root.Ns}::{p.Root.Name}();");
            b.W("    return 0;");
            b.W("}");
            files.Add((rel, b.ToString()));
        }
        p.Files = files;
        return files;
    }

    public static string Site(string rel, int line) => $"{rel}:{line}";

    private static void EmitClass(CppClass c, List<(string, string)> files)
    {
        var h = new Buf();
        h.W("#pragma once");
        h.W("#include \"../rt/free.hpp\"");
        foreach (var dep in new[] { c.Base, c.Mixin }.Concat(c.Members))
            if (dep is not null) h.W($"#include \"../{dep.Ns}/{dep.Stem}.hpp\"");
        h.W($"namespace {c.Ns} {{");
        var bases = new[] { c.Base, c.Mixin }.Where(b => b is not null).Select(b => $"public {b!.Q}").ToList();
        c.DefSite = Site(c.Header, h.W($"class {c.Name}{(c.Final ? " final" : "")}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")} {{"));
        h.W("public:");

        string Sig(CppFn m) => m.Override ? $"void {m.Name}(){(m.Final ? " override final" : " override")}" : m.Virtual ? $"virtual void {m.Name}()" : $"void {m.Name}()";
        var fns = new List<CppFn> { c.Ctor!, c.Dtor! };
        fns.AddRange(c.Methods);
        if (c.Inline)
        {
            foreach (var f in fns)
            {
                if (f.Pure) { f.DefSite = Site(c.Header, h.W($"    virtual void {f.Name}() = 0;")); continue; }
                string head = f.Kind == FnKind.Method ? Sig(f) : $"{f.Name}()";
                f.DefSite = Site(c.Header, h.W($"    {head} {{"));
                EmitBody(h, c.Header, f, "        ");
                h.W("    }");
            }
        }
        else
        {
            foreach (var f in fns)
            {
                if (f.Pure) f.DefSite = Site(c.Header, h.W($"    virtual void {f.Name}() = 0;"));
                else h.W(f.Kind == FnKind.Method ? $"    {Sig(f)};" : $"    {f.Name}();");
            }
        }
        for (int k = 0; k < c.Members.Count; k++) h.W($"    {c.Members[k].Q} mem_{k};");
        h.W("};");
        h.W("}");
        files.Add((c.Header, h.ToString()));

        if (c.Inline) return;
        string rel = $"cpp/{c.Ns}/{c.Stem}.cpp";
        var s = new Buf();
        s.W("#include \"../rt/all.hpp\"");
        s.W($"namespace {c.Ns} {{");
        foreach (var f in fns.Where(f => !f.Pure))
        {
            string head = f.Kind == FnKind.Method ? $"void {c.Name}::{f.Name}()" : $"{c.Name}::{f.Name}()";
            f.DefSite = Site(rel, s.W($"{head} {{"));
            EmitBody(s, rel, f, "    ");
            s.W("}");
        }
        s.W("}");
        files.Add((rel, s.ToString()));
    }

    private static void EmitTemplate(CppTemplate t, List<(string, string)> files)
    {
        var b = new Buf();
        b.W("#pragma once");
        b.W("#include \"../rt/free.hpp\"");
        foreach (var s in t.Body.Body.Where(s => s.Kind == StmtKind.CallTmpl).Select(s => s.Tmpl!.Index).Distinct())
            b.W($"#include \"tmpl_{s}.hpp\"");
        b.W($"namespace {t.Ns} {{");
        b.W("template <typename T>");
        if (t.IsClass)
        {
            b.W($"struct {t.Name} {{");
            b.W("    T v_{};");
            t.Body.DefSite = Site(t.File, b.W("    void m_0() {"));
            EmitBody(b, t.File, t.Body, "        ");
            b.W("    }");
            b.W("};");
        }
        else
        {
            t.Body.DefSite = Site(t.File, b.W($"void {t.Name}() {{"));
            EmitBody(b, t.File, t.Body, "    ");
            b.W("}");
        }
        b.W("}");
        files.Add((t.File, b.ToString()));
    }

    private static void EmitBody(Buf b, string rel, CppFn f, string ind)
    {
        int n = 0;
        foreach (var s in f.Body)
        {
            s.File = rel;
            n++;
            switch (s.Kind)
            {
                case StmtKind.CallFree:
                    s.Line = b.W($"{ind}{s.Target!.Ns}::{s.Target.Name}();");
                    break;
                case StmtKind.CallMethod:
                    s.Line = b.W($"{ind}gp_{s.Cls!.Name}->{s.Target!.Name}();");
                    break;
                case StmtKind.CallVirtual:
                    s.Line = b.W($"{ind}gp_{s.Cls!.Name}->{s.Method}();");
                    break;
                case StmtKind.SuperCall:
                    s.Line = b.W($"{ind}{s.Target!.Owner!.Q}::{s.Target.Name}();");
                    break;
                case StmtKind.ConstructLocal:
                    s.Line = b.W($"{ind}{s.Cls!.Q} obj_{n};");
                    break;
                case StmtKind.ConstructNew:
                    s.Line = b.W($"{ind}gp_{s.Cls!.Name} = new {s.Cls.Q}();");    // never deleted: no dtor call
                    break;
                case StmtKind.CallTmpl:
                    if (s.Tmpl!.IsClass)
                    {
                        b.W($"{ind}{s.Tmpl.Ns}::{s.Tmpl.Name}<{s.TypeArg}> tv_{n};");
                        s.Line = b.W($"{ind}tv_{n}.m_0();");
                    }
                    else s.Line = b.W($"{ind}{s.Tmpl.Ns}::{s.Tmpl.Name}<{s.TypeArg}>();");
                    break;
                case StmtKind.MemberPtrTable:
                    b.W($"{ind}static void ({s.Cls!.Q}::* const tbl_{n}[])() = {{");
                    foreach (var t in s.MpTargets) s.MpLines.Add(b.W($"{ind}    &{s.Cls.Q}::{t.Name},"));
                    b.W($"{ind}}};");
                    b.W(s.Dispatched ? $"{ind}for (auto f : tbl_{n}) (gp_{s.Cls.Name}->*f)();" : $"{ind}(void)tbl_{n};");
                    break;
                case StmtKind.MemberPtrArg:
                    s.Line = b.W($"{ind}cs_rt::{s.Target!.Name}(&{s.Cls!.Q}::{s.MpTargets[0].Name});");
                    break;
            }
        }
    }
}

/// <summary>The ground truth computed from the emitted program: symbols, edges, the RTA fixpoint, and the manifest.</summary>
internal sealed class CppTruth
{
    private readonly CppProgram _p;
    private readonly Dictionary<string, SymbolEntry> _syms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CppClass>> _constructs = new(StringComparer.Ordinal);
    // Virtual sites still waiting for their rta set: owner symbol, record, receiver static type, method name.
    private readonly List<(string Owner, VirtualEdge Edge, CppClass Cls, string Method)> _sites = new();
    private readonly List<string> _staticInit = new();

    public HashSet<string> SoundReach { get; private set; } = new();
    public HashSet<string> PreciseReach { get; private set; } = new();
    public HashSet<CppClass> Live { get; private set; } = new();

    private CppTruth(CppProgram p) => _p = p;

    public static CppTruth Build(CppProgram p)
    {
        var t = new CppTruth(p);
        t.Collect();
        t.Solve();
        return t;
    }

    private SymbolEntry Sym(string name) => _syms[name];

    private void Declare(CppFn f, string? name = null, string? instanceOf = null)
    {
        string key = name ?? f.Symbol;
        // A clash would mean two C++ declarations of one member; the compiler rejects that, so the generator must too.
        if (_syms.ContainsKey(key)) throw new InvalidOperationException($"C++ generator: symbol '{key}' declared twice");
        _syms[key] = new SymbolEntry
        {
            Def = f.DefSite,
            Kind = instanceOf is not null ? "instance" : f.ManifestKind,
            SimpleName = f.Name,
            InstanceOf = instanceOf,
            Pure = f.Pure,
        };
    }

    private void Collect()
    {
        // Declare every symbol first (stable order: free functions, classes, mixins, templates + instances, helpers).
        foreach (var f in _p.Free) Declare(f);
        foreach (var c in _p.Classes)
        {
            Declare(c.Ctor!);
            Declare(c.Dtor!);
            foreach (var m in c.Methods) Declare(m);
        }
        foreach (var mx in _p.Mixins) foreach (var m in mx.Methods) Declare(m);

        // Template instances: seeded by concrete call sites anywhere, then propagated through template bodies.
        var bodies = _p.Free.Concat(_p.Classes.SelectMany(c => c.Methods.Append(c.Ctor!).Append(c.Dtor!))).ToList();
        var inst = new Dictionary<CppTemplate, List<string>>();
        var work = new Queue<(CppTemplate, string)>();
        void Seed(CppTemplate t, string x)
        {
            if (!inst.TryGetValue(t, out var l)) inst[t] = l = new List<string>();
            if (l.Contains(x)) return;
            l.Add(x);
            work.Enqueue((t, x));
        }
        foreach (var f in bodies) foreach (var s in f.Body) if (s.Kind == StmtKind.CallTmpl) Seed(s.Tmpl!, s.TypeArg!);
        while (work.Count > 0)
        {
            var (t, x) = work.Dequeue();
            foreach (var s in t.Body.Body) if (s.Kind == StmtKind.CallTmpl) Seed(s.Tmpl!, x);
        }
        foreach (var t in _p.Templates)
        {
            Declare(t.Body);
            if (!inst.TryGetValue(t, out var xs)) continue;
            foreach (var x in CppProgram.TypeArgs.Where(xs.Contains)) Declare(t.Body, t.InstanceName(x), t.SymbolName);
        }
        foreach (var h in _p.Helpers) Declare(h);
        foreach (var c in _p.Classes.Concat(_p.Mixins))
            _syms[c.Q] = new SymbolEntry { Def = c.DefSite, Kind = "class", SimpleName = c.Name };

        // Bodies.
        foreach (var f in bodies) Apply(f.Symbol, f, null);
        foreach (var t in _p.Templates)
            if (inst.TryGetValue(t, out var xs))
                foreach (var x in CppProgram.TypeArgs.Where(xs.Contains)) Apply(t.InstanceName(x), t.Body, x);

        // Compiler-added calls: ctor -> base ctor, member ctors; dtor -> member dtors (reverse), base dtor.
        foreach (var c in _p.Classes)
        {
            var ctor = Sym(c.Ctor!.Symbol);
            var dtor = Sym(c.Dtor!.Symbol);
            if (c.Base is not null) ctor.ImplicitEdges.Add(c.Base.Ctor!.Symbol);
            foreach (var m in c.Members) ctor.ImplicitEdges.Add(m.Ctor!.Symbol);
            for (int k = c.Members.Count - 1; k >= 0; k--) dtor.ImplicitEdges.Add(c.Members[k].Dtor!.Symbol);
            if (c.Base is not null) dtor.ImplicitEdges.Add(c.Base.Dtor!.Symbol);
        }

        // Static-storage objects: ctor + dtor are roots; the definition line is a ref of the ctor.
        var lines = _p.StaticInitLines;
        for (int k = 0; k < _p.StaticObjs.Count; k++)
        {
            var c = _p.StaticObjs[k];
            _staticInit.Add(c.Ctor!.Symbol);
            _staticInit.Add(c.Dtor!.Symbol);
            Ref(c.Ctor!.Symbol, CppEmitter.Site("cpp/rt/static_init.cpp", lines[k]));
        }
        Ref(_p.Root.Symbol, CppEmitter.Site("cpp/rt/main.cpp", _p.MainLine));
        ClassRefs();

        foreach (var s in _syms.Values) Dedup(s);
    }

    private void Ref(string sym, string site) => Sym(sym).Refs.Add(site);

    private static readonly Regex StringLit = new("\"[^\"]*\"", RegexOptions.Compiled);
    private static readonly Regex Ident = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>
    /// A class symbol's refs are every line, other than its def, where its name appears as a whole word outside
    /// strings: forward declarations, base lists, pointer and member declarations, <c>Cls::</c> qualifiers, ctor and
    /// dtor declarations and bodies, constructions, member-pointer types. That is exactly what a name search returns,
    /// which is the truth CodeCompass grades a class name against (agreed 2026-10-14).
    /// </summary>
    private void ClassRefs()
    {
        var byName = _p.Classes.Concat(_p.Mixins).ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var (rel, text) in _p.Files)
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in Ident.Matches(StringLit.Replace(lines[i], "\"\"")))
                    if (byName.TryGetValue(m.Value, out var c))
                    {
                        string site = CppEmitter.Site(rel, i + 1);
                        if (site != c.DefSite) Ref(c.Q, site);
                    }
        }
    }

    private void Apply(string src, CppFn f, string? typeArg)
    {
        var me = Sym(src);
        foreach (var s in f.Body)
        {
            string site = CppEmitter.Site(s.File, s.Line);
            switch (s.Kind)
            {
                case StmtKind.CallFree:
                case StmtKind.CallMethod:
                case StmtKind.SuperCall:
                    me.Edges.Add(s.Target!.Symbol);
                    Ref(s.Target.Symbol, site);
                    break;
                case StmtKind.CallVirtual:
                {
                    var stat = s.Cls!.Resolve(s.Method!)!;
                    var ve = new VirtualEdge { Static = stat.Symbol };
                    foreach (var c in s.Cls.Subtree())
                        if (c.Resolve(s.Method!) is { Pure: false } impl && !ve.Cha.Contains(impl.Symbol)) ve.Cha.Add(impl.Symbol);
                    me.VirtualEdges.Add(ve);                         // identical records are merged after Solve
                    _sites.Add((src, ve, s.Cls, s.Method!));
                    Ref(stat.Symbol, site);
                    break;
                }
                case StmtKind.ConstructLocal:
                case StmtKind.ConstructNew:
                    me.Edges.Add(s.Cls!.Ctor!.Symbol);
                    if (s.Kind == StmtKind.ConstructLocal) me.ImplicitEdges.Add(s.Cls.Dtor!.Symbol);   // scope exit
                    Ref(s.Cls.Ctor!.Symbol, site);
                    if (!_constructs.TryGetValue(src, out var cl)) _constructs[src] = cl = new List<CppClass>();
                    cl.Add(s.Cls);
                    break;
                case StmtKind.CallTmpl:
                {
                    string target = s.Tmpl!.InstanceName(s.TypeArg == "T" ? typeArg! : s.TypeArg!);
                    me.Edges.Add(target);
                    Ref(target, site);
                    break;
                }
                case StmtKind.MemberPtrTable:
                    for (int k = 0; k < s.MpTargets.Count; k++)
                    {
                        me.MemberPtrEdges.Add(new MemberPtrEdge { Target = s.MpTargets[k].Symbol, Via = MemberPtrVia.Table, Dispatched = s.Dispatched });
                        Ref(s.MpTargets[k].Symbol, CppEmitter.Site(s.File, s.MpLines[k]));
                    }
                    break;
                case StmtKind.MemberPtrArg:
                    me.Edges.Add(s.Target!.Symbol);
                    Ref(s.Target.Symbol, site);
                    me.MemberPtrEdges.Add(new MemberPtrEdge { Target = s.MpTargets[0].Symbol, Via = MemberPtrVia.Arg, Dispatched = s.Dispatched });
                    Ref(s.MpTargets[0].Symbol, site);
                    break;
            }
        }
    }

    private static void Dedup(SymbolEntry s)
    {
        static void D(List<string> l)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            l.RemoveAll(x => !seen.Add(x));
        }
        D(s.Refs);
        D(s.Edges);
        D(s.ImplicitEdges);
        var mp = new HashSet<string>(StringComparer.Ordinal);
        s.MemberPtrEdges.RemoveAll(m => !mp.Add($"{m.Target}|{m.Via.Label()}|{m.Dispatched}"));
    }

    // RTA fixpoint: a class is live when a reachable body constructs it (or it is a static object, or a member of a
    // live class); rta = what live receivers dispatch to; reachability over rta can construct more, so iterate.
    private void Solve()
    {
        var roots = new List<string> { _p.Root.Symbol };
        roots.AddRange(_staticInit);

        var live = new HashSet<CppClass>();
        void MakeLive(CppClass c) { if (live.Add(c)) foreach (var m in c.Members) MakeLive(m); }
        foreach (var c in _p.StaticObjs) MakeLive(c);

        HashSet<string> reach;
        while (true)
        {
            foreach (var (_, ve, cls, method) in _sites)
            {
                ve.Rta.Clear();
                foreach (var c in cls.Subtree())
                    if (live.Contains(c) && c.Resolve(method) is { Pure: false } impl && !ve.Rta.Contains(impl.Symbol)) ve.Rta.Add(impl.Symbol);
            }
            reach = Closure(roots, precise: true);
            int before = live.Count;
            foreach (var s in reach)
                if (_constructs.TryGetValue(s, out var cl)) foreach (var c in cl) MakeLive(c);
            if (live.Count == before) break;
        }
        PreciseReach = reach;
        // Two receivers can name the same static method (one inherits it): keep one record per distinct
        // (static, cha, rta), so a record is never dropped while its sets differ.
        foreach (var s in _syms.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            s.VirtualEdges.RemoveAll(v => !seen.Add($"{v.Static}|{string.Join(",", v.Cha)}|{string.Join(",", v.Rta)}"));
        }
        SoundReach = Closure(roots, precise: false);
        Live = live;
    }

    public HashSet<string> Closure(IEnumerable<string> roots, bool precise)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        foreach (var r in roots) if (seen.Add(r)) stack.Push(r);
        while (stack.Count > 0)
        {
            var s = _syms[stack.Pop()];
            IEnumerable<string> next = s.Edges.Concat(s.ImplicitEdges)
                .Concat(s.VirtualEdges.SelectMany(v => precise ? v.Rta : v.Cha))
                .Concat(s.MemberPtrEdges.Where(m => !precise || m.Dispatched).Select(m => m.Target));
            foreach (var n in next) if (seen.Add(n)) stack.Push(n);
        }
        return seen;
    }

    public ManifestModel ToManifest(GenOptions o, string outFull)
    {
        var m = new ManifestModel
        {
            ManifestVersion = 2,
            GeneratorVersion = Program.Version,
            Seed = o.Seed,
            CorpusRoot = outFull,
            Lang = "cpp",
        };
        m.Gen.Add(GenKnob.Of("lang", "cpp"));
        m.Gen.Add(GenKnob.Of("cppClasses", (long)o.CppClasses));
        m.Gen.Add(GenKnob.Of("cppDepth", (long)o.CppDepth));
        m.Gen.Add(GenKnob.Of("cppMultiInherit", o.CppMultiInherit));
        m.Gen.Add(GenKnob.Of("cppVirtualFrac", o.CppVirtualFrac));
        m.Gen.Add(GenKnob.Of("cppInstantiatedFrac", o.CppInstantiatedFrac));
        m.Gen.Add(GenKnob.Of("cppTemplates", (long)o.CppTemplates));
        m.Gen.Add(GenKnob.Of("cppInstances", (long)o.CppInstances));
        m.Gen.Add(GenKnob.Of("cppStaticInit", (long)o.CppStaticInit));
        m.Gen.Add(GenKnob.Of("oracleChain", (long)o.OracleChain));
        m.Gen.Add(GenKnob.Of("oracleFanout", (long)o.OracleFanout));
        m.Gen.Add(GenKnob.Of("oracleDepth", (long)o.OracleDepth));
        m.Gen.Add(GenKnob.Of("oracleSharedLeaves", (long)o.OracleSharedLeaves));
        m.Gen.Add(GenKnob.Of("oracleReachableFrac", o.OracleReachableFrac));
        m.Roots.Add(_p.Root.Symbol);
        m.StaticInitRoots.AddRange(_staticInit.Distinct());
        foreach (var c in _p.Mixins.Concat(_p.Classes))
            m.Classes.Add(new(c.Q, new[] { c.Base, c.Mixin }.Where(b => b is not null).Select(b => b!.Q).ToList()));
        foreach (var (k, v) in _syms) m.Symbols[k] = v;
        return m;
    }
}
