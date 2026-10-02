using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// conflictTruthSha is the 3-way component digest, LOCKED with CodeDiffer (code-spawner chat, 2026-10-02).
/// digest-selftest pins the golden vector in the exe; these add the canonical-form invariants: order
/// independence, both-sections-always-present, and that each digested field (side, ops, coords) moves the hash.
/// </summary>
public class ConflictDigestTests
{
    private const string Golden = "68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a";

    private static List<Conflict> GoldenConflicts() => new()
    {
        new("f.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Replace, 5, 1),
        new("a.c", 9, 2, HunkOp.Replace, 9, 2, HunkOp.Delete, 9, 0),
    };
    private static List<CleanMerge> GoldenClean() => new()
    {
        new("f.c", "v1", HunkOp.Replace, 3, 1, 3, 1),
        new("a.c", "v2", HunkOp.Insert, 7, 0, 7, 2),
    };

    [Fact]
    public void ReproducesGoldenVector()
        => Assert.Equal(Golden, ConflictDigest.Compute(GoldenConflicts(), GoldenClean()));

    [Fact]
    public void IsLowercaseHex64()
        => Assert.Matches("^[0-9a-f]{64}$", ConflictDigest.Compute(GoldenConflicts(), GoldenClean()));

    [Fact]
    public void IndependentOfRecordOrder()
    {
        var conflicts = new List<Conflict>
        {
            new("a.c", 9, 2, HunkOp.Replace, 9, 2, HunkOp.Delete, 9, 0),
            new("f.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Replace, 5, 1),
        };
        var clean = new List<CleanMerge>
        {
            new("a.c", "v2", HunkOp.Insert, 7, 0, 7, 2),
            new("f.c", "v1", HunkOp.Replace, 3, 1, 3, 1),
        };
        Assert.Equal(Golden, ConflictDigest.Compute(conflicts, clean));
    }

    [Fact]
    public void SideFlip_ChangesHash()
    {
        var a = new List<CleanMerge> { new("x.c", "v1", HunkOp.Replace, 1, 1, 1, 1) };
        var b = new List<CleanMerge> { new("x.c", "v2", HunkOp.Replace, 1, 1, 1, 1) };
        Assert.NotEqual(ConflictDigest.Compute(Array.Empty<Conflict>(), a), ConflictDigest.Compute(Array.Empty<Conflict>(), b));
    }

    [Fact]
    public void ConflictSideOp_ChangesHash()
    {
        var a = new List<Conflict> { new("x.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Replace, 5, 1) };
        var b = new List<Conflict> { new("x.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Delete, 5, 0) };
        Assert.NotEqual(ConflictDigest.Compute(a, Array.Empty<CleanMerge>()), ConflictDigest.Compute(b, Array.Empty<CleanMerge>()));
    }

    [Fact]
    public void EmptyThreeWay_IsStableHash()
    {
        string a = ConflictDigest.Compute(Array.Empty<Conflict>(), Array.Empty<CleanMerge>());
        Assert.Equal(a, ConflictDigest.Compute(Array.Empty<Conflict>(), Array.Empty<CleanMerge>()));
        Assert.Matches("^[0-9a-f]{64}$", a);
    }
}
