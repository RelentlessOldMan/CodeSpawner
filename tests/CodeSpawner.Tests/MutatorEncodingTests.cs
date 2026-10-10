using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The diff truth must describe the bytes on disk. Line-level edits decode a file and re-encode it, so the
/// round-trip has to be byte-faithful: UTF-16 stays UTF-16, a BOM stays, and the only bytes that change are the
/// edited lines. A file that can't be round-tripped exactly (invalid UTF-8, NUL-bearing binary) is left untouched
/// and unrecorded — never transcoded or U+FFFD-substituted under a "content" record. Also: the generator's
/// compile_commands.json (absolute paths) is never a mutation target.
/// </summary>
public class MutatorEncodingTests
{
    private static readonly string[] Body = Enumerable.Range(0, 40).Select(l => $"int f_{l}(void) {{ return {l}; }} // café").ToArray();

    private static byte[] Encode(Encoding enc, string text) => enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
    private static string Text => string.Join("\n", Body) + "\n";

    private static string BuildCorpus(TempDir tmp, params (string name, byte[] bytes)[] files)
    {
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(corpus, name), bytes);
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        m.Symbols["func_0"] = new SymbolEntry { Def = $"{files[0].name}:1" };
        ManifestWriter.Write(m, Path.Combine(tmp.Path, "corpus-manifest.json"));
        return corpus;
    }

    private static Dictionary<string, JsonElement> Records(string deltaPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(deltaPath));
        return doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!, e => e);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static TheoryData<string> Encodings => new() { "utf16le", "utf16be", "utf8bom" };
    private static Encoding EncOf(string key) => key switch
    {
        "utf16le" => new UnicodeEncoding(false, true),
        "utf16be" => new UnicodeEncoding(true, true),
        _         => new UTF8Encoding(true),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Content_KeepsEncodingAndBom_OnlyEditedLinesChange(string key)
    {
        using var tmp = new TempDir();
        var enc = EncOf(key);
        string corpus = BuildCorpus(tmp, ("src_enc.c", Encode(enc, Text)));

        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.5, Seed = 3 }));

        byte[] after = File.ReadAllBytes(Path.Combine(corpus, "src_enc.c"));
        Assert.True(after.AsSpan().StartsWith(enc.GetPreamble()), "BOM/encoding not preserved");
        string[] lines = enc.GetString(after, enc.GetPreamble().Length, after.Length - enc.GetPreamble().Length).Split('\n');
        Assert.Equal(Body.Length + 1, lines.Length);            // same line count (+ the empty tail after the final \n)

        var rec = Records(Path.Combine(tmp.Path, "corpus-delta.json"))["src_enc.c"];
        Assert.Equal(Sha(after), rec.GetProperty("newSha").GetString());
        var edited = new HashSet<int>();
        foreach (var h in rec.GetProperty("hunks").EnumerateArray())
            for (int l = h.GetProperty("oldStart").GetInt32(); l < h.GetProperty("oldStart").GetInt32() + h.GetProperty("oldLines").GetInt32(); l++)
                edited.Add(l);
        Assert.NotEmpty(edited);
        for (int i = 0; i < Body.Length; i++)
        {
            if (edited.Contains(i + 1)) Assert.StartsWith(Body[i] + " /*mut:", lines[i]);
            else Assert.Equal(Body[i], lines[i]);                // untouched lines are byte-for-byte the original text
        }
    }

    [Fact]
    public void NonText_InvalidUtf8AndNulBinary_AreLeftUntouchedAndUnrecorded()
    {
        using var tmp = new TempDir();
        byte[] invalid = Enumerable.Range(0, 4096).Select(i => (byte)(0x80 + i % 128)).ToArray();
        byte[] nul = Encoding.ASCII.GetBytes("int a;\0\0\0\nint b;\n");
        string corpus = BuildCorpus(tmp, ("a_text.c", Encoding.ASCII.GetBytes(Text)), ("b_invalid.c", invalid), ("c_nul.c", nul));

        foreach (var kind in new[] { "content", "whitespace", "line-insert", "line-delete", "eol", "encoding" })
        {
            Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = kind, EditDensity = 1.0, Seed = 1 }));
            var recs = Records(Path.Combine(tmp.Path, "corpus-delta.json"));
            Assert.False(recs.ContainsKey("b_invalid.c"), $"{kind}: invalid UTF-8 recorded");
            Assert.False(recs.ContainsKey("c_nul.c"), $"{kind}: NUL binary recorded");
            Assert.Equal(invalid, File.ReadAllBytes(Path.Combine(corpus, "b_invalid.c")));
            Assert.Equal(nul, File.ReadAllBytes(Path.Combine(corpus, "c_nul.c")));
        }
    }

    [Fact]
    public void Eol_OnUtf8Bom_FlipsOnlyTerminators_BomKept()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, ("bom.c", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("a\nb\n")).ToArray()));
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "eol", Seed = 1 }));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("a\r\nb\r\n")).ToArray(),
                     File.ReadAllBytes(Path.Combine(corpus, "bom.c")));
    }

    // 3-way: a variant file differs from B exactly when that side's delta has a record for it. Non-text files and
    // sides that drew no edits for a file are never rewritten; UTF-16 stays UTF-16 on both sides.
    [Fact]
    public void ThreeWay_VariantBytesDifferExactlyWhereTheDeltaSaysSo()
    {
        using var tmp = new TempDir();
        byte[] invalid = Enumerable.Range(0, 2048).Select(i => (byte)(0x80 + i % 128)).ToArray();
        string corpus = BuildCorpus(tmp,
            ("a_plain.c", Encoding.ASCII.GetBytes(Text)),
            ("b_u16.c", Encode(new UnicodeEncoding(false, true), Text)),
            ("c_invalid.c", invalid),
            ("d_short.c", Encoding.ASCII.GetBytes("int only;\n")));

        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, EditDensity = 0.3, Seed = 5 }));

        foreach (var side in new[] { "v1", "v2" })
        {
            var recs = Records(Path.Combine(tmp.Path, $"corpus-delta-{side}.json"));
            foreach (var f in new[] { "a_plain.c", "b_u16.c", "c_invalid.c", "d_short.c" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(corpus, f));
                byte[] v = File.ReadAllBytes(Path.Combine(tmp.Path, $"corpus_{side}", f));
                Assert.True(recs.ContainsKey(f) == !b.SequenceEqual(v), $"{side}/{f}: record={recs.ContainsKey(f)} but bytes differ={!b.SequenceEqual(v)}");
                if (recs.ContainsKey(f)) Assert.Equal(Sha(v), recs[f].GetProperty("newSha").GetString());
            }
            Assert.True(File.ReadAllBytes(Path.Combine(tmp.Path, $"corpus_{side}", "b_u16.c")).AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }));
            Assert.False(recs.ContainsKey("c_invalid.c"));
        }
    }

    [Fact]
    public void Rename_OfNonTextFile_IsAPureRename()
    {
        using var tmp = new TempDir();
        byte[] invalid = Enumerable.Range(0, 512).Select(i => (byte)(0x80 + i % 128)).ToArray();
        // ordinal order: a_ok.c (band 1000 → pure anyway), b_bad.c (band 900 → would be rename+edit if it were text)
        string corpus = BuildCorpus(tmp, ("a_ok.c", Encoding.ASCII.GetBytes(Text)), ("b_bad.c", invalid));
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "rename", Seed = 1 }));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "corpus-delta.json")));
        var ren = doc.RootElement.GetProperty("fileOps").GetProperty("renamed").EnumerateArray()
            .Single(r => r.GetProperty("from").GetString() == "b_bad.c");
        Assert.Equal(1000, ren.GetProperty("similarityMilli").GetInt32());
        Assert.Equal(invalid, File.ReadAllBytes(Path.Combine(corpus, ren.GetProperty("to").GetString()!)));
        Assert.Empty(doc.RootElement.GetProperty("fileOps").GetProperty("modified").EnumerateArray());
    }

    [Fact]
    public void CompileCommandsJson_IsNeverAMutationTarget()
    {
        using var tmp = new TempDir();
        byte[] db = Encoding.ASCII.GetBytes("[\n  {\"directory\": \"C:/abs/path\", \"file\": \"C:/abs/path/a.c\"}\n]\n");
        string corpus = BuildCorpus(tmp, ("a.c", Encoding.ASCII.GetBytes(Text)), ("compile_commands.json", db));
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "all", EditDensity = 1.0, Seed = 1 }));
        Assert.Equal(db, File.ReadAllBytes(Path.Combine(corpus, "compile_commands.json")));
        Assert.Equal(new[] { "a.c" }, Records(Path.Combine(tmp.Path, "corpus-delta.json")).Keys);
    }

    [Theory]
    [InlineData("strided")]
    [InlineData("single")]
    public void Giant_Utf16_StaysUtf16_InvalidGiant_Untouched(string mode)
    {
        using var tmp = new TempDir();
        var u16 = new UnicodeEncoding(false, true);
        string bigText = string.Concat(Enumerable.Range(0, 40_000).Select(i => $"#define REG_{i} {i}\n"));   // > 1 MB as UTF-16
        byte[] invalid = Enumerable.Range(0, 1_200_000).Select(i => (byte)(0x80 + i % 128)).ToArray();
        string corpus = BuildCorpus(tmp, ("a_u16.h", Encode(u16, bigText)), ("b_invalid.h", invalid));

        Assert.Equal(0, Mutator.Run(new MutateOptions
            { Corpus = corpus, Target = "giant", GiantMinMb = 1, GiantEdit = mode, EditDensity = 0.1, Seed = 1 }));

        byte[] after = File.ReadAllBytes(Path.Combine(corpus, "a_u16.h"));
        Assert.True(after.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }));
        string decoded = u16.GetString(after, 2, after.Length - 2);
        Assert.Contains(mode == "single" ? "// mutate single-locality insert" : "/*mut:", decoded);
        Assert.StartsWith("#define REG_0 0", decoded);

        Assert.Equal(invalid, File.ReadAllBytes(Path.Combine(corpus, "b_invalid.h")));
        Assert.Empty(Directory.GetFiles(corpus, "*.mut.tmp"));
        Assert.Equal(new[] { "a_u16.h" }, Records(Path.Combine(tmp.Path, "corpus-delta.json")).Keys);
    }
}
