using System.Globalization;
using CodeSpawner.Profile;

namespace CodeSpawner.Scan;

/// <summary>
/// Builds the include fan-out / unresolved-include-risk stats (CodeCompass, 2026-09-29). Resolution is
/// within-tree by BASENAME only — the signal that matters is system/vendor headers genuinely outside the
/// tree (the false-zero driver). Fan-out is a bounded 2-hop expansion over the sampled files' include maps
/// (one-hop + unresolved-rate is the guaranteed floor for unsampled targets). No names/paths are emitted —
/// only distributions and rates.
/// </summary>
public sealed class IncludeAggregate
{
    private readonly Dictionary<string, List<string>> _fileIncludes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string File, List<string> Targets)> _files = new();
    private Dictionary<string, int> _basenameCounts = new(StringComparer.OrdinalIgnoreCase);

    private long _totalTargets, _unresolvedTargets, _ambiguity;

    public void AddFile(string fileBasename, List<string> targets, Dictionary<string, int> basenameCounts)
    {
        _basenameCounts = basenameCounts;
        _fileIncludes[fileBasename] = targets;
        _files.Add((fileBasename, targets));
        foreach (var t in targets)
        {
            _totalTargets++;
            if (basenameCounts.TryGetValue(t, out int c))
            {
                if (c > 1) _ambiguity++;               // basename resolves to >1 file: -I pick is ambiguous
            }
            else _unresolvedTargets++;                  // not in tree: system/vendor header (unresolved risk)
        }
    }

    public IncludeStats Build(int hops)
    {
        var fanoutHist = new SortedDictionary<int, long>();
        foreach (var (file, targets) in _files)
        {
            int fanout = Reachable(file, targets, hops);
            Bump(fanoutHist, Bucket(fanout));
        }

        var stats = new IncludeStats
        {
            UnresolvedIncludeRate = _totalTargets > 0 ? (double)_unresolvedTargets / _totalTargets : 0,
            DuplicateBasenameAmbiguity = _ambiguity,
            Hops = hops,
        };
        foreach (var kv in fanoutHist) stats.FanoutHistogram.Add(BucketLabel(kv.Key), kv.Value);
        return stats;
    }

    // Distinct in-tree basenames reachable within `hops` (BFS over the sampled include map).
    private int Reachable(string file, List<string> directTargets, int hops)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<string>();
        foreach (var t in directTargets)
            if (_basenameCounts.ContainsKey(t) && seen.Add(t)) frontier.Add(t);

        for (int h = 1; h < hops && frontier.Count > 0; h++)
        {
            var next = new List<string>();
            foreach (var f in frontier)
                if (_fileIncludes.TryGetValue(f, out var deps))
                    foreach (var d in deps)
                        if (_basenameCounts.ContainsKey(d) && seen.Add(d)) next.Add(d);
            frontier = next;
        }
        return seen.Count;
    }

    private static void Bump(SortedDictionary<int, long> d, int k) { d.TryGetValue(k, out long v); d[k] = v + 1; }
    private static int Bucket(int n) => n <= 4 ? 4 : n <= 16 ? 16 : n <= 64 ? 64 : 65;
    private static string BucketLabel(int b) => b switch { 4 => "0-4", 16 => "5-16", 64 => "17-64", _ => "65+" };
}
