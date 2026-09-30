namespace CodeSpawner.Manifest;

/// <summary>
/// One ground-truth symbol: where it is defined, where it is referenced, its call-graph edges, and
/// (for the negative case) references that must NOT resolve. Paths are repo-relative to the corpus root;
/// the symbol NAME — not the path — is the seed-stable identity.
/// </summary>
public sealed class SymbolEntry
{
    public required string Def { get; set; }
    public List<string> Refs { get; init; } = new();
    public List<string> Edges { get; init; } = new();
    public List<string>? UnreachableRefs { get; set; }

    /// <summary>
    /// True when a correct lexical / preprocessor-blind indexer is EXPECTED not to resolve this symbol
    /// (e.g. a token-paste-generated name that never appears literally in the source). The honest-miss dual
    /// of <see cref="UnreachableRefs"/>: not finding it is correct, not a recall failure.
    /// </summary>
    public bool ExpectedMiss { get; set; }
}

/// <summary>
/// A set of byte-identical files (the dedup target — they MUST collapse to one posting set) plus optional
/// near-identical controls (1 line different — they must NOT collapse). Paths are repo-relative.
/// </summary>
public sealed class DupGroup
{
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public List<string> Paths { get; init; } = new();
    public List<string> NearVariants { get; init; } = new();
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

    /// <summary>
    /// Declared entry-point symbol names (oracle overlay). Reachability = closure over (edges ∪ indirectEdges)
    /// from these. Emitted even for the linear default so a consumer never guesses the chain entry.
    /// </summary>
    public List<string> Roots { get; } = new();

    // Insertion order preserved for stable, diffable output.
    public Dictionary<string, SymbolEntry> Symbols { get; } = new();

    /// <summary>Optional duplicate-content groups (dedup / posting-collapse oracle). Empty when unused.</summary>
    public List<DupGroup> DupGroups { get; } = new();
}
