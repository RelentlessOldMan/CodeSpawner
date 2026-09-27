namespace CodeSpawner.Manifest;

/// <summary>
/// One ground-truth symbol: where it is defined, where it is referenced, its call-graph edges, and
/// (for the negative case) references that must NOT resolve. Paths are repo-relative to the corpus root;
/// the symbol NAME — not the path — is the seed-stable identity.
/// </summary>
public sealed class SymbolEntry
{
    public required string Def { get; init; }
    public List<string> Refs { get; init; } = new();
    public List<string> Edges { get; init; } = new();
    public List<string>? UnreachableRefs { get; set; }
}

/// <summary>Per-population shape stats surfaced under <c>_meta.populations</c> (additive, v1).</summary>
public sealed record PopulationStat(string Name, long FileCount, long TotalBytes, long IdentCount);

/// <summary>The full v1 manifest: <c>_meta</c> + a symbol table keyed by symbol name.</summary>
public sealed class ManifestModel
{
    public int ManifestVersion { get; init; } = 1;
    public required string GeneratorVersion { get; init; }
    public required int Seed { get; init; }
    public required string CorpusRoot { get; init; }

    /// <summary>Optional per-population shape stats for consumer "is this the corpus I expect?" assertions.</summary>
    public List<PopulationStat> Populations { get; } = new();

    // Insertion order preserved for stable, diffable output.
    public Dictionary<string, SymbolEntry> Symbols { get; } = new();
}
