using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The truth digest byte format is LOCKED with CodeCompass (both sides reproduced the golden vector). A change
/// to field order, separators, sort, or the expectedMiss-absent="0" rule silently breaks delta-chain
/// detection cross-tool. digest-selftest guards the exact vector inside the exe; these add the surrounding
/// invariants (ordering independence, sort-normalization) that the single vector doesn't exercise.
/// </summary>
public class TruthDigestTests
{
    private const string Golden = "7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e";

    private static Dictionary<string, SymbolEntry> GoldenTruth() => new(StringComparer.Ordinal)
    {
        ["func_0"] = new() { Def = "block1/src_0.c:11", Refs = { "block1/src_1.c:14" } },
        ["func_1"] = new() { Def = "block1/src_1.c:12", Edges = { "func_0" } },
    };

    [Fact]
    public void Compute_ReproducesGoldenVector()
        => Assert.Equal(Golden, TruthDigest.Compute(GoldenTruth()));

    [Fact]
    public void Compute_IsIndependentOfInsertionOrder()
    {
        // Names are ordinal-sorted internally, so a different insertion order must hash identically.
        var reordered = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["func_1"] = new() { Def = "block1/src_1.c:12", Edges = { "func_0" } },
            ["func_0"] = new() { Def = "block1/src_0.c:11", Refs = { "block1/src_1.c:14" } },
        };
        Assert.Equal(Golden, TruthDigest.Compute(reordered));
    }

    [Fact]
    public void Compute_NormalizesRefAndEdgeOrder()
    {
        var a = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "d", Refs = { "a/1.c:1", "a/2.c:2" }, Edges = { "x", "y" } },
        };
        var b = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "d", Refs = { "a/2.c:2", "a/1.c:1" }, Edges = { "y", "x" } },
        };
        Assert.Equal(TruthDigest.Compute(a), TruthDigest.Compute(b));
    }

    [Fact]
    public void Compute_ExpectedMissAbsent_HashesLikeExplicitFalse()
    {
        var absent = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "d" },
        };
        var explicitFalse = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "d", ExpectedMiss = false },
        };
        Assert.Equal(TruthDigest.Compute(absent), TruthDigest.Compute(explicitFalse));
    }

    [Fact]
    public void Compute_ExpectedMissTrue_ChangesHash()
    {
        var off = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal) { ["s"] = new() { Def = "d" } };
        var on = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal) { ["s"] = new() { Def = "d", ExpectedMiss = true } };
        Assert.NotEqual(TruthDigest.Compute(off), TruthDigest.Compute(on));
    }

    [Fact]
    public void Compute_IsLowercaseHex64()
    {
        string h = TruthDigest.Compute(GoldenTruth());
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Fact]
    public void Compute_UnreachableRefs_DoNotAffectHash()
    {
        // Field set is name/def/refs/edges/expectedMiss — unreachableRefs is explicitly NOT included.
        var without = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal) { ["s"] = new() { Def = "d" } };
        var with = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "d", UnreachableRefs = new() { "z/9.c:9" } },
        };
        Assert.Equal(TruthDigest.Compute(without), TruthDigest.Compute(with));
    }
}
