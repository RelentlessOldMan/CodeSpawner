using System.Security.Cryptography;
using System.Text;

namespace CodeSpawner.Manifest;

/// <summary>
/// The canonical component digest of the indirect-edge ground truth + declared roots, surfaced as
/// <c>_meta.indirectTruthSha</c>. It is DELIBERATELY separate from the frozen primary <c>prevTruthSha</c>
/// (which stays untouched — no re-sign): a rewired indirect edge or a moved root trips THIS hash, catching
/// silent truth-drift the primary digest can't see. The byte format is LOCKED with CodeCarver + CodeCompass
/// (3-way chat, 2026-09-30); do not change it without re-agreeing:
///
///   two sections, GS(0x1D) between them, both always present (empty = zero records):
///     indirectEdges: for each edge (dedup then ORDINAL sort):
///        source US target US via US (dispatched?"1":"0") US (resolved?"1":"0") RS
///     roots:         for each root (dedup then ORDINAL sort):
///        name RS
///   sha256( indirectSection + GS + rootsSection ), lowercase hex.
///
/// US = 0x1F (unit separator), RS = 0x1E (record separator), GS = 0x1D (group separator). Bools are 0/1.
/// See docs/oracle-v1-design.md.
/// </summary>
public static class IndirectDigest
{
    private const char US = (char)0x1F;
    private const char RS = (char)0x1E;
    private const char GS = (char)0x1D;

    /// <summary>True if any symbol carries an indirect edge (so the digest is worth emitting).</summary>
    public static bool Any(IReadOnlyDictionary<string, SymbolEntry> symbols)
    {
        foreach (var s in symbols.Values) if (s.IndirectEdges.Count > 0) return true;
        return false;
    }

    public static string Compute(IReadOnlyDictionary<string, SymbolEntry> symbols, IEnumerable<string> roots)
    {
        // indirectEdges section: source is the owning symbol name.
        var edgeRecords = new List<string>();
        foreach (var (name, s) in symbols)
            foreach (var e in s.IndirectEdges)
                edgeRecords.Add(string.Concat(
                    name, US, e.Target, US, e.Via.Label(), US,
                    e.Dispatched ? "1" : "0", US, e.Resolved ? "1" : "0"));

        var rootRecords = new List<string>(roots);

        var sb = new StringBuilder();
        foreach (var r in DedupSort(edgeRecords)) sb.Append(r).Append(RS);
        sb.Append(GS);
        foreach (var r in DedupSort(rootRecords)) sb.Append(r).Append(RS);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static List<string> DedupSort(List<string> records)
    {
        var set = new HashSet<string>(records, StringComparer.Ordinal);
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
