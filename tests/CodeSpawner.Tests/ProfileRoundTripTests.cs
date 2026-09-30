using CodeSpawner.Profile;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// scan writes a profile; gen --from-profile reads it back. The two halves must agree on the wire format, and
/// — the privacy contract — posture must gate content-derived fields OUT of the file. These cover both the
/// structural round-trip (numbers survive) and the posture gating (nothing content-derived leaks under
/// structure-only).
/// </summary>
public class ProfileRoundTripTests
{
    private static ProfileModel SampleModel(PrivacyPosture posture)
    {
        var m = new ProfileModel { Posture = posture, ScannedAt = "2026-09-30T00:00:00Z", MinCluster = 5 };
        m.TotalFiles = 100; m.TotalBytes = 1_000_000;
        m.ParsedSourceBytes = 250_000; m.TotalIndexedBytes = 900_000;
        m.DistinctTrigramEstimate = 4242; m.TrigramOccurrences = 999_999;

        m.Dirs = new DirStats { Count = 12 };
        m.Dirs.DepthHistogram.Add("1", 3);
        m.Dirs.DepthHistogram.Add("2", 9);

        m.Archetypes.Add(new Archetype
        {
            Label = "a1",
            Extension = ".c",
            Count = 40,
            Class = ContentClass.InlineFunctionHeavy,
            SizeDistribution = new SizeDistribution { P50 = 1000, P90 = 5000, Max = 20000 },
            SymbolDensity = new SymbolDensity
            {
                FunctionsPerKB = DensityBand.High,
                CallsPerFunction = DensityBand.Med,
                GlobalRefsPerFile = DensityBand.Low,
            },
        });
        m.Archetypes.Add(new Archetype
        {
            Label = "a2",
            Extension = ".dat",
            Count = 7,
            Class = ContentClass.DataBlobHighEntropy,
            SizeDistribution = new SizeDistribution { P50 = 500, P90 = 2000, Max = 8000 },
        });
        return m;
    }

    [Fact]
    public void ClassLabeled_RoundTrips_StructureAndClasses()
    {
        using var tmp = new TempDir();
        string path = tmp.File("profile.json");
        var src = SampleModel(PrivacyPosture.ClassLabeled);
        ProfileWriter.Write(src, path);
        var back = ProfileReader.Load(path);

        Assert.Equal(PrivacyPosture.ClassLabeled, back.Posture);
        Assert.Equal(src.TotalFiles, back.TotalFiles);
        Assert.Equal(src.TotalBytes, back.TotalBytes);
        Assert.Equal(src.ParsedSourceBytes, back.ParsedSourceBytes);
        Assert.Equal(src.TotalIndexedBytes, back.TotalIndexedBytes);
        Assert.Equal(src.Dirs.Count, back.Dirs.Count);
        Assert.Equal(2, back.Dirs.DepthHistogram.Entries.Count);
        Assert.Equal(("2", 9L), back.Dirs.DepthHistogram.Entries[1]);

        Assert.Equal(2, back.Archetypes.Count);
        var a1 = back.Archetypes[0];
        Assert.Equal("a1", a1.Label);
        Assert.Equal(".c", a1.Extension);
        Assert.Equal(40, a1.Count);
        Assert.Equal(ContentClass.InlineFunctionHeavy, a1.Class);
        Assert.Equal(20000, a1.SizeDistribution.Max);
        Assert.Equal(DensityBand.High, a1.SymbolDensity!.FunctionsPerKB);
        Assert.Equal(ContentClass.DataBlobHighEntropy, back.Archetypes[1].Class);
    }

    [Fact]
    public void StructureOnly_OmitsAllContentDerivedFields()
    {
        using var tmp = new TempDir();
        string path = tmp.File("profile.json");
        ProfileWriter.Write(SampleModel(PrivacyPosture.StructureOnly), path);
        string json = File.ReadAllText(path);

        // Privacy contract: structure-only never reads content, so none of these may appear.
        Assert.DoesNotContain("\"class\"", json);
        Assert.DoesNotContain("\"parseCost\"", json);
        Assert.DoesNotContain("\"symbolDensity\"", json);
        Assert.DoesNotContain("\"trigram\"", json);
        Assert.DoesNotContain("distinctTrigramEstimate", json);
        Assert.DoesNotContain("\"includes\"", json);
    }

