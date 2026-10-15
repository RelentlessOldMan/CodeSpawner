using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The CLI entry point: subcommand dispatch + exit codes, and a tiny end-to-end gen → verify → scan →
/// from-profile → mutate round-trip. The round-trip is the unit-level smoke of CorpusGenerator / Scanner /
/// ProfileGenerator / ManifestVerifier / Mutator wired together through Program.Main.
/// </summary>
[Collection("CapturesConsole")]   // swaps the process-wide Console: never alongside another class that does
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

    // The usage text is a raw string: a doubled backslash prints doubled, and an inserted line must not split a
    // sentence (the decoy-fraction note once landed between "decoys" and "(original kept)").
    [Fact]
    public void UsageText_HasNoDoubledBackslash_AndDecoyHelpReadsInOrder()
    {
        Assert.DoesNotContain(@"\\", Program.UsageText);
        Assert.Matches(@"near-duplicate ADD decoys\s+\(original kept\)", Program.UsageText);
    }

    // A newer manifest format may carry fields this build cannot honour, so mutate must refuse it before editing.
    [Theory]
    [InlineData("--edits", "1")]
    [InlineData("--target", "source")]
    public void Mutate_NewerManifestVersion_IsRefused_CorpusUntouched(params string[] mode)
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c"), man = Path.Combine(tmp.Path, "c-manifest.json");
        Assert.Equal(0, Cli("gen", "--out", corpus, "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0",
                            "--med-headers", "0", "--cfiles", "2"));
        string text = File.ReadAllText(man);
        Assert.Contains("\"manifestVersion\": 1", text);
        File.WriteAllText(man, text.Replace("\"manifestVersion\": 1", "\"manifestVersion\": 2"));
        string Snapshot() => string.Join("\n", Directory.EnumerateFiles(tmp.Path, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
        string before = Snapshot();

        Assert.NotEqual(0, Cli(new[] { "mutate", "--corpus", corpus }.Concat(mode).ToArray()));
        Assert.Contains("manifest version 2", _lastErr);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Dispatch_ExitCodes()
    {
        Assert.Equal(0, Cli("version"));
        Assert.Equal(0, Cli("--help"));
        Assert.Equal(0, Cli("gen", "--help"));            // <command> --help prints usage, not "unknown knob"
        Assert.Equal(0, Cli("mutate", "--corpus", "x", "-h"));
        Assert.Equal(2, Cli());                       // no args ⇒ usage + usage-error exit
        Assert.Equal(2, Cli("bogus-command"));        // unknown command
        Assert.Equal(0, Cli("digest-selftest"));      // the five golden vectors reproduce
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
        // the first shuffled pick here is not editable text: another file stands in, so the delta holds the 1 asked for
        Assert.DoesNotContain("warning:", _lastErr);
        using (var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "corpus-delta.json"))))
            Assert.Equal(1, d.RootElement.GetProperty("fileOps").GetProperty("modified").GetArrayLength());

        // asking for more files than the target holds still runs, but says so (it used to exit 0 silently)
        string corpus3 = Path.Combine(tmp.Path, "corpus3");
        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus3 }.Concat(tiny).ToArray()));
        Assert.True(Cli("mutate", "--corpus", corpus3, "--target", "source", "--files-changed", "1000000") == 0, _lastErr);
        Assert.Contains("--files-changed 1000000 asked for more files", _lastErr);
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
        // Federation scatters defs into the sibling root (../rich_root1/...); verify's containment check treats
        // those as part of the corpus, so the whole federated corpus verifies against its own manifest.
        Assert.True(File.ReadAllText(Path.Combine(tmp.Path, "rich-manifest.json")).Contains("../rich_root1/"),
            "expected the manifest to reach into the linked root");
        int vrc = Cli("verify", "--corpus", corpus);
        Assert.True(vrc == 0, $"verify exit {vrc}: {_lastErr}");
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

    // The recipe must actually replay: feed _meta.gen back to gen (camelCase key → --kebab-case knob) and the tree
    // is byte-identical. Default (scaled) --dirs is the case that used to break: it was recorded unscaled.
    [Fact]
    public void Gen_MetaGen_ReplaysToAByteIdenticalTree()
    {
        using var tmp = new TempDir();
        string a = Path.Combine(tmp.Path, "a"), b = Path.Combine(tmp.Path, "b");
        int rc = Cli(RichGenArgs(a).Concat(new[] { "--linked-roots", "2" }).ToArray());
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        ReplayMetaGen(Path.Combine(tmp.Path, "a-manifest.json"), b);

        foreach (var (root, other) in new[] { ("a", "b"), ("a_root1", "b_root1") })
            Assert.Equal(TreeSnapshot(Path.Combine(tmp.Path, root)), TreeSnapshot(Path.Combine(tmp.Path, other)));
    }

    // A tiny --scale scales --dirs to 0, which DirTree builds as 1 but _meta.gen recorded as 0, and --dirs 0 is
    // refused, so the replay failed. Every non-default structure knob must replay too.
    [Fact]
    public void Gen_MetaGen_TinyScaleAndNonDefaultKnobs_Replay()
    {
        using var tmp = new TempDir();
        string a = Path.Combine(tmp.Path, "a"), b = Path.Combine(tmp.Path, "b");
        var args = RichGenArgs(a).Select(x => x == "0.0003" ? "0.00001" : x).Concat(new[]
        {
            "--build-output", "--compile-db", "partial", "--no-newline", "--depth", "5",
            "--giant-includers", "2", "--shrink-seeds", "1",
        }).ToArray();
        int rc = Cli(args);
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        Assert.Equal("1", MetaGen(Path.Combine(tmp.Path, "a-manifest.json"))["dirs"]);
        ReplayMetaGen(Path.Combine(tmp.Path, "a-manifest.json"), b);
        // compile_commands.json holds absolute paths (as a real one does): equal once the output root is aligned.
        const string Db = "compile_commands.json";
        var sa = TreeSnapshot(a); var sb = TreeSnapshot(b);
        Assert.True(sa.Remove(Db) && sb.Remove(Db));
        Assert.Equal(sa, sb);
        static string Esc(string p) => p.Replace("\\", "\\\\");
        Assert.Equal(File.ReadAllText(Path.Combine(a, Db)).Replace(Esc(a), "<root>"),
                     File.ReadAllText(Path.Combine(b, Db)).Replace(Esc(b), "<root>"));
    }

    // --macro-density is a cap only when set; unset, giants fill to maxHeaderMB uncapped. Recording the 1M default
    // made a replay cap every giant over 1M defines (a death giant came back ~56 MB instead of 1229 MB). Too big to
    // replay here, so: the default is not recorded, and a set cap is recorded and replays byte-identically.
    [Fact]
    public void Gen_MetaGen_MacroDensity_RecordedOnlyWhenSet_AndCapReplays()
    {
        using var tmp = new TempDir();
        string a = Path.Combine(tmp.Path, "a"), b = Path.Combine(tmp.Path, "b"), c = Path.Combine(tmp.Path, "c");
        int rc = Cli(RichGenArgs(a));
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        Assert.False(MetaGen(Path.Combine(tmp.Path, "a-manifest.json")).ContainsKey("macroDensity"));

        rc = Cli(RichGenArgs(b).Concat(new[] { "--macro-density", "500" }).ToArray());
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        Assert.Equal("500", MetaGen(Path.Combine(tmp.Path, "b-manifest.json"))["macroDensity"]);
        Assert.NotEqual(TreeSnapshot(a), TreeSnapshot(b));              // the cap really bit (1 MB giant > 500 defines)
        ReplayMetaGen(Path.Combine(tmp.Path, "b-manifest.json"), c);
        Assert.Equal(TreeSnapshot(b), TreeSnapshot(c));
    }

    // _meta.gen as knob → value text (bools lower-case, strings bare, numbers raw).
    // --source-bodies varied gives every src_i.c its own body lines (rename fixtures need sources that aren't
    // near-copies of each other). Default output is untouched and the knob is recorded only when varied, so an
    // existing corpus regenerates byte-identically; varied leaves the symbol truth as-is and replays.
    [Fact]
    public void Gen_SourceBodiesVaried_DistinctBodies_SameTruth_RecordedAndReplays()
    {
        using var tmp = new TempDir();
        string a = Path.Combine(tmp.Path, "a"), b = Path.Combine(tmp.Path, "b"), c = Path.Combine(tmp.Path, "c");
        int rc = Cli(RichGenArgs(a));
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        Assert.False(MetaGen(Path.Combine(tmp.Path, "a-manifest.json")).ContainsKey("sourceBodies"));
        rc = Cli(RichGenArgs(b).Concat(new[] { "--source-bodies", "varied" }).ToArray());
        Assert.True(rc == 0, $"gen exit {rc}: {_lastErr}");
        Assert.Equal("varied", MetaGen(Path.Combine(tmp.Path, "b-manifest.json"))["sourceBodies"]);

        static string Symbols(string m) =>
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(m)).RootElement.GetProperty("symbols").GetRawText();
        Assert.Equal(Symbols(Path.Combine(tmp.Path, "a-manifest.json")), Symbols(Path.Combine(tmp.Path, "b-manifest.json")));

        var srcs = Directory.GetFiles(b, "src_*.c", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(srcs.Count >= 2);
        var x = File.ReadAllLines(srcs[0]); var y = File.ReadAllLines(srcs[1]);
        var yc = y.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        int common = x.GroupBy(l => l).Sum(g => Math.Min(g.Count(), yc.GetValueOrDefault(g.Key)));
        Assert.True(common * 10 < Math.Max(x.Length, y.Length), $"{common} of {x.Length}/{y.Length} lines shared");

        ReplayMetaGen(Path.Combine(tmp.Path, "b-manifest.json"), c);
        Assert.Equal(TreeSnapshot(b), TreeSnapshot(c));
        Assert.Equal(2, Cli("gen", "--out", Path.Combine(tmp.Path, "d"), "--source-bodies", "odd"));
    }

    private static Dictionary<string, string> MetaGen(string manifest)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        var d = new Dictionary<string, string>();
        foreach (var k in doc.RootElement.GetProperty("_meta").GetProperty("gen").EnumerateObject())
            d[k.Name] = k.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.String => k.Value.GetString()!,
                _ => k.Value.GetRawText(),
            };
        return d;
    }

    // The documented replay recipe: every _meta.gen key back to gen (camelCase → --kebab-case).
    private static void ReplayMetaGen(string manifest, string outDir)
    {
        var replay = new List<string> { "gen", "--out", outDir };
        foreach (var (name, val) in MetaGen(manifest))
        {
            string knob = name == "maxHeaderMB" ? "max-header-mb"
                : System.Text.RegularExpressions.Regex.Replace(name, "[A-Z]", m => "-" + char.ToLowerInvariant(m.Value[0]));
            replay.Add($"--{knob}={val}");
        }
        int rc = Cli(replay.ToArray());
        Assert.True(rc == 0, $"replay exit {rc}: {_lastErr}");
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

    private static readonly string[] Tiny =
    {
        "--scale", "0.0003", "--giant-headers", "0", "--big-headers", "0", "--med-headers", "0",
        "--ordinary-headers", "0", "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2",
    };

    // --force means "overwrite this corpus", not "delete whatever path was typed": a git checkout is refused
    // even with --force (exit 2) and nothing in it is touched.
    [Fact]
    public void Gen_Force_NeverDeletesAGitCheckout()
    {
        using var tmp = new TempDir();
        string repo = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "work.c"), "precious");

        Assert.Equal(2, Cli(new[] { "gen", "--out", repo, "--force" }.Concat(Tiny).ToArray()));
        Assert.Contains("--force does not override", _lastErr);
        Assert.True(File.Exists(Path.Combine(repo, "work.c")));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git")));
    }

    // The guard itself, without pointing a real delete at anything: drive/share roots, the cwd and its parents,
    // the user profile, and git checkouts are flagged; an ordinary scratch directory is not.
    [Fact]
    public void OutputDir_UnsafeToDelete_FlagsRootsCwdHomeAndGitCheckouts()
    {
        string cwd = Directory.GetCurrentDirectory();
        Assert.NotNull(Generation.OutputDir.UnsafeToDelete(Path.GetPathRoot(cwd)!));
        Assert.NotNull(Generation.OutputDir.UnsafeToDelete(cwd));
        Assert.NotNull(Generation.OutputDir.UnsafeToDelete(Path.GetDirectoryName(cwd)!));
        Assert.NotNull(Generation.OutputDir.UnsafeToDelete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        if (OperatingSystem.IsWindows())
            Assert.NotNull(Generation.OutputDir.UnsafeToDelete(@"\\server\share"));   // UNC share root

        using var tmp = new TempDir();
        string plain = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(plain);
        Assert.Null(Generation.OutputDir.UnsafeToDelete(plain));
        Directory.CreateDirectory(Path.Combine(plain, ".git"));
        Assert.NotNull(Generation.OutputDir.UnsafeToDelete(plain));
    }

    // Regenerating over a corpus drops its old sibling manifest up front: with --manifest=false (or an interrupted
    // run) the stale manifest of the deleted tree must not survive beside the new one. No .tmp is left behind.
    [Fact]
    public void Gen_RegenOverCorpus_DropsStaleSiblingManifest()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c");
        string mf = Path.Combine(tmp.Path, "c-manifest.json");

        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus }.Concat(Tiny).ToArray()));
        Assert.True(File.Exists(mf));
        Assert.False(File.Exists(mf + ".tmp"));

        Assert.Equal(0, Cli(new[] { "gen", "--out", corpus, "--seed", "2", "--manifest=false" }.Concat(Tiny).ToArray()));
        Assert.False(File.Exists(mf));
    }

    // A profile is passed between machines and people; its archetype label/extension become file names, so a
    // path-like value is refused before --out is touched — nothing is written anywhere.
    [Theory]
    [InlineData("../escape", ".c")]
    [InlineData("sub/dir", ".c")]
    [InlineData("a", "/../../x.c")]
    [InlineData("a", ".c:stream")]
    public void FromProfile_RefusesPathLikeArchetypeNames(string label, string ext)
    {
        using var tmp = new TempDir();
        string prof = tmp.File("hostile.json");
        File.WriteAllText(prof,
            "{\"_meta\":{\"profileVersion\":1},\"archetypes\":[{\"label\":" + System.Text.Json.JsonSerializer.Serialize(label) +
            ",\"extension\":" + System.Text.Json.JsonSerializer.Serialize(ext) +
            ",\"count\":1,\"sizeDistribution\":{\"p50\":10,\"p90\":10,\"max\":10}}]}");

        Assert.Equal(2, Cli("gen", "--out", Path.Combine(tmp.Path, "out"), "--from-profile", prof));   // bad input
        Assert.Contains("plain file-name fragment", _lastErr);
        Assert.Equal(new[] { prof }, Directory.EnumerateFileSystemEntries(tmp.Path, "*", SearchOption.AllDirectories));
    }

    // A truncated manifest is bad input: a clean "error:" line and exit 2, not an unhandled-exception stack dump.
    [Fact]
    public void Mutate_MalformedManifest_CleanErrorExit2()
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        File.WriteAllText(Path.Combine(corpus, "a.c"), "int a;\n");
        File.WriteAllText(Path.Combine(tmp.Path, "c-manifest.json"), "{\"_meta\": {\"gen\": {");

        Assert.Equal(2, Cli("mutate", "--corpus", corpus, "--target", "source"));
        Assert.Contains("error: malformed JSON", _lastErr);
        Assert.Equal("int a;\n", File.ReadAllText(Path.Combine(corpus, "a.c")));
    }

    // Well-formed JSON of the wrong shape is bad input too: mutate exits 2 with an "error:" line, verify reports
    // FAIL, or exits 2 when it is no manifest (no _meta) or declares nothing to check — neither throws. A symbol without a def is a verify FAIL,
    // never a silent pass.
    [Theory]
    [InlineData("{\"symbols\":{}}", 2)]                                                              // no _meta
    [InlineData("{\"_meta\":{\"manifestVersion\":1.5},\"symbols\":{}}", 1)]                        // non-integer version
    [InlineData("{\"_meta\":{\"manifestVersion\":1,\"seed\":99999999999},\"symbols\":{}}", 2)]    // seed overflows int; nothing to check
    [InlineData("{\"_meta\":{\"manifestVersion\":1},\"symbols\":{\"f\":{\"refs\":[]}}}", 1)]      // symbol without def
    [InlineData("{\"_meta\":{\"manifestVersion\":1},\"symbols\":{\"f\":{\"def\":null}}}", 1)]     // null def
    [InlineData("{\"_meta\":{\"manifestVersion\":1},\"symbols\":{\"f\":{\"def\":\"a.c:1\",\"refs\":[null]}}}", 2)]   // null ref: malformed (exit 2)
    [InlineData("{\"_meta\":{\"manifestVersion\":1},\"symbols\":{\"f\":{\"def\":\"a.c:1\",\"bytes\":1e40}}}", 1)]
    public void MalformedManifestShape_MutateExit2_VerifyFailsCleanly(string manifest, int verifyExit)
    {
        using var tmp = new TempDir();
        string corpus = Path.Combine(tmp.Path, "c");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");
        File.WriteAllText(Path.Combine(corpus, "a.c"), "int a;\n");
        File.WriteAllText(Path.Combine(tmp.Path, "c-manifest.json"), manifest);

        Assert.Equal(2, Cli("mutate", "--corpus", corpus));
        Assert.Contains("error: malformed", _lastErr);
        Assert.Equal("int a;\n", File.ReadAllText(Path.Combine(corpus, "a.c")));
        Assert.Equal(verifyExit, Cli("verify", "--corpus", corpus));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Mutate_EditsBelowOne_IsAnArgError(string edits) =>
        Assert.Equal(2, Cli("mutate", "--corpus", "x", "--edits", edits));

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
        Assert.Equal(0, Cli("gen", "--out", regen, "--from-profile", prof, "--with-oracle", "--oracle-fanout", "2", "--oracle-indirect", "3"));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "regen-manifest.json")));   // sibling manifest emitted
        var gen = MetaGen(Path.Combine(tmp.Path, "regen-manifest.json"));           // the oracle knobs are recorded
        Assert.Equal("2", gen["oracleFanout"]);
        Assert.Equal("3", gen["oracleIndirect"]);
        Assert.Equal("true", gen["withOracle"]);
        Assert.DoesNotContain(gen.Values, v => v.Contains("prof.json"));            // the profile path is not

        // Each recorded indirect edge's `dispatched` matches the emitted C: the owner's file takes the target's
        // address into a slot, and calls through that slot exactly when the edge says dispatched.
        var m = CodeSpawner.Manifest.ManifestReader.Load(Path.Combine(tmp.Path, "regen-manifest.json"));
        var edges = m.Symbols.Values.SelectMany(s => s.IndirectEdges.Select(e => (s.Def, e))).ToList();
        Assert.Equal(3, edges.Count);
        Assert.Contains(edges, x => x.e.Dispatched);
        Assert.Contains(edges, x => !x.e.Dispatched);
        foreach (var (def, e) in edges)
        {
            string c = File.ReadAllText(Path.Combine(regen, def[..def.LastIndexOf(':')]));
            var slot = System.Text.RegularExpressions.Regex.Match(c, $@"\(\*(ip_\d+)(?:\[1\])?\)\(int\) = \{{? ?&{e.Target}\b");
            Assert.True(slot.Success, $"{e.Target}: no address-taken slot in {def}");
            Assert.Equal(e.Dispatched, System.Text.RegularExpressions.Regex.IsMatch(c, $@"acc \+= {slot.Groups[1].Value}(\[0\])?\(x\);"));
        }

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
