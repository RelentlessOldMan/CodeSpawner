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

    /// <summary>Native 3-way: leave B pristine, emit two mutated variant trees B_v1/B_v2, their B→V deltas, and
    /// a conflict artifact. Edits land only on odd base lines (the stable-separator guarantee).</summary>
    public bool ThreeWay { get; set; }

    /// <summary>3-way dial: fraction of V2's edited lines that coincide with V1's (⇒ conflicts). 0 = all
    /// clean-merge, 1 = every V2 edit conflicts. 0..1, default 0.5.</summary>
    public double OverlapFraction { get; set; } = 0.5;

    /// <summary>With <c>--three-way</c>: emit the edge-case conflict kinds (adjacent multi-line, modify/delete,
    /// add/add, identical-overlap-clean) as cleanly-separated regions, instead of the random single-line
    /// modify/modify model. Truth computed by the union-span coalescer (diff3 maximal hunk). See
    /// docs/diff-delta-design.md §3-way-edges.</summary>
    public bool ConflictEdges { get; set; }

    /// <summary>How a content edit touches a GIANT file (&gt;= giant floor): <c>strided</c> (default — mark every
    /// stride-th line, emitted as a compact run-rule) or <c>single</c> (one localized one-line insert, emitted as
    /// ONE explicit hunk — the content-defined-chunker locality case: a tiny edit re-diffs ~1 block of thousands).
    /// Normal-sized files ignore this.</summary>
    public string? GiantEdit { get; set; }
    public string GiantEditMode => GiantEdit ?? "strided";

    /// <summary>
    /// Max modified-file records per shard. 0 (default) = one monolithic <c>&lt;corpus&gt;-delta.json</c>. &gt;0 =
    /// paged transport: a <c>&lt;corpus&gt;-delta.index.json</c> + <c>&lt;corpus&gt;-delta.shard-NNN.json</c> files,
    /// so a death-scale delta a consumer can't <c>JsonDocument.Parse</c> whole streams shard-by-shard. The
    /// diffTruthSha is sharding-INVARIANT (pure transport). Applies to the 2-way delta only.
    /// </summary>
    public int ShardSize { get; set; }

    /// <summary>True when bulk in-place mutation is requested.</summary>
    public bool IsBulk => Target is not null;
}
