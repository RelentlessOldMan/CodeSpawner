using CodeSpawner.Cli;
using CodeSpawner.Generation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The call-graph and pathology emitters (source/unresolved/broad-token/pathological/dup/long-line/
/// encoding-mix). These carry the ground-truth the manifest is built from — def/call sites, unreachable
/// refs, dup-group hashes — so their returned shapes are what CodeCompass's verify oracle leans on.
/// </summary>
public class GenerationGraphTests
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

    private static string Sha(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Source_EmitsCFiles_WithDefAndCallLines()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp);
        var res = SourceEmitter.Emit(o, tree, Array.Empty<string>(), nC: 5, nSmallH: 2);

        Assert.Equal(5, res.Files.Count);
        for (int i = 0; i < 5; i++)
        {
            var ci = res.Files.Single(f => f.Index == i);
            Assert.True(File.Exists(ci.Path));
            Assert.EndsWith($"src_{i}.c", ci.Path.Replace('\\', '/'));
            Assert.True(ci.DefLine > 0);
            if (i == 0) Assert.Equal(0, ci.CallLine);           // no predecessor to call
            else Assert.True(ci.CallLine > 0);                  // calls func_{i-1}
        }
        Assert.True(File.Exists(res.HotPath));                  // hot_shared.c always defined
    }

    [Fact]
    public void Source_WithGiant_CoLocatesIncludersAndHotRefs()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp, "--giant-includers", "1");
        // a stand-in giant header the source emitter co-locates .c includers with
        string giant = Path.Combine(tree.Dirs[0], "regmap_block0.h");
        File.WriteAllText(giant, "#pragma once\n");
        var res = SourceEmitter.Emit(o, tree, new[] { giant }, nC: 4, nSmallH: 1);

        Assert.True(res.GiantIncluders >= 1);
        Assert.NotEmpty(res.HotRefs);                           // each includer calls hot_shared()
        Assert.All(res.HotRefs, r => Assert.Contains(":", r));  // "path:line" sites
    }

    [Fact]
    public void Unresolved_EmitsVendorGated_AndUnreachableRefs()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp, "--unresolved-includes", "2");
        var res = UnresolvedIncludeEmitter.Emit(o, tree, nC: 4);

        Assert.NotNull(res);
        Assert.True(File.Exists(res!.VendorGatedPath));
        Assert.NotEmpty(res.UnreachableRefs);
        Assert.All(res.UnreachableRefs, r => Assert.Contains(":", r));
    }

    [Fact]
    public void Unresolved_Disabled_ReturnsNull()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp, "--unresolved-includes", "0");
        Assert.Null(UnresolvedIncludeEmitter.Emit(o, tree, nC: 4));
    }

    [Fact]
    public void BroadToken_EmitsCarrierAndDef_WithRefs()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp, "--hot-token-share", "1");   // every carrier gets token call sites
        var stats = new PopulationStats();
        var res = BroadTokenEmitter.Emit(o, tree, nFiles: 1, stats);

        Assert.True(File.Exists(res.DefPath));                  // broad_hot.c
        Assert.Equal(2, res.FileCount);                         // 1 carrier + the def
        Assert.NotEmpty(res.Refs);                              // injected call sites
        Assert.True(res.TotalBytes > 0);
    }

    [Fact]
    public void Pathological_Emits3SymbolsPerFile_WithExpectedMissMix()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp);
        var stats = new PopulationStats();
        var syms = PathologicalSymbolEmitter.Emit(o, tree, nFiles: 2, stats);

        Assert.True(syms.Count >= 6 && syms.Count % 2 == 0);    // a fixed set of symbols per file, evenly split
        Assert.Contains(syms, s => s.ExpectedMiss);             // token-paste ⇒ expected miss
        Assert.Contains(syms, s => !s.ExpectedMiss);            // lexically-present
        Assert.All(syms, s => { Assert.True(File.Exists(s.Path)); Assert.True(s.Line > 0); });
    }

    [Fact]
    public void Dup_GroupsAreByteIdentical_NearVariantDiffers()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp);
        var stats = new PopulationStats();
        var groups = DupContentEmitter.Emit(o, tree, nGroups: 2, copies: 2, stats);

        Assert.Equal(2, groups.Count);
        foreach (var g in groups)
        {
            Assert.True(g.Paths.Count >= 2);
            string sha0 = Sha(g.Paths[0]);
            Assert.All(g.Paths, p => Assert.Equal(sha0, Sha(p)));   // every copy byte-identical
            Assert.Equal(sha0.ToLowerInvariant(), g.Sha256);       // reported hash matches
            Assert.NotEmpty(g.NearVariants);
            Assert.NotEqual(sha0, Sha(g.NearVariants[0]));         // the near-variant is NOT identical
        }
    }

    [Fact]
    public void LongLine_WritesSizedFiles_WithNewlineAlternation()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp, "--max-line-bytes", "4096");   // small so the test writes KB
        var stats = new PopulationStats();
        LongLineEmitter.Emit(o, tree, nFiles: 2, stats);

        string f0 = FindOne(tree, "longline_0.min.js");
        string f1 = FindOne(tree, "longline_1.min.js");
        Assert.True(new FileInfo(f0).Length >= 4096);
        Assert.EndsWith("\n", File.ReadAllText(f0));            // even index: trailing newline
        Assert.False(File.ReadAllText(f1).EndsWith("\n"));      // odd index: no trailing newline
    }

    [Fact]
    public void EncodingMix_EmitsAllFiveVariants_WithExpectedBoms()
    {
        using var tmp = new TempDir();
        var (o, tree, _) = Setup(tmp);
        var stats = new PopulationStats();
        EncodingMixEmitter.Emit(o, tree, nFiles: 5, stats);

        byte[] le = File.ReadAllBytes(FindOne(tree, "enc_0_utf16le.c"));
        byte[] be = File.ReadAllBytes(FindOne(tree, "enc_1_utf16be.c"));
        byte[] u8 = File.ReadAllBytes(FindOne(tree, "enc_2_utf8bom.c"));
        Assert.Equal(new byte[] { 0xFF, 0xFE }, le[..2]);       // UTF-16LE BOM
        Assert.Equal(new byte[] { 0xFE, 0xFF }, be[..2]);       // UTF-16BE BOM
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, u8[..3]); // UTF-8 BOM
        Assert.True(File.Exists(FindOne(tree, "enc_3_invalid.dat")));
        Assert.True(File.Exists(FindOne(tree, "enc_4_nonascii.c")));
    }

    // Emitters scatter files across tree dirs via RNG, so locate one by name.
    private static string FindOne(DirTree tree, string name)
    {
        foreach (var d in tree.Roots)
        {
            var hit = Directory.EnumerateFiles(d, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        throw new Xunit.Sdk.XunitException($"file not found in tree: {name}");
    }
}
