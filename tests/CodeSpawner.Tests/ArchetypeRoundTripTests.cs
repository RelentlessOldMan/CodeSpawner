using CodeSpawner.Generation;
using CodeSpawner.Profile;
using CodeSpawner.Scan;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The load-bearing round-trip invariant (ArchetypeSynthesizer's own contract): content synthesized for a
/// class must RE-CLASSIFY to that same class — otherwise gen --from-profile reproduces the wrong cost/shape.
/// This is the exact invariant that broke in the shipped v1.0.6 blob bug (digit blobs mis-read as text); this
/// test would have caught it. Template intentionally degrades to inline-function-heavy until the C++ profile
/// lands, so its expectation reflects that documented degradation.
/// </summary>
public class ArchetypeRoundTripTests
{
    public static IEnumerable<object[]> Cases()
    {
        // class, target bytes, expected re-classified class
        yield return new object[] { ContentClass.PreprocessorDense, 32_000L, ContentClass.PreprocessorDense };
        yield return new object[] { ContentClass.InlineFunctionHeavy, 32_000L, ContentClass.InlineFunctionHeavy };
        yield return new object[] { ContentClass.EnumStructTable, 32_000L, ContentClass.EnumStructTable };
        yield return new object[] { ContentClass.XMacro, 32_000L, ContentClass.XMacro };
        yield return new object[] { ContentClass.TabularData, 32_000L, ContentClass.TabularData };
        yield return new object[] { ContentClass.MinifiedLongLine, 200_000L, ContentClass.MinifiedLongLine };
        yield return new object[] { ContentClass.GenericCode, 32_000L, ContentClass.GenericCode };
        yield return new object[] { ContentClass.DataBlobHighEntropy, 32_000L, ContentClass.DataBlobHighEntropy };
        yield return new object[] { ContentClass.DataBlobRepetitive, 32_000L, ContentClass.DataBlobRepetitive };
        yield return new object[] { ContentClass.Text, 32_000L, ContentClass.Text };
        yield return new object[] { ContentClass.Binary, 32_000L, ContentClass.Binary };
        // documented degradation: template synthesizes as inline-function-heavy content.
        yield return new object[] { ContentClass.TemplateMetaprogramming, 32_000L, ContentClass.InlineFunctionHeavy };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Synthesized_ReClassifiesTo_SameClass(ContentClass cls, long target, ContentClass expected)
    {
        using var tmp = new TempDir();
        string path = tmp.File("archetype.dat");
        var rng = Rng.For(1337, Category.ProfileGen, (int)cls);
        ArchetypeSynthesizer.Write(path, cls, target, ref rng);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= target - 4096,
            $"synthesizer produced {bytes.Length} bytes, well under the {target} target");

        var got = ContentClassifier.Classify(bytes).Class;
        Assert.Equal(expected, got);
    }

    [Fact]
    public void DrawSize_StaysWithinDistributionEnvelope()
    {
        var dist = new SizeDistribution { P50 = 1000, P90 = 5000, Max = 20000 };
        var rng = Rng.For(1, Category.ProfileGen, 0);
        for (int i = 0; i < 5000; i++)
        {
            long s = ArchetypeSynthesizer.DrawSize(ref rng, dist);
            Assert.InRange(s, 1, dist.Max);
        }
    }

    [Fact]
    public void DrawSize_IsDeterministic()
    {
        var dist = new SizeDistribution { P50 = 1000, P90 = 5000, Max = 20000 };
        var a = Rng.For(42, Category.ProfileGen, 9);
        var b = Rng.For(42, Category.ProfileGen, 9);
        for (int i = 0; i < 100; i++)
            Assert.Equal(ArchetypeSynthesizer.DrawSize(ref a, dist), ArchetypeSynthesizer.DrawSize(ref b, dist));
    }
}
