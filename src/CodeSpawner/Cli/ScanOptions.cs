using CodeSpawner.Profile;

namespace CodeSpawner.Cli;

/// <summary>
/// Options for <c>scan &lt;tree&gt; --out profile.json</c> — the read-only characterize half of the
/// characterize→regenerate round-trip. Never writes into the scanned tree; emits a numbers-only profile.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>The tree to measure (read-only).</summary>
    public required string Tree { get; set; }
    /// <summary>Where to write the JSON profile.</summary>
    public string Out { get; set; } = "";

    /// <summary>Privacy posture. Default is class-labeled (reads bytes to classify, emits no numeric stats).</summary>
    public PrivacyPosture Posture { get; set; } = PrivacyPosture.ClassLabeled;

    /// <summary>k-anonymity floor — archetypes with fewer than this many files fold into an "other" bucket.</summary>
    public int MinCluster { get; set; } = 5;

    /// <summary>Content stats/labels are computed from up to this many files per (ext, size-band) cluster.
    /// Structure counts stay exact. 0 = no per-cluster cap (sample everything).</summary>
    public int Sample { get; set; } = 64;
}
