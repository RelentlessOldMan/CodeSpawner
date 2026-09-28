using System.Security.Cryptography;
using System.Text;

namespace CodeSpawner.Manifest;

/// <summary>
/// The canonical digest of a composed symbol-truth, used as <c>_meta.prevTruthSha</c> in delta manifests so
/// a consumer can detect an out-of-order / misapplied / skipped delta mid-chain. The byte format is LOCKED
/// with CodeCompass (both sides reproduced the golden vector); do not change it without bumping the delta
/// format and re-agreeing:
///
///   sha256( for each symbol in ASCENDING ORDINAL name order:
///     name US def US refsSortedOrdinalJoined"," US edgesSortedOrdinalJoined"," US (expectedMiss?"1":"0") RS )
///   where US = 0x1F (unit separator), RS = 0x1E (record separator), including a trailing RS.
///
/// Ordinal (byte-wise) sort throughout; paths verbatim; expectedMiss-absent counts as "0". Field set =
/// name/def/refs/edges/expectedMiss (unreachableRefs is NOT included). Golden vector: the 2-symbol truth in
/// docs/mutate-design.md hashes to 7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e.
/// </summary>
public static class TruthDigest
{
    private const char US = (char)0x1F; // unit separator
    private const char RS = (char)0x1E; // record separator

    public static string Compute(IReadOnlyDictionary<string, SymbolEntry> symbols)
    {
        var names = new List<string>(symbols.Keys);
        names.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var name in names)
        {
            var s = symbols[name];
            var refs = new List<string>(s.Refs); refs.Sort(StringComparer.Ordinal);
            var edges = new List<string>(s.Edges); edges.Sort(StringComparer.Ordinal);
            sb.Append(name).Append(US)
              .Append(s.Def).Append(US)
              .Append(string.Join(',', refs)).Append(US)
              .Append(string.Join(',', edges)).Append(US)
              .Append(s.ExpectedMiss ? '1' : '0').Append(RS);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
