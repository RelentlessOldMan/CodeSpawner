namespace CodeSpawner.Profile;

/// <summary>
/// Privacy posture (docs/scan-design.md). <see cref="ClassLabeled"/> is the work-tree default: bytes are read
/// to assign a k-anonymized content-class ENUM per cluster, but no numeric content stats are emitted.
/// </summary>
public enum PrivacyPosture
{
    StructureOnly, // never opens a byte — size/type/dir only
    ClassLabeled,  // DEFAULT — reads bytes to classify; emits class enum + coarse density, no numeric content
    ContentStats,  // opt-in — adds numeric histograms (defineFrac, identUniqueRatio, entropy, exact density)
}

/// <summary>Coarse ordinal for symbol/edge density — leaks no more than the class label itself.</summary>
public enum DensityBand { Low, Med, High }

public static class PostureExtensions
{
    public static string Label(this PrivacyPosture p) => p switch
    {
        PrivacyPosture.StructureOnly => "structure-only",
        PrivacyPosture.ClassLabeled => "class-labeled",
        PrivacyPosture.ContentStats => "content-stats",
        _ => "class-labeled",
    };

    public static string Label(this DensityBand b) => b switch
    {
        DensityBand.Low => "low",
        DensityBand.Med => "med",
        DensityBand.High => "high",
        _ => "low",
    };

    public static bool ReadsContent(this PrivacyPosture p) => p != PrivacyPosture.StructureOnly;
    public static bool EmitsNumericStats(this PrivacyPosture p) => p == PrivacyPosture.ContentStats;
}

/// <summary>One count+bytes row in a size histogram, or count-only row in a fraction histogram.</summary>
public sealed class HistogramBucket
{
    public required string Bucket { get; init; }
    public long Files { get; init; }
    public long Bytes { get; init; }
}

/// <summary>A {label: count} distribution (depth, fan-out, files-per-dir, include fan-out).</summary>
public sealed class LabeledHistogram
{
    // Insertion order preserved for a stable, diffable profile.
    public List<(string Label, long Count)> Entries { get; } = new();
    public void Add(string label, long count) => Entries.Add((label, count));
}

public sealed class ExtensionStat
{
    public required string Ext { get; init; }
    public long Files { get; init; }
    public long Bytes { get; init; }
}

public sealed class SizeDistribution
{
    public long P50 { get; init; }
    public long P90 { get; init; }
    public long Max { get; init; }
}

public sealed class TrigramStat
{
    /// <summary>HLL/bitset cardinality of distinct byte 3-grams = posting-key count.</summary>
    public long DistinctEstimate { get; init; }
    /// <summary>Total 3-gram occurrences (≈ bytes) = posting-list length. Both are required for index size.</summary>
    public long Occurrences { get; init; }
}

/// <summary>Coarse (always) + exact (content-stats only) symbol/edge density — the graph-RAM axis.</summary>
public sealed class SymbolDensity
{
    public DensityBand FunctionsPerKB { get; init; }
    public DensityBand CallsPerFunction { get; init; }
    public DensityBand GlobalRefsPerFile { get; init; }

    // Exact values, emitted only under --content-stats.
    public double? ExactFunctionsPerKB { get; init; }
    public double? ExactCallsPerFunction { get; init; }
    public double? ExactGlobalRefsPerFile { get; init; }
}

/// <summary>Numeric content stats — emitted only under --content-stats.</summary>
public sealed class ContentStatBlock
{
    public double DefineFrac { get; init; }
    public double CommentFrac { get; init; }
    public double BlankFrac { get; init; }
    public double IncludeFrac { get; init; }
    public double IdentUniqueRatio { get; init; }
    public double AvgIdentLen { get; init; }
    public double AvgLineLen { get; init; }
    public long MaxLineLen { get; init; }
    public string Encoding { get; init; } = "ascii";
    public bool Bom { get; init; }
    public string Newline { get; init; } = "lf";
}

/// <summary>A cluster of like files — the reusable unit gen --from-profile mints from.</summary>
public sealed class Archetype
{
    public required string Label { get; init; }        // generic ("a1"), never a real name
    public required string Extension { get; init; }
    public long Count { get; init; }
    public required ContentClass Class { get; init; }
    public required SizeDistribution SizeDistribution { get; init; }
    public TrigramStat? Trigram { get; init; }
    public SymbolDensity? SymbolDensity { get; init; }
    public ContentStatBlock? Content { get; init; }    // content-stats posture only
}

public sealed class ParsedSourceClassBytes
{
    public required ContentClass Class { get; init; }
    public long Bytes { get; init; }
}

public sealed class HeaderStats
{
    public long Gt20MB { get; init; }
    public long Between1And20MB { get; init; }
    public long Lt1MB { get; init; }
    /// <summary>#define-fraction histogram for headers ≥1 MB — content-stats posture only.</summary>
    public List<HistogramBucket>? DefineFractionHistogramGe1MB { get; init; }
}

public sealed class IncludeStats
{
    public LabeledHistogram FanoutHistogram { get; } = new();
    public double UnresolvedIncludeRate { get; init; }
    public long DuplicateBasenameAmbiguity { get; init; }
    public int Hops { get; init; }
}

public sealed class DirStats
{
    public long Count { get; init; }
    public LabeledHistogram DepthHistogram { get; } = new();
    public LabeledHistogram FanoutHistogram { get; } = new();
    public LabeledHistogram FilesPerDirHistogram { get; } = new();
}

/// <summary>The full privacy-safe shape profile — numbers + enum labels only, no names/paths/content ever.</summary>
public sealed class ProfileModel
{
    public const int ProfileVersion = 1;

    public string ScannedAt { get; set; } = "";
    public int MinCluster { get; set; } = 5;
    public PrivacyPosture Posture { get; set; } = PrivacyPosture.ClassLabeled;

    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public long ParsedSourceBytes { get; set; }
    public long TotalIndexedBytes { get; set; }
    public long DistinctTrigramEstimate { get; set; }
    public long TrigramOccurrences { get; set; }

    public List<HistogramBucket> SizeHistogram { get; } = new();
    public List<ExtensionStat> Extensions { get; } = new();
    public HeaderStats Headers { get; set; } = new();
    public IncludeStats? Includes { get; set; }        // null under --structure-only (needs byte reads)
    public DirStats Dirs { get; set; } = new();
    public List<Archetype> Archetypes { get; } = new();
    public List<ParsedSourceClassBytes> ParsedSourceByClass { get; } = new();
}
