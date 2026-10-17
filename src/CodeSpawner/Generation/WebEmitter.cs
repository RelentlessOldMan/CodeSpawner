using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>A unit's include closure (docs/include-web-design.md): what a real preprocessor enters, and how often.</summary>
public sealed record WebClosure(string Unit, long Files, long Bytes, long Reads, long ReadBytes, int Depth, long Defines, int TemplateInstances);

/// <summary>One template instance: a template header entered while TNAME is defined. Its symbol is named after the
/// primary head (<c>u12_r7_t3</c>); the def is the unit's own #include line that led there.</summary>
public sealed class WebInstance
{
    public required string Name { get; init; }
    public required string Def { get; init; }
    public required string TemplateHead { get; init; }
    public required string IncludedVia { get; init; }
    public required int Depth { get; init; }
    public List<string> Refs { get; } = new();
}

public sealed class WebEmitResult
{
    /// <summary>The unit files (absolute), in unit order: the compile_commands.json entries.</summary>
    public List<string> Units { get; } = new();
    public List<WebClosure> Closures { get; } = new();
    public List<WebInstance> Instances { get; } = new();
    public long TotalBytes { get; set; }
}

/// <summary>
/// The include web (docs/include-web-design.md): register-map headers chained in segments, aggregators, template
/// headers instantiated through TNAME, a vendor include dir reached by #include_next, and closure-root units. The
/// generator keeps a model of every file (its guard, the defines it executes, the includes it makes active given the
/// fixed CFG_ switches) and computes each unit's closure from that model, never from the text: verify recomputes it
/// from the text with its own preprocessor, so the two are independent.
/// </summary>
public static class WebEmitter
{
    public const string IncDir = "inc", VendorDir = "inc_vnd", UnitDir = "web";
    /// <summary>The corpus-root directories the web owns (mutate leaves them alone: the closure truth names their bytes).</summary>
    public static readonly string[] Dirs = [IncDir, VendorDir, UnitDir];
    /// <summary>The -I search order, relative to the corpus root (also <c>_meta.includeDirs</c>).</summary>
    public static readonly string[] IncludeDirs = [IncDir, VendorDir];
    private const int CfgSwitches = 16;
    private const int VendorPairs = 4;

    // Rng index bases: one stream per file kind and index, all under Category.Web.
    private const long ModelBase = 0, HeaderBase = 1_000_000_000L, AggBase = 2_000_000_000L, TmplBase = 3_000_000_000L,
        UnitBase = 4_000_000_000L, VendorBase = 5_000_000_000L, ShadowBase = 6_000_000_000L;

    private enum Eol : byte { Lf, Crlf, Mixed }
    private enum Form : byte { Plain, Relative, Computed }

    // A model file. Defines and Includes are what a counted entry executes (for a template: while TNAME is defined;
    // with TNAME undefined a template executes nothing).
    private sealed class WFile
    {
        public required string Rel;
        public bool Guarded = true;
        public bool Tmpl;
        public long Defines;
        public readonly List<int> Includes = new();
        public long Bytes;
        public int HeadLine;   // a template's primary head line
        public int Tmpl_;      // the template number
    }

    // Blocks of a register-map header, in file order.
    private abstract record Blk;
    private sealed record DefRun(int From, int Count) : Blk;
    private sealed record Inc(int Target, string Name, Form Form, int Cond) : Blk;       // Cond -1 = unconditional
    private sealed record Cont(int Idx, int Lines) : Blk;
    private sealed record Comment(int Idx, int FakeA, int FakeB) : Blk;
    private sealed record CondDefs(int Idx, int K, bool Ifdef, int NTrue, int NFalse) : Blk;
    private sealed record Undef(int Idx, int Count) : Blk;

    private static bool Chance(ref Rng r, double p) => p > 0 && (r.NextULong() >> 11) * (1.0 / (1UL << 53)) < p;

