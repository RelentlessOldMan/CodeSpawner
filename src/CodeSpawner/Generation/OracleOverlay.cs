using CodeSpawner.Cli;
using CodeSpawner.Manifest;
using CodeSpawner.Profile;

namespace CodeSpawner.Generation;

/// <summary>
/// Overlays the ground-truth spine on a regenerated tree: a <c>func_i</c> call graph (linear chain by
/// default; a seeded DAG under --oracle-fanout, see <see cref="OracleGraph"/>), a shared hot symbol, an
/// unreachable vendor-gated symbol, and an expected-miss token-paste symbol — with a v1 manifest — so
/// <c>verify</c> and a carve/soundness oracle run at the profile's cost/shape. Declared entry points land in
/// <c>_meta.roots</c>. The spine (edges/refs) is INVARIANT to size; <c>--oracle-scale</c> only inflates
/// function BODIES with filler that adds no edges, so reproducing realistic parse cost never perturbs
/// reachability (CodeCarver, 2026-09-29 / 2026-09-30).
/// </summary>
public sealed class OracleOverlay
{
    private readonly GenOptions _o;
    private readonly ProfileModel _profile;
    private readonly string _outFull;
    private readonly string _dir;

    public OracleOverlay(GenOptions o, ProfileModel profile, string outFull, List<string> dirs)
    {
        _o = o;
        _profile = profile;
        _outFull = outFull;
        _dir = Path.Combine(outFull, "_oracle");
        Directory.CreateDirectory(_dir);
    }

