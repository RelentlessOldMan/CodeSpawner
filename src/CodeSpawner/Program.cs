using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Verify;

namespace CodeSpawner;

public static class Program
{
    public const string Version = "1.0.0";

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
                    new CorpusGenerator(ArgParser.ParseGen(Presets.Expand(rest))).Run();
                    return 0;

                case "verify":
                    return ManifestVerifier.Run(ArgParser.ParseVerify(rest));

                case "version":
                    Console.WriteLine(Version);
                    return 0;

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

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "help" or "/?";

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            codespawner {Version} — synthetic code-corpus generator

            USAGE:
              codespawner gen --out <dir> [knobs...]     generate a corpus + ground-truth manifest
              codespawner verify --corpus <dir>          self-check a corpus against its manifest
              codespawner version

            PRESETS (bundled knob sets; your own knobs still override):
              --preset death         ~90 GB, ~50k files, 12 headers >1 GB (the "repo of death")
              --preset ci            ~1/100 counts, keeps >=1 pathology header (fast smoke)
              --preset memory        one giant header + a few includers (preprocessor-memory axis)
              --preset dense-band    dense sub-threshold headers (posting/trigram memory OOM shape)
              --preset broad-token   hot token across a 2-8MB band (find_references + sidecar reads)
              --preset long-lines    pathological long-line / no-newline files
              --preset encoding-mix  UTF-16/BOM/invalid-byte/non-ASCII encoding stress
              --preset many-tiny     tiny-file-dominated (walker / stat pressure)

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
