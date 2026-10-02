using CodeSpawner.Cli;
using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The leaf emitters (one file each from a dir + index) and the generation support types (Rng, DirTree,
/// PopulationStats, DiskProbe). These produce the corpus bytes the whole tool exists to fabricate; the
/// end-to-end bench gate exercises them at scale, these pin their shape + determinism at unit level.
/// </summary>
public class GenerationEmitterTests
{
    private static (GenOptions o, DirTree tree, string outFull) Setup(TempDir tmp, params string[] extra)
    {
        string outDir = Path.Combine(tmp.Path, "corpus");
        var args = new List<string> { "--out", outDir, "--seed", "1", "--dirs", "2", "--depth", "3", "--linked-roots", "1" };
        args.AddRange(extra);
        var o = ArgParser.ParseGen(args.ToArray());
        string outFull = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outFull);
        return (o, DirTree.Build(o, outFull), outFull);
    }

    // ---- RegHeaderEmitter ----

    [Fact]
    public void RegHeader_RespectsByteCeiling_AndReportsStats()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "regmap.h");
        var s = RegHeaderEmitter.Write(path, defines: long.MaxValue, maxBytes: 8 * 1024, fam: 0);

        Assert.True(File.Exists(path));
        Assert.Equal(new FileInfo(path).Length, s.Bytes);       // reported bytes == on-disk bytes
        Assert.True(s.Bytes > 0 && s.Bytes <= 8 * 1024 + 4096);  // honoured the ceiling (+ at most one register)
        Assert.True(s.Idents > 0);
        string text = File.ReadAllText(path);
        Assert.Contains("#ifndef REGMAP_", text);
        Assert.Contains("#define HWIO_BLK0_", text);
    }

    [Fact]
    public void RegHeader_DefineCapStopsEarly_AndIsDeterministic()
    {
        using var tmp = new TempDir();
        // same leaf name in different dirs — the include guard embeds the file name, so the bytes only match
        // when the name matches.
        string da = Path.Combine(tmp.Path, "da"), db = Path.Combine(tmp.Path, "db");
        Directory.CreateDirectory(da); Directory.CreateDirectory(db);
        string a = Path.Combine(da, "reg.h"), b = Path.Combine(db, "reg.h");
        var sa = RegHeaderEmitter.Write(a, defines: 30, maxBytes: long.MaxValue, fam: 7);
        var sb = RegHeaderEmitter.Write(b, defines: 30, maxBytes: long.MaxValue, fam: 7);
        Assert.Equal(sa.Bytes, sb.Bytes);                       // same inputs ⇒ byte-identical
        Assert.Equal(sa.Idents, sb.Idents);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.True(sa.Bytes < 8 * 1024);                        // the define cap, not a byte ceiling, stopped it
    }

    // ---- BlobEmitter / TinyFileEmitter / OrdinaryHeaderEmitter ----

    [Fact]
    public void Blob_WritesDeterministicCArray()
    {
        using var tmp = new TempDir();
        var (_, tree, _) = Setup(tmp);
        string d1 = Path.Combine(tmp.Path, "d1"), d2 = Path.Combine(tmp.Path, "d2");
        Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
        var r1 = Rng.For(1, Category.Blob, 3); BlobEmitter.Write(d1, 3, ref r1);
        var r2 = Rng.For(1, Category.Blob, 3); BlobEmitter.Write(d2, 3, ref r2);

        Assert.True(File.Exists(Path.Combine(d1, "blob_3.c")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(d1, "blob_3.c")),
                     File.ReadAllBytes(Path.Combine(d2, "blob_3.c")));   // same seed+index ⇒ identical
        Assert.Contains("unsigned char", File.ReadAllText(Path.Combine(d1, "blob_3.c")));
    }

    [Fact]
    public void TinyFile_WritesCsvWithHeaderAndRows()
    {
        using var tmp = new TempDir();
        string d = Path.Combine(tmp.Path, "d"); Directory.CreateDirectory(d);
        var rng = Rng.For(1, Category.TinyFile, 0);
        TinyFileEmitter.Write(d, 0, ref rng);
        var lines = File.ReadAllLines(Path.Combine(d, "data_0.csv"));
        Assert.Equal("id,name,value,ts", lines[0]);
        Assert.Equal(26, lines.Length);                         // header + 25 rows
        Assert.StartsWith("0,item_0,", lines[1]);               // rows are 0-indexed
    }

    [Fact]
    public void OrdinaryHeader_WritesGuardedPrototypes_NoRng()
    {
        using var tmp = new TempDir();
        string d = Path.Combine(tmp.Path, "d"); Directory.CreateDirectory(d);
        OrdinaryHeaderEmitter.Write(d, 5);
        string text = File.ReadAllText(Path.Combine(d, "hdr_5.h"));
        Assert.Contains("#ifndef HDR_5_H", text);
        Assert.Contains("#endif", text);
        Assert.Contains("mod5_op0", text);
        Assert.Contains("mod5_op29", text);                     // 30 prototypes, 0..29
    }

    // ---- CompileDbEmitter ----

    [Theory]
    [InlineData("full", 4)]
    [InlineData("partial", 2)]
    public void CompileDb_EmitsEntriesPerMode(string mode, int expected)
    {
        using var tmp = new TempDir();
        var (o, _, outFull) = Setup(tmp, "--compile-db", mode);
        var files = Enumerable.Range(0, 4).Select(i => new CFileInfo(i, Path.Combine(outFull, $"src_{i}.c"), 2, 0)).ToList();
        CompileDbEmitter.Write(o, outFull, files);

        string db = Path.Combine(outFull, "compile_commands.json");
        Assert.True(File.Exists(db));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(db));
        Assert.Equal(expected, doc.RootElement.GetArrayLength());
        Assert.Contains("clang", doc.RootElement[0].GetProperty("command").GetString());
    }

    [Fact]
    public void CompileDb_NoneMode_WritesNothing()
    {
        using var tmp = new TempDir();
        var (o, _, outFull) = Setup(tmp);   // default compile-db is none
        CompileDbEmitter.Write(o, outFull, new List<CFileInfo> { new(0, Path.Combine(outFull, "a.c"), 1, 0) });
        Assert.False(File.Exists(Path.Combine(outFull, "compile_commands.json")));
    }

    // ---- Rng / DirTree / PopulationStats / DiskProbe ----

    [Fact]
    public void Rng_IsDeterministicPerStream_AndVariesByIndex()
    {
        var a = Rng.For(1, Category.Blob, 0);
        var b = Rng.For(1, Category.Blob, 0);
        var c = Rng.For(1, Category.Blob, 1);
        Assert.Equal(a.NextULong(), b.NextULong());             // same (seed,cat,index) ⇒ same stream
        Assert.NotEqual(Rng.For(1, Category.Blob, 0).NextULong(), c.NextULong());  // different index ⇒ different
        var r = Rng.For(1, Category.Source, 0);
        int v = r.Next(10);
        Assert.InRange(v, 0, 9);
        Assert.InRange(Rng.For(1, Category.Source, 0).Next(5, 8), 5, 7);
    }

    [Fact]
    public void DirTree_BuildsLeafDirs_AndPickIsInRange()
    {
        using var tmp = new TempDir();
        var (_, tree, outFull) = Setup(tmp);
        Assert.NotEmpty(tree.Dirs);
        Assert.All(tree.Dirs, d => Assert.True(Directory.Exists(d)));
        Assert.Contains(outFull, tree.Roots);
        var rng = Rng.For(1, Category.Placement, 0);
        Assert.Contains(tree.PickDir(ref rng), tree.Dirs);
    }

    [Fact]
    public void PopulationStats_AccumulatesAndSnapshotsSorted()
    {
        var s = new PopulationStats();
        s.Add("zebra", 1, 100, 10);
        s.Add("alpha", 2, 200, 20);
        s.Add("alpha", 3, 50, 5);                               // accumulates onto alpha
        var snap = s.Snapshot();
        Assert.Equal(new[] { "alpha", "zebra" }, snap.Select(p => p.Name).ToArray());   // sorted
        var alpha = snap.First(p => p.Name == "alpha");
        Assert.Equal(5, alpha.Files);
        Assert.Equal(250, alpha.Bytes);
        Assert.Equal(25, alpha.Idents);
    }

    [Fact]
    public void DiskProbe_BogusPath_ReturnsNull_DoesNotThrow()
    {
        // A non-existent / unresolvable path must degrade to null (unknown), never throw.
        Assert.Null(DiskProbe.IsSolidState(Path.Combine("Z:\\", "no-such-" + Guid.NewGuid())));
    }
}