    public ManifestModel Emit()
    {
        var graph = OracleGraph.Build(_o);
        int n = graph.Count;
        long bodyPad = BodyPadStatements();

        // hot_shared: defined once, called from every func_i.
        string hotPath = Path.Combine(_dir, "hot_shared.c");
        const string hotContent = "int hot_shared(int x) { return x + 1; }\n";
        File.WriteAllText(hotPath, hotContent, Encodings.Utf8NoBom);

        var defLine = new int[n];
        var hotLine = new int[n];             // site where hot_shared is referenced (in src_i.c)
        var paths = new string[n];
        var funcBytes = new long[n];          // byte mass of each func_i definition span (--oracle-bytes)
        var refsOf = new List<string>[n];     // refsOf[c] = call sites (in callers' files) invoking func_c
        var indirectOf = new List<IndirectEdge>[n];  // indirect edges owned by func_i
        for (int i = 0; i < n; i++)
        {
            paths[i] = Path.Combine(_dir, $"src_{i}.c");
            refsOf[i] = new List<string>();
            indirectOf[i] = new List<IndirectEdge>();
        }

        // Indirect-edge plan (phase 2): scatter fnptr/vector-table/init_array edges across the graph, spread
        // over {reachable-source, dead-source} × {dispatched, not}, some resolved (in-corpus) / some external.
        var indirectBySrc = PlanIndirect(graph, out var resolvedTargets);

        for (int i = 0; i < n; i++)
        {
            var children = graph.Children[i];
            var lines = new List<string> { "#include <stddef.h>", $"int func_{i}(int x);", "int hot_shared(int x);" };
            foreach (int c in children) lines.Add($"int func_{c}(int x);");

            lines.Add($"int func_{i}(int x) {{");
            defLine[i] = lines.Count;                 // 1-based line of the definition opener
            lines.Add("    int acc = x;");
            foreach (int c in children)               // one call per direct edge (linear = single child)
            {
                lines.Add($"    acc += func_{c}(x - 1);");
                refsOf[c].Add($"{Rel(paths[i])}:{lines.Count}"); // func_c is referenced here by func_i
            }
            lines.Add("    acc += hot_shared(x);"); hotLine[i] = lines.Count;
            if (indirectBySrc.TryGetValue(i, out var plan))
                foreach (var pe in plan) EmitIndirectConstruct(lines, pe, indirectOf[i]);
            for (long k = 0; k < bodyPad; k++) lines.Add($"    acc ^= {k % 97};"); // filler: no calls, spine-invariant
            lines.Add("    return acc;");
            lines.Add("}");
            File.WriteAllText(paths[i], string.Join('\n', lines) + "\n", Encodings.Utf8NoBom);
            if (_o.OracleBytes)                       // byte mass = the definition span (opener .. closing brace)
            {
                long b = 0;
                for (int li = defLine[i] - 1; li < lines.Count; li++) b += lines[li].Length + 1;
                funcBytes[i] = b;
            }
        }

        // vendor_gated: the call is behind #ifdef VENDOR_OK (never defined) with a vendor header absent from
        // the tree -> an UNREACHABLE reference (negative oracle). Matches the verify convention exactly.
        string vendorPath = Path.Combine(_dir, "vendor.c");
        const string vendorContent = "int vendor_gated(int x) { return x - 1; }\n";
        File.WriteAllText(vendorPath, vendorContent, Encodings.Utf8NoBom);
        string consumerPath = Path.Combine(_dir, "vendor_consumer.c");
        var consumerLines = new List<string>
        {
            "#include \"missing_vendor_sdk.h\"   /* not in tree */",
            "#ifdef VENDOR_OK",
            "int vendor_consumer(int x) {",
            "    return vendor_gated(x);",
        };
        int unreachLine = consumerLines.Count;    // the guarded (unreachable) reference site
        consumerLines.Add("}");
        consumerLines.Add("#endif");
        File.WriteAllText(consumerPath, string.Join('\n', consumerLines) + "\n", Encodings.Utf8NoBom);

        // expectedMiss: a token-paste symbol whose real name a lexical index won't recover. The def line is
        // the CS_MK_HANDLER invocation (line 2) — it carries the generator macro, never the fused name.
        string mkPath = Path.Combine(_dir, "handlers.h");
        string mkContent =
            $"#define {PathologicalSymbolEmitter.GenMacro}(id) int oracle_handler_##id(int x) {{ return x + (id); }}\n"
            + $"{PathologicalSymbolEmitter.GenMacro}(0)\n";
        File.WriteAllText(mkPath, mkContent, Encodings.Utf8NoBom);

        // --- Build the manifest. ---
        var m = new ManifestModel
        {
            GeneratorVersion = Program.Version,
            Seed = _o.Seed,
            CorpusRoot = _outFull,
        };

        for (int i = 0; i < n; i++)
        {
            var e = new SymbolEntry { Def = $"{Rel(paths[i])}:{defLine[i]}" };
            foreach (int c in graph.Children[i]) e.Edges.Add($"func_{c}");   // direct call edges (DAG or chain)
            e.Edges.Add("hot_shared");
            e.Refs.AddRange(refsOf[i]);                                       // sites where callers invoke func_i
            e.IndirectEdges.AddRange(indirectOf[i]);                          // fnptr/vtable/init_array edges
            if (_o.OracleBytes) e.Bytes = funcBytes[i];
            m.Symbols[$"func_{i}"] = e;
        }

        var hot = new SymbolEntry { Def = $"{Rel(hotPath)}:1" };
        for (int i = 0; i < n; i++) hot.Refs.Add($"{Rel(paths[i])}:{hotLine[i]}");
        if (_o.OracleBytes) hot.Bytes = hotContent.Length;
        m.Symbols["hot_shared"] = hot;

        var vg = new SymbolEntry { Def = $"{Rel(vendorPath)}:1", UnreachableRefs = new List<string> { $"{Rel(consumerPath)}:{unreachLine}" } };
        if (_o.OracleBytes) vg.Bytes = vendorContent.Length;
        m.Symbols["vendor_gated"] = vg;

        var handler = new SymbolEntry { Def = $"{Rel(mkPath)}:2", ExpectedMiss = true };
        if (_o.OracleBytes) handler.Bytes = mkContent.Length;
        m.Symbols["oracle_handler_0"] = handler;

        // Resolved indirect-edge targets: real functions whose address is taken. Defined in one file so each
        // is a declared symbol with a def site (external/unresolved targets are NOT defined and get no symbol).
        if (resolvedTargets.Count > 0)
        {
            string itgtPath = Path.Combine(_dir, "indirect_targets.c");
            var tlines = new List<string>();
            var tgtLine = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in resolvedTargets)
            {
                tlines.Add($"int {t}(int x) {{ return x + 1; }}");
                tgtLine[t] = tlines.Count; // 1-based
            }
            File.WriteAllText(itgtPath, string.Join('\n', tlines) + "\n", Encodings.Utf8NoBom);
            foreach (var t in resolvedTargets)
            {
                var sym = new SymbolEntry { Def = $"{Rel(itgtPath)}:{tgtLine[t]}" };
                if (_o.OracleBytes) sym.Bytes = tlines[tgtLine[t] - 1].Length + 1;
                m.Symbols[t] = sym;
            }
        }

        // Declared entry points for the reachability closure. Emitted even for the linear default.
        foreach (int r in graph.Roots) m.Roots.Add($"func_{r}");
        foreach (var root in m.Roots)
            if (!m.Symbols.ContainsKey(root))
                throw new InvalidOperationException($"oracle root '{root}' is not a declared symbol");

