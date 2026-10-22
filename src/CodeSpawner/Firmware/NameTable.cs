using System.Text;

namespace CodeSpawner.Firmware;

/// <summary>
/// Interned identifiers for the firmware analyzer (docs/firmware-design.md). A full-scale corpus has about a hundred
/// million define names, so names live as length-prefixed bytes in 64 MB chunks (never as strings), at a 32-bit
/// offset, looked up by span through open addressing, with a set of flags per name: about 6 bytes per name besides
/// its bytes and its slots. Not thread-safe: the analyzer is single-threaded.
/// </summary>
internal sealed class NameTable
{
    private const int ChunkShift = 26, ChunkBytes = 1 << ChunkShift;

    /// <summary>The name has a define in a carved (C) file.</summary>
    public const ushort CName = 1;
    /// <summary>Rule 1: some definition pastes a parameter or forwards to a paste macro.</summary>
    public const ushort Paster = 2;
    public const ushort PieceMin = 4, PieceMax = 8;
    public const ushort WantMin = 16, WantMax = 32;
    public const ushort S5Min = 64, S5Max = 128;
    /// <summary>Seeded (rule 3): named by kept text, -D, a reserved name or a comment-open define.</summary>
    public const ushort Seed = 256;

    private readonly List<byte[]> _chunks = new();
    private int _chunkUsed = ChunkBytes;   // forces a first chunk
    private uint[] _off = new uint[1024];  // chunk << ChunkShift | offset of the length prefix
    private int[] _slots = new int[2048];  // id + 1; 0 = empty
    public ushort[] Flags = new ushort[1024];
    public int Count { get; private set; }

    public NameTable(int capacity = 1024)
    {
        if (capacity > 1024) Grow(capacity);
    }

    private static int Hash(ReadOnlySpan<byte> s)
    {
        uint h = 2166136261;
        foreach (byte b in s) h = (h ^ b) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>The next capacity past <paramref name="n"/>: 1.25x, so a big table wastes and copies less.</summary>
    public static int NextCapacity(int n) => (int)Math.Min(Array.MaxLength, Math.Max(1024L, n + (long)n / 4));

    public ReadOnlySpan<byte> Get(int id)
    {
        uint o = _off[id];
        var c = _chunks[(int)(o >> ChunkShift)];
        int i = (int)(o & (ChunkBytes - 1));
        int len = c[i];
        if (len < 255) return new ReadOnlySpan<byte>(c, i + 1, len);
        return new ReadOnlySpan<byte>(c, i + 5, BitConverter.ToInt32(c, i + 1));
    }

    public string Str(int id) => Encoding.ASCII.GetString(Get(id));

    public int Find(ReadOnlySpan<byte> s)
    {
        int mask = _slots.Length - 1;
        for (int i = Hash(s) & mask; ; i = (i + 1) & mask)
        {
            int v = _slots[i];
            if (v == 0) return -1;
            if (Get(v - 1).SequenceEqual(s)) return v - 1;
        }
    }

    public int Find(string s) => Find(Encoding.ASCII.GetBytes(s));

    public int GetOrAdd(ReadOnlySpan<byte> s)
    {
        if (Count == _off.Length) Grow(NextCapacity(Count));   // before probing: growing can rebuild the slots
        int mask = _slots.Length - 1;
        int i = Hash(s) & mask;
        for (; ; i = (i + 1) & mask)
        {
            int v = _slots[i];
            if (v == 0) break;
            if (Get(v - 1).SequenceEqual(s)) return v - 1;
        }
        int id = Count;
        int need = (s.Length < 255 ? 1 : 5) + s.Length;
        if (need > ChunkBytes) throw new InvalidOperationException($"an identifier of {s.Length:N0} bytes is too long");
        if (_chunkUsed + need > ChunkBytes)
        {
            if (_chunks.Count == 1 << (32 - ChunkShift))
                throw new InvalidOperationException("the firmware names exceed 4 GB; the corpus is too big for the analyzer");
            _chunks.Add(new byte[ChunkBytes]);
            _chunkUsed = 0;
        }
        var chunk = _chunks[^1];
        int at = _chunkUsed;
        if (s.Length < 255) chunk[at] = (byte)s.Length;
        else { chunk[at] = 255; BitConverter.TryWriteBytes(chunk.AsSpan(at + 1), s.Length); }
        s.CopyTo(chunk.AsSpan(at + need - s.Length));
        _off[id] = (uint)(_chunks.Count - 1) << ChunkShift | (uint)at;
        _chunkUsed += need;
        Count = id + 1;
        _slots[i] = id + 1;
        if (Count * 2 > _slots.Length) Rehash();
        return id;
    }

    public int GetOrAdd(string s) => GetOrAdd(Encoding.ASCII.GetBytes(s));

    private void Grow(int n)
    {
        Array.Resize(ref _off, n);
        Array.Resize(ref Flags, n);
        if (_slots.Length < n * 2L) { _slots = new int[(int)Math.Min(1L << 30, BitOps(n * 2L))]; Reinsert(); }
    }

    private static long BitOps(long n) { long p = 1; while (p < n) p <<= 1; return p; }

    private void Rehash()
    {
        _slots = new int[_slots.Length * 2];
        Reinsert();
    }

    private void Reinsert()
    {
        int mask = _slots.Length - 1;
        for (int id = 0; id < Count; id++)
        {
            int i = Hash(Get(id)) & mask;
            while (_slots[i] != 0) i = (i + 1) & mask;
            _slots[i] = id + 1;
        }
    }

    public bool Has(int id, ushort f) => (Flags[id] & f) != 0;

    /// <summary>Sets <paramref name="f"/>; true if it was not set before.</summary>
    public bool Set(int id, ushort f)
    {
        if ((Flags[id] & f) != 0) return false;
        Flags[id] |= f;
        return true;
    }

    /// <summary>Ordinal byte order of two names, forwards or (<paramref name="reversed"/>) from their last byte.</summary>
    public int Compare(int a, int b, bool reversed) => reversed ? CompareReversed(Get(a), Get(b)) : Get(a).SequenceCompareTo(Get(b));

    public static int CompareReversed(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        int n = Math.Min(x.Length, y.Length);
        for (int k = 1; k <= n; k++)
        {
            int d = x[^k] - y[^k];
            if (d != 0) return d;
        }
        return x.Length - y.Length;
    }
}

/// <summary>A growable int list without the bounds and version checks of <see cref="List{T}"/>.</summary>
internal sealed class IntList
{
    public int[] A = new int[16];
    public int Count;
    public void Add(int v)
    {
        if (Count == A.Length) Array.Resize(ref A, NameTable.NextCapacity(A.Length));
        A[Count++] = v;
    }
    public int this[int i] => A[i];
}
