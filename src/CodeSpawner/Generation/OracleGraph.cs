using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// Deterministic call-graph model for the <c>--with-oracle</c> overlay. Nodes are <c>func_i</c>; edges are
/// DIRECT call edges (hot_shared is a universal callee the overlay adds to every node, not modeled here).
///
/// Shape is knob-driven but the DEFAULT is the historical linear spine so existing runs stay byte-identical:
///  - fanout ≤ 0 → linear chain <c>func_i → func_{i-1}</c>, root = the top of the chain (reaches all).
///  - fanout &gt; 0 → a complete F-ary tree over the node budget (+ optional depth cap), plus shared sink
///    nodes multiple callers edge into (diamonds), so a reachable subset has real precision hazards.
///  - reachableFrac ∈ (0,1) → append DEAD nodes (unreachable from the root) as short dead chains until
///    reachable/total ≈ the target fraction → graded reduction assertions.
///
/// Reachability here is the closure over direct edges from <see cref="Roots"/>; phase 2 folds indirect edges
/// into the same closure. See docs/oracle-v1-design.md.
/// </summary>
public sealed class OracleGraph
{
    /// <summary>Direct-call child node indices per node (call-graph edges, excluding hot_shared).</summary>
    public List<int>[] Children { get; }
    /// <summary>Declared entry-point node indices.</summary>
    public List<int> Roots { get; }
    /// <summary>Closure over <see cref="Children"/> from <see cref="Roots"/>.</summary>
    public bool[] Reachable { get; }

    public int Count => Children.Length;
    public static string Name(int i) => $"func_{i}";

    public int ReachableCount
    {
        get { int c = 0; foreach (bool r in Reachable) if (r) c++; return c; }
    }

    private OracleGraph(List<int>[] children, List<int> roots)
    {
        Children = children;
        Roots = roots;
        Reachable = ComputeReachable(children, roots);
    }

    public static OracleGraph Build(GenOptions o)
    {
        int live = Math.Max(2, o.OracleChain);
        return o.OracleFanout <= 0 ? BuildLinear(live) : BuildDag(o, live);
    }

    private static OracleGraph BuildLinear(int n)
    {
        var children = NewChildren(n);
        for (int i = 1; i < n; i++) children[i].Add(i - 1);   // func_i -> func_{i-1}
        return new OracleGraph(children, new List<int> { n - 1 }); // top of the chain reaches everything
    }

    private static OracleGraph BuildDag(GenOptions o, int live)
    {
        int f = Math.Max(1, o.OracleFanout);

        // Optional depth cap: shrink the node budget to a full F-ary tree of the requested depth.
        if (o.OracleDepth > 0)
        {
            long cap = 0, layer = 1;
            for (int d = 0; d <= o.OracleDepth; d++) { cap += layer; layer *= f; if (cap >= live) break; }
            live = (int)Math.Max(2, Math.Min(live, cap));
        }

        var kids = new List<List<int>>(live);
        for (int i = 0; i < live; i++) kids.Add(new List<int>());
        for (int i = 1; i < live; i++) kids[(i - 1) / f].Add(i);   // complete F-ary tree, out-degree ≤ f

        // Shared leaves (diamonds): S sink nodes, each edged into by two distinct existing nodes.
        int s = Math.Max(0, o.OracleSharedLeaves);
        var rng = Rng.For(o.Seed, Category.ProfileOracle, 1);
        for (int k = 0; k < s; k++)
        {
            int leaf = kids.Count;
            kids.Add(new List<int>());
            int p1 = rng.Next(live);
            int p2 = rng.Next(live);
            if (p2 == p1) p2 = (p2 + 1) % live;
            kids[p1].Add(leaf);
            kids[p2].Add(leaf);
        }
        int liveTotal = kids.Count; // all reachable from node 0 (tree + leaves under reachable parents)

        // Reachable-fraction dial: append dead nodes (never linked from a reachable node) as short dead
        // chains until reachable/total ≈ frac.
        double frac = o.OracleReachableFrac;
        if (frac > 0 && frac < 1)
        {
            int total = (int)Math.Round(liveTotal / frac, MidpointRounding.AwayFromZero);
            int dead = Math.Max(0, total - liveTotal);
            int baseIdx = kids.Count;
            for (int j = 0; j < dead; j++) kids.Add(new List<int>());
            for (int j = 1; j < dead; j++)
                if (j % 4 != 0) kids[baseIdx + j].Add(baseIdx + j - 1); // chains of ~4 within the dead block
        }

        var children = new List<int>[kids.Count];
        for (int i = 0; i < kids.Count; i++) children[i] = kids[i];
        return new OracleGraph(children, new List<int> { 0 });
    }

    private static List<int>[] NewChildren(int n)
    {
        var c = new List<int>[n];
        for (int i = 0; i < n; i++) c[i] = new List<int>();
        return c;
    }

    private static bool[] ComputeReachable(List<int>[] children, List<int> roots)
    {
        var seen = new bool[children.Length];
        var stack = new Stack<int>();
        foreach (int r in roots) if (r >= 0 && r < seen.Length && !seen[r]) { seen[r] = true; stack.Push(r); }
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            foreach (int c in children[n]) if (!seen[c]) { seen[c] = true; stack.Push(c); }
        }
        return seen;
    }
}
