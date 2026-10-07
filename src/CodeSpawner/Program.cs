using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using CodeSpawner.Scan;
using CodeSpawner.Verify;

namespace CodeSpawner;

public static class Program
{
    public const string Version = "1.1.1";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string cmd = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        try
        {
            switch (cmd)
            {
                case "gen":
                    var genOpts = ArgParser.ParseGen(Presets.Expand(rest));
                    if (genOpts.FromProfile is not null) new ProfileGenerator(genOpts).Run();
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
        catch (ArgException ex)
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

        if (okPrimary && okIndirect && okDiff && okConflict)
        { Console.WriteLine($"digest self-test: OK (primary {got}, indirect {gotI}, diff {gotD}, conflict {gotC})"); return 0; }
        return 1;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "help" or "/?";

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            codespawner {Version} — synthetic code-corpus generator

            USAGE:
              codespawner gen --out <dir> [knobs...]     generate a corpus + ground-truth manifest
              codespawner scan <tree> --out <profile>    measure a real tree -> numbers-only shape profile
              codespawner gen --from-profile <p> --out <dir>   regenerate a look-alike from a profile
              codespawner verify --corpus <dir>          self-check a corpus against its manifest
              codespawner mutate --corpus <dir> [opts]   deterministically edit + emit a delta manifest
              codespawner version

            SCAN (privacy-preserving characterize -> regenerate; see docs/scan-design.md):
              scan <tree> --out <profile.json>   read-only; emits a numbers-only shape/cost profile
              --structure-only   never opens a byte (size/type/dir only) — max paranoia
              --content-stats    add numeric content histograms (opt-in; default is class-labeled)
              --min-cluster <n>  k-anonymity floor (default 5; small clusters fold into "other")
              --sample <n>       classify up to n files per (ext,size-band) cluster (default 64; 0 = all)
              gen --from-profile <p> [--with-oracle] [--oracle-scale] [--oracle-chain <n>]
                                 regenerate; --with-oracle overlays the ground-truth spine + manifest

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

            MUTATE (incremental/watcher oracle — emits delta manifests that compose: truth = base ⊕ delta):
              --corpus <dir>   an existing CodeSpawner corpus (guarded by the .codespawner marker)
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
                                    (original kept) — rename false-positive traps, 0..1 (default 0)
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
              --preset death         ~90 GB, ~50k files, 12 headers >1 GB (the "repo of death")
              --preset ci            ~1/100 counts, keeps >=1 pathology header (fast smoke)
              --preset memory        one giant header + a few includers (preprocessor-memory axis)
              --preset dense-band    dense sub-threshold headers (posting/trigram memory OOM shape)
              --preset broad-token   hot token across a 2-8MB band (find_references + sidecar reads)
              --preset long-lines    pathological long-line / no-newline files
              --preset encoding-mix  UTF-16/BOM/invalid-byte/non-ASCII encoding stress
              --preset many-tiny     tiny-file-dominated (walker / stat pressure)
              --preset pathological-symbols  token-paste (expected-miss)/long idents/deep nesting
              --preset dup-content   byte-identical groups (content-hash dedup / posting collapse)

            COMMON KNOBS (all optional; counts scale by --scale unless set explicitly):
              --out <dir>            output directory (required; cleared if it exists)
              --force                overwrite --out even if it wasn't created by CodeSpawner
              --scale <f>            count multiplier (default 0.01)
              --seed <n>             deterministic seed (default 1337)
              --giant-headers <n>    count of >MaxHeaderMB register headers (the byte pathology)
              --max-header-mb <n>    size of each giant header (default 110; never scaled)
              --macro-density <n>    #defines per giant header (preprocessor-memory axis)
              --cfiles <n>           .c files with cross-dir call edges
              --tiny-files <n>       tiny .csv files (per-file / stat pressure)
              --unresolved-includes <n>   TUs referencing a missing vendor header (negative oracle)
              --compile-db none|partial|full
              --linked-roots <n>     split output across N sibling trees
              --io-parallelism <n>   max concurrent large-header writes

            Run 'codespawner gen --out .\\_fw --scale 0.01 --giant-headers 0' for a fast smoke.
            """);
    }
}