    public static WebEmitResult Emit(GenOptions o, string outFull, PopulationStats stats, ParallelOptions par)
    {
        var res = new WebEmitResult();
        foreach (var d in Dirs) Directory.CreateDirectory(Path.Combine(outFull, d));

        var mr = Rng.For(o.Seed, Category.Web, ModelBase);
        var cfg = new bool[CfgSwitches];
        for (int k = 0; k < CfgSwitches; k++) cfg[k] = mr.Next(2) == 1;

        int nH = o.WebHeaders, D = Math.Max(1, o.WebDepth);
        int nSeg = (nH + D - 1) / D;
        int nT = o.WebTemplates, nAgg = o.WebAggregators;
        int nShadow = Math.Min(o.WebShadow, nH);
        int nVx = o.WebNextFrac > 0 ? VendorPairs : 0;

        // File ids: cfg, rm_0..rm_{nH-1}, all_*, tmpl_*, vx (inc), vx (vnd). Shadows are never reached, so not modelled.
        var files = new List<WFile>();
        int cfgId = Add(files, new WFile { Rel = $"{IncDir}/cfg.h", Defines = 1 + 2 + CfgSwitches });
        int rm0 = files.Count;
        for (int i = 0; i < nH; i++) files.Add(new WFile { Rel = $"{IncDir}/rm_{i}.h" });
        int agg0 = files.Count;
        for (int k = 0; k < nAgg; k++) files.Add(new WFile { Rel = $"{IncDir}/all_{k}.h" });
        int tm0 = files.Count;
        for (int t = 0; t < nT; t++) files.Add(new WFile { Rel = $"{IncDir}/tmpl_{t}.h", Guarded = false, Tmpl = true, Tmpl_ = t });
        int vx0 = files.Count;
        for (int j = 0; j < nVx; j++) files.Add(new WFile { Rel = $"{IncDir}/vx_{j}.h" });
        int vn0 = files.Count;
        for (int j = 0; j < nVx; j++) files.Add(new WFile { Rel = $"{VendorDir}/vx_{j}.h" });

        // --- the register-map headers' blocks ---
        var tail = new HashSet<int>();
        while (tail.Count < Math.Min(o.WebTail, nH)) tail.Add(mr.Next(nH));
        var blocks = new List<Blk>[nH];
        var eols = new Eol[nH];
        for (int i = 0; i < nH; i++)
        {
            var r = Rng.For(o.Seed, Category.Web, HeaderBase + i);
            eols[i] = PickEol(ref r, o);
            int seg = i / D, segHead = seg * D, segEnd = Math.Min(segHead + D, nH);
            var special = new List<Blk>();
            int idx = 0;
            Blk IncTo(int target, string name, int cond, ref Rng rr)
            {
                Form f = cond < 0 && Chance(ref rr, o.WebComputedFrac) ? Form.Computed
                    : Chance(ref rr, o.WebRelativeFrac) ? Form.Relative : Form.Plain;
                return new Inc(target, name, f, cond);
            }
            if (i + 1 < segEnd) special.Add(IncTo(rm0 + i + 1, $"rm_{i + 1}.h", -1, ref r));
            if (i > segHead && Chance(ref r, o.WebCycleFrac))
            {
                int back = r.Next(segHead, i);
                special.Add(IncTo(rm0 + back, $"rm_{back}.h", -1, ref r));
            }
            if (Chance(ref r, o.WebIfdefFrac))
            {
                // A nearby segment: random targets anywhere would percolate, every closure the whole web.
                int xs = ((seg + r.Next(-2, 3)) % nSeg + nSeg) % nSeg;
                int x = Math.Min(nH - 1, xs * D + r.Next(D));
                special.Add(IncTo(rm0 + x, $"rm_{x}.h", r.Next(CfgSwitches), ref r));
                special.Add(new CondDefs(idx++, r.Next(CfgSwitches), r.Next(3) == 0, r.Next(2, 9), r.Next(2, 9)));
            }
            if (nT > 0 && r.Next(10) == 0)
            {
                int t = r.Next(nT);
                special.Add(IncTo(tm0 + t, $"tmpl_{t}.h", -1, ref r));
            }
            if (Chance(ref r, o.WebContinuationFrac)) special.Add(new Cont(idx++, r.Next(20, 400)));
            if (Chance(ref r, o.WebCommentFrac)) special.Add(new Comment(idx++, r.Next(nH), r.Next(nH)));
            if (Chance(ref r, o.WebUndefFrac)) special.Add(new Undef(idx++, r.Next(2, 9)));
            Shuffle(special, ref r);

            long lines = tail.Contains(i)
                ? (long)o.WebTailMb * 1024 * 1024 / 30
                : r.Next(Math.Max(1, o.WebHeaderLines / 2), o.WebHeaderLines * 3 / 2 + 1);
            var bl = new List<Blk>(special.Count * 2 + 1);
            long left = lines; int from = 0;
            for (int s = 0; s <= special.Count; s++)
            {
                long n = s == special.Count ? left : left == 0 ? 0 : r.Next((int)Math.Min(left, int.MaxValue - 1) + 1);
                n = Math.Min(n, left);
                if (n > 0) { bl.Add(new DefRun(from, (int)n)); from += (int)n; left -= n; }
                if (s < special.Count) bl.Add(special[s]);
            }
            blocks[i] = bl;

            var wf = files[rm0 + i];
            wf.Defines = 1;   // the guard
            foreach (var b in bl)
                switch (b)
                {
                    case DefRun d: wf.Defines += d.Count; break;
                    case Inc inc when inc.Cond < 0 || cfg[inc.Cond]:
                        wf.Includes.Add(inc.Target);
                        if (inc.Form == Form.Computed) wf.Defines++;
                        break;
                    case Cont: wf.Defines++; break;
                    case Comment: wf.Defines += 3; break;
                    case CondDefs c: wf.Defines += c.Ifdef || cfg[c.K] ? c.NTrue : c.NFalse; break;
                    case Undef u: wf.Defines += 2L * u.Count; break;
                }
        }

        // --- aggregators: a run of segment heads each ---
        var aggHeads = new List<int>[nAgg];
        for (int k = 0; k < nAgg; k++)
        {
            var r = Rng.For(o.Seed, Category.Web, AggBase + k);
            int start = r.Next(Math.Max(1, nSeg)), len = Math.Min(nSeg, r.Next(4, 13));
            aggHeads[k] = new List<int>(len);
            for (int s = 0; s < len; s++) aggHeads[k].Add(((start + s) % nSeg) * D);
            var wf = files[agg0 + k];
            wf.Defines = 2;
            foreach (var h in aggHeads[k]) wf.Includes.Add(rm0 + h);
        }
        for (int j = 0; j < nVx; j++)
        {
            files[vx0 + j].Defines = 2;
            files[vx0 + j].Includes.Add(vn0 + j);
            files[vn0 + j].Defines = 2 + 8;
        }

        // --- write the headers (parallel; each file's bytes come from its own model and stream) ---
        string incPath = Path.Combine(outFull, IncDir), vnd = Path.Combine(outFull, VendorDir);
        files[cfgId].Bytes = WriteCfg(Path.Combine(incPath, "cfg.h"), cfg);
        Parallel.For(0, nH, par, i => files[rm0 + i].Bytes = WriteRm(Path.Combine(incPath, $"rm_{i}.h"), i, blocks[i], eols[i]));
        for (int k = 0; k < nAgg; k++) files[agg0 + k].Bytes = WriteAgg(Path.Combine(incPath, $"all_{k}.h"), k, aggHeads[k]);
        for (int t = 0; t < nT; t++)
        {
            var r = Rng.For(o.Seed, Category.Web, TmplBase + t);
            var (bytes, head, defs) = WriteTmpl(Path.Combine(incPath, $"tmpl_{t}.h"), t, o.WebTemplateHeads, o.WebTemplateHiddenFrac, PickEol(ref r, o), ref r);
            files[tm0 + t].Bytes = bytes; files[tm0 + t].HeadLine = head; files[tm0 + t].Defines = defs;
        }
        for (int j = 0; j < nVx; j++)
        {
            files[vx0 + j].Bytes = WriteVx(Path.Combine(incPath, $"vx_{j}.h"), j);
            files[vn0 + j].Bytes = WriteVxVendor(Path.Combine(vnd, $"vx_{j}.h"), j);
        }
        long shadowBytes = 0;
        var shadows = new HashSet<int>();
        while (shadows.Count < nShadow) shadows.Add(mr.Next(nH));
        foreach (var s in shadows.Order()) shadowBytes += WriteShadow(Path.Combine(vnd, $"rm_{s}.h"), s);

        foreach (var f in files)
        {
            string pop = f.Rel.StartsWith(VendorDir + "/", StringComparison.Ordinal) ? "web-vendor" : f.Tmpl ? "web-templates" : "web-headers";
            stats.Add(pop, 1, f.Bytes, f.Defines);
        }
        if (nShadow > 0) stats.Add("web-vendor", nShadow, shadowBytes, 0);

        // --- units: built in order, each simulated as it is written (a TNAME site probes the guard state) ---
        var (wMin, wMax) = ArgParser.ParseWebWindow(o.WebWindow);
        var sim = new Sim(files);
        string unitDir = Path.Combine(outFull, UnitDir);
        for (int u = 0; u < o.WebUnits; u++)
        {
            var r = Rng.For(o.Seed, Category.Web, UnitBase + u);
            var eol = PickEol(ref r, o);
            string rel = $"{UnitDir}/u_{u}.c";
            var lines = new List<string>();
            int Line() => lines.Count + 1;   // the line the next Add lands on
            var own = new List<WebInstance>();
            sim.Begin();
            long unitDefines = 0;

            lines.Add($"/* web unit {u}: an include-closure root (CodeSpawner include web) */");
            lines.Add($"#define WEB_UNIT u{u}"); unitDefines++;
            sim.Include(cfgId, Line(), null); lines.Add("#include \"cfg.h\"");

            // The plan: window heads, aggregators, a gated include, a vendor pair, and the TNAME sites among them.
            var plan = new List<(int Kind, int A)>();   // 0 rm head, 1 aggregator, 2 gated rm, 3 vx, 4 site
            int w = Math.Min(nSeg, r.Next(wMin, wMax + 1));
            int start = (int)((long)u * nSeg / Math.Max(1, o.WebUnits)) + r.Next(3);
            for (int s = 0; s < w; s++) plan.Add((0, ((start + s) % nSeg) * D));
            if (nAgg > 0) for (int a = r.Next(1, 3); a > 0; a--) plan.Add((1, r.Next(nAgg)));
            if (Chance(ref r, o.WebIfdefFrac)) plan.Add((2, r.Next(nH)));
            if (nVx > 0 && Chance(ref r, o.WebNextFrac)) plan.Add((3, r.Next(nVx)));
            int sites = nT > 0 ? o.WebTemplateSites : 0;
            for (int s = 0; s < sites; s++) plan.Insert(r.Next(plan.Count + 1), (4, s));

            int computed = 0;
            void IncludeLine(int target, string name, ref Rng rr)
            {
                if (Chance(ref rr, o.WebComputedFrac))
                {
                    string m = $"WEB_HDR_U{u}_{computed++}";
                    lines.Add($"#define {m} \"{name}\""); unitDefines++;
                    sim.Include(target, Line(), null); lines.Add($"#include {m}");
                }
                else
                {
                    bool relative = Chance(ref rr, o.WebRelativeFrac);
                    sim.Include(target, Line(), null);
                    lines.Add(relative ? $"#include \"../{IncDir}/{name}\"" : $"#include \"{name}\"");
                }
            }

            foreach (var (kind, a) in plan)
                switch (kind)
                {
                    case 0: IncludeLine(rm0 + a, $"rm_{a}.h", ref r); break;
                    case 1: IncludeLine(agg0 + a, $"all_{a}.h", ref r); break;
                    case 2:
                    {
                        int k = r.Next(CfgSwitches);
                        lines.Add($"#if CFG_{k}");
                        if (cfg[k]) sim.Include(rm0 + a, Line(), null);
                        lines.Add($"#include \"rm_{a}.h\"");
                        lines.Add("#endif");
                        break;
                    }
                    case 3: sim.Include(vx0 + a, Line(), null); lines.Add($"#include <vx_{a}.h>"); break;
                    case 4:
                    {
                        // Deep: a register-map header not yet entered whose chain reaches templates, each once. Else direct.
                        int target = -1;
                        if (Chance(ref r, o.WebTemplateReachFrac))
                            for (int tries = 0; tries < 24 && target < 0; tries++)
                            {
                                int cand = tries < 12 ? r.Next(nSeg) * D : r.Next(nH);
                                if (sim.ProbeTemplates(rm0 + cand) is { } got && got.Count > 0 && got.Distinct().Count() == got.Count) target = rm0 + cand;
                            }
                        string name;
                        if (target < 0) { int t = r.Next(nT); target = tm0 + t; name = $"tmpl_{t}.h"; }
                        else name = $"rm_{target - rm0}.h";
                        lines.Add($"#define TNAME CAT(WEB_UNIT, r{a})"); unitDefines++;
                        int at = Line();
                        var made = sim.Include(target, at, $"u{u}_r{a}");
                        lines.Add($"#include \"{name}\"");
                        lines.Add("#undef TNAME");
                        foreach (var (tf, depth) in made)
                        {
                            var f = files[tf];
                            own.Add(new WebInstance
                            {
                                Name = $"u{u}_r{a}_t{f.Tmpl_}",
                                Def = $"{rel}:{at}",
                                TemplateHead = $"{f.Rel}:{f.HeadLine}",
                                IncludedVia = f.Rel,
                                Depth = depth,
                            });
                        }
                        break;
                    }
                }

            if (Chance(ref r, o.WebCommentFrac))
            {
                lines.Add("/* retired, kept for reference:");
                lines.Add($"#include \"rm_{r.Next(nH)}.h\"");
                lines.Add("*/");
                lines.Add($"// #include \"rm_{r.Next(nH)}.h\"");
            }

            // Calls: every own instance, a couple of earlier units' instances (through prototypes), and the previous unit.
            var foreign = new List<WebInstance>();
            if (res.Instances.Count > 0)
                for (int f = r.Next(3); f > 0; f--) foreign.Add(res.Instances[r.Next(res.Instances.Count)]);
            foreign = foreign.Distinct().ToList();
            lines.Add("");
            foreach (var f in foreign) lines.Add($"int {f.Name}(void);");
            if (u > 0) lines.Add($"int web_u{u - 1}(void);");
            lines.Add($"int web_u{u}(void)");
            lines.Add("{");
            lines.Add("    int s = 0;");
            foreach (var x in own.Concat(foreign))
            {
                x.Refs.Add($"{rel}:{Line()}");
                lines.Add($"    s += {x.Name}();");
            }
            if (u > 0) lines.Add($"    s += web_u{u - 1}();");
            lines.Add("    return s;");
            lines.Add("}");

            string path = Path.Combine(unitDir, $"u_{u}.c");
            long ub;
            using (var wr = new WebWriter(path, eol))
            {
                foreach (var l in lines) wr.L(l);
                ub = wr.Bytes;
            }
            res.Units.Add(path);
            res.Instances.AddRange(own);
            var c = sim.End(ub, unitDefines);
            res.Closures.Add(new WebClosure(rel, c.Files, c.Bytes, c.Reads, c.ReadBytes, c.Depth, c.Defines, c.Instances));
            stats.Add("web-units", 1, ub, unitDefines);
        }
        res.TotalBytes = files.Sum(f => f.Bytes) + shadowBytes;
        return res;
    }

