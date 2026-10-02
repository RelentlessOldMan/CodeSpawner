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

    // ---- bulk in-place mutation (diff-oracle mode; set --target to activate) -------------------------
    // Decoupled from the legacy round-robin: pick a population, say how many files to change and how much
    // of each, and emit one base->variant delta. The delta records file-level truth (fileOps.modified);
    // hunk/line-level ground truth is deferred until the diff-tool contract is locked.

    /// <summary>Population to perturb in bulk mode: source | headers | giant | all. Null = legacy mode.</summary>
    public string? Target { get; set; }
    /// <summary>How many files of the target to modify in place. Null = all matching files.</summary>
    public int? FilesChanged { get; set; }
    /// <summary>Fraction of each target file's lines to change in place (0..1]. Default 0.05.</summary>
    public double EditDensity { get; set; } = 0.05;
    /// <summary>Size floor in MB for the <c>giant</c> target (the big-header case). Default 100.</summary>
    public int GiantMinMb { get; set; } = 100;

    /// <summary>
    /// The mechanism of the bulk edit (null = content). Maps to a <c>reason</c> classification CodeDiffer
    /// asserts against (see docs/diff-delta-design.md):
    ///   content | line-insert | line-delete -> reason "content" (line-insert/delete exercise insert/delete
    ///     hunks + renumbering); eol -> "eol"; whitespace -> "whitespace"; encoding -> "encoding";
    ///     binary -> "binary"; metadata -> "metadata".
    /// </summary>
    public string? EditKind { get; set; }

    /// <summary>The edit mechanism, defaulted. See <see cref="EditKind"/>.</summary>
    public string Kind => EditKind ?? "content";

    /// <summary>For <c>--edit-kind rename</c>: fraction of chosen files emitted as near-duplicate ADD decoys
    /// (original kept) instead of renames — rename false-positive traps. 0..1, default 0.</summary>
    public double DecoyFraction { get; set; }

    /// <summary>True when bulk in-place mutation is requested.</summary>
    public bool IsBulk => Target is not null;
}
