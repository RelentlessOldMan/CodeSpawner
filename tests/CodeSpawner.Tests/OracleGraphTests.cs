using CodeSpawner.Cli;
using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The oracle call-graph model (phase 1+3). The linear DEFAULT must stay a chain (existing runs are
/// byte-identical, guarded separately), and the DAG / reachable-fraction knobs must produce the shape
/// CodeCarver's correctness oracle needs: bounded fan-out, real diamonds, and a graded reachable subset.
/// </summary>
public class OracleGraphTests
{
    private static GenOptions Opts(int chain, int fanout = 0, int depth = 0, int shared = 0, double frac = 0)
        => new()
        {
            Out = "x", Seed = 1337,
            OracleChain = chain, OracleFanout = fanout, OracleDepth = depth,
            OracleSharedLeaves = shared, OracleReachableFrac = frac,
        };

    private static int[] InDegrees(OracleGraph g)
    {
        var indeg = new int[g.Count];
        for (int i = 0; i < g.Count; i++) foreach (int c in g.Children[i]) indeg[c]++;
        return indeg;
    }

    [Fact]
    public void LinearDefault_IsAChain_RootAtTop_AllReachable()
    {
        var g = OracleGraph.Build(Opts(chain: 6));
        Assert.Equal(6, g.Count);
        Assert.Empty(g.Children[0]);
        for (int i = 1; i < 6; i++) Assert.Equal(new[] { i - 1 }, g.Children[i].ToArray());
        Assert.Equal(new[] { 5 }, g.Roots.ToArray());       // top of the chain reaches all
        Assert.Equal(6, g.ReachableCount);
    }

    [Fact]
    public void Chain_FloorsAtTwo()
        => Assert.Equal(2, OracleGraph.Build(Opts(chain: 1)).Count);

    [Fact]
    public void Dag_BoundsOutDegree_RootZero_AllReachable()
    {
        var g = OracleGraph.Build(Opts(chain: 20, fanout: 3));
        Assert.Equal(20, g.Count);
        Assert.Equal(new[] { 0 }, g.Roots.ToArray());
        foreach (var kids in g.Children) Assert.True(kids.Count <= 3, "out-degree exceeded fanout");
        Assert.Equal(20, g.ReachableCount); // a pure tree from the root reaches every node
    }

    [Fact]
    public void Dag_SharedLeaves_CreateDiamonds()
    {
        var g = OracleGraph.Build(Opts(chain: 10, fanout: 2, shared: 3));
        Assert.Equal(13, g.Count); // 10 tree + 3 shared sink nodes
        int diamonds = InDegrees(g).Count(d => d >= 2);
        Assert.True(diamonds >= 3, $"expected ≥3 shared-in-degree nodes, saw {diamonds}");
        Assert.Equal(13, g.ReachableCount); // leaves hang under reachable parents
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void ReachableFraction_ApproximatesTarget(double frac)
    {
        var g = OracleGraph.Build(Opts(chain: 40, fanout: 2, frac: frac));
        double got = (double)g.ReachableCount / g.Count;
        Assert.InRange(got, frac - 0.06, frac + 0.06);
        Assert.True(g.Count > g.ReachableCount, "dial must add unreachable dead nodes");
    }

    [Fact]
    public void ReachableFraction_DeadNodes_AreUnreachable()
    {
        var g = OracleGraph.Build(Opts(chain: 20, fanout: 2, frac: 0.5));
        // Every dead (unreachable) node must be outside the closure from roots.
        int unreachable = g.Reachable.Count(r => !r);
        Assert.True(unreachable > 0);
        // and no root is dead
        foreach (int r in g.Roots) Assert.True(g.Reachable[r]);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = OracleGraph.Build(Opts(chain: 15, fanout: 3, shared: 4, frac: 0.5));
        var b = OracleGraph.Build(Opts(chain: 15, fanout: 3, shared: 4, frac: 0.5));
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a.Children[i].ToArray(), b.Children[i].ToArray());
    }

    [Fact]
    public void SharedLeafPlacement_VariesWithSeed()
    {
        var a = OracleGraph.Build(Opts(chain: 12, fanout: 2, shared: 5));
        var o2 = Opts(chain: 12, fanout: 2, shared: 5); o2.Seed = 9999;
        var b = OracleGraph.Build(o2);
        // Same node count, but the diamond wiring should differ across seeds.
        Assert.Equal(a.Count, b.Count);
        bool anyDiff = false;
        for (int i = 0; i < a.Count && !anyDiff; i++)
            if (!a.Children[i].SequenceEqual(b.Children[i])) anyDiff = true;
        Assert.True(anyDiff, "shared-leaf placement should vary with seed");
    }
}
