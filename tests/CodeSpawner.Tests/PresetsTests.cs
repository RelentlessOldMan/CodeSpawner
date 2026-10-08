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

    // Every advertised preset name must resolve to a non-empty knob set. Driven off Presets.Names (the single
    // source of truth) rather than a hand-copied list, so a preset added to the registry is covered automatically
    // and a registry entry that resolves to nothing is caught here.
    [Fact]
    public void TryGet_EveryAdvertisedPreset_ResolvesToTokens()
    {
        Assert.NotEmpty(Presets.Names);
        foreach (var name in Presets.Names)
        {
            Assert.True(Presets.TryGet(name, out var tokens), $"preset '{name}' is advertised but does not resolve");
            Assert.NotEmpty(tokens);
        }
    }

    // The advertised name list must stay in sync with what the error messages promise. Catches a registry entry
    // that drifts from the user-facing hint (both now derive from Names, so this also guards that wiring).
    [Fact]
    public void UnknownPresetError_ListsEveryAdvertisedName()
    {
        var ex = Assert.Throws<ArgException>(() => Presets.Expand(new[] { "--preset", "no-such-preset" }));
        foreach (var name in Presets.Names)
            Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        Assert.True(Presets.TryGet("DEATH", out var tokens));
        Assert.NotEmpty(tokens);
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
