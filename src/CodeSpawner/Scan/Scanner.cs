using System.Diagnostics;
using System.Globalization;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Profile;

namespace CodeSpawner.Scan;

/// <summary>
/// The read-only characterize half of the round-trip. Walks a real tree and produces a numbers-only
/// <see cref="ProfileModel"/>: size/type/dir shape, content-class archetypes, trigram + parse + include cost,
/// and symbol-density bands — never a name, path, identifier, or byte of content. See docs/scan-design.md.
/// </summary>
public sealed class Scanner
{
    private const int ReadCapBytes = 4 * 1024 * 1024; // classify/trigram off a bounded prefix of each sampled file
    private const int IncludeHops = 2;

    private static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
    { ".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp" };

    private readonly ScanOptions _o;
    public Scanner(ScanOptions o) => _o = o;

    // Files and dirs the scan could not read; they are left out of the profile, so the run reports them.
    private int _unreadableFiles, _unreadableDirs;
    internal int UnreadableFiles => _unreadableFiles;
    internal int UnreadableDirs => _unreadableDirs;

    private sealed class FileEntry
    {
        public required string Path;
        public required string Ext;
        public long Size;
        public FileStats? Stats; // populated only for sampled files under a content posture
    }

    public int Run()
    {
        var sw = Stopwatch.StartNew();
        string treeFull = new DirectoryInfo(_o.Tree).FullName;
        if (!Directory.Exists(treeFull))
        {
            Console.Error.WriteLine($"error: tree '{_o.Tree}' is not a directory");
            return 2;
        }
        Console.WriteLine($"Scanning {treeFull} (posture={_o.Posture.Label()}, minCluster={_o.MinCluster}, sample={_o.Sample}) ...");

        var files = new List<FileEntry>();
        var basenameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        WalkFiles(treeFull, files, basenameCounts);

        var model = new ProfileModel
        {
            ScannedAt = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            MinCluster = _o.MinCluster,
            Posture = _o.Posture,
            TotalFiles = files.Count,
        };
        foreach (var f in files) model.TotalBytes += f.Size;

        BuildSizeHistogram(files, model);
        BuildExtensionHistogram(files, model);
        BuildDirStats(treeFull, model);
        BuildHeaderStats(files, model);

        // Cluster by (ext, sizeBand); classify a deterministic sample per cluster under a content posture.
        var clusters = ClusterFiles(files);
        var global = _o.Posture.ReadsContent() ? new TrigramMeter() : null;
        var arch = _o.Posture.ReadsContent() ? new TrigramMeter() : null;

        var byClassBytes = new Dictionary<ContentClass, long>();
        var small = new List<(FileEntry[] cluster, ContentClass cls)>(); // sub-min clusters folded into "other"
        int label = 0;

        var includeAgg = new IncludeAggregate();

        foreach (var cluster in clusters)
        {
            ContentClass cls = ContentClass.GenericCode;
            SymbolDensity? density = null;
            ContentStatBlock? content = null;
            TrigramStat? trigram = null;

            if (_o.Posture.ReadsContent())
            {
                arch!.Reset();
                var sampled = SampleAndClassify(cluster, arch, global!, includeAgg, basenameCounts);
                cls = ModalClass(sampled);
                density = AggregateDensity(sampled, _o.Posture.EmitsNumericStats());
                if (_o.Posture.EmitsNumericStats()) content = AggregateContent(sampled);
                long occ = 0;
                foreach (var f in cluster) occ += Math.Max(0, f.Size - 2);
                trigram = new TrigramStat { DistinctEstimate = arch.DistinctCount(), Occurrences = occ };
            }

            long clusterBytes = 0;
            foreach (var f in cluster) clusterBytes += f.Size;

            if (cluster.Length < _o.MinCluster)
            {
                small.Add((cluster, cls));
                continue;
            }

            model.Archetypes.Add(new Archetype
            {
                Label = "a" + (++label),
                Extension = cluster[0].Ext,
                Count = cluster.Length,
                Class = cls,
                SizeDistribution = SizeDist(cluster),
                Trigram = trigram,
                SymbolDensity = density,
                Content = content,
            });
            if (cls.IsParsedSource()) Accumulate(byClassBytes, cls, clusterBytes);
        }

        FoldOther(small, model, byClassBytes);

        // Totals that depend on class.
        if (_o.Posture.ReadsContent())
        {
            foreach (var kv in byClassBytes) model.ParsedSourceBytes += kv.Value;
            foreach (var kv in byClassBytes)
                model.ParsedSourceByClass.Add(new ParsedSourceClassBytes { Class = kv.Key, Bytes = kv.Value });
            model.ParsedSourceByClass.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
            model.DistinctTrigramEstimate = global!.DistinctCount();
            model.TrigramOccurrences = global.Occurrences;
            model.TotalIndexedBytes = IndexedBytes(files, clusters, model);
            model.Includes = includeAgg.Build(IncludeHops);
        }
        else
        {
            // structure-only: no class info — approximate parsed source by extension, no decomposition.
            foreach (var f in files) if (SourceExts.Contains(f.Ext)) model.ParsedSourceBytes += f.Size;
            model.TotalIndexedBytes = model.TotalBytes;
        }

        ProfileWriter.Write(model, _o.Out);
        Console.WriteLine($"  archetypes: {model.Archetypes.Count}, parsedSourceBytes: {model.ParsedSourceBytes:N0}, " +
                          $"totalIndexedBytes: {model.TotalIndexedBytes:N0}");
        if (_unreadableFiles + _unreadableDirs > 0)
            Console.WriteLine($"  skipped (unreadable): {_unreadableFiles} file(s), {_unreadableDirs} dir(s)' contents");
        Console.WriteLine($"Wrote {_o.Out} in {sw.Elapsed.TotalSeconds:N1}s (numbers-only, no names/paths/content).");
        return 0;
    }