    private static int Add(List<WFile> files, WFile f) { files.Add(f); return files.Count - 1; }

    private static Eol PickEol(ref Rng r, GenOptions o)
    {
        ulong x = r.NextULong() >> 11;
        double p = x * (1.0 / (1UL << 53));
        return p < o.WebCrlfFrac ? Eol.Crlf : p < o.WebCrlfFrac + o.WebMixedEolFrac ? Eol.Mixed : Eol.Lf;
    }

    private static void Shuffle<T>(List<T> list, ref Rng r)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = r.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---- the closure simulation, over the model ----

    internal readonly record struct Counts(long Files, long Bytes, long Reads, long ReadBytes, int Depth, long Defines, int Instances);

    private sealed class Sim(List<WFile> files)
    {
        private readonly int[] _stamp = new int[files.Count];   // == _cur: entered (a guarded file's guard is defined)
        private int _cur;
        private long _files, _bytes, _reads, _readBytes, _defines;
        private int _depth, _instances;

        public void Begin()
        {
            _cur++;
            _files = _bytes = _reads = _readBytes = _defines = 0;
            _depth = _instances = 0;
        }

        /// <summary>A top-level include of the unit; returns the template entries made under <paramref name="tname"/>
        /// (file, depth).</summary>
        public List<(int File, int Depth)> Include(int f, int line, string? tname)
        {
            var made = new List<(int, int)>();
            Visit(f, 1, tname, made);
            return made;
        }

