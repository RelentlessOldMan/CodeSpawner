using CodeSpawner.Cli;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// Presets expand to knob tokens placed BEFORE the user's own, so anything the user passes explicitly still
/// wins. That ordering IS the feature — a regression that appended presets after user args would silently
/// override intentional overrides. Tested through the parser so the override semantics are exercised end-to-end.
/// </summary>
public class PresetsTests
{
    [Fact]
    public void TryGet_KnownPreset_ReturnsTokens()
    {
        Assert.True(Presets.TryGet("death", out var tokens));
        Assert.Contains("--giant-headers", tokens);
    }

    [Fact]
    public void TryGet_UnknownPreset_ReturnsFalse()
    {
        Assert.False(Presets.TryGet("does-not-exist", out var tokens));
        Assert.Empty(tokens);
    }

    [Fact]
    public void Expand_PrependsPresetTokens()
    {
        var expanded = Presets.Expand(new[] { "--preset", "ci", "--out", "x" });
        // ci = ["--scale","0.01"] placed first, user args after.
        Assert.Equal("--scale", expanded[0]);
        Assert.Equal("0.01", expanded[1]);
        Assert.Contains("--out", expanded);
    }

    [Fact]
    public void Expand_InlinePresetForm_Works()
    {
        var expanded = Presets.Expand(new[] { "--preset=ci" });
        Assert.Equal(new[] { "--scale", "0.01" }, expanded);
    }

    [Fact]
    public void Expand_NoPreset_ReturnsArgsUnchanged()
    {
        var args = new[] { "--out", "x", "--seed", "5" };
        Assert.Same(args, Presets.Expand(args));
    }

    [Fact]
    public void Expand_PresetWithoutName_Throws()
        => Assert.Throws<ArgException>(() => Presets.Expand(new[] { "--preset" }));

    [Fact]
    public void Expand_UnknownPreset_Throws()
        => Assert.Throws<ArgException>(() => Presets.Expand(new[] { "--preset", "bogus" }));

    [Fact]
    public void UserArg_Overrides_PresetValue_EndToEnd()
    {
        // The whole point of preset-first ordering: a user --scale beats the preset's --scale.
        var o = ArgParser.ParseGen(Presets.Expand(new[] { "--preset", "ci", "--out", "x", "--scale", "0.5" }));
        Assert.Equal(0.5, o.Scale);
    }
}
