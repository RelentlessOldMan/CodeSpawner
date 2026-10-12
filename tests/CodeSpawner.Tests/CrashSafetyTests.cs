using System.Diagnostics;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// What a failure or a kill part-way leaves behind, and what a regen clears. Mutate journals every tree it edits,
/// so an interrupted run is refused by the next mutate and FAILed by verify instead of passing as a corpus its
/// manifest no longer describes. Every in-place rewrite goes through a temp file, so a failed edit leaves the file
/// whole and no temp. A regen clears the old corpus's mutate artifacts, variants and linked roots, deleting its
/// marker last. Manifest paths never pass through a junction or symlink, and only reach recorded linked roots.
/// </summary>
[Collection("CapturesConsole")]   // swaps the process-wide Console: never alongside another class that does
public class CrashSafetyTests
{
    private static string _lastErr = "";
    private static int Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var err = new StringWriter();
        Console.SetOut(TextWriter.Null);
        Console.SetError(err);
        try { return CodeSpawner.Program.Main(args); }
        finally { _lastErr = err.ToString(); Console.SetOut(o); Console.SetError(e); }
    }

    private static readonly string[] Tiny =
    {
        "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
        "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2",
    };

    private static string Gen(TempDir tmp, string name, params string[] extra)
    {
        string corpus = Path.Combine(tmp.Path, name);
        int rc = Cli(new[] { "gen", "--out", corpus }.Concat(Tiny).Concat(extra).ToArray());
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        return corpus;
    }

    private static void Junction(string link, string target)
    {
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(link), "junction setup failed");
    }

    private static string[] Temps(string root) =>
        Directory.GetFiles(root, "*.mut.tmp", SearchOption.AllDirectories);

    // ---- the mutate journal ----

    // A bulk run that fails after editing some files: the journal stays, no delta is written, the next mutate is
    // refused and verify FAILs. A regen clears it all.
    [Fact]
    public void InterruptedBulkMutate_LeavesJournal_MutateRefuses_VerifyFails_RegenClears()
    {
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c");
        Assert.Equal(0, Cli("verify", "--corpus", corpus));

        var sources = Directory.GetFiles(corpus, "*.c", SearchOption.AllDirectories)
            .OrderBy(f => PathUtil.Rel(corpus, f), StringComparer.Ordinal).ToList();
        Assert.True(sources.Count >= 2);
        string first = sources[0], last = sources[^1];
        string firstBefore = File.ReadAllText(first), lastBefore = File.ReadAllText(last);
        Directory.CreateDirectory(last + ".mut.tmp");                   // the last file's rewrite fails

        Assert.Equal(1, Cli("mutate", "--corpus", corpus, "--target", "source", "--edit-kind", "eol"));
        Assert.NotEqual(firstBefore, File.ReadAllText(first));          // part-way: earlier files are edited
        Assert.Equal(lastBefore, File.ReadAllText(last));
        Assert.True(File.Exists(Path.Combine(corpus, Mutator.JournalName)));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "c-delta.json")));
        Directory.Delete(last + ".mut.tmp");

        Assert.Equal(1, Cli("mutate", "--corpus", corpus, "--target", "source", "--edit-kind", "eol"));
        Assert.Contains("interrupted", _lastErr);
        Assert.Equal(firstBefore.ReplaceLineEndings("\r\n"), File.ReadAllText(first));   // not applied twice

        Assert.Equal(1, Cli("verify", "--corpus", corpus));
        Assert.Contains(Mutator.JournalName, _lastErr);

        Gen(tmp, "c");
        Assert.False(File.Exists(Path.Combine(corpus, Mutator.JournalName)));
        Assert.Equal(0, Cli("verify", "--corpus", corpus));
    }

    [Fact]
    public void SuccessfulMutates_LeaveNoJournal()
    {
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c", "--cfiles", "8");                // symbol mode needs a few sources per edit type
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--edits", "2"));
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--target", "source", "--three-way"));
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--target", "source", "--edit-kind", "rename"));
        foreach (var dir in new[] { corpus, corpus + "_v1", corpus + "_v2" })
            Assert.False(File.Exists(Path.Combine(dir, Mutator.JournalName)), dir);
    }

    // ---- in-place rewrites go through a temp file ----

    private static string OneFileCorpus(TempDir tmp, string name, string text)
    {
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        File.WriteAllText(Path.Combine(corpus, name), text);
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        m.Symbols["func_0"] = new SymbolEntry { Def = name + ":1" };
        ManifestWriter.Write(m, Path.Combine(tmp.Path, "corpus-manifest.json"));
        return corpus;
    }

    // The target is held open without delete sharing, so the final rename fails (Windows): the file keeps its
    // bytes, no .mut.tmp is left (the giant and binary paths used to leave theirs), and the journal stays.
    [Theory]
    [InlineData("eol", false, null)]
    [InlineData("eol", true, null)]                                    // streamed (above the in-memory ceiling)
    [InlineData("binary", false, null)]
    [InlineData("content", true, "strided")]                             // giant strided run
    [InlineData("content", true, "single")]                            // giant single-locality insert
    public void FailedInPlaceRewrite_KeepsFileWhole_AndLeavesNoTemp(string kind, bool big, string? giantEdit)
    {
        if (!OperatingSystem.IsWindows()) return;                      // POSIX lets a rename replace an open file
        using var tmp = new TempDir();
        int lines = big ? 60_000 : 50;                                // big: over the 1 MB giant floor
        string text = string.Concat(Enumerable.Range(0, lines).Select(i => $"int line_{i:D5} = {i};\n"));
        string corpus = OneFileCorpus(tmp, "a.c", text);
        string path = Path.Combine(corpus, "a.c");
        byte[] before = File.ReadAllBytes(path);

        var o = new MutateOptions { Corpus = corpus, Target = giantEdit is null ? "source" : "giant", GiantMinMb = 1,
                                    EditKind = kind, EditDensity = 0.5, Seed = 1, GiantEdit = giantEdit ?? "strided" };
        Mutator.InMemoryTextMaxOverride = big && giantEdit is null ? 1024 : null;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = Record.Exception(() => Mutator.Run(o));
                Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected: {ex}");
            }
        }
        finally { Mutator.InMemoryTextMaxOverride = null; }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Temps(corpus));
        Assert.True(File.Exists(Path.Combine(corpus, Mutator.JournalName)));
    }

    // ---- regen clears what the old corpus left beside it ----

    [Fact]
    public void Regen_DropsDeltasStepDeltasAndVariants_SparesLookalikes()
    {
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c", "--cfiles", "8");
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--target", "source", "--three-way"));
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--edits", "2"));
        string[] ours = { "c-delta-v1.json", "c-delta-v2.json", "c-conflict.json", "c-delta-1.json", "c-delta-2.json" };
        foreach (var f in ours) Assert.True(File.Exists(tmp.File(f)), f);
        File.WriteAllText(tmp.File("c-delta-notes.json"), "{}");        // matches the glob, not a name we write

        Gen(tmp, "c", "--cfiles", "8");
        foreach (var f in ours) Assert.False(File.Exists(tmp.File(f)), f);
        Assert.False(Directory.Exists(corpus + "_v1"));
        Assert.False(Directory.Exists(corpus + "_v2"));
        Assert.True(File.Exists(tmp.File("c-delta-notes.json")));
    }

    [Fact]
    public void Regen_ClearsRecordedLinkedRoots_AndFewerRootsDropTheExtra()
    {
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c", "--linked-roots", "3");
        File.WriteAllText(Path.Combine(corpus + "_root1", "stray.txt"), "left over");
        File.WriteAllText(Path.Combine(corpus + "_root2", "stray.txt"), "left over");

        Gen(tmp, "c", "--linked-roots", "2");
        Assert.False(File.Exists(Path.Combine(corpus + "_root1", "stray.txt")));
        Assert.False(Directory.Exists(corpus + "_root2"));
        Assert.Equal(0, Cli("verify", "--corpus", corpus));
    }

    [Fact]
    public void UnrecordedLinkedRoot_InTheWay_NeedsForce_OutOfRangeIsLeftAlone()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c");
        string mine = Path.Combine(corpus + "_root1", "mine.txt"), far = Path.Combine(corpus + "_root5", "keep.txt");
        foreach (var f in new[] { mine, far }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, "mine"); }

        Assert.Equal(2, Cli(new[] { "gen", "--out", corpus, "--linked-roots", "2" }.Concat(Tiny).ToArray()));
        Assert.Contains("linked root", _lastErr);
        Assert.True(File.Exists(mine));
        Assert.False(Directory.Exists(corpus));                        // refused before anything was written

        Gen(tmp, "c", "--linked-roots", "2", "--force");
        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(far));                                 // not in the new corpus's way: untouched
    }

    // A 1.1.5 corpus's marker doesn't record its linked roots: its _rootN in the new range are its own (cleared, no
    // --force needed), and an unrecorded one beyond that range is left alone.
    [Fact]
    public void LegacyMarker_InRangeLinkedRootsCleared_WithoutForce_BeyondRangeLeftAlone()
    {
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c", "--linked-roots", "2");
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner 1.1.5\n");
        File.WriteAllText(Path.Combine(corpus + "_root1", "stray.txt"), "left over");
        string far = Path.Combine(corpus + "_root4", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(far)!);
        File.WriteAllText(far, "mine");

        Gen(tmp, "c", "--linked-roots", "2");
        Assert.False(File.Exists(Path.Combine(corpus + "_root1", "stray.txt")));
        Assert.True(File.Exists(far));
        Assert.Equal(2, OutputDir.ReadLinkedRoots(corpus));
        Assert.Equal(0, Cli("verify", "--corpus", corpus));
    }

    // A wipe that fails part-way (a file held open) keeps the marker, so the next regen recognises the directory
    // and needs no --force. A plain recursive delete took the marker first (it sorts first).
    [Fact]
    public void InterruptedWipe_KeepsTheMarker_SoRegenNeedsNoForce()
    {
        if (!OperatingSystem.IsWindows()) return;                      // POSIX deletes an open file
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c");
        string held = Directory.GetFiles(corpus, "*.c", SearchOption.AllDirectories).First();
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(1, Cli(new[] { "gen", "--out", corpus }.Concat(Tiny).ToArray()));
        Assert.True(File.Exists(Path.Combine(corpus, ".codespawner")));

        Gen(tmp, "c");
        Assert.Equal(0, Cli("verify", "--corpus", corpus));
    }

    // ---- containment ----

    [Fact]
    public void Containment_RefusesPathsThroughAJunction_InTheCorpusOrAsALinkedRoot()
    {
        if (!OperatingSystem.IsWindows()) return;                      // junctions are an NTFS construct
        using var tmp = new TempDir();
        string outside = Path.Combine(tmp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "user.c"), "int user;\n");
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(Path.Combine(corpus, "real"));
        Junction(Path.Combine(corpus, "ext"), outside);
        Junction(corpus + "_root1", outside);
        try
        {
            Assert.NotNull(PathUtil.ResolveInCorpus(corpus, "real/x.c"));
            Assert.NotNull(PathUtil.ResolveInCorpus(corpus, "real/new/x.c"));          // not there yet: fine
            Assert.Null(PathUtil.ResolveInCorpus(corpus, "ext/user.c"));
            Assert.Null(PathUtil.ResolveInCorpus(corpus, "ext/deeper/x.c"));
            Assert.Null(PathUtil.ResolveInCorpus(corpus, "../corpus_root1/user.c"));
        }
        finally { Directory.Delete(Path.Combine(corpus, "ext")); Directory.Delete(corpus + "_root1"); }
    }

    [Fact]
    public void SymbolMutate_DefThroughAJunction_RefusedAndOutsideUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string outside = Path.Combine(tmp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "user.c"), "#include <x>\nint func_1(int x) { return x; }\n");
        string corpus = OneFileCorpus(tmp, "a.c", "int func_0(void) { return 0; }\n");
        var model = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        model.Symbols["func_1"] = new SymbolEntry { Def = "ext/user.c:2" };
        ManifestWriter.Write(model, Path.Combine(tmp.Path, "corpus-manifest.json"));
        Junction(Path.Combine(corpus, "ext"), outside);
        try
        {
            var ex = Assert.Throws<ArgException>(() => Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 1, Seed = 1 }));
            Assert.Contains("points outside the corpus", ex.Message);
            Assert.True(File.Exists(Path.Combine(outside, "user.c")));
            Assert.Equal(1, Cli("verify", "--corpus", corpus));                                    // verify FAILs it too
            Assert.Contains("escapes corpus root", _lastErr);
        }
        finally { Directory.Delete(Path.Combine(corpus, "ext")); }
    }

    [Fact]
    public void LinkedRootIndex_IsCappedByTheMarker_LegacyMarkerUncapped()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner 9.9.9\nlinkedRoots 2\n");
        Assert.NotNull(PathUtil.ResolveInCorpus(corpus, "../corpus_root1/x.c"));
        Assert.Null(PathUtil.ResolveInCorpus(corpus, "../corpus_root2/x.c"));
        Assert.Null(PathUtil.ResolveInCorpus(corpus, "../corpus_root0/x.c"));

        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner 1.1.5\n");
        Assert.NotNull(PathUtil.ResolveInCorpus(corpus, "../corpus_root2/x.c"));
    }

    // A VENDOR_missing_*.h reachable only through a junction is not in the tree: verify must not follow the link.
    [Fact]
    public void Verify_VendorScan_DoesNotFollowAJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        string corpus = Gen(tmp, "c", "--unresolved-includes", "1");
        Assert.Contains("unreachableRefs", File.ReadAllText(tmp.File("c-manifest.json")));
        string outside = Path.Combine(tmp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "VENDOR_missing_zz.h"), "#define VENDOR_OK 1\n");
        Junction(Path.Combine(corpus, "ext"), outside);
        try { Assert.Equal(0, Cli("verify", "--corpus", corpus)); }
        finally { Directory.Delete(Path.Combine(corpus, "ext")); }

        File.Copy(Path.Combine(outside, "VENDOR_missing_zz.h"), Path.Combine(corpus, "VENDOR_missing_zz.h"));
        Assert.Equal(1, Cli("verify", "--corpus", corpus));            // the check is live for a real one
    }

    // ---- from-profile --with-oracle probes the manifest location before synthesizing anything ----

    [Fact]
    public void FromProfile_WithOracle_UnwritableManifest_FailsBeforeSynthesis()
    {
        using var tmp = new TempDir();
        string tree = Path.Combine(tmp.Path, "tree");
        Directory.CreateDirectory(tree);
        for (int i = 0; i < 6; i++) File.WriteAllText(Path.Combine(tree, $"f{i}.c"), $"int f{i}(void) {{ return {i}; }}\n");
        string prof = tmp.File("p.json");
        Assert.Equal(0, Cli("scan", tree, "--out", prof));

        string o = Path.Combine(tmp.Path, "g");
        Directory.CreateDirectory(tmp.File("g-manifest.json.tmp"));    // the manifest's temp name is taken
        Assert.Equal(2, Cli("gen", "--out", o, "--from-profile", prof, "--with-oracle"));
        Assert.Contains("cannot write the manifest", _lastErr);
        Assert.Equal(new[] { ".codespawner" }, Directory.GetFileSystemEntries(o).Select(Path.GetFileName));
    }
}
