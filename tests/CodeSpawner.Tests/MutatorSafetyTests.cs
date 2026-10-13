using System.Diagnostics;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// mutate edits and deletes files in place, so every path it acts on must stay inside the corpus: manifest-supplied
/// def paths are contained (linked-root siblings allowed), junctions are never followed, a foreign 3-way variant dir
/// is never wiped, the stale-artifact sweep only deletes names this tool writes, and a failed artifact write leaves
/// no truncated file behind.
/// </summary>
public class MutatorSafetyTests
{
    // tmp\corpus with n one-func source files + a sibling manifest whose func_i defs are given by `defs`.
    private static string BuildCorpus(TempDir tmp, int nSource, params (string name, string def)[] defs)
    {
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        for (int i = 0; i < nSource; i++)
            File.WriteAllText(Path.Combine(corpus, $"src_{i}.c"),
                string.Join("\n", Enumerable.Range(0, 30).Select(l => $"int f{i}_{l}(void) {{ return {l}; }}")) + "\n");
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        foreach (var (name, def) in defs) m.Symbols[name] = new SymbolEntry { Def = def };
        if (defs.Length == 0) m.Symbols["func_0"] = new SymbolEntry { Def = "src_0.c:1" };
        ManifestWriter.Write(m, Path.Combine(tmp.Path, "corpus-manifest.json"));
        return corpus;
    }

    private static string Victim(TempDir tmp, string name, string text)
    {
        string dir = Path.Combine(tmp.Path, "victim");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Legacy_ManifestDefOutsideCorpus_RefusedBeforeAnyDiskChange()
    {
        using var tmp = new TempDir();
        string a = Victim(tmp, "a.c", "#include <x>\nint keep_a;\n");
        string b = Victim(tmp, "b.c", "#include <x>\nint keep_b;\n");
        string corpus = BuildCorpus(tmp, 2,
            ("func_1", "../victim/a.c:2"),                          // '..' escape
            ("func_2", b.Replace('\\', '/') + ":2"));               // absolute path

        var ex = Assert.Throws<ArgException>(() => Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 2, Seed = 1 }));
        Assert.Contains("points outside the corpus", ex.Message);
        Assert.Equal("#include <x>\nint keep_a;\n", File.ReadAllText(a));
        Assert.Equal("#include <x>\nint keep_b;\n", File.ReadAllText(b));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "corpus-delta-1.json")));
    }

    [Fact]
    public void Legacy_LinkedRootSiblingDef_IsInsideTheCorpus()
    {
        using var tmp = new TempDir();
        string sib = Path.Combine(tmp.Path, "corpus_root1");
        Directory.CreateDirectory(sib);
        File.WriteAllText(Path.Combine(sib, "fed.c"), "#include <x>\nint func_1(int x) { return x; }\n");
        string corpus = BuildCorpus(tmp, 1, ("func_1", "../corpus_root1/fed.c:2"));

        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 1, Seed = 1 }));   // edit 1 = remove
        Assert.False(File.Exists(Path.Combine(sib, "fed.c")));
    }

    [Fact]
    public void ResolveInCorpus_AllowsCorpusAndLinkedRoots_RefusesEverythingElse()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "corpus");
        Assert.NotNull(Generation.PathUtil.ResolveInCorpus(corpus, "a/b.c"));
        Assert.NotNull(Generation.PathUtil.ResolveInCorpus(corpus, "../corpus_root12/x.c"));
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, "../corpus_rootX/x.c"));    // not digits
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, "../corpus_root/x.c"));     // no index
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, "../corpus_root1"));        // the root itself, no file
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, "../corpus2/x.c"));         // a lookalike sibling
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, "../../x.c"));
        Assert.Null(Generation.PathUtil.ResolveInCorpus(corpus, Path.Combine(tmp.Path, "x.c")));
    }

    [Fact]
    public void Bulk_DoesNotFollowAJunctionOutOfTheCorpus()
    {
        if (!OperatingSystem.IsWindows()) return;                    // junctions are an NTFS construct
        using var tmp = new TempDir();
        string outside = Victim(tmp, "user.c", "int user_code;\nint more;\n");
        string corpus = BuildCorpus(tmp, 2);
        string link = Path.Combine(corpus, "ext");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(outside)}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.True(File.Exists(Path.Combine(link, "user.c")), "junction setup failed");
        try
        {
            Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", EditKind = "eol", Seed = 1 }));
            Assert.Equal("int user_code;\nint more;\n", File.ReadAllText(outside));
            Assert.DoesNotContain("ext/user.c", File.ReadAllText(Path.Combine(tmp.Path, "corpus-delta.json")));
        }
        finally { Directory.Delete(link); }                          // removes the junction only, never its target
    }

    [Fact]
    public void ThreeWay_ForeignVariantDir_RefusedBeforeTouchingAnything()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, 3);
        string precious = Path.Combine(tmp.Path, "corpus_v1", "myproject", "IMPORTANT.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(precious)!);
        File.WriteAllText(precious, "mine");
        string priorDelta = Path.Combine(tmp.Path, "corpus-delta.json");
        File.WriteAllText(priorDelta, "{\"prior\":true}");
        string before = File.ReadAllText(Path.Combine(corpus, "src_0.c"));

        var ex = Assert.Throws<ArgException>(() =>
            Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, EditDensity = 0.5 }));
        Assert.Contains("was not created by CodeSpawner", ex.Message);
        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(priorDelta));                       // refused BEFORE the stale-artifact sweep
        Assert.Equal(before, File.ReadAllText(Path.Combine(corpus, "src_0.c")));
    }

    [Fact]
    public void ThreeWay_ReplacesItsOwnPreviousVariants()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, 3);
        var o = new MutateOptions { Corpus = corpus, Target = "source", ThreeWay = true, EditDensity = 0.5 };
        Assert.Equal(0, Mutator.Run(o));
        Assert.Equal(0, Mutator.Run(o));                             // the previous _v1/_v2 carry the marker
    }

    [Fact]
    public void StaleArtifactSweep_SparesUserFilesThatMatchTheShardGlob()
    {
        using var tmp = new TempDir();
        string corpus = BuildCorpus(tmp, 3);
        string notes = Path.Combine(tmp.Path, "corpus-delta.shard-notes.json");
        File.WriteAllText(notes, "{}");

        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", ShardSize = 1, Seed = 1 }));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "corpus-delta.shard-000.json")));

        ForgetMutation(corpus);
        Assert.Equal(0, Mutator.Run(new MutateOptions { Corpus = corpus, Target = "source", Seed = 2 }));   // monolithic now
        Assert.Empty(Directory.GetFiles(tmp.Path, "corpus-delta.shard-0*.json"));                          // ours swept
        Assert.True(File.Exists(notes));                                                                   // user's kept
    }

    /// <summary>Drop the tree's mutation record, as a corpus mutated by 1.1.8 or earlier has none, so a second
    /// bulk run is allowed and its stale-artifact sweep can be exercised.</summary>
    internal static void ForgetMutation(string corpus) => File.Delete(Mutator.MutationStatePath(corpus));

    [Fact]
    public void AtomicFile_FailedWrite_KeepsTheOriginalAndLeavesNoTemp()
    {
        using var tmp = new TempDir();
        string path = tmp.File("artifact.json");
        File.WriteAllText(path, "good");

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, fs =>
        {
            fs.Write("partial"u8);
            throw new InvalidOperationException("writer blew up mid-document");
        }));
        Assert.Equal("good", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));

        AtomicFile.Write(path, fs => fs.Write("new"u8));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