        // Byte-mass total (phase 4): the denominator for a byte-based reduction assertion.
        if (_o.OracleBytes) m.TotalOracleBytes = m.Symbols.Values.Sum(s => s.Bytes);

        return m;
    }

    private readonly record struct IndirectPlan(string Target, IndirectVia Via, bool Dispatched, bool Resolved, int K);

    // Deterministically assign indirect edges to source nodes, scattered across {reachable, dead} sources and
    // {dispatched, not}, ~2/3 resolved (in-corpus) / ~1/3 external, so a graded corpus carries genuine
    // over-keep (reachable + never-dispatched) rather than a clean chain. Out: the resolved target names.
    private Dictionary<int, List<IndirectPlan>> PlanIndirect(OracleGraph graph, out List<string> resolvedTargets)
    {
        var bySrc = new Dictionary<int, List<IndirectPlan>>();
        resolvedTargets = new List<string>();
        int count = Math.Max(0, _o.OracleIndirect);
        if (count == 0) return bySrc;

        var reach = new List<int>();
        var dead = new List<int>();
        for (int i = 0; i < graph.Count; i++) (graph.Reachable[i] ? reach : dead).Add(i);
        var rng = Rng.For(_o.Seed, Category.ProfileOracle, 2);

        for (int k = 0; k < count; k++)
        {
            var via = (IndirectVia)(k % 3);
            bool dispatched = (k & 1) == 0;                   // bit 0
            bool resolved = (k % 3 != 2);                     // ~2/3 in-corpus, 1/3 external
            // Source pool keyed on an INDEPENDENT bit (bit 1), NOT the dispatched bit, so the four
            // {reachable, dead} × {dispatched, not} quadrants cycle every 4 edges. Critically this makes the
            // indirection-TAX case — a REACHABLE source with a never-dispatched edge, a legitimate over-keep
            // a carver cannot drop — appear at k=1. It was previously impossible: dispatched:false was welded
            // to the dead pool (both keyed on k%2), so every undispatched edge sat in dead islands the carve
            // drops anyway, leaving the tax untestable (CodeCarver correctness pass, 2026-09-30).
            bool wantDead = ((k >> 1) & 1) == 1;
            var pool = (wantDead && dead.Count > 0) ? dead : reach;
            int src = pool[rng.Next(pool.Count)];
            string target = resolved ? $"itgt_{k}" : $"iext_{k}";
            if (resolved) resolvedTargets.Add(target);
            if (!bySrc.TryGetValue(src, out var lst)) bySrc[src] = lst = new List<IndirectPlan>();
            lst.Add(new IndirectPlan(target, via, dispatched, resolved, k));
        }
        return bySrc;
    }

    // Emit the real C construct (address-taken via fnptr / vector table / init_array; dispatched adds an
    // indirect call through the slot) and record the edge. verify never compiles this, but it is plausible C.
    private static void EmitIndirectConstruct(List<string> lines, IndirectPlan pe, List<IndirectEdge> record)
    {
        lines.Add($"    int {pe.Target}(int);");   // decl; target defined in indirect_targets.c, or external
        string slot = $"ip_{pe.K}";
        switch (pe.Via)
        {
            case IndirectVia.VectorTable:
                lines.Add($"    int (*{slot}[1])(int) = {{ &{pe.Target} }};");
                if (pe.Dispatched) lines.Add($"    acc += {slot}[0](x);");
                break;
            case IndirectVia.InitArray:
                lines.Add($"    int (*{slot})(int) = &{pe.Target}; /* .init_array */");
                if (pe.Dispatched) lines.Add($"    acc += {slot}(x);");
                break;
            default: // FnPtr
                lines.Add($"    int (*{slot})(int) = &{pe.Target};");
                if (pe.Dispatched) lines.Add($"    acc += {slot}(x);");
                break;
        }
        record.Add(new IndirectEdge { Target = pe.Target, Via = pe.Via, Dispatched = pe.Dispatched, Resolved = pe.Resolved });
    }

    // With --oracle-scale, pad each body to the measured .c size distribution; else compact (no filler).
    private long BodyPadStatements()
    {
        if (!_o.OracleScale) return 0;
        long targetBytes = 0;
        foreach (var a in _profile.Archetypes)
            if (a.Extension == ".c") { targetBytes = a.SizeDistribution.P50; break; }
        if (targetBytes <= 0) return 0;
        // Each filler statement is ~14 bytes ("    acc ^= NN;\n"); leave headroom for the fixed spine lines.
        long pad = Math.Max(0, (targetBytes - 200) / 14);
        return Math.Min(pad, 5_000_000); // safety cap
    }

    private string Rel(string abs) => PathUtil.Rel(_outFull, abs);
}
