namespace CodeSpawner.Manifest;

/// <summary>
/// The canonical component digest of the C++ (manifest v2) ground truth, surfaced as <c>_meta.cppTruthSha</c>
/// (docs/cpp-profile-design.md D7). Built like <see cref="IndirectDigest"/>; <c>prevTruthSha</c> and
/// <c>indirectTruthSha</c> keep their exact definitions. Byte format (LOCKED; do not change without re-agreeing
/// with CodeCarver + CodeCompass):
///
///   five sections, GS(0x1D) between them, all always present (empty = zero records); each section's records
///   are deduplicated then ORDINAL-sorted, and each record is followed by RS:
///     virtualEdges:   source US static US cha US rta      (cha / rta: the set dedup + ordinal-sorted, joined by ",")
///     implicitEdges:  source US target
///     memberPtrEdges: source US target US via US (dispatched?"1":"0")
///     kinds:          name US kind US instanceOf           (every symbol with a kind; instanceOf "" when absent)
///     staticInitRoots: name
///   sha256( s1 + GS + s2 + GS + s3 + GS + s4 + GS + s5 ), lowercase hex.
///
/// US = 0x1F, RS = 0x1E, GS = 0x1D. Symbol names never contain ",".
/// </summary>
public static class CppDigest
{
    private const char US = (char)0x1F;
    private const char RS = (char)0x1E;
    private const char GS = (char)0x1D;

    public static string Compute(IReadOnlyDictionary<string, SymbolEntry> symbols, IEnumerable<string> staticInitRoots)
    {
        var virt = new List<string>();
        var impl = new List<string>();
        var mptr = new List<string>();
        var kinds = new List<string>();
        foreach (var (name, s) in symbols)
        {
            foreach (var v in s.VirtualEdges)
                virt.Add(string.Concat(name, US, v.Static, US, string.Join(",", DedupSort(v.Cha)), US, string.Join(",", DedupSort(v.Rta))));
            foreach (var t in s.ImplicitEdges) impl.Add(string.Concat(name, US, t));
            foreach (var m in s.MemberPtrEdges)
                mptr.Add(string.Concat(name, US, m.Target, US, m.Via.Label(), US, m.Dispatched ? "1" : "0"));
            if (s.Kind is { } k) kinds.Add(string.Concat(name, US, k, US, s.InstanceOf ?? ""));
        }

        using var sink = new DigestSink();
        bool first = true;
        foreach (var section in new[] { virt, impl, mptr, kinds, new List<string>(staticInitRoots) })
        {
            if (!first) sink.Append(GS);
            first = false;
            foreach (var r in DedupSort(section)) sink.Append(r).Append(RS);
        }
        return sink.Finish();
    }

    private static List<string> DedupSort(IEnumerable<string> records)
    {
        var list = new List<string>(new HashSet<string>(records, StringComparer.Ordinal));
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