        private void Visit(int f, int depth, string? tname, List<(int, int)> made)
        {
            var wf = files[f];
            bool seen = _stamp[f] == _cur;
            if (wf.Guarded && seen) return;          // the guard skips the whole file: not a read
            _reads++; _readBytes += wf.Bytes;
            if (!seen) { _stamp[f] = _cur; _files++; _bytes += wf.Bytes; }
            if (depth > _depth) _depth = depth;
            if (wf.Tmpl)
            {
                if (tname is null) return;           // #ifdef TNAME: nothing
                _instances++;
                made.Add((f, depth));
            }
            _defines += wf.Defines;
            foreach (var g in wf.Includes) Visit(g, depth + 1, tname, made);
        }

        /// <summary>The templates an include of <paramref name="f"/> would enter now, without entering anything.</summary>
        public List<int>? ProbeTemplates(int f)
        {
            if (files[f].Guarded && _stamp[f] == _cur) return null;
            var tmp = new HashSet<int>();
            var got = new List<int>();
            void P(int g)
            {
                var wf = files[g];
                if (wf.Guarded && (_stamp[g] == _cur || tmp.Contains(g))) return;
                tmp.Add(g);
                if (wf.Tmpl) { got.Add(g); return; }
                foreach (var h in wf.Includes) P(h);
            }
            P(f);
            return got;
        }

