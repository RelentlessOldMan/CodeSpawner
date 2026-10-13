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
        byte[] invalid = Enumerable.Range(0, 4096).Select(i => (byte)(0x80 + i % 128)).ToArray();
        byte[] nul = Encoding.ASCII.GetBytes("int a;\0\0\0\nint b;\n");

        foreach (var kind in new[] { "content", "whitespace", "line-insert", "line-delete", "eol", "encoding" })
        {
            using var tmp = new TempDir();                      // a fresh corpus per kind: mutate refuses a mutated tree
            string corpus = BuildCorpus(tmp, ("a_text.c", Encoding.ASCII.GetBytes(Text)), ("b_invalid.c", invalid), ("c_nul.c", nul));
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

    // The hunks alone must rebuild the new file from the old one (pinned convention: insert oldStart = old lines
    // before it; every newStart = new lines before it + 1).
    [Theory]
    [InlineData("line-insert", 0.2)]
    [InlineData("line-insert", 1.0)]
    [InlineData("line-delete", 0.3)]
    [InlineData("line-delete", 1.0)]
    [InlineData("content", 0.3)]
    public void Hunks_ReconstructNewFileFromOld(string kind, double density)
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, ("a.c", Encoding.ASCII.GetBytes(Text)));
        string[] oldLines = File.ReadAllLines(Path.Combine(corpus, "a.c"));

        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = kind, EditDensity = density, Seed = 3 }));
        string[] newLines = File.ReadAllLines(Path.Combine(corpus, "a.c"));

        var rebuilt = new List<string>();
        int oldPos = 0;                                          // old lines consumed so far
        foreach (var h in Records(Path.Combine(tmp.Path, "corpus-delta.json"))["a.c"].GetProperty("hunks").EnumerateArray())
        {
            int os = h.GetProperty("oldStart").GetInt32(), ol = h.GetProperty("oldLines").GetInt32();
            int ns = h.GetProperty("newStart").GetInt32(), nlines = h.GetProperty("newLines").GetInt32();
            int copyTo = ol == 0 ? os : os - 1;                  // insert: after old line os; else: before it
            while (oldPos < copyTo) rebuilt.Add(oldLines[oldPos++]);
            Assert.Equal(rebuilt.Count + 1, ns);                 // newStart = new lines before + 1, every op
            for (int i = 0; i < nlines; i++) rebuilt.Add(newLines[ns - 1 + i]);
            oldPos += ol;
        }
        while (oldPos < oldLines.Length) rebuilt.Add(oldLines[oldPos++]);
        Assert.Equal(newLines, rebuilt);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'i', (byte)'n', (byte)'t', (byte)' ', (byte)'a', (byte)';', (byte)'\n', (byte)'x' })]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0, (byte)'c', 0 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a', 0, (byte)'b', 0, (byte)'c' })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0, 0, 0, (byte)'a' })]
    [InlineData(new byte[] { (byte)'a', (byte)'b' })]
    public void Binary_ResultHasEarlyNul_AndNoWideBom(byte[] original)
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, ("a.bin", original));
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "all", EditKind = "binary", EditDensity = 0.0, Seed = 1 }));

        byte[] after = File.ReadAllBytes(Path.Combine(corpus, "a.bin"));
        Assert.Equal(original.Length, after.Length);
        Assert.Contains((byte)0, after.Take(8192));
        Assert.False(after.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || after.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
                     || after.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }), "wide BOM prefix survived");
        var rec = Records(Path.Combine(tmp.Path, "corpus-delta.json"))["a.bin"];
        Assert.Equal("binary", rec.GetProperty("reason").GetString());
        Assert.Equal(Sha(after), rec.GetProperty("newSha").GetString());
    }

    // Files above Mutator.InMemoryTextMax (512 MiB) take the streamed path for eol/encoding; it must write exactly
    // the bytes the in-memory path writes. Lower the ceiling on this thread to drive small files through it.
    public static TheoryData<string, byte[]> StreamCases => new()
    {
        { "eol", Encoding.ASCII.GetBytes("a\nb\r\nc\rd\r\n\r\re") },          // LF, CRLF, lone CR, no final newline
        { "eol", Encoding.ASCII.GetBytes("x\r") },                             // trailing lone CR
        { "eol", Encode(new UnicodeEncoding(false, true), Text) },
        { "eol", Encode(new UnicodeEncoding(true, true), "a\r\nb\n") },
        { "eol", Encode(new UTF8Encoding(true), Text.Replace("\n", "\r\n")) },
        { "encoding", Encoding.UTF8.GetBytes(Text) },
        { "encoding", Encode(new UTF8Encoding(true), Text) },
        { "encoding", Encode(new UnicodeEncoding(true, true), Text) },
    };

    private static (byte[] bytes, string sha) RunOn(string kind, byte[] original, long? ceiling)
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, ("a.c", original));
        Mutator.InMemoryTextMaxOverride = ceiling;
        try { Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = kind, Seed = 1 })); }
        finally { Mutator.InMemoryTextMaxOverride = null; }
        var recs = Records(Path.Combine(tmp.Path, "corpus-delta.json"));
        return (File.ReadAllBytes(Path.Combine(corpus, "a.c")), recs.TryGetValue("a.c", out var r) ? r.GetProperty("newSha").GetString()! : "");
    }

    [Theory]
    [MemberData(nameof(StreamCases))]
    public void StreamedEolAndEncoding_MatchTheInMemoryBytes(string kind, byte[] original)
    {
        var inMemory = RunOn(kind, original, null);
        var streamed = RunOn(kind, original, 1);
        Assert.NotEqual(original, inMemory.bytes);
        Assert.Equal(inMemory.bytes, streamed.bytes);
        Assert.Equal(inMemory.sha, streamed.sha);
    }

    [Fact]
    public void AboveTheCeiling_HunkKindsSkip_AndNonTextStaysUntouched()
    {
        byte[] text = Encoding.ASCII.GetBytes(Text);
        foreach (var kind in new[] { "content", "whitespace", "line-insert", "line-delete" })
            AssertUntouched(text, RunOn(kind, text, 1));
        byte[] nul = Encoding.ASCII.GetBytes("int a;\0\nint b;\n");
        byte[] invalid = Enumerable.Range(0, 300).Select(i => (byte)(0x80 + i % 128)).ToArray();
        foreach (var kind in new[] { "eol", "encoding" })
            foreach (var bad in new[] { nul, invalid })
                AssertUntouched(bad, RunOn(kind, bad, 1));
    }

    private static void AssertUntouched(byte[] original, (byte[] bytes, string sha) after)
    {
        Assert.Equal(original, after.bytes);
        Assert.Equal("", after.sha);                           // no record
    }

    [Fact]
    public void BinaryEdit_StreamedOutputMatchesTheReferenceAlgorithm()
    {
        // 3 MB spans several 1 MiB chunks; the reference is the original whole-array algorithm.
        byte[] original = Enumerable.Range(0, 3 * 1024 * 1024 + 17).Select(i => (byte)(i * 31 % 251)).ToArray();
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, ("a.bin", original));
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "all", EditKind = "binary", EditDensity = 0.1, Seed = 9 }));

        byte[] expected = (byte[])original.Clone();
        var rng = CodeSpawner.Generation.Rng.For(9, CodeSpawner.Generation.Category.Mutate, 100_000 + 0);
        for (int k = 0; k < expected.Length; k++) if (rng.Next(10000) < 1000) expected[k] ^= 0x5A;
        expected[4] = 0;
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(corpus, "a.bin")));
        Assert.Empty(Directory.GetFiles(corpus, "*.mut.tmp"));
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
