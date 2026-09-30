using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The indirectTruthSha canonical form is LOCKED with CodeCarver + CodeCompass (a rewired indirect edge or a
/// moved root must trip it, and all three tools reproduce it byte-for-byte). digest-selftest pins the golden
/// vector inside the exe; these add the surrounding invariants — order independence, and that each field the
/// form includes actually changes the hash.
/// </summary>
public class IndirectDigestTests
{
    private const string Golden = "fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f";

    private static Dictionary<string, SymbolEntry> GoldenSyms() => new(StringComparer.Ordinal)
    {
        ["func_0"] = new()
        {
            Def = "a:1",
            IndirectEdges =
            {
                new() { Target = "itgt_0", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true },
                new() { Target = "iext_1", Via = IndirectVia.VectorTable, Dispatched = false, Resolved = false },
            },
        },
        ["func_1"] = new()
        {
            Def = "b:1",
            IndirectEdges = { new() { Target = "itgt_0", Via = IndirectVia.InitArray, Dispatched = true, Resolved = true } },
        },
    };

    [Fact]
    public void ReproducesGoldenVector()
        => Assert.Equal(Golden, IndirectDigest.Compute(GoldenSyms(), new[] { "func_0" }));

    [Fact]
    public void IsLowercaseHex64()
    {
        string h = IndirectDigest.Compute(GoldenSyms(), new[] { "func_0" });
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Fact]
    public void IndependentOfEdgeAndSymbolOrder()
    {
        // Same edges, different owner-insertion order and different per-symbol edge order → dedup+sort ⇒ equal.
        var reordered = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["func_1"] = new()
            {
                Def = "b:1",
                IndirectEdges = { new() { Target = "itgt_0", Via = IndirectVia.InitArray, Dispatched = true, Resolved = true } },
            },
            ["func_0"] = new()
            {
                Def = "a:1",
                IndirectEdges =
                {
                    new() { Target = "iext_1", Via = IndirectVia.VectorTable, Dispatched = false, Resolved = false },
                    new() { Target = "itgt_0", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true },
                },
            },
        };
        Assert.Equal(Golden, IndirectDigest.Compute(reordered, new[] { "func_0" }));
    }

    [Fact]
    public void RootOrder_DoesNotMatter_ButRootSet_Does()
    {
        var syms = GoldenSyms();
        string ab = IndirectDigest.Compute(syms, new[] { "func_0", "func_1" });
        string ba = IndirectDigest.Compute(syms, new[] { "func_1", "func_0" });
        Assert.Equal(ab, ba);                                   // sort-normalized
        Assert.NotEqual(Golden, ab);                            // a different root SET changes the hash
    }

    [Fact]
    public void Dispatched_ChangesHash()
    {
        var syms = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true } } },
        };
        var flipped = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.FnPtr, Dispatched = false, Resolved = true } } },
        };
        Assert.NotEqual(IndirectDigest.Compute(syms, new[] { "s" }), IndirectDigest.Compute(flipped, new[] { "s" }));
    }

    [Fact]
    public void Resolved_ChangesHash()
    {
        var syms = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true } } },
        };
        var flipped = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = false } } },
        };
        Assert.NotEqual(IndirectDigest.Compute(syms, new[] { "s" }), IndirectDigest.Compute(flipped, new[] { "s" }));
    }

    [Fact]
    public void Via_ChangesHash()
    {
        var a = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true } } },
        };
        var b = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["s"] = new() { Def = "a:1", IndirectEdges = { new() { Target = "t", Via = IndirectVia.VectorTable, Dispatched = true, Resolved = true } } },
        };
        Assert.NotEqual(IndirectDigest.Compute(a, new[] { "s" }), IndirectDigest.Compute(b, new[] { "s" }));
    }

    [Fact]
    public void Any_TrueOnlyWhenAnIndirectEdgeExists()
    {
        Assert.True(IndirectDigest.Any(GoldenSyms()));
        var none = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal) { ["s"] = new() { Def = "a:1" } };
        Assert.False(IndirectDigest.Any(none));
    }

    [Fact]
    public void Via_RoundTripsLabel()
    {
        foreach (var v in Enum.GetValues<IndirectVia>())
        {
            Assert.True(IndirectViaExtensions.TryParse(v.Label(), out var back));
            Assert.Equal(v, back);
        }
        Assert.False(IndirectViaExtensions.TryParse("vtable", out _)); // C++ set is v2, not a valid C via
    }
}
