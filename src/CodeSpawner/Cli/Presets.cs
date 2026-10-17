namespace CodeSpawner.Cli;

/// <summary>
/// Named knob bundles that replace the old wrapper scripts. A preset is expanded to its knob tokens,
/// placed BEFORE the user's own tokens so anything the user passes explicitly still wins.
/// </summary>
public static class Presets
{
    // The include web's counts, shared by death and death-lite (only the header sizes differ). Declared first:
    // static fields initialize in textual order.
    private static readonly string[] WebCounts =
        ["--web-units", "3000", "--web-headers", "3000", "--web-depth", "15", "--web-window", "18-28",
         "--web-aggregators", "40", "--web-templates", "60", "--web-template-sites", "4", "--web-tail", "24"];

    // death: the "repo of death" — ~90 GB, ~50k files, 12 headers OVER 1 GB (the headline pathology).
    // 12 giants * 1.2 GB = ~14.4 GB; the rest (~75 GB) is made of ~55 MB big headers.
    // Plus the include web (docs/include-web-design.md): 3000 units over 3000 register maps, deep and overlapping,
    // with 24 tail headers of 48 MB each.
    private static readonly string[] Death =
        ["--scale", "1.0", "--giant-headers", "12", "--max-header-mb", "1229", "--big-headers", "1375",
         .. WebCounts, "--web-header-lines", "2000", "--web-tail-mb", "48"];

    // death-lite: all of death's shapes in ~0.5 GB, no compute gate. Giants of 32 MB, the rest at 1/20 the counts,
    // and the web at death's counts with small headers (150 lines, tails of 8 MB).
    private static readonly string[] DeathLite =
        ["--scale", "0.05", "--giant-headers", "3", "--max-header-mb", "32", "--big-headers", "1", "--med-headers", "12",
         .. WebCounts, "--web-header-lines", "150", "--web-tail-mb", "8"];

    // ci: ~1/100 counts, keeps >=1 pathology header. Seconds-to-minutes smoke tier.
    private static readonly string[] Ci = ["--scale", "0.01"];

    // memory: pure preprocessor-memory axis — one giant header + a few .c that include it, nothing else.
    private static readonly string[] Memory =
    [
        "--giant-headers", "1", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
        "--tiny-files", "0", "--blob-files", "0", "--cfiles", "5", "--macro-density", "1000000",
    ];

    // Every pathology preset isolates its shape: the population on, all normal noise off (cfiles floored at 2).
    private static readonly string[] Bare =
        ["--giant-headers", "0", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
         "--tiny-files", "0", "--blob-files", "0", "--cfiles", "2"];

    // dense-band: N headers parked just under the stream threshold, unique idents — the OOM regression.
    private static readonly string[] DenseBand = [.. Bare, "--dense-headers", "40", "--dense-under-mb", "127"];

    // broad-token: a hot token in 0.5 of a 2-8 MB carrier band (find_references expected-set + sidecar test).
    private static readonly string[] BroadToken = [.. Bare, "--broad-token-files", "200", "--hot-token-share", "0.5"];

    // long-lines: pathologically long single-line files (emitter alternates newline / no-newline).
    private static readonly string[] LongLines = [.. Bare, "--long-line-files", "8", "--max-line-bytes", "8388608"];

    // encoding-mix: UTF-16LE/BE, UTF-8-BOM, invalid byte runs, non-ASCII identifiers.
    private static readonly string[] EncodingMix = [.. Bare, "--encoding-mix", "40"];

    // many-tiny: tiny-file-dominated — walker/stat pressure. Just the existing count knob, made a one-liner.
    private static readonly string[] ManyTiny =
        ["--giant-headers", "0", "--big-headers", "0", "--med-headers", "0", "--ordinary-headers", "0",
         "--blob-files", "0", "--cfiles", "2", "--tiny-files", "500000"];

    // pathological-symbols: token-paste (expected-miss), long idents, deep nesting — symbol-extractor stress.
    private static readonly string[] PathologicalSymbols = [.. Bare, "--pathological-symbols", "50"];

    // dup-content: byte-identical copies + near-identical controls — content-hash dedup / posting collapse.
    private static readonly string[] DupContent = [.. Bare, "--dup-groups", "200", "--dup-copies", "4"];

    // Single source of truth: resolution (TryGet), the advertised name list (Names), and the error-message
    // hints all derive from this one ordered registry, so a preset can't be added in one place and forgotten
    // in another. Order here is the advertised order.
    private static readonly (string Name, string[] Tokens)[] Registry =
    [
        ("death", Death),
        ("death-lite", DeathLite),
        ("ci", Ci),
        ("memory", Memory),
        ("dense-band", DenseBand),
        ("broad-token", BroadToken),
        ("long-lines", LongLines),
        ("encoding-mix", EncodingMix),
        ("many-tiny", ManyTiny),
        ("pathological-symbols", PathologicalSymbols),
        ("dup-content", DupContent),
    ];

    /// <summary>The advertised preset names, in registry order.</summary>
    public static readonly string[] Names = Array.ConvertAll(Registry, p => p.Name);

    public static bool TryGet(string name, out string[] tokens)
    {
        string key = name.ToLowerInvariant();
        foreach (var (n, t) in Registry)
        {
            if (n == key) { tokens = t; return true; }
        }
        tokens = [];
        return false;
    }

    /// <summary>Pull a leading/embedded <c>--preset NAME</c> out of args and prepend its tokens.</summary>
    public static string[] Expand(string[] args) => Expand(args, out _);

    /// <summary>The preset's tokens followed by the user's own args; <paramref name="presetTokens"/> is how many
    /// leading tokens came from the preset, so the parser can let the user override them without calling that a
    /// repeated option.</summary>
    public static string[] Expand(string[] args, out int presetTokens)
    {
        string? name = null;
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? n = null;
            if (a == "--preset")
            {
                if (i + 1 >= args.Length) throw new ArgException($"--preset needs a name ({string.Join("|", Names)})");
                n = args[++i];
            }
            else if (a.StartsWith("--preset=", StringComparison.Ordinal)) n = a["--preset=".Length..];
            else { rest.Add(a); continue; }
            if (name is not null) throw new ArgException("--preset is given twice; pick one");
            name = n;
        }

        presetTokens = 0;
        if (name is null) return args;
        if (!TryGet(name, out var tokens))
            throw new ArgException($"unknown preset '{name}' ({string.Join("|", Names)})");

        // preset first, user args after -> user overrides.
        presetTokens = tokens.Length;
        return [.. tokens, .. rest];
    }
}
