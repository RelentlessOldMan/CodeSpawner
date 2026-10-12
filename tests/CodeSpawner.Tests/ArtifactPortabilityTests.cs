using System.Text;
using System.Text.Json;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The JSON artifacts must not depend on where, or on which OS, they were made: every artifact is LF-only, and
/// baseManifestSha ignores the manifest's corpusRoot and line endings while still binding to its content. Every
/// delta also names its kind.
/// </summary>
public class ArtifactPortabilityTests
{
    private static string BuildCorpus(TempDir tmp, string name, string defLine = "a.c:1")
    {
        string corpus = Path.Combine(tmp.Path, name);
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        File.WriteAllText(Path.Combine(corpus, "a.c"), "int func_0(void) { return 0; }\nint pad(void) { return 1; }\n// tail\n");
        File.WriteAllText(Path.Combine(corpus, "b.c"), "int func_1(void) { return 1; }\n// tail\n");
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        m.Symbols["func_0"] = new SymbolEntry { Def = defLine };
        m.Symbols["func_1"] = new SymbolEntry { Def = "b.c:1" };
        ManifestWriter.Write(m, Path.Combine(tmp.Path, $"{name}-manifest.json"));
        return corpus;
    }

    [Fact]
    public void Artifacts_AreLfOnly()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, "corpus");
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditDensity = 0.5, Seed = 1, ShardSize = 1 }));
        string[] jsons = Directory.GetFiles(tmp.Path, "*.json");
        Assert.True(jsons.Length >= 4, "manifest + index + shards expected");
        foreach (var f in jsons)
        {
            byte[] b = File.ReadAllBytes(f);
            Assert.DoesNotContain((byte)'\r', b);
            Assert.Contains((byte)'\n', b);
            JsonDocument.Parse(b).Dispose();                     // still valid JSON
        }
    }

    [Fact]
    public void BaseManifestSha_IgnoresCorpusRootAndLineEndings_ButNotContent()
    {
        using var tmp = new TempDir();
        BuildCorpus(tmp, "here");
        BuildCorpus(tmp, "somewhere_else_entirely");
        BuildCorpus(tmp, "changed", defLine: "a.c:2");
        byte[] here = File.ReadAllBytes(tmp.File("here-manifest.json"));
        byte[] there = File.ReadAllBytes(tmp.File("somewhere_else_entirely-manifest.json"));
        byte[] changed = File.ReadAllBytes(tmp.File("changed-manifest.json"));
        byte[] crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(here).Replace("\n", "\r\n"));

        Assert.NotEqual(here, there);                            // the raw bytes differ by path...
        Assert.Equal(ManifestReader.IdentitySha(here), ManifestReader.IdentitySha(there));   // ...the identity doesn't
        Assert.Equal(ManifestReader.IdentitySha(here), ManifestReader.IdentitySha(crlf));
        Assert.NotEqual(ManifestReader.IdentitySha(here), ManifestReader.IdentitySha(changed));
        Assert.Equal(ManifestReader.IdentitySha(here), ManifestReader.Load(tmp.File("here-manifest.json")).Sha256);
    }

    [Fact]
    public void DeltaFiles_AreIdentical_AcrossCorpusLocations()
    {
        using var tmp = new TempDir();
        string a = BuildCorpus(tmp, "first"), b = BuildCorpus(tmp, "second_place");
        foreach (var c in new[] { a, b })
            Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = c, Target = "source", EditDensity = 0.5, Seed = 2 }));
        Assert.Equal(File.ReadAllBytes(tmp.File("first-delta.json")), File.ReadAllBytes(tmp.File("second_place-delta.json")));
    }

    [Fact]
    public void SymbolDelta_NamesItsKind()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, "corpus");
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 1, Seed = 1 }));
        using var d = JsonDocument.Parse(File.ReadAllBytes(tmp.File("corpus-delta-1.json")));
        Assert.Equal("symbol", d.RootElement.GetProperty("_meta").GetProperty("deltaKind").GetString());
    }
}
