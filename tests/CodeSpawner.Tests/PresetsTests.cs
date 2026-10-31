using CodeSpawner;
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
        Assert.True(Presets.TryGet("huge", out var tokens));
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

    // The gen --help PRESETS block (Program.UsageText) is the one preset-name list the single-source registry
    // can't derive — it carries human descriptions Names doesn't have — so it's hand-maintained and could drift.
    // This fails the build the moment a preset is added/renamed in the registry without updating the help text.
    [Fact]
    public void HelpText_ListsEveryAdvertisedPreset()
    {
        foreach (var name in Presets.Names)
            Assert.Contains($"--preset {name}", Program.UsageText);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        Assert.True(Presets.TryGet("HUGE", out var tokens));
        Assert.NotEmpty(tokens);
    }

    // The 1.5.4 rename keeps the old names working without advertising them.
    [Fact]
    public void TryGet_OldDeathNames_AreHiddenAliases()
    {
        Assert.True(Presets.TryGet("death", out var a));
        Assert.True(Presets.TryGet("huge", out var b));
        Assert.Equal(b, a);
        Assert.True(Presets.TryGet("Death-Lite", out a));
        Assert.True(Presets.TryGet("huge-lite", out b));
        Assert.Equal(b, a);
        Assert.DoesNotContain("death", Presets.Names);
        Assert.DoesNotContain("death", Program.UsageText);
    }

    // The 1.5.5 rename: firmware-work is firmware-bulk, and --fw-work* parses as --fw-bulk*.
    [Fact]
    public void OldFirmwareWorkNames_AreHiddenAliases()
    {
        Assert.True(Presets.TryGet("firmware-work", out var a));
        Assert.True(Presets.TryGet("firmware-bulk", out var b));
        Assert.Equal(b, a);
        Assert.DoesNotContain("firmware-work", Presets.Names);
        Assert.DoesNotContain("fw-work", Program.UsageText);
        var o = ArgParser.ParseGen(["--out", "x", "--firmware", "--fw-work", "--fw-work-defines", "1234", "--FW-WORK-keep-pct=2"]);
        Assert.True(o.FwWork);
        Assert.Equal((1234, 2.0), (o.FwWorkDefines, o.FwWorkKeepPct));
        Assert.True(o.WasSet("fw-bulk-defines"));
        // the old and the new spelling are the same option
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(["--out", "x", "--firmware", "--fw-bulk", "--fw-work"]));
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
        var args = Presets.Expand(new[] { "--preset", "ci", "--out", "x", "--scale", "0.5" }, out int n);
        Assert.Equal(0.5, ArgParser.ParseGen(args, n).Scale);
    }

    // Overriding the preset is not a repeat; the user giving a knob twice, or two presets, is.
    [Fact]
    public void UserRepeat_And_TwoPresets_AreRefused()
    {
        var args = Presets.Expand(new[] { "--preset", "ci", "--out", "x", "--scale", "0.5", "--scale", "0.2" }, out int n);
        Assert.Throws<ArgException>(() => ArgParser.ParseGen(args, n));
        Assert.Throws<ArgException>(() => Presets.Expand(new[] { "--preset", "ci", "--preset=memory" }, out _));
    }
}