    [Fact]
    public void StructureOnly_Reader_DefaultsClassToGenericCode()
    {
        using var tmp = new TempDir();
        string path = tmp.File("profile.json");
        ProfileWriter.Write(SampleModel(PrivacyPosture.StructureOnly), path);
        var back = ProfileReader.Load(path);

        Assert.Equal(PrivacyPosture.StructureOnly, back.Posture);
        // class omitted from the file → reader falls back to generic-code (documented default).
        Assert.All(back.Archetypes, a => Assert.Equal(ContentClass.GenericCode, a.Class));
    }

    [Fact]
    public void ContentStats_EmitsNumericContentBlock()
    {
        using var tmp = new TempDir();
        string path = tmp.File("profile.json");
        var m = SampleModel(PrivacyPosture.ContentStats);
        m.DistinctTrigramEstimate = 123; m.TrigramOccurrences = 456;
        ProfileWriter.Write(m, path);
        string json = File.ReadAllText(path);

        Assert.Contains("distinctTrigramEstimate", json);
        Assert.Contains("\"class\"", json);
    }

    [Fact]
    public void ContentStats_EmitsFullNumericBlock_OnlyUnderThatPosture()
    {
        // content-stats is the ONLY posture that emits numeric content — the emission must be correct AND
        // must never fire under a lower posture. This exercises every content-gated writer branch.
        var m = SampleModel(PrivacyPosture.ContentStats);
        m.Includes = new IncludeStats { UnresolvedIncludeRate = 0.125, DuplicateBasenameAmbiguity = 3, Hops = 2 };
        m.Includes.FanoutHistogram.Add("0-4", 10);
        m.Headers = new HeaderStats
        {
            Gt20MB = 1,
            Between1And20MB = 2,
            Lt1MB = 3,
            DefineFractionHistogramGe1MB = new List<HistogramBucket>
            {
                new() { Bucket = "0.0-0.5", Files = 4 },
            },
        };
        m.ParsedSourceByClass.Add(new ParsedSourceClassBytes { Class = ContentClass.InlineFunctionHeavy, Bytes = 111 });
        // give the first archetype a trigram + exact densities so those writer branches fire
        var a0 = m.Archetypes[0];
        m.Archetypes[0] = new Archetype
        {
            Label = a0.Label, Extension = a0.Extension, Count = a0.Count, Class = a0.Class,
            SizeDistribution = a0.SizeDistribution,
            Trigram = new TrigramStat { DistinctEstimate = 500, Occurrences = 9000 },
            SymbolDensity = new SymbolDensity
            {
                FunctionsPerKB = DensityBand.High, CallsPerFunction = DensityBand.Med, GlobalRefsPerFile = DensityBand.Low,
                ExactFunctionsPerKB = 2.5, ExactCallsPerFunction = 3.1, ExactGlobalRefsPerFile = 120.0,
            },
            Content = new ContentStatBlock
            {
                DefineFrac = 0.1, CommentFrac = 0.2, BlankFrac = 0.05, IncludeFrac = 0.03,
                IdentUniqueRatio = 0.4, AvgIdentLen = 6.2, AvgLineLen = 40.5, MaxLineLen = 200,
                Encoding = "utf-8", Bom = false, Newline = "lf",
            },
        };

        using var tmp = new TempDir();
        string path = tmp.File("cs.json");
        ProfileWriter.Write(m, path);
        string json = File.ReadAllText(path);

        Assert.Contains("\"includes\"", json);
        Assert.Contains("unresolvedIncludeRate", json);
        Assert.Contains("defineFractionHistogram_ge1MB", json);
        Assert.Contains("\"trigram\"", json);
        Assert.Contains("symbolDensityExact", json);
        Assert.Contains("parsedSourceByClass", json);
        Assert.Contains("identUniqueRatio", json);
    }

    [Fact]
    public void Ratios_AreRoundedToThreeDecimals_ForPrivacy()
    {
        // Writer buckets ratios to 3 dp so no exact per-file value leaks. 0.123456 must serialize as 0.123.
        var m = SampleModel(PrivacyPosture.ContentStats);
        m.Includes = new IncludeStats { UnresolvedIncludeRate = 0.123456, Hops = 1 };
        using var tmp = new TempDir();
        string path = tmp.File("round.json");
        ProfileWriter.Write(m, path);
        string json = File.ReadAllText(path);
        Assert.Contains("0.123", json);
        Assert.DoesNotContain("0.123456", json);
    }

    [Fact]
    public void Load_RejectsWrongProfileVersion()
    {
        using var tmp = new TempDir();
        string path = tmp.File("bad.json");
        File.WriteAllText(path, "{\"_meta\":{\"profileVersion\":999}}");
        Assert.Throws<InvalidOperationException>(() => ProfileReader.Load(path));
    }
}