        public Counts End(long unitBytes, long unitDefines) =>
            new(_files + 1, _bytes + unitBytes, _reads + 1, _readBytes + unitBytes, _depth, _defines + unitDefines, _instances);
    }

    // ---- writers ----

    private static long WriteCfg(string path, bool[] cfg)
    {
        using var w = new WebWriter(path, Eol.Lf);
        w.L("/* web configuration: every unit includes this first */");
        w.L("#ifndef WEB_CFG_H");
        w.L("#define WEB_CFG_H");
        w.L("#define CAT_(a, b) a##_##b");
        w.L("#define CAT(a, b) CAT_(a, b)");
        for (int k = 0; k < cfg.Length; k++) w.L($"#define CFG_{k} {(cfg[k] ? 1 : 0)}");
        w.L("#endif /* WEB_CFG_H */");
        return w.Bytes;
    }

    private static long WriteRm(string path, int n, List<Blk> blocks, Eol eol)
    {
        using var w = new WebWriter(path, eol);
        w.L($"/* web register map {n} (CodeSpawner include web) */");
        w.L($"#ifndef WEB_RM_{n}_H");
        w.L($"#define WEB_RM_{n}_H");
        int computed = 0;
        foreach (var b in blocks)
            switch (b)
            {
                case DefRun d:
                    for (int j = d.From; j < d.From + d.Count; j++) RegLine(w, n, j);
                    break;
                case Inc inc:
                    if (inc.Cond >= 0) w.L($"#if CFG_{inc.Cond}");
                    switch (inc.Form)
                    {
                        case Form.Computed:
                            string m = $"WEB_HDR_{n}_{computed++}";
                            w.L($"#define {m} \"{inc.Name}\"");
                            w.L($"#include {m}");
                            break;
                        case Form.Relative: w.L($"#include \"../{IncDir}/{inc.Name}\""); break;
                        default: w.L($"#include \"{inc.Name}\""); break;
                    }
                    if (inc.Cond >= 0) w.L("#endif");
                    break;
                case Cont c:
                    w.L($"#define WEB{n}_M{c.Idx}(x) \\");
                    for (int j = 0; j < c.Lines; j++) w.L($"    ((x) + 0x{j:X}u) + \\");
                    w.L("    0");
                    break;
                case Comment c:
                    w.L("/* register notes, not includes:");
                    w.L($"#include \"rm_{c.FakeA}.h\"");
                    w.L($"   #define WEB{n}_FAKE{c.Idx} 1");
                    w.L("*/");
                    w.L($"// #include \"rm_{c.FakeB}.h\"");
                    w.L($"#define /*x*/WEB{n}_CA{c.Idx} 1");
                    w.L($"#define WEB{n}_CB{c.Idx}/*x*/1");
                    w.L($"#undef WEB{n}_CB{c.Idx}/*c*/");
                    w.L($"#define WEB{n}_CC{c.Idx} 2 /* spans lines:");
                    w.L($"#include \"rm_{c.FakeB}.h\"");
                    w.L("*/");
                    break;
                case CondDefs c:
                    w.L(c.Ifdef ? $"#ifdef CFG_{c.K}" : $"#if CFG_{c.K}");
                    for (int j = 0; j < c.NTrue; j++) w.L($"#define WEB{n}_I{c.Idx}_{j} 0x{j + 1:X}u");
                    w.L("#else");
                    for (int j = 0; j < c.NFalse; j++) w.L($"#define WEB{n}_I{c.Idx}_{j} 0x0u");
                    w.L("#endif");
                    break;
                case Undef u:
                    for (int j = 0; j < u.Count; j++) w.L($"#define WEB{n}_U{u.Idx}_{j} {j}");
                    for (int j = 0; j < u.Count; j++) w.L($"#undef WEB{n}_U{u.Idx}_{j}");
                    for (int j = 0; j < u.Count; j++) w.L($"#define WEB{n}_U{u.Idx}_{j} ({j} + 1)");
                    break;
            }
        w.L($"#endif /* WEB_RM_{n}_H */");
        return w.Bytes;
    }

