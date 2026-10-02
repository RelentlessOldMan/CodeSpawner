using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpawner.Manifest;

/// <summary>Unified-diff op for an explicit hunk. insert ⇒ oldLines==0; delete ⇒ newLines==0; replace ⇒ both ≥1.</summary>
public enum HunkOp { Insert, Delete, Replace }

public static class HunkOpExt
{
    public static string Label(this HunkOp op) => op switch
    {
        HunkOp.Insert => "insert",
        HunkOp.Delete => "delete",
        _             => "replace",
    };
}

/// <summary>An explicit unified-diff hunk, 1-based coordinates (contiguous changed lines coalesced into one).</summary>
public sealed record Hunk(HunkOp Op, int OldStart, int OldLines, int NewStart, int NewLines);

/// <summary>
/// A compact run-rule standing in for a regular stride of 1-line replaces on a giant file — so a >1 GB header's
/// delta stays small. Expansion (pinned byte-exact with CodeDiffer): touched lines =
/// { RangeStart + k*Stride : k=0,1,… while ≤ RangeEnd }, 1-based INCLUSIVE, each a PerHunk-length replace.
/// </summary>
public sealed record RunHunk(HunkOp Op, int Stride, int RangeStart, int RangeEnd, int PerHunk);

/// <summary>A detected rename/move; Similarity encoded as integer thousandths (milli) to dodge float ambiguity.</summary>
public sealed record Rename(string From, string To, int SimilarityMilli);

/// <summary>
/// One changed file's ground truth: its classification reason + before/after shas &amp; sizes + hunks (either
/// an explicit list OR a single giant-file run-rule, never both). Reason is itself digested so a misclassify
/// trips the hash. SHA = lowercase-hex SHA-256 of the raw file bytes.
/// </summary>
public sealed class DiffFile
{
    public required string Path { get; init; }
    public string Reason { get; init; } = "content";
    public required string OldSha { get; init; }
    public required string NewSha { get; init; }
    public long OldSize { get; init; }
    public long NewSize { get; init; }
    public List<Hunk> Hunks { get; } = new();   // explicit hunks (normal files)
    public RunHunk? Run { get; init; }           // giant-file run-rule (mutually exclusive with Hunks)
    /// <summary>For reason=metadata only: a content-identical metadata change (field, old, new). NOT digested
    /// (the locked modified-files section is exactly path·reason·oldSha·newSha·oldSize·newSize); emitted for
    /// CodeDiffer's metadata-diff path, which is opt-in.</summary>
    public (string Field, string Old, string New)? Metadata { get; init; }
}

/// <summary>
/// The canonical component digest of diff-delta ground truth, surfaced as <c>_meta.diffTruthSha</c>. It is
/// DELIBERATELY separate from the primary <c>prevTruthSha</c> (same discipline as <see cref="IndirectDigest"/>):
/// a reason-flip, sha-flip, hunk shift, or rename change trips THIS hash. The byte format is LOCKED with the
/// CodeDiffer session (code-spawner chat, 2026-10-02); do not change it without re-agreeing. See
/// docs/diff-delta-design.md.
///
///   four sections, GS(0x1D) between them, ALL always present (empty = header + zero records):
///     1 modified-files:  path US reason US oldSha US newSha US oldSize US newSize RS
///     2 hunks-explicit:  path US op US oldStart US oldLines US newStart US newLines RS
///     3 hunks-run:       path US op US stride US rangeStart US rangeEnd US perHunk RS
///     4 renames:         from US to US similarityMilli RS
///   each section: dedup then ORDINAL sort. sha256( s1 + GS + s2 + GS + s3 + GS + s4 ), lowercase hex.
///
/// US = 0x1F (unit), RS = 0x1E (record), GS = 0x1D (group). op ∈ insert|delete|replace. Numbers invariant decimal.
/// </summary>
public static class DiffDigest
{
    private const char US = (char)0x1F;
    private const char RS = (char)0x1E;
    private const char GS = (char)0x1D;

    public static string Compute(IReadOnlyList<DiffFile> files, IReadOnlyList<Rename> renames)
    {
        var modified = new List<string>();
        var hunksExplicit = new List<string>();
        var hunksRun = new List<string>();

        foreach (var f in files)
        {
            modified.Add(string.Concat(
                f.Path, US, f.Reason, US, f.OldSha, US, f.NewSha, US, Num(f.OldSize), US, Num(f.NewSize)));
            foreach (var h in f.Hunks)
                hunksExplicit.Add(string.Concat(
                    f.Path, US, h.Op.Label(), US, Num(h.OldStart), US, Num(h.OldLines), US, Num(h.NewStart), US, Num(h.NewLines)));
            if (f.Run is { } r)
                hunksRun.Add(string.Concat(
                    f.Path, US, r.Op.Label(), US, Num(r.Stride), US, Num(r.RangeStart), US, Num(r.RangeEnd), US, Num(r.PerHunk)));
        }

        var renameRecords = new List<string>();
        foreach (var r in renames)
            renameRecords.Add(string.Concat(r.From, US, r.To, US, Num(r.SimilarityMilli)));

        var sb = new StringBuilder();
        Emit(sb, modified);      sb.Append(GS);
        Emit(sb, hunksExplicit); sb.Append(GS);
        Emit(sb, hunksRun);      sb.Append(GS);
        Emit(sb, renameRecords);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Emit(StringBuilder sb, List<string> records)
    {
        foreach (var r in DedupSort(records)) sb.Append(r).Append(RS);
    }

    private static List<string> DedupSort(List<string> records)
    {
        var list = new List<string>(new HashSet<string>(records, StringComparer.Ordinal));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);
}
