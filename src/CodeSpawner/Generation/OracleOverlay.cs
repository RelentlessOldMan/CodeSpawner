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
        File.WriteAllText(hotPath, "int hot_shared(int x) { return x + 1; }\n", Encodings.Utf8NoBom);

        var defLine = new int[n];
        var hotLine = new int[n];             // site where hot_shared is referenced (in src_i.c)
        var paths = new string[n];
        var refsOf = new List<string>[n];     // refsOf[c] = call sites (in callers' files) invoking func_c
        for (int i = 0; i < n; i++) { paths[i] = Path.Combine(_dir, $"src_{i}.c"); refsOf[i] = new List<string>(); }

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
            for (long k = 0; k < bodyPad; k++) lines.Add($"    acc ^= {k % 97};"); // filler: no calls, spine-invariant
            lines.Add("    return acc;");
            lines.Add("}");
            File.WriteAllText(paths[i], string.Join('\n', lines) + "\n", Encodings.Utf8NoBom);
        }

        // vendor_gated: the call is behind #ifdef VENDOR_OK (never defined) with a vendor header absent from
        // the tree -> an UNREACHABLE reference (negative oracle). Matches the verify convention exactly.
        string vendorPath = Path.Combine(_dir, "vendor.c");
        File.WriteAllText(vendorPath,
            "int vendor_gated(int x) { return x - 1; }\n", Encodings.Utf8NoBom);
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
        File.WriteAllText(mkPath,
            $"#define {PathologicalSymbolEmitter.GenMacro}(id) int oracle_handler_##id(int x) {{ return x + (id); }}\n"
            + $"{PathologicalSymbolEmitter.GenMacro}(0)\n", Encodings.Utf8NoBom);

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
            m.Symbols[$"func_{i}"] = e;
        }

        var hot = new SymbolEntry { Def = $"{Rel(hotPath)}:1" };
        for (int i = 0; i < n; i++) hot.Refs.Add($"{Rel(paths[i])}:{hotLine[i]}");
        m.Symbols["hot_shared"] = hot;

        var vg = new SymbolEntry { Def = $"{Rel(vendorPath)}:1", UnreachableRefs = new List<string> { $"{Rel(consumerPath)}:{unreachLine}" } };
        m.Symbols["vendor_gated"] = vg;

        m.Symbols["oracle_handler_0"] = new SymbolEntry { Def = $"{Rel(mkPath)}:2", ExpectedMiss = true };

        // Declared entry points for the reachability closure. Emitted even for the linear default.
        foreach (int r in graph.Roots) m.Roots.Add($"func_{r}");
        foreach (var root in m.Roots)
            if (!m.Symbols.ContainsKey(root))
                throw new InvalidOperationException($"oracle root '{root}' is not a declared symbol");

        return m;
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
