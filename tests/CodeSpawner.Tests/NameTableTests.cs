using System.Text;
using CodeSpawner.Firmware;
using Xunit;

namespace CodeSpawner.Tests;

public class NameTableTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(1 << 16)]   // FwSpec's capacity
    public void EveryName_IsFoundAgain_AcrossGrowth(int capacity)
    {
        // Growing rebuilds the slots: a name added across a grow must still be found, never interned twice.
        var t = new NameTable(capacity);
        const int n = 300_000;
        for (int i = 0; i < n; i++) Assert.Equal(i, t.GetOrAdd($"N{i}"));
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i, t.Find($"N{i}"));
            Assert.Equal(i, t.GetOrAdd($"N{i}"));
        }
        Assert.Equal(n, t.Count);
        Assert.Equal(-1, t.Find("N-1"));
    }

    [Fact]
    public void LongNames_RoundTrip()
    {
        var t = new NameTable();
        string a = new('A', 254), b = new('B', 255), c = new('C', 70_000);
        int ia = t.GetOrAdd(a), ib = t.GetOrAdd(b), ic = t.GetOrAdd(c), ie = t.GetOrAdd("");
        Assert.Equal(a, t.Str(ia));
        Assert.Equal(b, t.Str(ib));
        Assert.Equal(c, t.Str(ic));
        Assert.Equal("", t.Str(ie));
        Assert.Equal(ib, t.Find(Encoding.ASCII.GetBytes(b)));
        Assert.Equal(4, t.Count);
    }
}
