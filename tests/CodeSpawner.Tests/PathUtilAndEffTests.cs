using CodeSpawner.Cli;
using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// Manifest paths are repo-relative with forward slashes so the oracle stays valid whether the corpus is read
/// locally (Windows) or over the SMB share. A backslash leaking into a manifest path breaks cross-host verify.
/// </summary>
public class PathUtilTests
{
    [Fact]
    public void Rel_IsForwardSlashed()
    {
        string root = Path.Combine("C:", "root");
        string abs = Path.Combine(root, "block1", "src_0.c");
        string rel = PathUtil.Rel(root, abs);
        Assert.Equal("block1/src_0.c", rel);
        Assert.DoesNotContain('\\', rel);
    }

    [Fact]
    public void Rel_SamePath_IsDot()
    {
        string root = Path.Combine("C:", "root");
        Assert.Equal(".", PathUtil.Rel(root, root));
    }

    [Fact]
    public void SlashOrdinal_SortsOsPathsAsTheirSlashForm()
    {
        // Plain ordinal on Windows puts block10\ before block1\ ('\' > '0'); the '/' form puts block1/ first, as
        // Linux always did. Mutate's source pool and scan's --sample order rely on this.
        string root = Path.Combine("C:", "root");
        var paths = new List<string>
        {
            Path.Combine(root, "block10", "a.c"), Path.Combine(root, "block1", "z.c"),
            Path.Combine(root, "block2", "a.c"), Path.Combine(root, "block1A", "a.c"),
        };
        paths.Sort(PathUtil.SlashOrdinal);
        Assert.Equal(new[] { "block1/z.c", "block10/a.c", "block1A/a.c", "block2/a.c" },
                     paths.Select(p => PathUtil.Rel(root, p)).ToArray());
    }
}

/// <summary>
/// GenOptions.Eff is the scale contract: default counts × Scale, but an explicitly-passed knob is literal, and
/// a "keepOne" pathology knob never rounds away to zero while enabled. Off-by-one here changes corpus size.
/// </summary>
public class GenOptionsEffTests
{
    [Fact]
    public void Eff_ScalesDefaultCounts()
    {
        var o = new GenOptions { Out = "x", Scale = 0.01 };
        Assert.Equal(1, o.Eff("GiantHeaders", 100)); // round(100 * 0.01)
    }

    [Fact]
    public void Eff_ExplicitKnob_IsLiteral_NotScaled()
    {
        var o = new GenOptions { Out = "x", Scale = 0.01 };
        o.Explicit.Add("GiantHeaders");
        Assert.Equal(100, o.Eff("GiantHeaders", 100));
    }

    [Fact]
    public void Eff_ZeroValue_DisablesPopulation()
    {
        var o = new GenOptions { Out = "x", Scale = 1.0 };
        Assert.Equal(0, o.Eff("GiantHeaders", 0));
    }

    [Fact]
    public void Eff_KeepOne_FloorsAtOne_WhenScaledToZero()
    {
        var o = new GenOptions { Out = "x", Scale = 0.0001 };
        Assert.Equal(1, o.Eff("GiantHeaders", 100, keepOne: true)); // round(0.01)=0 → floored to 1
    }

    [Fact]
    public void Eff_WithoutKeepOne_CanScaleToZero()
    {
        var o = new GenOptions { Out = "x", Scale = 0.0001 };
        Assert.Equal(0, o.Eff("GiantHeaders", 100));
    }
}
