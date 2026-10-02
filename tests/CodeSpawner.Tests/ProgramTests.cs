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
}
