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
}
