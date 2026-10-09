using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The CLI entry point: subcommand dispatch + exit codes, and a tiny end-to-end gen → verify → scan →
/// from-profile → mutate round-trip. The round-trip is the unit-level smoke of CorpusGenerator / Scanner /
/// ProfileGenerator / ManifestVerifier / Mutator wired together through Program.Main.
/// </summary>
public class ProgramTests
{
    // Run the CLI with stdout muted + stderr captured (gen/scan are chatty); return the exit code.
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

    [Fact]
    public void Dispatch_ExitCodes()
    {
        Assert.Equal(0, Cli("version"));
        Assert.Equal(0, Cli("--help"));
        Assert.Equal(1, Cli());                       // no args ⇒ usage + error exit
        Assert.Equal(2, Cli("bogus-command"));        // unknown command
        Assert.Equal(0, Cli("digest-selftest"));      // the four golden vectors reproduce
        Assert.Equal(2, Cli("gen"));                  // ArgException (missing --out) ⇒ exit 2
    }

    [Fact]
    public void RoundTrip_Gen_Verify_Scan_FromProfile_Mutate()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "corpus");
        string[] tiny = { "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
                          "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2" };

        // gen
        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus }.Concat(tiny).ToArray()));
        Assert.True(File.Exists(Path.Combine(corpus, ".codespawner")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "corpus-manifest.json")));   // sibling manifest

        // verify the generated corpus against its own manifest
        Assert.Equal(0, Cli("verify", "--corpus", corpus));

        // scan → profile
        string prof = Path.Combine(tmp.Path, "prof.json");
        Assert.Equal(0, Cli("scan", corpus, "--out", prof));
        Assert.True(File.Exists(prof));

        // regenerate a corpus from that profile (the ProfileGenerator path)
        string corpus2 = Path.Combine(tmp.Path, "corpus2");
        Assert.Equal(0, Cli("gen", "--out", corpus2, "--from-profile", prof));
        Assert.True(Directory.EnumerateFiles(corpus2, "*", SearchOption.AllDirectories).Any());

        // a tiny bulk mutate on the generated corpus (diff-oracle mode)
        Assert.Equal(0, Cli("mutate", "--corpus", corpus, "--target", "source", "--files-changed", "1", "--edit-density", "0.5"));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "corpus-delta.json")));
    }

    [Fact]
    public void Gen_RichPathologies_PopulateManifestAndCompileDb()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "rich");
        // Every pathology band, but SIZE-CAPPED (--max-header-mb/--dense-under-mb 1, tiny max-line-bytes) so the
        // giant/dense/broad/long/enc/patho/dup/unresolved/compile-db/linked-root branches all run cheaply.
        // (The 10-129 MB big/med/shrink/restream bands stay on the bench gate.)
        int rc = Cli("gen", "--out", corpus, "--scale", "0.0003",
            "--giant-headers", "1", "--max-header-mb", "1", "--big-headers", "0", "--med-headers", "0",
            "--dense-headers", "1", "--dense-under-mb", "1", "--ordinary-headers", "2",
            "--broad-token-files", "1", "--hot-token-share", "1",
            "--long-line-files", "2", "--max-line-bytes", "4096",
            "--encoding-mix", "5", "--pathological-symbols", "1", "--dup-groups", "1", "--dup-copies", "2",
            "--unresolved-includes", "1", "--compile-db", "full", "--linked-roots", "2", "--cfiles", "3");
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");

        string mf = File.ReadAllText(Path.Combine(tmp.Path, "rich-manifest.json"));
        Assert.Contains("\"populations\"", mf);
        Assert.Contains("\"dupGroups\"", mf);
        Assert.Contains("vendor_gated", mf);            // the unresolved negative-case symbol
        Assert.Contains("broad_hot", mf);               // the broad-token symbol
        Assert.True(File.Exists(Path.Combine(corpus, "compile_commands.json")));
        Assert.True(Directory.Exists(corpus + "_root1"));   // --linked-roots 2 ⇒ a sibling federation root
        // (not verifying here: federation scatters defs into the sibling root, which a single-root verify
        //  legitimately can't resolve — verify success/failure are covered by the other round-trip tests.)
    }

    [Fact]
    public void Gen_RecordsEffectiveKnobsUnderMetaGen_ForReproducibility()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "corpus");
        int rc = Cli("gen", "--out", corpus, "--scale", "0.0003", "--seed", "42",
            "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
            "--tiny-files", "0", "--blob-files", "0", "--cfiles", "4", "--shrink-seeds", "2");
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "corpus-manifest.json")));
        var gen = doc.RootElement.GetProperty("_meta").GetProperty("gen");
        // effective knobs = the recipe to regen this corpus byte-identically
        Assert.Equal(42, gen.GetProperty("seed").GetInt32());
        Assert.Equal(0.0003, gen.GetProperty("scale").GetDouble(), 6);
        Assert.Equal(4, gen.GetProperty("cfiles").GetInt32());        // explicit ⇒ literal (not scaled)
        Assert.Equal(0, gen.GetProperty("giantHeaders").GetInt32());  // disabled population recorded as 0
        Assert.Equal(2, gen.GetProperty("shrinkSeeds").GetInt32());
        Assert.Equal("none", gen.GetProperty("compileDb").GetString());
        Assert.False(gen.GetProperty("withOracle").GetBoolean());
        Assert.False(gen.TryGetProperty("fromProfile", out _));       // absent when --from-profile not set
    }

    [Fact]
    public void Gen_MetaGenBlock_IsDeterministic_SameArgs()
    {
        string GenBlock()
        {
            using var tmp = new TempDir();
            string corpus = Path.Combine(tmp.Path, "c");
            Cli("gen", "--out", corpus, "--scale", "0.0003", "--seed", "9",
                "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
                "--tiny-files", "0", "--blob-files", "0", "--cfiles", "3");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "c-manifest.json")));
            return doc.RootElement.GetProperty("_meta").GetProperty("gen").GetRawText();
        }
        Assert.Equal(GenBlock(), GenBlock());   // knobs are machine-independent ⇒ byte-identical across runs
    }

    [Fact]
    public void Gen_RefusesForeignDir_UnlessForced()
    {
        using var tmp = new TempDir();
        string dir = Path.Combine(tmp.Path, "foreign");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "precious.txt"), "not ours");   // non-empty, no .codespawner marker

        string[] tiny = { "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
                          "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2" };
        Assert.Equal(2, Cli(new[] { "gen", "--out", dir }.Concat(tiny).ToArray()));           // refuses to clobber
        Assert.True(File.Exists(Path.Combine(dir, "precious.txt")));                          // left intact
        Assert.Equal(0, Cli(new[] { "gen", "--out", dir, "--force" }.Concat(tiny).ToArray()));// --force overwrites
        Assert.True(File.Exists(Path.Combine(dir, ".codespawner")));
    }

    [Fact]
    public void Verify_FailsOnTamperedCorpus()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c");
        string[] tiny = { "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
                          "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "3" };
        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus }.Concat(tiny).ToArray()));
        Assert.Equal(0, Cli("verify", "--corpus", corpus));

        // delete a tracked source file ⇒ the manifest's symbol defs no longer resolve ⇒ verify must fail.
        string victim = Directory.EnumerateFiles(corpus, "src_*.c", SearchOption.AllDirectories).First();
        File.Delete(victim);
        Assert.NotEqual(0, Cli("verify", "--corpus", corpus));
    }

    [Fact]
    public void FromProfile_WithOracle_AndOutputGuards()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "src");
        string[] tiny = { "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
                          "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2" };
        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus }.Concat(tiny).ToArray()));
        string prof = Path.Combine(tmp.Path, "prof.json");
        Assert.Equal(0, Cli("scan", corpus, "--out", prof));

        // --from-profile --with-oracle regenerates a look-alike AND overlays a ground-truth manifest.
        string regen = Path.Combine(tmp.Path, "regen");
        Assert.Equal(0, Cli("gen", "--out", regen, "--from-profile", prof, "--with-oracle"));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "regen-manifest.json")));   // sibling manifest emitted

        // refuses a foreign non-empty dir, honours --force
        string foreign = Path.Combine(tmp.Path, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "mine");
        Assert.Equal(2, Cli("gen", "--out", foreign, "--from-profile", prof));
        Assert.Equal(0, Cli("gen", "--out", foreign, "--from-profile", prof, "--force"));

        // --out pointing at an existing FILE is rejected
        string asFile = Path.Combine(tmp.Path, "afile");
        File.WriteAllText(asFile, "x");
        Assert.Equal(2, Cli("gen", "--out", asFile, "--from-profile", prof));
    }

    [Fact]
    public void Gen_Preset_Parses_AndGenerates()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "ci");
        // the 'ci' preset is the fast-smoke knob set (~1/100 counts); cap header size so it stays unit-cheap.
        Assert.Equal(0, Cli("gen", "--out", corpus, "--preset", "ci", "--max-header-mb", "1",
            "--big-headers", "0", "--med-headers", "0"));
        Assert.True(File.Exists(Path.Combine(corpus, ".codespawner")));
    }

    // ---- regen identity (determinism end-to-end) ----

    // A deliberately rich but size-capped gen: a giant header (1 MB cap), plus the dense/broad/long-line/
    // encoding/patho/dup/unresolved populations and a few .c files — so the regen-identity checks span real
    // manifest ground truth (func edges, hot_shared, vendor_gated, broad_hot, dupGroups, populations) without the
    // multi-GB bench-scale bands. Single-root (default --linked-roots 1) so the whole corpus lives under --out.
    private static string[] RichGenArgs(string outDir) => new[]
    {
        "gen", "--out", outDir, "--scale", "0.0003", "--seed", "20260109",
        "--giant-headers", "1", "--max-header-mb", "1", "--big-headers", "0", "--med-headers", "0",
        "--dense-headers", "1", "--dense-under-mb", "1", "--ordinary-headers", "2",
        "--broad-token-files", "1", "--hot-token-share", "1",
        "--long-line-files", "2", "--max-line-bytes", "4096",
        "--encoding-mix", "5", "--pathological-symbols", "1", "--dup-groups", "1", "--dup-copies", "2",
        "--unresolved-includes", "1", "--cfiles", "3",
    };

    // relative-path (separator-normalized) -> SHA256 hex of the file's bytes, for every file under root. The
    // tree-identity half of the regen guarantee: not just "same files" but "same bytes", keyed by path so a
    // mismatch names the offending file.
    private static SortedDictionary<string, string> TreeSnapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            map[rel] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        }
        return map;
    }

    // Neutralize the one absolute path in a manifest (`_meta.corpusRoot`) so two manifests from different out dirs
    // can be compared for everything else. `.*` stops at the line end, so only the value is replaced.
    private static string StripCorpusRoot(string manifestJson) =>
        System.Text.RegularExpressions.Regex.Replace(
            manifestJson, "\"corpusRoot\": \".*\"", "\"corpusRoot\": \"<root>\"");

    [Fact]
    public void Gen_InPlaceRegen_SameSeed_ByteIdentical_TreeAndManifest()
    {
        // The regen-in-place guarantee the cross-machine / death-scale workflow leans on (regen big corpora in
        // place instead of copying them over SMB): re-running gen with the same args over its OWN output —
        // CodeSpawner recognizes its .codespawner marker and overwrites without --force — reproduces every file
        // byte-for-byte AND the manifest byte-for-byte. Index-stable parallel emission must not leak into output.
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "corpus");
        string manifest = Path.Combine(tmp.Path, "corpus-manifest.json");

        Assert.Equal(0, Cli(RichGenArgs(corpus)));
        var tree1 = TreeSnapshot(corpus);
        byte[] mf1 = File.ReadAllBytes(manifest);

        Assert.Equal(0, Cli(RichGenArgs(corpus)));   // regenerate in place over the first run
        var tree2 = TreeSnapshot(corpus);
        byte[] mf2 = File.ReadAllBytes(manifest);

        Assert.True(tree1.Count > 8, $"expected a non-trivial corpus, saw {tree1.Count} files");
        Assert.Equal(tree1, tree2);   // same relative paths AND same per-file SHA256
        Assert.Equal(mf1, mf2);       // manifest ground truth byte-identical
    }

    [Fact]
    public void Gen_RegenToDifferentDir_SameContent_OnlyCorpusRootDiffers()
    {
        // Path-independence: the ground truth is corpus-relative. Two gens with identical args to DIFFERENT out
        // dirs produce byte-identical file content (keyed by relative path), and manifests that differ ONLY in the
        // absolute `_meta.corpusRoot` — every def/ref/edge/dup path is relative. Locks the invariant that nothing
        // absolute leaks into the ground truth (what makes the cross-machine regen workflow sound).
        using var tmp = new TempDir();
        string a = Path.Combine(tmp.Path, "aaa");
        string b = Path.Combine(tmp.Path, "bbb");

        Assert.Equal(0, Cli(RichGenArgs(a)));
        Assert.Equal(0, Cli(RichGenArgs(b)));

        Assert.Equal(TreeSnapshot(a), TreeSnapshot(b));   // identical bytes under identical relative paths

        string mfA = StripCorpusRoot(File.ReadAllText(Path.Combine(tmp.Path, "aaa-manifest.json")));
        string mfB = StripCorpusRoot(File.ReadAllText(Path.Combine(tmp.Path, "bbb-manifest.json")));
        Assert.Equal(mfA, mfB);   // manifests identical once the sole absolute field is neutralized
    }
}