    // One register #define: three shapes, all one define per line, names unique within the header.
    private static void RegLine(WebWriter w, int n, int j)
    {
        w.Raw("#define WEB"); w.Num(n); w.Raw("_R"); w.Num(j);
        switch (j % 3)
        {
            case 0: w.Raw("_ADDR 0x"); w.Hex8(unchecked((uint)n * 2654435761u + (uint)j * 40503u)); w.Raw("u"); break;
            case 1: w.Raw("_MASK (1u << "); w.Num(j % 32); w.Raw(")"); break;
            default: w.Raw("_RST 0x0u"); break;
        }
        w.Nl();
    }

    private static long WriteAgg(string path, int k, List<int> heads)
    {
        using var w = new WebWriter(path, Eol.Lf);
        w.L($"/* web aggregator {k} */");
        w.L($"#ifndef WEB_ALL_{k}_H");
        w.L($"#define WEB_ALL_{k}_H");
        w.L($"#define WEB_ALL_{k}_COUNT {heads.Count}");
        foreach (var h in heads) w.L($"#include \"rm_{h}.h\"");
        w.L($"#endif /* WEB_ALL_{k}_H */");
        return w.Bytes;
    }

    /// <summary>A template header: no guard, everything under #ifdef TNAME. Returns its bytes, the primary head's line
    /// and the defines one instantiation executes (the hidden heads' alias macros).</summary>
    private static (long Bytes, int Head, long Defines) WriteTmpl(string path, int t, int heads, double hiddenFrac, Eol eol, ref Rng r)
    {
        var hidden = new List<int>();
        for (int j = 1; j < heads; j++) if (Chance(ref r, hiddenFrac)) hidden.Add(j);
        using var w = new WebWriter(path, eol);
        w.L($"/* web template {t}: instantiated once per TNAME (deliberately no include guard) */");
        w.L("#ifdef TNAME");
        foreach (var j in hidden) w.L($"#define WEBT{t}_A{j} CAT(TNAME, t{t}_a{j})");
        int head = w.Line;
        w.L($"int CAT(TNAME, t{t})(void) {{ return {t}; }}");
        for (int j = 1; j < heads; j++)
            w.L(hidden.Contains(j) ? $"int WEBT{t}_A{j}(void) {{ return {j}; }}" : $"int CAT(TNAME, t{t}_h{j})(void) {{ return {j}; }}");
        foreach (var j in hidden) w.L($"#undef WEBT{t}_A{j}");
        w.L("#endif");
        return (w.Bytes, head, hidden.Count);
    }

