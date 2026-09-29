using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using CodeSpawner.Scan;
using CodeSpawner.Verify;

namespace CodeSpawner;

public static class Program
{
    public const string Version = "1.0.4";

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
        if (got == golden) { Console.WriteLine($"digest self-test: OK ({got})"); return 0; }
        Console.Error.WriteLine($"digest self-test: FAIL\n  expected {golden}\n  got      {got}");
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

            MUTATE (incremental/watcher oracle — emits delta manifests that compose: truth = base ⊕ delta):
              --corpus <dir>   an existing CodeSpawner corpus (guarded by the .codespawner marker)
              --seed <n>       edit-selection seed (default 7)
              --edits <n>      number of edits (default 5)
              --step <k>       apply ONLY edit k (the chain driver; emits corpus-delta-k.json)
              --through        apply all N edits, emit one cumulative <corpus>-delta.json
              --restream       include 4-restream edits (needs --restream-seeds in the base corpus)
              (gen the base with --shrink-seeds N [--restream-seeds N] to enable 4-shrink/4-restream)

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
