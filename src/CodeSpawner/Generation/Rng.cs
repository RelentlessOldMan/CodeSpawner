namespace CodeSpawner.Generation;

/// <summary>
/// Deterministic, allocation-free RNG. Every file/unit derives its own stream from
/// <c>(masterSeed, category, index)</c>, so generation is fully reproducible regardless of thread
/// scheduling — the whole point of being able to parallelize the emitters. This is a small,
/// fast xoshiro256** seeded through SplitMix64, not a cryptographic generator.
/// </summary>
public struct Rng
{
    private ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        // SplitMix64 to spread a single seed into the four state words (xoshiro authors' recipe).
        _s0 = SplitMix64(ref seed);
        _s1 = SplitMix64(ref seed);
        _s2 = SplitMix64(ref seed);
        _s3 = SplitMix64(ref seed);
    }

    /// <summary>Derive a per-unit stream. Categories keep distinct populations from colliding.</summary>
    public static Rng For(int masterSeed, Category category, int index)
    {
        // Mix the three inputs into one 64-bit seed; unsigned so shifts are well-defined.
        ulong h = 0x9E3779B97F4A7C15UL;
        h = Mix(h ^ (uint)masterSeed);
        h = Mix(h ^ ((ulong)category << 32));
        h = Mix(h ^ (uint)index);
        return new Rng(h);
    }

    private static ulong SplitMix64(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>Next raw 64-bit value (xoshiro256**).</summary>
    public ulong NextULong()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform int in [0, maxExclusive). Matches System.Random.Next semantics closely enough.</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        // Multiply-high reduction: unbiased enough for corpus content, no modulo.
        ulong m = (uint)maxExclusive * (NextULong() >> 32);
        return (int)(m >> 32);
    }

    /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive) =>
        minInclusive + Next(maxExclusive - minInclusive);
}

/// <summary>
/// Distinct RNG streams per generated population, so counts/placement never collide. Values are STABLE —
/// only ever APPEND new members; renumbering would change every derived stream and break determinism of
/// existing corpora.
/// </summary>
public enum Category
{
    DirTree = 1,
    GiantHeader,
    BigHeader,
    MedHeader,
    OrdinaryHeader,
    Blob,
    Source,
    TinyFile,
    Unresolved,
    Placement,
    // Batch 1 pathologies (appended — see docs/ROADMAP.md):
    DenseHeader,
    BroadToken,
    LongLine,
    EncodingMix,
    // Batch 2 pathologies (appended):
    PathoSymbol,
    DupContent,
    // mutate/churn seeds + edit selection (appended):
    MutateSeed,
    Mutate,
    // scan/shape-profile regeneration (appended):
    ProfileGen,
    ProfileDir,
    ProfileOracle,
}
