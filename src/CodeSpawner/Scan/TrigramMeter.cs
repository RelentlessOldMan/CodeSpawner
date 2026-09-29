namespace CodeSpawner.Scan;

/// <summary>
/// Exact distinct-count of byte 3-grams via a 2^24-bit (2 MB) presence bitset — one bit per possible
/// trigram (256^3). Cardinality = popcount; occurrences are tallied separately. Bounded, deterministic, and
/// content-free: only "which trigrams appeared" and "how many," never the bytes themselves. A meter is
/// reused per archetype (Reset) and a second instance unions the whole tree for the global roll-up.
/// </summary>
public sealed class TrigramMeter
{
    private readonly ulong[] _bits = new ulong[(1 << 24) / 64]; // 2^24 bits = 262144 ulongs = 2 MB
    private long _occurrences;

    public long Occurrences => _occurrences;

    /// <summary>Feed a byte span; sets a bit per distinct trigram and counts occurrences (= len-2).</summary>
    public void Add(ReadOnlySpan<byte> data, TrigramMeter? alsoInto = null)
    {
        if (data.Length < 3) return;
        int a = data[0], b = data[1];
        for (int i = 2; i < data.Length; i++)
        {
            int c = data[i];
            int tri = (a << 16) | (b << 8) | c;
            _bits[tri >> 6] |= 1UL << (tri & 63);
            alsoInto?.Set(tri);
            a = b; b = c;
        }
        long occ = data.Length - 2;
        _occurrences += occ;
        if (alsoInto is not null) alsoInto._occurrences += occ;
    }

    private void Set(int tri) => _bits[tri >> 6] |= 1UL << (tri & 63);

    /// <summary>Distinct trigrams seen so far (popcount of the presence bitset).</summary>
    public long DistinctCount()
    {
        long n = 0;
        foreach (ulong w in _bits) n += System.Numerics.BitOperations.PopCount(w);
        return n;
    }

    public void Reset()
    {
        Array.Clear(_bits);
        _occurrences = 0;
    }
}
