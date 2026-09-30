using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Profile;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The emit→manifest wiring for the oracle overlay: the graph model must land as the right edges/refs/roots,
/// the linear default must keep its exact spine shape (the byte-identical contract with CodeCarver), and a
/// DAG must actually produce branching + a declared root that reaches the graph.
/// </summary>
public class OracleOverlayTests
{
    private static ManifestModel Emit(TempDir tmp, GenOptions o)
    {
        string outFull = tmp.Path;
        var overlay = new OracleOverlay(o, new ProfileModel(), outFull, new List<string> { outFull });
        return overlay.Emit();
    }

    private static GenOptions Opts(int chain, int fanout = 0)
        => new() { Out = "x", Seed = 1337, WithOracle = true, OracleChain = chain, OracleFanout = fanout };

    [Fact]
    public void LinearDefault_HasChainEdges_HotShared_AndTopRoot()
    {
        using var tmp = new TempDir();
        var m = Emit(tmp, Opts(chain: 5));

        // func_0..func_4 + hot_shared + vendor_gated + oracle_handler_0
        Assert.Equal(8, m.Symbols.Count);
        Assert.Equal(new[] { "hot_shared" }, m.Symbols["func_0"].Edges.ToArray());
        for (int i = 1; i < 5; i++)
            Assert.Equal(new[] { $"func_{i - 1}", "hot_shared" }, m.Symbols[$"func_{i}"].Edges.ToArray());

        Assert.Equal(new[] { "func_4" }, m.Roots.ToArray());   // top of the chain
        Assert.True(m.Symbols["oracle_handler_0"].ExpectedMiss);
        Assert.NotNull(m.Symbols["vendor_gated"].UnreachableRefs);
    }

    [Fact]
    public void EveryEdgeTarget_IsADeclaredSymbol()
    {
        using var tmp = new TempDir();
        var m = Emit(tmp, Opts(chain: 12, fanout: 3));
        foreach (var (_, s) in m.Symbols)
            foreach (var e in s.Edges)
                Assert.True(m.Symbols.ContainsKey(e), $"edge target {e} missing");
    }

    [Fact]
    public void Dag_ProducesBranching_AndRootReachesGraph()
    {
        using var tmp = new TempDir();
        var m = Emit(tmp, Opts(chain: 13, fanout: 3));

        // At least one func has >1 direct call edge (excluding the universal hot_shared) → real branching.
        bool branched = m.Symbols
            .Where(kv => kv.Key.StartsWith("func_"))
            .Any(kv => kv.Value.Edges.Count(e => e != "hot_shared") >= 2);
        Assert.True(branched, "a DAG oracle must branch");

        Assert.Equal(new[] { "func_0" }, m.Roots.ToArray());

        // BFS over func→func edges from the root reaches every func symbol.
        var funcs = m.Symbols.Keys.Where(k => k.StartsWith("func_")).ToHashSet();
        var seen = new HashSet<string>();
        var q = new Queue<string>(m.Roots);
        while (q.Count > 0)
        {
            var f = q.Dequeue();
            if (!seen.Add(f)) continue;
            foreach (var e in m.Symbols[f].Edges)
                if (e.StartsWith("func_") && !seen.Contains(e)) q.Enqueue(e);
        }
        Assert.Equal(funcs.Count, seen.Count); // no dial → whole graph reachable
    }

    [Fact]
    public void IndirectEdges_AreEmitted_Scattered_AndTargetsConsistent()
    {
        using var tmp = new TempDir();
        var o = new GenOptions
        {
            Out = "x", Seed = 1337, WithOracle = true,
            OracleChain = 15, OracleFanout = 2, OracleReachableFrac = 0.5, OracleIndirect = 8,
        };
        var m = Emit(tmp, o);

        var edges = m.Symbols.SelectMany(kv => kv.Value.IndirectEdges).ToList();
        Assert.Equal(8, edges.Count);
        Assert.True(edges.Select(e => e.Via).Distinct().Count() >= 2, "via kinds should be scattered");
        Assert.Contains(edges, e => e.Dispatched);
        Assert.Contains(edges, e => !e.Dispatched);
        Assert.Contains(edges, e => e.Resolved);
        Assert.Contains(edges, e => !e.Resolved);

        // closed-world: resolved target ⇒ declared symbol; unresolved (external) ⇒ NOT a declared symbol.
        foreach (var e in edges)
            Assert.Equal(e.Resolved, m.Symbols.ContainsKey(e.Target));
    }