    private static long WriteVx(string path, int j)
    {
        using var w = new WebWriter(path, Eol.Lf);
        w.L($"/* web vendor wrapper {j}: the vendor's own vx_{j}.h is next on the include path */");
        w.L($"#ifndef WEB_VX_{j}_H");
        w.L($"#define WEB_VX_{j}_H");
        w.L($"#define WEB_VX_{j}_LOCAL 1");
        w.L($"#include_next <vx_{j}.h>");
        w.L($"#endif /* WEB_VX_{j}_H */");
        return w.Bytes;
    }

    private static long WriteVxVendor(string path, int j)
    {
        using var w = new WebWriter(path, Eol.Lf);
        w.L($"/* vendor vx_{j}.h (reached by #include_next) */");
        w.L($"#ifndef WEB_VXN_{j}_H");
        w.L($"#define WEB_VXN_{j}_H");
        w.L($"#define WEB_VXN_{j}_VENDOR 1");
        for (int k = 0; k < 8; k++) w.L($"#define WEB_VXN_{j}_R{k} 0x{k * 4:X}u");
        w.L($"#endif /* WEB_VXN_{j}_H */");
        return w.Bytes;
    }

    // A decoy sharing inc/rm_N.h's basename: inc/ comes first on the include path, so nothing may reach this.
    private static long WriteShadow(string path, int n)
    {
        using var w = new WebWriter(path, Eol.Lf);
        w.L($"/* shadow of inc/rm_{n}.h: never reached, inc/ is searched first */");
        w.L($"#error \"inc_vnd/rm_{n}.h reached: the include search order is wrong\"");
        for (int k = 0; k < 64; k++) w.L($"#define WEB_SHADOW_{n}_{k} {k}");
        return w.Bytes;
    }

