namespace CodeSpawner.Cli;

/// <summary>
/// Named knob bundles that replace the old wrapper scripts. A preset is expanded to its knob tokens,
/// placed BEFORE the user's own tokens so anything the user passes explicitly still wins.
/// </summary>
public static class Presets
{
    // death: the "repo of death" — ~90 GB, ~50k files, 12 headers OVER 1 GB (the headline pathology).
    // 12 giants * 1.2 GB = ~14.4 GB; the rest (~75 GB) is made of ~55 MB big headers.
    private static readonly string[] Death =
        ["--scale", "1.0", "--giant-headers", "12", "--max-header-mb", "1229", "--big-headers", "1375"];

    // ci: ~1/100 counts, keeps >=1 pathology header. Seconds-to-minutes smoke tier.
    private static readonly string[] Ci = ["--scale", "0.01"];

    // memory: pure preprocessor-memory axis — one giant header + a few .c that include it, nothing else.
    private static readonly string[] Memory =
    [
        "--giant-headers", "1", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
        "--tiny-files", "0", "--blob-files", "0", "--cfiles", "5", "--macro-density", "1000000",
    ];

    public static bool TryGet(string name, out string[] tokens)
    {
        tokens = name.ToLowerInvariant() switch
        {
            "death" => Death,
            "ci" => Ci,
            "memory" => Memory,
            _ => [],
        };
        return tokens.Length > 0;
    }

    /// <summary>Pull a leading/embedded <c>--preset NAME</c> out of args and prepend its tokens.</summary>
    public static string[] Expand(string[] args)
    {
        string? name = null;
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--preset")
            {
                if (i + 1 >= args.Length) throw new ArgException("--preset needs a name (death|ci|memory)");
                name = args[++i];
            }
            else if (a.StartsWith("--preset=", StringComparison.Ordinal))
            {
                name = a["--preset=".Length..];
            }
            else rest.Add(a);
        }

        if (name is null) return args;
        if (!TryGet(name, out var tokens)) throw new ArgException($"unknown preset '{name}' (death|ci|memory)");

        // preset first, user args after -> user overrides.
        return [.. tokens, .. rest];
    }
}
