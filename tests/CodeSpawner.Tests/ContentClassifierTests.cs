using CodeSpawner.Profile;
using CodeSpawner.Scan;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The classifier is the crown jewel of scan fidelity — its priority ladder decides every archetype's class,
/// which drives regenerated parse/index cost. High branch density, fixed thresholds: exactly where branch
/// coverage matters most. Each test pins one rung of Decide()'s most-specific-first ladder.
/// </summary>
public class ContentClassifierTests
{
    private static ContentClass Classify(byte[] data)
        => ContentClassifier.Classify(data).Class;

    [Fact]
    public void Empty_IsText()
        => Assert.Equal(ContentClass.Text, Classify(Array.Empty<byte>()));

    [Fact]
    public void MostlyNonPrintable_IsBinary()
    {
        var data = new byte[2048]; // all 0x00 → printable ratio 0
        Assert.Equal(ContentClass.Binary, Classify(data));
    }

    [Fact]
    public void RandomDigits_AreHighEntropyBlob()
    {
        // Uniform over 10 digit values → entropy ≈ 3.32 bits ≥ 3.0, few idents → high-entropy blob.
        var data = Bytes.Of(string.Concat(Enumerable.Repeat("0123456789", 400)));
        Assert.Equal(ContentClass.DataBlobHighEntropy, Classify(data));
    }

    [Fact]
    public void RepeatingPattern_IsRepetitiveBlob()
    {
        // Two byte values → entropy ≈ 1.0 bit < 3.0 → cheap, low-cardinality blob.
        var data = Bytes.Of(string.Concat(Enumerable.Repeat("0,", 2000)));
        Assert.Equal(ContentClass.DataBlobRepetitive, Classify(data));
    }

    [Fact]
    public void DefineDenseHeader_IsPreprocessorDense()
    {
        var data = Bytes.Lines("#define REG_ALPHA_ADDR value", 60);
        Assert.Equal(ContentClass.PreprocessorDense, Classify(data));
    }

    [Fact]
    public void UppercaseMacroCalls_IsXMacro()
    {
        var data = Bytes.Lines("ENTRY(alpha, beta)", 40);
        Assert.Equal(ContentClass.XMacro, Classify(data));
    }

    [Fact]
    public void TemplateAngleBrackets_IsTemplateMetaprogramming()
    {
        var data = Bytes.Lines("template <class T> struct Wrap { T v; };", 30);
        Assert.Equal(ContentClass.TemplateMetaprogramming, Classify(data));
    }

    [Fact]
    public void DenseFunctionBodies_IsInlineFunctionHeavy()
    {
        var data = Bytes.Lines("static int fn(int x){ int a=x; return a; }", 60);
        Assert.Equal(ContentClass.InlineFunctionHeavy, Classify(data));
    }

    [Fact]
    public void EnumStructLines_IsEnumStructTable()
    {
        var data = Bytes.Lines("enum Color { Red, Green };", 40);
        Assert.Equal(ContentClass.EnumStructTable, Classify(data));
    }

    [Fact]
    public void RegularNonNumericColumns_IsTabularData()
    {
        var data = Bytes.Lines("alpha,beta,gamma,delta,epsilon", 40);
        Assert.Equal(ContentClass.TabularData, Classify(data));
    }

    [Fact]
    public void SingleVeryLongLine_IsMinifiedLongLine()
    {
        // Realistic minified content: one very long line of mixed non-hex code tokens (so it isn't caught by
        // the higher-priority digit/hex blob branch, which correctly claims a single-line pure-hex dump).
        var data = Bytes.Of(string.Concat(Enumerable.Repeat("function(){return this;}", 6000)));
        Assert.Equal(ContentClass.MinifiedLongLine, Classify(data));
    }

    [Fact]
    public void SparseFunctionsAmongDecls_IsGenericCode()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append("int var_").Append(i).Append(" = ").Append(i).Append(";\n");
        sb.Append("int compute(int x){ return x + 1; }\n");
        Assert.Equal(ContentClass.GenericCode, Classify(Bytes.Of(sb.ToString())));
    }

    [Fact]
    public void Prose_IsText()
    {
        var data = Bytes.Lines("the quick brown fox jumps over the lazy dog", 20);
        Assert.Equal(ContentClass.Text, Classify(data));
    }

    // --- Encoding detection (a UTF-16 text file must NOT be mistaken for binary) ---

    [Fact]
    public void Utf16LeBom_IsDetected_AndNotBinary()
    {
        var text = "hello world this is text";
        var body = System.Text.Encoding.Unicode.GetBytes(text); // UTF-16LE, no BOM from GetBytes
        var data = new byte[2 + body.Length];
        data[0] = 0xFF; data[1] = 0xFE;
        body.CopyTo(data, 2);
        var st = ContentClassifier.Classify(data);
        Assert.Equal("utf-16le", st.Encoding);
        Assert.True(st.Bom);
        Assert.NotEqual(ContentClass.Binary, st.Class);
    }

    [Fact]
    public void Utf8Bom_IsDetected()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Bytes.Of("int f(void){ return 0; }\n")).ToArray();
        var st = ContentClassifier.Classify(data);
        Assert.Equal("utf-8", st.Encoding);
        Assert.True(st.Bom);
    }

    [Fact]
    public void CrlfNewlines_AreDetected()
    {
        var data = Bytes.Of("line one\r\nline two\r\nline three\r\n");
        var st = ContentClassifier.Classify(data);
        Assert.Equal("crlf", st.Newline);
    }

    // --- Density bands (boundary values for each Low/Med/High cut) ---

    [Theory]
    [InlineData(0.4, DensityBand.Low)]
    [InlineData(0.5, DensityBand.Med)]  // == low cut → Med (v < low is Low)
    [InlineData(1.9, DensityBand.Med)]
    [InlineData(2.0, DensityBand.High)] // == high cut → High
    public void FunctionsPerKbBand_Boundaries(double v, DensityBand expected)
        => Assert.Equal(expected, ContentClassifier.FunctionsPerKbBand(v));

    [Theory]
    [InlineData(0.9, DensityBand.Low)]
    [InlineData(1.0, DensityBand.Med)]
    [InlineData(5.0, DensityBand.High)]
    public void CallsPerFunctionBand_Boundaries(double v, DensityBand expected)
        => Assert.Equal(expected, ContentClassifier.CallsPerFunctionBand(v));

    [Theory]
    [InlineData(99, DensityBand.Low)]
    [InlineData(100, DensityBand.Med)]
    [InlineData(1000, DensityBand.High)]
    public void GlobalRefsBand_Boundaries(double v, DensityBand expected)
        => Assert.Equal(expected, ContentClassifier.GlobalRefsBand(v));
}
