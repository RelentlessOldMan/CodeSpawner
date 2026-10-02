using CodeSpawner.Cli;
using CodeSpawner.Profile;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The arg parser is the CLI's whole input-validation surface. Invalid input must fail loudly (ArgException →
/// exit 2), not silently do the wrong thing. Covers defaults, explicit-tracking, boolean/enum parsing, and
/// every required-arg / mutual-exclusion guard.
/// </summary>
public class ArgParserTests
{
    // --- gen ---

    [Fact]
    public void Gen_Defaults_AreApplied()
    {
        var o = ArgParser.ParseGen(new[] { "--out", "corpus" });
        Assert.Equal("corpus", o.Out);
        Assert.Equal(0.01, o.Scale);
        Assert.Equal(1337, o.Seed);
        Assert.False(o.WasSet("seed"));
    }

    [Fact]
    public void Gen_ExplicitKnob_IsTracked()
    {
        var o = ArgParser.ParseGen(new[] { "--out", "c", "--seed", "42" });
        Assert.Equal(42, o.Seed);
        Assert.True(o.WasSet("seed"));
    }

    [Fact]
    public void Gen_InlineValue_Works()
    {
        var o = ArgParser.ParseGen(new[] { "--out=c", "--scale=0.5" });
        Assert.Equal("c", o.Out);
        Assert.Equal(0.5, o.Scale);
    }

    [Fact]
    public void Gen_BoolFlag_DefaultsTrue_AndAcceptsInlineFalse()
    {
        Assert.True(ArgParser.ParseGen(new[] { "--out", "c", "--force" }).Force);
        Assert.False(ArgParser.ParseGen(new[] { "--out", "c", "--force=false" }).Force);
    }

