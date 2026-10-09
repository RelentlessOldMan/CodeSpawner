using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The whole "regenerate the same corpus regardless of thread scheduling" guarantee rests on Rng.For being a
/// pure function of (seed, category, index). If any of these break, determinism (and every bench check that
/// depends on it) silently rots. xoshiro/SplitMix constants are load-bearing.
/// </summary>
public class RngTests
{
    [Fact]
    public void For_IsDeterministic_ForSameInputs()
    {
        var a = Rng.For(1337, Category.Source, 42);
        var b = Rng.For(1337, Category.Source, 42);
        for (int i = 0; i < 32; i++)
            Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void For_DiffersBySeed()
    {
        var a = Rng.For(1, Category.Source, 0);
        var b = Rng.For(2, Category.Source, 0);
        Assert.NotEqual(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void For_DiffersByCategory()
    {
        // Categories exist precisely so distinct populations never share a stream.
        var a = Rng.For(1, Category.Source, 0);
        var b = Rng.For(1, Category.TinyFile, 0);
        Assert.NotEqual(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void For_DiffersByIndex()
    {
        var a = Rng.For(1, Category.Source, 0);
        var b = Rng.For(1, Category.Source, 1);
        Assert.NotEqual(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void Next_StaysInRange()
    {
        var r = Rng.For(9, Category.Placement, 3);
        for (int i = 0; i < 10_000; i++)
        {
            int v = r.Next(7);
            Assert.InRange(v, 0, 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Next_NonPositiveBound_ReturnsZero(int bound)
    {
        var r = Rng.For(1, Category.Source, 1);
        Assert.Equal(0, r.Next(bound));
    }

    [Fact]
    public void Next_One_AlwaysZero()
    {
        var r = Rng.For(5, Category.Blob, 2);
        for (int i = 0; i < 1000; i++) Assert.Equal(0, r.Next(1));
    }

    [Fact]
    public void Next_MinMax_StaysInRange()
    {
        var r = Rng.For(3, Category.DirTree, 8);
        for (int i = 0; i < 10_000; i++)
        {
            int v = r.Next(10, 20);
            Assert.InRange(v, 10, 19);
        }
    }

    [Fact]
    public void Next_ProducesMoreThanOneValue()
    {
        // Guards against a degenerate generator that returns a constant (which would still pass range checks).
        var r = Rng.For(11, Category.Source, 7);
        var seen = new HashSet<int>();
        for (int i = 0; i < 500; i++) seen.Add(r.Next(1000));
        Assert.True(seen.Count > 50, $"expected a spread of values, saw only {seen.Count} distinct");
    }

    // FROZEN GOLDEN STREAM. The self-relative tests above prove the stream is a pure function of
    // (seed, category, index); these prove it is the SAME stream as every shipped release. The SplitMix64 /
    // xoshiro256** constants and the For() mixing in Rng.cs are load-bearing: a refactor that keeps the RNG
    // perfectly deterministic but changes the *values* (swap the two SplitMix multipliers, change Next's `>> 32`,
    // reorder the For mix) passes every other test while silently making every previously-generated corpus
    // (death_1.0.9, the cross-machine regen-in-place workflow) unreproducible. This is the only unit test that
    // fails on such a change. If you are changing the generator ON PURPOSE, re-baseline these literals.
    [Fact]
    public void For_ProducesFrozenGoldenStream()
    {
        var r = Rng.For(1337, Category.Source, 42);
        Assert.Equal(14422954827547773155UL, r.NextULong());
        Assert.Equal(8900491109341867659UL, r.NextULong());
        Assert.Equal(16519258455261250301UL, r.NextULong());
    }

    // The 64-bit-index overload (added for ProfileGenerator's archetype*lane + file index) must be
    // byte-identical to the int overload wherever the int form was already correct — otherwise it would churn
    // existing profile corpora — AND must NOT collide for indices that differ only above bit 31, which the old
    // `(int)` cast silently collapsed together (the archIndex >= 22 truncation bug).
    [Fact]
    public void For_LongIndex_ByteIdenticalForSmallValues_AndNoTruncationCollision()
    {
        // (a) byte-identical to the int overload for values that fit in an int (zero churn).
        for (int idx = 0; idx < 8; idx++)
            Assert.Equal(Rng.For(1337, Category.ProfileGen, idx).NextULong(),
                         Rng.For(1337, Category.ProfileGen, (long)idx).NextULong());

        // (b) two indices with the same low 32 bits — exactly what `(int)` truncation collapsed — now differ.
        long lo = 100L;
        long hi = 100L + (1L << 32);            // identical low 32 bits; old (int) cast made both == 100
        Assert.Equal((int)lo, (int)hi);         // confirm the truncation really did collide them
        Assert.NotEqual(Rng.For(1337, Category.ProfileGen, lo).NextULong(),
                        Rng.For(1337, Category.ProfileGen, hi).NextULong());
    }

    [Fact]
    public void Next_ProducesFrozenGoldenValues()
    {
        // Covers the multiply-high reduction (Next(bound)) and the min/max offset form against frozen values,
        // so a change to the reduction math is caught even if NextULong itself were somehow unchanged.
        Assert.Equal(781, Rng.For(1337, Category.Source, 42).Next(1000));
        Assert.Equal(14, Rng.For(7, Category.Placement, 3).Next(10, 20));
    }

    // Category integer values are LOAD-BEARING: Rng.For folds ((ulong)category << 32) into the seed, so each
    // member's numeric value selects its stream. The enum is append-only by contract (see the comment on the
    // enum itself); renumbering it — alphabetizing, or inserting a member mid-list — shifts every subsequent
    // value, changes every derived stream, and makes all existing corpora unreproducible, with a green suite.
    // This freezes the wire values so that cannot happen silently. APPENDING a new member: add its line here.
    [Fact]
    public void Category_IntValues_AreFrozen()
    {
        Assert.Equal(1, (int)Category.DirTree);
        Assert.Equal(2, (int)Category.GiantHeader);
        Assert.Equal(3, (int)Category.BigHeader);
        Assert.Equal(4, (int)Category.MedHeader);
        Assert.Equal(5, (int)Category.OrdinaryHeader);
        Assert.Equal(6, (int)Category.Blob);
        Assert.Equal(7, (int)Category.Source);
        Assert.Equal(8, (int)Category.TinyFile);
        Assert.Equal(9, (int)Category.Unresolved);
        Assert.Equal(10, (int)Category.Placement);
        Assert.Equal(11, (int)Category.DenseHeader);
        Assert.Equal(12, (int)Category.BroadToken);
        Assert.Equal(13, (int)Category.LongLine);
        Assert.Equal(14, (int)Category.EncodingMix);
        Assert.Equal(15, (int)Category.PathoSymbol);
        Assert.Equal(16, (int)Category.DupContent);
        Assert.Equal(17, (int)Category.MutateSeed);
        Assert.Equal(18, (int)Category.Mutate);
        Assert.Equal(19, (int)Category.ProfileGen);
        Assert.Equal(20, (int)Category.ProfileDir);
        Assert.Equal(21, (int)Category.ProfileOracle);
        // Fails if a member is added or removed without updating the pins above.
        Assert.Equal(21, Enum.GetValues<Category>().Length);
    }
}
