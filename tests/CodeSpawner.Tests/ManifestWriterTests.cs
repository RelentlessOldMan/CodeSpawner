using CodeSpawner.Manifest;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// Full-manifest serialization: every optional block (populations, dupGroups, indirectEdges, byte mass,
/// roots, unreachableRefs, expectedMiss) must serialize when present and the whole thing must read back.
/// Guards the writer branches the oracle tests don't reach (populations / dupGroups).
/// </summary>
public class ManifestWriterTests
{
    private static ManifestModel FullModel()
    {
        var m = new ManifestModel { GeneratorVersion = "9.9.9", Seed = 42, CorpusRoot = "C:/x" };
        m.Populations.Add(new PopulationStat("broad-token", 8, 4096, 1000));
        m.Roots.Add("s1");

        var s1 = new SymbolEntry { Def = "a/1.c:10", Bytes = 128 };
        s1.Refs.Add("a/2.c:5");
        s1.Edges.Add("s2");
        s1.IndirectEdges.Add(new IndirectEdge { Target = "s2", Via = IndirectVia.VectorTable, Dispatched = true, Resolved = true });
        s1.UnreachableRefs = new List<string> { "a/3.c:7" };
        m.Symbols["s1"] = s1;

        m.Symbols["s2"] = new SymbolEntry { Def = "a/2.c:1", Bytes = 64 };
        m.Symbols["h0"] = new SymbolEntry { Def = "a/h.h:2", ExpectedMiss = true };

        m.DupGroups.Add(new DupGroup { Name = "g0", Sha256 = "deadbeef", Paths = { "d/a.c", "d/b.c" }, NearVariants = { "d/c.c" } });
        m.TotalOracleBytes = 192;
        return m;
    }

    [Fact]
    public void AllOptionalBlocks_Serialize()
    {
        using var tmp = new TempDir();
        string path = tmp.File("m.json");
        ManifestWriter.Write(FullModel(), path);
        string json = File.ReadAllText(path);

        foreach (var key in new[]
        {
            "\"populations\"", "\"broad-token\"", "\"dupGroups\"", "\"nearVariants\"",
            "\"indirectEdges\"", "\"vector-table\"", "\"bytes\"", "\"roots\"",
            "\"unreachableRefs\"", "\"expectedMiss\"", "\"indirectTruthSha\"", "\"totalOracleBytes\"",
        })
            Assert.Contains(key, json);
    }

    [Fact]
    public void RoundTrips_SymbolsRootsAndIndirectEdges()
    {
        using var tmp = new TempDir();
        string path = tmp.File("m.json");
        var m = FullModel();
        ManifestWriter.Write(m, path);
        var back = ManifestReader.Load(path);

        Assert.Equal(42, back.Seed);
        Assert.Equal(new[] { "s1" }, back.Roots.ToArray());
        Assert.Equal(128, back.Symbols["s1"].Bytes);
        var ie = Assert.Single(back.Symbols["s1"].IndirectEdges);
        Assert.Equal("s2", ie.Target);
        Assert.Equal(IndirectVia.VectorTable, ie.Via);
        Assert.True(ie.Dispatched);
        Assert.True(ie.Resolved);
        // the indirectTruthSha in the file reproduces from the read-back model
        Assert.Equal(IndirectDigest.Compute(m.Symbols, m.Roots), IndirectDigest.Compute(back.Symbols, back.Roots));
    }
}