    [Fact]
    public void TaxEdge_ReachableSource_HasUndispatchedIndirectEdge()
    {
        using var tmp = new TempDir();
        var o = new GenOptions
        {
            Out = "x", Seed = 1337, WithOracle = true,
            OracleChain = 15, OracleFanout = 2, OracleReachableFrac = 0.5, OracleIndirect = 8,
        };
        var m = Emit(tmp, o);

        // Reachable set from the declared roots over direct edges (the same set the carver keeps).
        var reachable = new HashSet<string>();
        var q = new Queue<string>(m.Roots);
        while (q.Count > 0)
        {
            var f = q.Dequeue();
            if (!reachable.Add(f)) continue;
            foreach (var e in m.Symbols[f].Edges)
                if (m.Symbols.ContainsKey(e)) q.Enqueue(e);
        }

        // The indirection TAX: a reachable function that takes the address of a target it never dispatches is
        // a legitimate over-keep no carver can drop. This quadrant must be non-empty, else the whole
        // `dispatched` bit is untestable. Regression guard — dispatched:false used to be welded to dead
        // sources, so no reachable source ever carried one (CodeCarver, 2026-09-30).
        bool taxPresent = m.Symbols.Any(kv =>
            reachable.Contains(kv.Key) && kv.Value.IndirectEdges.Any(e => !e.Dispatched));
        Assert.True(taxPresent, "a reachable source must carry a never-dispatched indirect edge (the tax case)");
    }

    [Fact]
    public void IndirectTruthSha_SurvivesManifestRoundTrip()
    {
        using var tmp = new TempDir();
        var o = new GenOptions
        {
            Out = "x", Seed = 7, WithOracle = true, OracleChain = 12, OracleFanout = 3, OracleIndirect = 6,
        };
        var m = Emit(tmp, o);
        string path = tmp.File("m.json");
        ManifestWriter.Write(m, path);

        var loaded = ManifestReader.Load(path);
        // write→read preserves the indirect edges + roots exactly, so the component digest reproduces.
        Assert.Equal(IndirectDigest.Compute(m.Symbols, m.Roots), IndirectDigest.Compute(loaded.Symbols, loaded.Roots));
    }

    [Fact]
    public void NoIndirect_DefaultOracle_HasNoIndirectEdges()
    {
        using var tmp = new TempDir();
        var m = Emit(tmp, Opts(chain: 6));
        Assert.All(m.Symbols.Values, s => Assert.Empty(s.IndirectEdges));
        Assert.False(IndirectDigest.Any(m.Symbols));
    }

    [Fact]
    public void OracleBytes_EmitsPerSymbolMass_AndConsistentTotal()
    {
        using var tmp = new TempDir();
        var o = new GenOptions { Out = "x", Seed = 1337, WithOracle = true, OracleChain = 6, OracleBytes = true };
        var m = Emit(tmp, o);

        Assert.All(m.Symbols.Where(kv => kv.Key.StartsWith("func_")), kv => Assert.True(kv.Value.Bytes > 0));
        Assert.Equal(m.Symbols.Values.Sum(s => s.Bytes), m.TotalOracleBytes); // total is the exact denominator
    }

    [Fact]
    public void ByteMass_IsAbsent_ByDefault()
    {
        using var tmp = new TempDir();
        var m = Emit(tmp, Opts(chain: 6)); // OracleBytes off
        Assert.All(m.Symbols.Values, s => Assert.Equal(0, s.Bytes));
        Assert.Equal(0, m.TotalOracleBytes);
    }
}
