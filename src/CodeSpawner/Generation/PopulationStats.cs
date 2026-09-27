using System.Collections.Concurrent;

namespace CodeSpawner.Generation;

/// <summary>
/// Thread-safe per-population tallies (fileCount / totalBytes / identCount) surfaced in the manifest's
/// <c>_meta.populations</c> so a consumer's verify adapter can assert the corpus has the intended SHAPE
/// before trusting any pass/fail. Emitters run in parallel, so updates are interlocked.
/// </summary>
public sealed class PopulationStats
{
    // [0]=files, [1]=bytes, [2]=idents
    private readonly ConcurrentDictionary<string, long[]> _pops = new();

    public void Add(string population, long files, long bytes, long idents)
    {
        var a = _pops.GetOrAdd(population, static _ => new long[3]);
        Interlocked.Add(ref a[0], files);
        Interlocked.Add(ref a[1], bytes);
        Interlocked.Add(ref a[2], idents);
    }

    public IReadOnlyList<(string Name, long Files, long Bytes, long Idents)> Snapshot()
    {
        var list = new List<(string, long, long, long)>();
        foreach (var kv in _pops) list.Add((kv.Key, kv.Value[0], kv.Value[1], kv.Value[2]));
        list.Sort(static (x, y) => string.CompareOrdinal(x.Item1, y.Item1)); // stable, diffable order
        return list;
    }
}
