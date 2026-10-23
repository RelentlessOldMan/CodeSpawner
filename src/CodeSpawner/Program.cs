using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using CodeSpawner.Scan;
using CodeSpawner.Verify;

namespace CodeSpawner;

public static class Program
{
    public const string Version = "1.4.5";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;                  // no command is a usage error, like any bad input
        }

        string cmd = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        if (Array.Exists(rest, a => a is "-h" or "--help"))     // `codespawner gen --help` etc.
        {
            PrintUsage();
            return 0;
        }

        try
        {
            switch (cmd)
            {
                case "gen":
                    var genOpts = ArgParser.ParseGen(Presets.Expand(rest, out int presetTokens), presetTokens);
                    if (genOpts.Lang == SourceLang.Cpp) new CppGenerator(genOpts).Run();
                    else if (genOpts.FromProfile is not null) new ProfileGenerator(genOpts).Run();
                    else new CorpusGenerator(genOpts).Run();
                    return 0;

                case "scan":
                    return new Scanner(ArgParser.ParseScan(rest)).Run();

                case "verify":
                    return ManifestVerifier.Run(ArgParser.ParseVerify(rest));

                case "mutate":
                    return Mutator.Run(ArgParser.ParseMutate(rest));

                case "version":
                    Console.WriteLine(Version);
                    return 0;

                case "digest-selftest":
                    return DigestSelfTest();

                default:
                    Console.Error.WriteLine($"unknown command '{cmd}'. Run 'codespawner --help'.");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgException or ManifestVersionException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (AggregateException ex)
        {
            // Parallel emitters wrap failures; surface the first real cause cleanly rather than a stack dump.
            var inner = ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
            Console.Error.WriteLine($"error: {inner.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // A truncated/garbage manifest or profile handed to us: bad input, not a crash.
            Console.Error.WriteLine($"error: malformed JSON input: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException)
        {
            // Well-formed JSON of the wrong shape: a missing required key, a non-string where a string belongs, or a
            // number that doesn't fit (1.5 or 1e40 where an integer belongs). Also bad input, not a crash.
            Console.Error.WriteLine($"error: malformed input: {ex.Message}");
            return 2;
        }
    }

    // Asserts the canonical truth digest still reproduces the golden vector locked with CodeCompass.
    // A permanent guard against the delta prevTruthSha format silently drifting.
    private static int DigestSelfTest()
    {
        const string golden = "7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e";
        var truth = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["func_0"] = new() { Def = "block1/src_0.c:11", Refs = { "block1/src_1.c:14" } },
            ["func_1"] = new() { Def = "block1/src_1.c:12", Edges = { "func_0" } },
        };
        string got = TruthDigest.Compute(truth);
        bool okPrimary = got == golden;
        if (!okPrimary) Console.Error.WriteLine($"digest self-test: FAIL primary\n  expected {golden}\n  got      {got}");

        // Second golden vector: the indirectTruthSha canonical form (docs/oracle-v1-design.md). Frozen the
        // same way — CodeSpawner emits, CodeCompass/CodeCarver reproduce.
        const string goldenIndirect = "fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f";
        var isyms = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["func_0"] = new()
            {
                Def = "a:1",
                IndirectEdges =
                {
                    new() { Target = "itgt_0", Via = IndirectVia.FnPtr, Dispatched = true, Resolved = true },
                    new() { Target = "iext_1", Via = IndirectVia.VectorTable, Dispatched = false, Resolved = false },
                },
            },
            ["func_1"] = new()
            {
                Def = "b:1",
                IndirectEdges = { new() { Target = "itgt_0", Via = IndirectVia.InitArray, Dispatched = true, Resolved = true } },
            },
        };
        string gotI = IndirectDigest.Compute(isyms, new[] { "func_0" });
        bool okIndirect = gotI == goldenIndirect;
        if (!okIndirect) Console.Error.WriteLine($"digest self-test: FAIL indirect\n  expected {goldenIndirect}\n  got      {gotI}");

        // Third golden vector: the diffTruthSha canonical form (docs/diff-delta-design.md). Frozen the same
        // way — CodeSpawner emits, CodeDiffer reproduces. Exercises all four sections: out-of-order files
        // (proves the ordinal sort), coalesced + multi explicit hunks, a giant-file run-rule, empty renames.
        const string goldenDiff = "66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542";
        var dfiles = new List<DiffFile>
        {
            new() { Path = "z/last.c",  OldSha = "o1", NewSha = "n1", OldSize = 100, NewSize = 110,
                    Hunks = { new Hunk(HunkOp.Replace, 5, 2, 5, 2) } },
            new() { Path = "a/first.c", OldSha = "o2", NewSha = "n2", OldSize = 200, NewSize = 205,
                    Hunks = { new Hunk(HunkOp.Replace, 1, 1, 1, 1), new Hunk(HunkOp.Replace, 9, 3, 9, 3) } },
            new() { Path = "big.h",     OldSha = "o3", NewSha = "n3", OldSize = 1048576, NewSize = 1050000,
                    Run = new RunHunk(HunkOp.Replace, 20, 1, 5000, 1) },
        };
        string gotD = DiffDigest.Compute(dfiles, Array.Empty<Rename>());
        bool okDiff = gotD == goldenDiff;
        if (!okDiff) Console.Error.WriteLine($"digest self-test: FAIL diff\n  expected {goldenDiff}\n  got      {gotD}");

        // Fourth golden vector: conflictTruthSha (docs/diff-delta-design.md §3-way). Out-of-order records,
        // a modify/delete conflict (non-replace op), and both clean sides — frozen the same way.
        const string goldenConflict = "68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a";
        var conflicts = new List<Conflict>
        {
            new("f.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Replace, 5, 1),
            new("a.c", 9, 2, HunkOp.Replace, 9, 2, HunkOp.Delete, 9, 0),
        };
        var clean = new List<CleanMerge>
        {
            new("f.c", "v1", HunkOp.Replace, 3, 1, 3, 1),
            new("a.c", "v2", HunkOp.Insert, 7, 0, 7, 2),
        };
        string gotC = ConflictDigest.Compute(conflicts, clean);
        bool okConflict = gotC == goldenConflict;
        if (!okConflict) Console.Error.WriteLine($"digest self-test: FAIL conflict\n  expected {goldenConflict}\n  got      {gotC}");

        // Fifth golden vector: cppTruthSha (docs/cpp-profile-design.md D7). All five sections populated, a cha set
        // given out of order (proves the inner sort), an instance — frozen the same way; CodeCarver/CodeCompass reproduce.
        const string goldenCpp = "28b4e1de981d282a6918de91846e990955f5b8220c88efc993d2a984c0bfed08";
        var csyms = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal)
        {
            ["ns::f"] = new()
            {
                Def = "a:1", Kind = "function",
                VirtualEdges = { new() { Static = "ns::A::m_0", Cha = { "ns::B::m_0", "ns::A::m_0" }, Rta = { "ns::A::m_0" } } },
                ImplicitEdges = { "ns::A::~A" },
                MemberPtrEdges = { new() { Target = "ns::A::m_1", Via = MemberPtrVia.Arg, Dispatched = false } },
            },
            ["ns::g"] = new()
            {
                Def = "a:2", Kind = "function",
                MemberPtrEdges = { new() { Target = "ns::B::m_1", Via = MemberPtrVia.Table, Dispatched = true } },
            },
            ["ns::A::m_0"] = new() { Def = "b:1", Kind = "method" },
            ["ns::B::m_0"] = new() { Def = "b:2", Kind = "method" },
            ["ns::A::m_1"] = new() { Def = "b:3", Kind = "method" },
            ["ns::B::m_1"] = new() { Def = "b:4", Kind = "method" },
            ["ns::A::A"] = new() { Def = "b:5", Kind = "ctor" },
            ["ns::A::~A"] = new() { Def = "b:6", Kind = "dtor" },
            ["ns::B::B"] = new() { Def = "b:7", Kind = "ctor", ImplicitEdges = { "ns::A::A" } },
            ["ns::tf"] = new() { Def = "c:1", Kind = "template" },
            ["ns::tf<int>"] = new() { Def = "c:1", Kind = "instance", InstanceOf = "ns::tf" },
        };
        string gotP = CppDigest.Compute(csyms, new[] { "ns::A::A", "ns::A::~A" });
        bool okCpp = gotP == goldenCpp;
        if (!okCpp) Console.Error.WriteLine($"digest self-test: FAIL cpp\n  expected {goldenCpp}\n  got      {gotP}");

        if (okPrimary && okIndirect && okDiff && okConflict && okCpp)
        { Console.WriteLine($"digest self-test: OK (primary {got}, indirect {gotI}, diff {gotD}, conflict {gotC}, cpp {gotP})"); return 0; }
        return 1;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "help" or "/?";

    private static void PrintUsage() => Console.WriteLine(UsageText);

    // The full gen/scan/verify/mutate help. Exposed internally (not just printed) so a test can assert the
    // PRESETS block stays in sync with Presets.Names — the one copy of the preset-name list the single-source
    // registry can't derive, because these lines carry human descriptions Names doesn't have. The test guards
    // the names; see PresetsTests.HelpText_ListsEveryAdvertisedPreset.
    internal static readonly string UsageText = $"""
            codespawner {Version} — synthetic code-corpus generator

            USAGE:
              codespawner gen --out <dir> [knobs...]     generate a corpus + ground-truth manifest
              codespawner scan <tree> --out <profile>    measure a real tree -> numbers-only shape profile
              codespawner gen --from-profile <p> --out <dir>   regenerate a look-alike from a profile
              codespawner verify --corpus <dir> [--manifest <path>]   self-check a corpus against its manifest
              codespawner verify --corpus <dir> --reference-carve <out>   also write the firmware truth's carve (to compile)
              codespawner mutate --corpus <dir> [opts]   deterministically edit + emit a delta manifest (C corpora)
              codespawner gen --lang cpp --out <dir> [cpp knobs]   a C++ program + manifest v2 (call-graph truth)
              codespawner digest-selftest                reproduce the five golden digest vectors (exit 1 on drift)
              codespawner version

            SCAN (privacy-preserving characterize -> regenerate; see docs/scan-design.md):
              scan <tree> --out <profile.json>   read-only; emits a numbers-only shape/cost profile
              --tree <dir>       the tree to scan, as an option instead of the positional <tree>
              --structure-only   never opens a byte (size/type/dir only) — max paranoia
              --content-stats    add numeric content histograms (opt-in; default is class-labeled)
              --min-cluster <n>  k-anonymity floor (default 5; small clusters fold into "other")
              --sample <n>       classify up to n files per (ext,size-band) cluster (default 64; 0 = all)
              gen --from-profile <p> [--with-oracle] [--oracle-scale] [--oracle-chain <n>]
                                 regenerate; --with-oracle overlays the ground-truth spine + manifest
                                 (--web-* knobs add the include web beside it; needs --with-oracle)

            ORACLE CALL-GRAPH SHAPE (with --with-oracle; see docs/oracle-v1-design.md; all default off = linear):
              --oracle-chain <n>           func_i node budget (default 12)
              --oracle-fanout <n>          out-degree per node (>0 = seeded DAG instead of a linear chain)
              --oracle-depth <n>           cap DAG depth (layers from the root; 0 = sized by --oracle-chain)
              --oracle-shared-leaves <n>   shared sink nodes multiple callers reach (diamonds)
              --oracle-reachable-frac <f>  target reachable fraction from _meta.roots (adds dead subgraphs)
              --oracle-indirect <n>        indirect edges (fnptr/vector-table/init_array) scattered across the
                                           graph; per-symbol indirectEdges + a _meta.indirectTruthSha digest
              --oracle-bytes               emit per-symbol byte mass + _meta.totalOracleBytes (byte-based
                                           reduction ground truth; most useful with --oracle-scale)

            C++ PROFILE (gen --lang cpp; see docs/cpp-profile-design.md). Writes only a self-contained C++ program
            (cpp/) + a manifest v2: edges, implicitEdges (ctor/dtor), virtualEdges (static + cha + rta sets),
            memberPtrEdges, kinds, staticInitRoots, _meta.cppTruthSha. Takes --out/--seed/--force, the
            --oracle-chain/-fanout/-depth/-shared-leaves/-reachable-frac free-function graph knobs (here
            --oracle-reachable-frac 0, the default, adds a dead block a quarter the live size; f in [0.01, 1]
            sizes it so live/total ~ f; 1 = none; at most 2,000,000 free functions in all), and:
              --lang <c|cpp>                c (default) or cpp (alias c++)
              --cpp-classes <n>             classes, 1..100000 (default 12)
              --cpp-depth <n>               max inheritance depth, 0..16 (default 3; 0 = every class a root)
              --cpp-multi-inherit <f>       fraction also inheriting a mixin interface (default 0.25)
              --cpp-virtual-frac <f>        chance a derived class overrides each inherited virtual (default 0.6)
              --cpp-instantiated-frac <f>   fraction of concrete classes constructed live (default 0.5;
                                            the rest are cha-only targets: cha minus rta)
              --cpp-templates <n>           function/class templates, alternating, 0..10000 (default 4)
              --cpp-instances <n>           max type arguments per used template, 1..5 (default 2)
              --cpp-static-init <n>         static-storage objects, 0..10000; ctor+dtor are staticInitRoots
                                            (default 2)

            MUTATE (incremental/watcher oracle — emits delta manifests that compose: truth = base ⊕ delta):
              --corpus <dir>   an existing CodeSpawner corpus (guarded by the .codespawner marker)
              --manifest <p>   base manifest to compose against (default <corpus>-manifest.json)
              --seed <n>       edit-selection seed (default 7)
              --edits <n>      number of edits (default 5)
              --step <k>       apply ONLY edit k (the chain driver; emits corpus-delta-k.json)
              --through        apply all N edits, emit one cumulative <corpus>-delta.json
              --restream       include 4-restream edits (needs --restream-seeds in the base corpus)
              (gen the base with --shrink-seeds N [--restream-seeds N] to enable 4-shrink/4-restream)

            MUTATE BULK (diff-oracle: deterministic, targeted, count/density-controlled in-place edits):
              --target <pop>        population to change: source | headers | giant | all (activates bulk mode)
              --files-changed <n>   how many target files to modify (default: all matching)
              --edit-density <f>    fraction of each file's lines to change in place, 0..1 (default 0.05)
              --giant-min-mb <n>    size floor (MB) for --target giant — the big-header case (default 100)
              --giant-edit <mode>   how a content edit touches a giant file: strided (default — mark every
                                      stride-th line, a compact run-rule) | single (ONE localized one-line insert
                                      as a single explicit hunk — the content-defined-chunker locality case)
              --edit-kind <k>       edit mechanism → reason class (default content):
                                      content      in-place marker (reason content; replace hunks)
                                      line-insert  add lines      (reason content; insert hunks, renumbers)
                                      line-delete  remove lines   (reason content; delete hunks, renumbers)
                                      whitespace   trailing spaces(reason whitespace; replace hunks)
                                      eol          flip LF<->CRLF (reason eol; no hunks)
                                      encoding     UTF-8<->UTF-16 (reason encoding; no hunks)
                                      binary       flip raw bytes (reason binary; no hunks)
                                      metadata     mode-only      (reason metadata; content identical)
                                      mixed        one kind/file  (cycles the six reasons; ≥6 content-bearing files ⇒ all)
                                      rename       move files     (graded renamed[] + rename+edit hunks)
              --decoy-fraction <f>  (rename only) fraction of files emitted as near-duplicate ADD decoys
                                    (original kept) — rename false-positive traps, 0..1 (default 0);
                                    a decoy within 20 milli of a recorded rename is dropped
              --shard-size <n>      0 = one monolithic <corpus>-delta.json (default); >0 = paged transport:
                                    a <corpus>-delta.index.json + <corpus>-delta.shard-NNN.json files of <= n
                                    modified records each (diffTruthSha is sharding-invariant; 2-way only)
              (emits one base->variant <corpus>-delta.json with per-file reason + old/new sha+size + unified
               hunks [a compact run-rule for giant files] + a _meta.diffTruthSha; see docs/diff-delta-design.md.
               e.g. --target giant --files-changed 3 --edit-density 0.5  vs
                    --target source --files-changed 1000 --edit-density 0.02)

            MUTATE 3-WAY (native diff3 oracle; leaves the base tree pristine):
              --three-way           produce two variant trees <corpus>_v1/ + <corpus>_v2/ off base B
              --overlap-fraction <f>  fraction of V2's edits that coincide with V1's ⇒ conflicts (default 0.5;
                                      0 = all clean-merge, 1 = every V2 edit conflicts). Edits land only on odd
                                      base lines so even lines are stable anchors (region ≡ line conflicts).
              --conflict-edges      emit the edge-case conflict kinds instead of the random single-line model:
                                      adjacent multi-line (union-span), modify/delete, add/add, and
                                      identical-overlap (⇒ clean, side v1). Truth = diff3 maximal-hunk coalescer.
              (emits <corpus>-delta-v1.json + -delta-v2.json [standard diff-deltas] and <corpus>-conflict.json
               with a _meta.conflictTruthSha over [conflicts-3way, merged-clean]; see docs/diff-delta-design.md)

            PRESETS (bundled knob sets; your own knobs still override):
              --preset death         ~90 GB, ~50k files, 12 headers >1 GB (the "repo of death") + the include web
              --preset death-lite    all of death's shapes in ~0.5 GB (32 MB giants; the web at death's counts)
              --preset ci            ~1/100 counts, keeps >=1 pathology header (fast smoke)
              --preset memory        one giant header + a few includers (preprocessor-memory axis)
              --preset dense-band    dense sub-threshold headers (posting/trigram memory OOM shape)
              --preset broad-token   hot token across a 2-8MB band (find_references + sidecar reads)
              --preset long-lines    pathological long-line / no-newline files
              --preset encoding-mix  UTF-16/BOM/invalid-byte/non-ASCII encoding stress
              --preset many-tiny     tiny-file-dominated (walker / stat pressure)
              --preset pathological-symbols  token-paste (expected-miss)/long idents/deep nesting
              --preset dup-content   byte-identical groups (content-hash dedup / posting collapse)
              --preset firmware      a bare tree + the firmware corpus (big paste-macro headers, carve truth)

            COMMON KNOBS (all optional; counts scale by --scale unless set explicitly; full table: docs/knobs.md):
              --out <dir>            output directory (required; cleared if it exists)
              --force                overwrite --out even if it wasn't created by CodeSpawner (never a drive
                                     root, the cwd/home or a parent of them, or a git checkout)
              --scale <f>            count multiplier (default 0.01)
              --seed <n>             deterministic seed (default 1337)
              --giant-headers <n>    count of >MaxHeaderMB register headers (the byte pathology)
              --max-header-mb <n>    size of each giant header (default 110; never scaled)
              --macro-density <n>    #defines per giant header (preprocessor-memory axis)
              --big-headers <n>      10-100 MB headers
              --med-headers <n>      1-10 MB headers
              --ordinary-headers <n> ~6.6 KB headers (ordinary parse volume)
              --cfiles <n>           .c files with cross-dir call edges
              --giant-includers <n>  .c files that #include a giant header + call hot_shared() (default 3)
              --tiny-files <n>       tiny .csv files (per-file / stat pressure)
              --blob-files <n>       high-byte, zero-symbol data blobs
              --unresolved-includes <n>   TUs referencing a missing vendor header (negative oracle)
              --compile-db none|partial|full
              --build-output [true|false]  .o/.lst/.bak beside sources (default true)
              --source-bodies same|varied  varied: each src_i.c gets its own body lines (for rename fixtures; default same)
              --dirs <n>             approximate directory count
              --depth <n>            approximate max path depth (default 8)
              --linked-roots <n>     split output across N sibling trees
              --manifest [true|false]  emit the ground-truth manifest (default true)
              --io-parallelism <n>   max concurrent large-header writes

            PATHOLOGY KNOBS (default 0/off; the presets above bundle them):
              --dense-headers <n>        dense sub-threshold headers (posting/trigram memory)
              --dense-under-mb <m>       byte ceiling each dense header sits just under (default 127)
              --broad-token-files <n>    2-8 MB carrier files for the hot token
              --hot-token-share <f>      fraction of carriers that contain the hot token (default 0.5)
              --long-line-files <n>      single-long-line files (minified-JS shaped)
              --max-line-bytes <m>       bytes in each long line (default 8388608)
              --no-newline [true|false]  no newline at all in long-line files (default alternates)
              --encoding-mix <n>         UTF-16/BOM/invalid-byte/non-ASCII encoding-stress files
              --pathological-symbols <n> token-paste (expectedMiss) / long-ident / deep-nesting files
              --dup-groups <n>           byte-identical duplicate groups (+ a near-identical control each)
              --dup-copies <n>           copies per dup group (default 4)
              --shrink-seeds <n>         ~9 MB headers that enable mutate 4-shrink
              --restream-seeds <n>       ~129 MB headers that enable mutate 4-restream

            INCLUDE WEB (off unless --web-units is given; never scaled; docs/include-web-design.md):
              --web-units <n>            .c units under web/, each with a recorded include closure (manifest "closures")
              --web-headers <n>          guarded register-map headers inc/rm_N.h (default 600)
              --web-depth <n>            headers per include chain segment (default 12, at most 150)
              --web-window <n|min-max>   chain heads each unit includes (default 6-12)
              --web-aggregators <n>      inc/all_K.h headers that each include a run of chain heads (default 16)
              --web-header-lines <n>     lines per register-map header (default 300)
              --web-tail <n>             register maps that are big tail headers instead (default 0)
              --web-tail-mb <m>          size of each tail header; comes out ~1.25x (default 32)
              --web-templates <n>        unguarded template headers inc/tmpl_T.h, expanded per TNAME (default 24)
              --web-template-sites <n>   TNAME include sites per unit (default 3)
              --web-template-heads <n>   CAT(TNAME, ...) heads per template (default 12)
              --web-template-reach-frac <f>   share of sites reaching templates through a deep chain (default 0.4)
              --web-template-hidden-frac <f>  share of heads named through an alias macro (default 0.25)
              --web-cycle-frac <f>       share of headers that include an earlier one (guarded cycle; default 0.2)
              --web-ifdef-frac <f>       share of headers and units with an include behind #if CFG_k (default 0.15)
              --web-continuation-frac <f>  share of headers with a long backslash-continued macro (default 0.2)
              --web-comment-frac <f>     share of headers with comment traps (fake includes, split defines; default 0.2)
              --web-undef-frac <f>       share of headers that #undef and redefine (default 0.1)
              --web-computed-frac <f>    share of includes through a macro (#include NAME; default 0.05)
              --web-next-frac <f>        share of units including inc/vx_j.h, which #include_next's inc_vnd/vx_j.h (default 0.05)
              --web-shadow <n>           inc_vnd/ decoys named like inc/ headers, never reached (default 4)
              --web-relative-frac <f>    share of includes spelled ../inc/x.h (default 0.05)
              --web-crlf-frac <f>        share of register maps, templates and units with CRLF (default 0.05)
              --web-mixed-eol-frac <f>   share of them with mixed LF/CRLF (default 0.02)

            FIRMWARE (off unless --firmware; counts scale with --scale; docs/firmware-design.md):
              --firmware                 a firmware tree (fw/, ext/, fw-build/, fw-truth/) + its header-carve truth
                                         (manifest "firmware": must-keep/must-drop/open defines, counts, treeSha256)
              --fw-big-headers <n>       headers past maxParseBytes, full of paste macros (default 400)
              --fw-big-mb <m>            size of each big header, 20..2000 (default 24; regs_0.h is maxParseBytes+1)
              --fw-files <n>             files under fw/src: units + kept headers (default 38000)
              --fw-families <n>          must-keep families across 18 paste kinds (default 1000000)
              --fw-pasters <n>           paste macros in the big headers, most never called (default 6000000)
              --fw-dense-headers <n>     dense headers (1 MB..maxParseBytes, mostly defines; default 200)
              --fw-chain-max <n>         longest forwarder chain (default 40)
              --fw-db-paths <absolute|relative>   how fw-build/compile_commands.json names paths (default absolute)

            Flags take --flag, --flag=false or --flag false; values take --knob v or --knob=v.
            '<command> --help' prints this help.

            Run 'codespawner gen --out .\_fw --scale 0.01 --giant-headers 0' for a fast smoke.
            """;
}
