using CodeSpawner.Scan;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// TrigramMeter models the consumer's index-size cost (distinct posting keys + posting-list length). Wrong
/// distinct/occurrence counts mis-size totalIndexedBytes. Content-free by construction — only counts, never
/// bytes — so these also stand as a privacy guard on that path.
/// </summary>
public class TrigramMeterTests
{
    [Fact]
    public void Counts_DistinctAndOccurrences()
    {
        var m = new TrigramMeter();
        m.Add(Bytes.Of("abcd")); // trigrams: abc, bcd
        Assert.Equal(2, m.DistinctCount());
        Assert.Equal(2, m.Occurrences); // len - 2
    }

    [Fact]
    public void RepeatedTrigram_CountsDistinctOnce_ButOccurrencesEach()
    {
        var m = new TrigramMeter();
        m.Add(Bytes.Of("aaaa")); // aaa, aaa
        Assert.Equal(1, m.DistinctCount());
        Assert.Equal(2, m.Occurrences);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void ShorterThanTrigram_IsNoOp(string s)
    {
        var m = new TrigramMeter();
        m.Add(Bytes.Of(s));
        Assert.Equal(0, m.DistinctCount());
        Assert.Equal(0, m.Occurrences);
    }

    [Fact]
    public void AlsoInto_UnionsIntoGlobalMeter()
    {
        var global = new TrigramMeter();
        var a = new TrigramMeter();
        var b = new TrigramMeter();
        a.Add(Bytes.Of("abc"), global);
        b.Add(Bytes.Of("xyz"), global);

        Assert.Equal(1, a.DistinctCount());
        Assert.Equal(1, b.DistinctCount());
        Assert.Equal(2, global.DistinctCount());        // union of both distinct trigrams
        Assert.Equal(2, global.Occurrences);            // occurrences accumulate too
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var m = new TrigramMeter();
        m.Add(Bytes.Of("abcdef"));
        Assert.True(m.DistinctCount() > 0);
        m.Reset();
        Assert.Equal(0, m.DistinctCount());
        Assert.Equal(0, m.Occurrences);
    }
}