    [Fact]
    public void Gen_MissingOut_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(Array.Empty<string>()));

    [Fact]
    public void Gen_UnknownKnob_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--nonsense", "1" }));

    [Fact]
    public void Gen_KnobWithoutValue_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--seed" }));

    [Fact]
    public void Gen_NonOptionToken_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "positional" }));

    [Fact]
    public void Gen_BadBoolean_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--force=maybe" }));

    [Theory]
    [InlineData("none", CompileDbMode.None)]
    [InlineData("partial", CompileDbMode.Partial)]
    [InlineData("full", CompileDbMode.Full)]
    public void Gen_CompileDb_Parses(string v, CompileDbMode expected)
        => Assert.Equal(expected, ArgParser.ParseGen(new[] { "--out", "c", "--compile-db", v }).CompileDb);

    [Fact]
    public void Gen_BadCompileDb_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--compile-db", "weird" }));

    [Fact]
    public void Gen_SpreadOfKnobs_ParseAndTrack()
    {
        // A representative spread across the int/double/bool knob families + batch-1/batch-2 pathologies,
        // guarding the switch table and the Canon() explicit-name mapping used by Eff scaling.
        var o = ArgParser.ParseGen(new[]
        {
            "--out", "c",
            "--giant-headers", "3", "--cfiles", "10", "--tiny-files", "20", "--blob-files", "1",
            "--scale", "0.25", "--macro-density", "500",
            "--broad-token-files", "8", "--hot-token-share", "0.75",
            "--long-line-files", "2", "--no-newline",
            "--pathological-symbols", "4", "--dup-groups", "5", "--dup-copies", "3",
            "--from-profile", "p.json", "--with-oracle", "--oracle-chain", "9",
        });
        Assert.Equal(3, o.GiantHeaders);
        Assert.Equal(10, o.CFiles);
        Assert.Equal(0.75, o.HotTokenShare);
        Assert.True(o.NoNewline);
        Assert.Equal("p.json", o.FromProfile);
        Assert.True(o.WithOracle);
        Assert.Equal(9, o.OracleChain);
        // Canon maps the CLI spelling to the Eff knob name, so scaling can skip explicitly-set knobs.
        Assert.True(o.WasSet("GiantHeaders"));
        Assert.True(o.WasSet("BroadTokenFiles"));
        Assert.True(o.WasSet("PathologicalSymbols"));
    }

    // Regression: a non-numeric knob value used to escape as an unhandled FormatException (ugly stack dump,
    // bypassing Program.Main's error handling). It must now be a clean ArgException like every other arg error.
    [Fact]
    public void Gen_OracleShapeKnobs_Parse()
    {
        var o = ArgParser.ParseGen(new[]
        {
            "--out", "c", "--with-oracle",
            "--oracle-chain", "20", "--oracle-fanout", "3", "--oracle-depth", "4",
            "--oracle-shared-leaves", "5", "--oracle-reachable-frac", "0.5",
            "--oracle-indirect", "8", "--oracle-bytes", "--oracle-scale",
        });
        Assert.True(o.WithOracle);
        Assert.Equal(20, o.OracleChain);
        Assert.Equal(3, o.OracleFanout);
        Assert.Equal(4, o.OracleDepth);
        Assert.Equal(5, o.OracleSharedLeaves);
        Assert.Equal(0.5, o.OracleReachableFrac);
        Assert.Equal(8, o.OracleIndirect);
        Assert.True(o.OracleBytes);
        Assert.True(o.OracleScale);
    }

    [Fact]
    public void Gen_BadIntValue_ThrowsArgException()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--seed", "notanumber" }));

    [Fact]
    public void Gen_BadDoubleValue_ThrowsArgException()
        => Assert.Throws<ArgException>(() => ArgParser.ParseGen(new[] { "--out", "c", "--scale", "abc" }));

    [Fact]
    public void Scan_BadIntValue_ThrowsArgException()
        => Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { "/t", "--out", "p", "--min-cluster", "xx" }));

    [Fact]
    public void Mutate_BadIntValue_ThrowsArgException()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--edits", "lots" }));

    // --- scan ---

    [Fact]
    public void Scan_PositionalTree_AndOut()
    {
        var o = ArgParser.ParseScan(new[] { "/some/tree", "--out", "p.json" });
        Assert.Equal("/some/tree", o.Tree);
        Assert.Equal("p.json", o.Out);
        Assert.Equal(PrivacyPosture.ClassLabeled, o.Posture);
    }

    [Fact]
    public void Scan_PostureFlags()
    {
        Assert.Equal(PrivacyPosture.StructureOnly,
            ArgParser.ParseScan(new[] { "/t", "--out", "p", "--structure-only" }).Posture);
        Assert.Equal(PrivacyPosture.ContentStats,
            ArgParser.ParseScan(new[] { "/t", "--out", "p", "--content-stats" }).Posture);
    }

    [Fact]
    public void Scan_MissingTree_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { "--out", "p.json" }));

    [Fact]
    public void Scan_MissingOut_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { "/tree" }));

    [Fact]
    public void Scan_SecondPositional_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { "/a", "/b", "--out", "p" }));

    [Fact]
    public void Scan_MinClusterBelowOne_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseScan(new[] { "/t", "--out", "p", "--min-cluster", "0" }));

    // --- verify ---

    [Fact]
    public void Verify_RequiresCorpus()
        => Assert.Throws<ArgException>(() => ArgParser.ParseVerify(Array.Empty<string>()));

    [Fact]
    public void Verify_ParsesCorpus()
        => Assert.Equal("c", ArgParser.ParseVerify(new[] { "--corpus", "c" }).Corpus);

    // --- mutate ---

    [Fact]
    public void Mutate_Defaults()
    {
        var o = ArgParser.ParseMutate(new[] { "--corpus", "c" });
        Assert.Equal(5, o.Edits);
        Assert.Equal(7, o.Seed);
        Assert.Null(o.Step);
    }

    [Fact]
    public void Mutate_StepAndThrough_AreMutuallyExclusive()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--step", "2", "--through" }));

    [Fact]
    public void Mutate_StepOutOfRange_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--edits", "3", "--step", "4" }));

    [Fact]
    public void Mutate_ValidStep_IsAccepted()
        => Assert.Equal(2, ArgParser.ParseMutate(new[] { "--corpus", "c", "--step", "2" }).Step);

    // --- mutate bulk (diff-oracle) mode ---

    [Fact]
    public void Mutate_Bulk_ParsesKnobs_AndActivatesBulk()
    {
        var o = ArgParser.ParseMutate(new[]
        {
            "--corpus", "c", "--target", "GIANT", "--files-changed", "1000",
            "--edit-density", "0.25", "--giant-min-mb", "500", "--seed", "4",
        });
        Assert.True(o.IsBulk);
        Assert.Equal("giant", o.Target);           // lowercased
        Assert.Equal(1000, o.FilesChanged);
        Assert.Equal(0.25, o.EditDensity);
        Assert.Equal(500, o.GiantMinMb);
    }

    [Fact]
    public void Mutate_Bulk_DefaultDensity_WhenOmitted()
        => Assert.Equal(0.05, ArgParser.ParseMutate(new[] { "--corpus", "c", "--target", "source" }).EditDensity);

    [Fact]
    public void Mutate_Bulk_BadTarget_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--target", "bogus" }));

    [Fact]
    public void Mutate_Bulk_FilesChangedWithoutTarget_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--files-changed", "10" }));

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    public void Mutate_Bulk_BadDensity_Throws(string d)
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--target", "all", "--edit-density", d }));

    [Fact]
    public void Mutate_Bulk_TargetWithStep_Throws()
        => Assert.Throws<ArgException>(() => ArgParser.ParseMutate(new[] { "--corpus", "c", "--target", "source", "--step", "1" }));
}