    // --- walk ---
    private void WalkFiles(string root, List<FileEntry> files, Dictionary<string, int> basenameCounts)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            long size;
            try { size = new FileInfo(path).Length; } catch { _unreadableFiles++; continue; }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext.Length == 0) ext = "(none)";
            else if (!PlainExtension.IsMatch(ext)) ext = OtherExt;   // a dot inside a name, not an extension: never kept
            files.Add(new FileEntry { Path = path, Ext = ext, Size = size });
            string bn = Path.GetFileName(path).ToLowerInvariant();
            basenameCounts.TryGetValue(bn, out int c);
            basenameCounts[bn] = c + 1;
        }
    }

    // --- histograms ---
    private static readonly (long Max, string Label)[] SizeBuckets =
    {
        (1024, "<1KB"), (10L * 1024, "1-10KB"), (100L * 1024, "10-100KB"), (1024L * 1024, "100KB-1MB"),
        (10L * 1024 * 1024, "1-10MB"), (100L * 1024 * 1024, "10-100MB"), (1024L * 1024 * 1024, "100MB-1GB"),
        (long.MaxValue, ">1GB"),
    };

    private static int SizeBand(long size)
    {
        for (int i = 0; i < SizeBuckets.Length; i++) if (size < SizeBuckets[i].Max) return i;
        return SizeBuckets.Length - 1;
    }

    private static void BuildSizeHistogram(List<FileEntry> files, ProfileModel m)
    {
        var fc = new long[SizeBuckets.Length];
        var bc = new long[SizeBuckets.Length];
        foreach (var f in files) { int b = SizeBand(f.Size); fc[b]++; bc[b] += f.Size; }
        for (int i = 0; i < SizeBuckets.Length; i++)
            if (fc[i] > 0) m.SizeHistogram.Add(new HistogramBucket { Bucket = SizeBuckets[i].Label, Files = fc[i], Bytes = bc[i] });
    }

    /// <summary>What an extension must look like to be recorded; anything else is part of a file name.</summary>
    private static readonly System.Text.RegularExpressions.Regex PlainExtension =
        new(@"^\.[a-z0-9_+-]{1,16}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal const string OtherExt = "(other)";

    /// <summary>Count + bytes per extension. An extension fewer than --min-cluster files share is folded into
    /// <c>(other)</c> like a small archetype is: a rare suffix can be a fragment of one file's name.</summary>
    private void BuildExtensionHistogram(List<FileEntry> files, ProfileModel m)
    {
        var byExt = new Dictionary<string, (long files, long bytes)>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            byExt.TryGetValue(f.Ext, out var t);
            byExt[f.Ext] = (t.files + 1, t.bytes + f.Size);
        }
        (long files, long bytes) other = default;
        foreach (var kv in byExt)
        {
            if (kv.Key == OtherExt || kv.Value.files < _o.MinCluster) other = (other.files + kv.Value.files, other.bytes + kv.Value.bytes);
            else m.Extensions.Add(new ExtensionStat { Ext = kv.Key, Files = kv.Value.files, Bytes = kv.Value.bytes });
        }
        if (other.files > 0) m.Extensions.Add(new ExtensionStat { Ext = OtherExt, Files = other.files, Bytes = other.bytes });
        m.Extensions.Sort((x, y) => y.Bytes != x.Bytes ? y.Bytes.CompareTo(x.Bytes) : string.CompareOrdinal(x.Ext, y.Ext));
    }

    private void BuildDirStats(string root, ProfileModel m)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var dirs = new List<string> { root };
        dirs.AddRange(Directory.EnumerateDirectories(root, "*", opts));

        var depthHist = new SortedDictionary<int, long>();
        var fanoutHist = new SortedDictionary<int, long>();
        var filesHist = new SortedDictionary<int, long>();
        foreach (var d in dirs)
        {
            // Relative to the root, so a trailing separator on the tree argument (or a drive root) doesn't shift it.
            int depth = ReferenceEquals(d, root) ? 0
                      : Path.GetRelativePath(root, d).Count(c => c == Path.DirectorySeparatorChar) + 1;
            int childDirs = 0, childFiles = 0;
            try
            {
                foreach (var _ in Directory.EnumerateDirectories(d)) childDirs++;
                foreach (var _ in Directory.EnumerateFiles(d)) childFiles++;
            }
            catch { _unreadableDirs++; /* inaccessible dir: count it but skip children */ }
            Bump(depthHist, depth);
            Bump(fanoutHist, BucketCount(childDirs));
            Bump(filesHist, BucketCount(childFiles));
        }

        var ds = new DirStats { Count = dirs.Count };
        foreach (var kv in depthHist) ds.DepthHistogram.Add(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value);
        foreach (var kv in fanoutHist) ds.FanoutHistogram.Add(CountBucketLabel(kv.Key), kv.Value);
        foreach (var kv in filesHist) ds.FilesPerDirHistogram.Add(CountBucketLabel(kv.Key), kv.Value);
        m.Dirs = ds;
    }

    private static void BuildHeaderStats(List<FileEntry> files, ProfileModel m)
    {
        long gt20 = 0, mid = 0, lt1 = 0;
        foreach (var f in files)
        {
            if (f.Ext is not (".h" or ".hh" or ".hpp" or ".hxx" or ".h++" or ".inl" or ".ipp")) continue;
            if (f.Size >= 20L * 1024 * 1024) gt20++;
            else if (f.Size >= 1L * 1024 * 1024) mid++;
            else lt1++;
        }
        m.Headers = new HeaderStats { Gt20MB = gt20, Between1And20MB = mid, Lt1MB = lt1 };
    }

    // --- clustering + sampling ---
    private static List<FileEntry[]> ClusterFiles(List<FileEntry> files)
    {
        var map = new Dictionary<(string, int), List<FileEntry>>();
        foreach (var f in files)
        {
            var key = (f.Ext, SizeBand(f.Size));
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<FileEntry>();
            list.Add(f);
        }
        var clusters = new List<FileEntry[]>();
        foreach (var kv in map)
        {
            kv.Value.Sort((a, b) => PathUtil.SlashOrdinal.Compare(a.Path, b.Path)); // deterministic sample order on every OS
            clusters.Add(kv.Value.ToArray());
        }
        // Stable cluster order: by extension then size band.
        clusters.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a[0].Ext, b[0].Ext);
            return c != 0 ? c : SizeBand(a[0].Size).CompareTo(SizeBand(b[0].Size));
        });
        return clusters;
    }

    private List<FileStats> SampleAndClassify(FileEntry[] cluster, TrigramMeter arch, TrigramMeter global,
        IncludeAggregate inc, Dictionary<string, int> basenameCounts)
    {
        int cap = _o.Sample <= 0 ? cluster.Length : Math.Min(_o.Sample, cluster.Length);
        var result = new List<FileStats>(cap);
        byte[] buf = new byte[ReadCapBytes];
        for (int i = 0; i < cap; i++)
        {
            var f = cluster[i];
            int n = ReadPrefix(f.Path, buf);
            if (n < 0) { _unreadableFiles++; continue; }
            var span = buf.AsSpan(0, n);
            var st = ContentClassifier.Classify(span);
            arch.Add(span, global);
            f.Stats = st;
            result.Add(st);
            if (SourceExts.Contains(f.Ext))
                inc.AddFile(Path.GetFileName(f.Path).ToLowerInvariant(), st.IncludeTargets, basenameCounts);
        }
        return result;
    }

    private static int ReadPrefix(string path, byte[] buf)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            int total = 0;
            while (total < buf.Length)
            {
                int r = fs.Read(buf, total, buf.Length - total);
                if (r == 0) break;
                total += r;
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static ContentClass ModalClass(List<FileStats> sampled)
    {
        if (sampled.Count == 0) return ContentClass.GenericCode;
        var counts = new Dictionary<ContentClass, int>();
        foreach (var s in sampled) { counts.TryGetValue(s.Class, out int c); counts[s.Class] = c + 1; }
        ContentClass best = ContentClass.GenericCode; int bestN = -1;
        foreach (var kv in counts) if (kv.Value > bestN || (kv.Value == bestN && (int)kv.Key < (int)best)) { bestN = kv.Value; best = kv.Key; }
        return best;
    }

    private static SymbolDensity AggregateDensity(List<FileStats> sampled, bool exact)
    {
        double fpk = 0, cpf = 0, refs = 0; int n = Math.Max(1, sampled.Count);
        foreach (var s in sampled) { fpk += s.FunctionsPerKB; cpf += s.CallsPerFunction; refs += s.GlobalRefsPerFile; }
        fpk /= n; cpf /= n; refs /= n;
        return new SymbolDensity
        {
            FunctionsPerKB = ContentClassifier.FunctionsPerKbBand(fpk),
            CallsPerFunction = ContentClassifier.CallsPerFunctionBand(cpf),
            GlobalRefsPerFile = ContentClassifier.GlobalRefsBand(refs),
            ExactFunctionsPerKB = exact ? fpk : null,
            ExactCallsPerFunction = exact ? cpf : null,
            ExactGlobalRefsPerFile = exact ? refs : null,
        };
    }

    private static ContentStatBlock AggregateContent(List<FileStats> sampled)
    {
        double def = 0, com = 0, blank = 0, inc = 0, uniq = 0, il = 0, ll = 0; long maxLl = 0; int n = Math.Max(1, sampled.Count);
        string enc = "ascii", nl = "lf"; bool bom = false;
        foreach (var s in sampled)
        {
            def += s.DefineFrac; com += s.CommentFrac; blank += s.BlankFrac; inc += s.IncludeFrac;
            uniq += s.IdentUniqueRatio; il += s.AvgIdentLen; ll += s.AvgLineLen;
            if (s.MaxLineLen > maxLl) maxLl = s.MaxLineLen;
            if (s.Encoding != "ascii") enc = s.Encoding;
            if (s.Newline == "crlf") nl = "crlf";
            bom |= s.Bom;
        }
        return new ContentStatBlock
        {
            DefineFrac = def / n, CommentFrac = com / n, BlankFrac = blank / n, IncludeFrac = inc / n,
            IdentUniqueRatio = uniq / n, AvgIdentLen = il / n, AvgLineLen = ll / n, MaxLineLen = maxLl,
            Encoding = enc, Bom = bom, Newline = nl,
        };
    }

    private static SizeDistribution SizeDist(FileEntry[] cluster)
    {
        var sizes = cluster.Select(f => f.Size).OrderBy(x => x).ToArray();
        long P(double q) => sizes[Math.Min(sizes.Length - 1, (int)(q * sizes.Length))];
        return new SizeDistribution { P50 = Round3(P(0.50)), P90 = Round3(P(0.90)), Max = Round3(sizes[^1]) };
    }

    /// <summary>A size rounded to 3 significant digits (1347 → 1350, 840043 → 840000): p50/p90/max are each one
    /// file's exact size, which the profile must not carry.</summary>
    internal static long Round3(long v)
    {
        long scale = 1;
        while (v / scale >= 1000) scale *= 10;
        return (v + scale / 2) / scale * scale;
    }

    private void FoldOther(List<(FileEntry[] cluster, ContentClass cls)> small, ProfileModel m,
        Dictionary<ContentClass, long> byClassBytes)
    {
        if (small.Count == 0) return;
        long count = 0, bytes = 0; var allSizes = new List<long>();
        var clsCount = new Dictionary<ContentClass, int>();
        foreach (var (cluster, cls) in small)
        {
            count += cluster.Length;
            foreach (var f in cluster) { bytes += f.Size; allSizes.Add(f.Size); }
            clsCount.TryGetValue(cls, out int c); clsCount[cls] = c + cluster.Length;
        }
        allSizes.Sort();
        ContentClass modal = ContentClass.GenericCode; int best = -1;
        foreach (var kv in clsCount) if (kv.Value > best) { best = kv.Value; modal = kv.Key; }

        m.Archetypes.Add(new Archetype
        {
            Label = "other",
            Extension = "*",
            Count = count,
            Class = modal,
            SizeDistribution = new SizeDistribution
            {
                P50 = Round3(allSizes[allSizes.Count / 2]),
                P90 = Round3(allSizes[Math.Min(allSizes.Count - 1, (int)(0.9 * allSizes.Count))]),
                Max = Round3(allSizes[^1]),
            },
        });
        if (_o.Posture.ReadsContent() && modal.IsParsedSource()) Accumulate(byClassBytes, modal, bytes);
    }

    private long IndexedBytes(List<FileEntry> files, List<FileEntry[]> clusters, ProfileModel m)
    {
        // Whole polyglot tree minus files whose archetype class is binary. Approximate a file's class by its
        // cluster modal class (already assigned via f.Stats where sampled; else count as indexable text).
        long indexed = 0;
        foreach (var f in files)
        {
            if (f.Stats is { Class: ContentClass.Binary }) continue;
            indexed += f.Size;
        }
        return indexed;
    }

    private static void Accumulate(Dictionary<ContentClass, long> d, ContentClass c, long v)
    { d.TryGetValue(c, out long x); d[c] = x + v; }

    private static void Bump(SortedDictionary<int, long> d, int k) { d.TryGetValue(k, out long v); d[k] = v + 1; }

    // Coarse count buckets shared by fan-out / files-per-dir: 0..4 exact-ish, then log-ish bands.
    private static int BucketCount(int n) => n <= 4 ? n : n <= 16 ? 16 : n <= 64 ? 64 : 65;
    private static string CountBucketLabel(int b) => b switch
    {
        <= 4 => b.ToString(CultureInfo.InvariantCulture),
        16 => "5-16",
        64 => "17-64",
        _ => "65+",
    };
}