    /// <summary>A buffered ASCII writer that counts bytes and lines and writes each line end as LF, CRLF, or (mixed)
    /// CRLF on every third line.</summary>
    private sealed class WebWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly byte[] _buf = new byte[1 << 16];
        private int _n;
        private readonly Eol _eol;
        public long Bytes { get; private set; }
        /// <summary>The 1-based line the next character lands on.</summary>
        public int Line { get; private set; } = 1;

        public WebWriter(string path, Eol eol)
        {
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
            _eol = eol;
        }

        public void Raw(string s)
        {
            if (_n + s.Length > _buf.Length) Flush();
            if (s.Length > _buf.Length) { var b = Encoding.ASCII.GetBytes(s); _fs.Write(b); Bytes += b.Length; return; }
            for (int i = 0; i < s.Length; i++) _buf[_n++] = (byte)s[i];
            Bytes += s.Length;
        }

        private void Byte(byte b)
        {
            if (_n == _buf.Length) Flush();
            _buf[_n++] = b;
            Bytes++;
        }

        public void Num(long v)
        {
            Span<byte> t = stackalloc byte[20];
            int k = 0;
            if (v == 0) t[k++] = (byte)'0';
            while (v > 0) { t[k++] = (byte)('0' + v % 10); v /= 10; }
            while (k > 0) Byte(t[--k]);
        }

        public void Hex8(uint v)
        {
            for (int s = 28; s >= 0; s -= 4) Byte((byte)"0123456789ABCDEF"[(int)(v >> s) & 0xF]);
        }

        public void Nl()
        {
            if (_eol == Eol.Crlf || _eol == Eol.Mixed && Line % 3 == 0) Byte((byte)'\r');
            Byte((byte)'\n');
            Line++;
        }

        public void L(string s) { Raw(s); Nl(); }

        private void Flush() { _fs.Write(_buf, 0, _n); _n = 0; }

        public void Dispose() { Flush(); _fs.Dispose(); }
    }
}
