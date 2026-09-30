using CodeSpawner.Profile;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The class labels ARE the cross-tool profile contract (docs/scan-design.md): a drifted label or a
/// broken Label↔TryParse round-trip silently corrupts every regenerated corpus. These pin the enum wire form.
/// </summary>
public class ContentClassTests
{
    public static IEnumerable<object[]> AllClasses() =>
        Enum.GetValues<ContentClass>().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(AllClasses))]
    public void Label_And_TryParse_RoundTrip(ContentClass c)
    {
        string label = c.Label();
        Assert.True(ContentClasses.TryParse(label, out var parsed), $"label '{label}' did not parse back");
        Assert.Equal(c, parsed);
    }

    [Theory]
    [InlineData("preprocessor-dense", ContentClass.PreprocessorDense)]
    [InlineData("inline-function-heavy", ContentClass.InlineFunctionHeavy)]
    [InlineData("data-blob-high-entropy", ContentClass.DataBlobHighEntropy)]
    [InlineData("binary", ContentClass.Binary)]
    public void TryParse_KnownLabels(string label, ContentClass expected)
    {
        Assert.True(ContentClasses.TryParse(label, out var c));
        Assert.Equal(expected, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-class")]
    [InlineData("PreprocessorDense")] // enum name, not the kebab label — must NOT parse
    public void TryParse_Rejects_Unknown(string label)
    {
        Assert.False(ContentClasses.TryParse(label, out _));
    }

    [Fact]
    public void AllLabels_AreUnique()
    {
        var labels = Enum.GetValues<ContentClass>().Select(c => c.Label()).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    // Cost drives the parse-headline accounting; getting Heavy vs Cheap wrong mis-sizes parsedSourceBytes.
    [Theory]
    [InlineData(ContentClass.InlineFunctionHeavy, ParseCost.Heavy)]
    [InlineData(ContentClass.EnumStructTable, ParseCost.Heavy)]
    [InlineData(ContentClass.TemplateMetaprogramming, ParseCost.Heavy)]
    [InlineData(ContentClass.GenericCode, ParseCost.Baseline)]
    [InlineData(ContentClass.PreprocessorDense, ParseCost.Cheap)]
    [InlineData(ContentClass.DataBlobHighEntropy, ParseCost.Cheap)]
    [InlineData(ContentClass.Text, ParseCost.Cheap)]
    [InlineData(ContentClass.Binary, ParseCost.Cheap)]
    public void Cost_Mapping(ContentClass c, ParseCost expected) => Assert.Equal(expected, c.Cost());

    // IsParsedSource decides what counts toward parsedSourceBytes: source + real header classes, never
    // preprocessor-dense (auto-skipped) or blobs/text/binary.
    [Theory]
    [InlineData(ContentClass.InlineFunctionHeavy, true)]
    [InlineData(ContentClass.EnumStructTable, true)]
    [InlineData(ContentClass.TemplateMetaprogramming, true)]
    [InlineData(ContentClass.XMacro, true)]
    [InlineData(ContentClass.GenericCode, true)]
    [InlineData(ContentClass.PreprocessorDense, false)]
    [InlineData(ContentClass.TabularData, false)]
    [InlineData(ContentClass.DataBlobHighEntropy, false)]
    [InlineData(ContentClass.Text, false)]
    [InlineData(ContentClass.Binary, false)]
    public void IsParsedSource_Mapping(ContentClass c, bool expected) => Assert.Equal(expected, c.IsParsedSource());

    [Theory]
    [InlineData(ParseCost.Cheap, "cheap")]
    [InlineData(ParseCost.Baseline, "baseline")]
    [InlineData(ParseCost.Heavy, "heavy")]
    public void ParseCost_Label(ParseCost p, string expected) => Assert.Equal(expected, p.Label());
}

public class PostureTests
{
    [Theory]
    [InlineData(PrivacyPosture.StructureOnly, "structure-only", false, false)]
    [InlineData(PrivacyPosture.ClassLabeled, "class-labeled", true, false)]
    [InlineData(PrivacyPosture.ContentStats, "content-stats", true, true)]
    public void Posture_Label_And_Gates(PrivacyPosture p, string label, bool reads, bool stats)
    {
        Assert.Equal(label, p.Label());
        Assert.Equal(reads, p.ReadsContent());
        Assert.Equal(stats, p.EmitsNumericStats());
    }

    [Theory]
    [InlineData(DensityBand.Low, "low")]
    [InlineData(DensityBand.Med, "med")]
    [InlineData(DensityBand.High, "high")]
    public void DensityBand_Label(DensityBand b, string expected) => Assert.Equal(expected, b.Label());
}
