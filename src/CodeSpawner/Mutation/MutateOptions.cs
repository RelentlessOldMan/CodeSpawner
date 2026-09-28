namespace CodeSpawner.Mutation;

public sealed class MutateOptions
{
    public required string Corpus { get; set; }
    public string? Manifest { get; set; }
    /// <summary>Edit-selection seed (independent of the corpus generation seed).</summary>
    public int Seed { get; set; } = 7;
    /// <summary>Number of edits in the mutation.</summary>
    public int Edits { get; set; } = 5;
    /// <summary>If set, apply only this 1-based edit to the current on-disk state (the chain driver).</summary>
    public int? Step { get; set; }
    /// <summary>Apply all N edits and emit one cumulative base→final delta.</summary>
    public bool Through { get; set; }
    /// <summary>Include 4-restream edits (needs mut_restream_*.h seeds in the base corpus).</summary>
    public bool Restream { get; set; }
}
