using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpawner.Cli;
using CodeSpawner.Generation;
using CodeSpawner.Manifest;

namespace CodeSpawner.Mutation;

public enum EditType { Remove, LineShift, Add, Grow, Shrink, Restream }

/// <summary>One planned edit: its type, the target file (abs), the primary symbol, and a line hint.</summary>
public sealed record Edit(EditType Type, string Path, string? Symbol, int DefLine);

/// <summary>FS-effect checklist for a delta (composition never needs it; consumer FS asserts do).</summary>
public sealed class FileOps
{
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Modified { get; } = new();
    public List<(string From, string To)> Renamed { get; } = new();
}

/// <summary>
/// Deterministically edits a corpus in place and emits symbol-overlay delta manifests (see
/// docs/mutate-design.md). Guarded by the <c>.codespawner</c> marker. The base manifest stays immutable;
/// deltas are the record of change. Composition: <c>truth_k = truth_{k-1} ⊕ delta_k</c>.
/// </summary>
public static class Mutator
{
    private const string MarkerName = ".codespawner";
    private const int LineShiftLines = 12;         // lines inserted above a def for the line-shift edit
    private const long GrowTargetBytes = 9L * 1024 * 1024;   // cross the 8 MB sidecar cutoff
    private const long ShrinkTargetBytes = 1L * 1024 * 1024; // below the 2 MB network cutoff
    private const long RestreamTargetBytes = 100L * 1024 * 1024;

    public static int Run(MutateOptions o)
    {
        string corpus = Path.GetFullPath(o.Corpus);
        if (!Directory.Exists(corpus)) { Console.Error.WriteLine($"error: corpus not found: {corpus}"); return 2; }
        if (!File.Exists(Path.Combine(corpus, MarkerName)))
            throw new ArgException($"--corpus '{corpus}' is not a CodeSpawner corpus (no .codespawner marker); refusing to edit it.");

        string manifestPath = o.Manifest ?? DefaultManifestPath(corpus);
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"error: base manifest not found: {manifestPath}"); return 2; }
        var basem = ManifestReader.Load(manifestPath);

        if (o.IsBulk) return RunBulk(o, corpus, basem);

        var plan = BuildPlan(o, corpus, basem);
        Console.WriteLine($"mutate: {plan.Count} edit(s), seed {o.Seed}, base {basem.Sha256[..12]}… " +
                          $"({string.Join(",", plan.Select(e => e.Type))})");

        if (o.Step is { } k)
        {
            // Replay edits 1..k-1 on the table only (their disk ops were done by prior --step calls),
            // then apply edit k for real (disk + table). Delta composes onto truth_{k-1}.
            var work = Clone(basem.Symbols);
            for (int j = 0; j < k - 1; j++) ApplyEdit(plan[j], corpus, work, touchDisk: false);
            string prevSha = TruthDigest.Compute(work);
            var before = Clone(work);
            var ops = ApplyEdit(plan[k - 1], corpus, work, touchDisk: true);
            WriteDelta(corpus, basem, o, k, prevSha, before, work, ops);
        }
        else if (o.Through)
        {
            var work = Clone(basem.Symbols);
            string prevSha = TruthDigest.Compute(basem.Symbols); // composes onto base
            var before = Clone(basem.Symbols);
            var ops = new FileOps();
            for (int j = 0; j < plan.Count; j++) Merge(ops, ApplyEdit(plan[j], corpus, work, touchDisk: true));
            WriteDelta(corpus, basem, o, null, prevSha, before, work, ops);
        }
        else
        {
            // Default: apply the whole chain to disk and emit per-step deltas (convenience / local test).
            var work = Clone(basem.Symbols);
            for (int j = 0; j < plan.Count; j++)
            {
                string prevSha = TruthDigest.Compute(work);
                var before = Clone(work);
                var ops = ApplyEdit(plan[j], corpus, work, touchDisk: true);
                WriteDelta(corpus, basem, o, j + 1, prevSha, before, work, ops);
            }
        }
        return 0;
    }

    // ---- bulk in-place mutation (diff-oracle mode) ----------------------------------------------------

    private static int RunBulk(MutateOptions o, string corpus, BaseManifest basem)
    {
        var pool = SelectTargets(corpus, o.Target!, o.GiantMinMb);
        if (pool.Count == 0) { Console.Error.WriteLine($"error: no files match --target {o.Target} in {corpus}"); return 2; }

        ShufflePaths(pool, o.Seed, 101);                       // deterministic selection order
        int n = o.FilesChanged is { } fc ? Math.Min(fc, pool.Count) : pool.Count;
        var chosen = pool.Take(n).OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (o.ThreeWay) return RunThreeWay(o, corpus, basem, chosen);

        Console.WriteLine($"mutate(bulk): target={o.Target} kind={o.Kind} files={chosen.Count}/{pool.Count} " +
                          $"density={o.EditDensity:0.###} seed {o.Seed} base {basem.Sha256[..12]}…");

        long giantFloor = (long)o.GiantMinMb * 1024 * 1024;
        int stride = Math.Max(1, (int)Math.Round(1.0 / Math.Clamp(o.EditDensity, 1e-6, 1.0)));

        if (o.Kind == "rename") return RunRename(o, corpus, basem, chosen);

        string reason = ReasonOf(o.Kind);
        var files = new List<DiffFile>();
        long touched = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            var df = BuildDiffFile(AbsOf(corpus, chosen[i]), chosen[i], reason, o, i, giantFloor, stride);
            if (df is null) continue;                          // honesty: no actual change ⇒ not recorded
            touched += df.Run is { } r ? (r.RangeEnd - r.RangeStart) / r.Stride + 1
                                       : df.Hunks.Sum(h => Math.Max(h.OldLines, h.NewLines));
            files.Add(df);
        }

        // Bulk edits leave the symbol table (and prevTruthSha) alone — line-insert/delete shift def lines but
        // bulk mode is the DIFF oracle, not the symbol oracle. The 2-way delta carries file+hunk truth and its
        // own _meta.diffTruthSha (docs/diff-delta-design.md).
        string prevSha = TruthDigest.Compute(basem.Symbols);
        string diffSha = DiffDigest.Compute(files, Array.Empty<Rename>());
        WriteDiffDelta(corpus, basem, o, prevSha, diffSha, files, Array.Empty<Rename>(), Array.Empty<string>());
        Console.WriteLine($"  ~{touched} line(s) changed across {files.Count} file(s); diffTruthSha {diffSha[..12]}…");
        return 0;
    }

    // ---- rename / move (step 3) -----------------------------------------------------------------------

    /// <summary>
    /// Rename chosen files with GRADED similarity (a rotating band incl. pure 1.0 + a spread around the ~0.5
    /// detector threshold), rename+edit emitting hunks keyed by the <c>to</c> path, plus <c>--decoy-fraction</c>
    /// near-duplicate ADDs (original kept) as rename false-positive traps. Truth = the {from,to,similarity} set
    /// in <c>renamed</c> + the decoys in <c>added</c>: a precision/recall oracle (docs/diff-delta-design.md §3).
    /// </summary>
    private static int RunRename(MutateOptions o, string corpus, BaseManifest basem, List<string> chosen)
    {
        int[] band = { 1000, 900, 600, 300 };                  // similarityMilli targets, cycled
        var decoyRng = Rng.For(o.Seed, Category.Mutate, 202);
        int decoyThreshold = (int)Math.Round(Math.Clamp(o.DecoyFraction, 0, 1) * 1000);

        var renames = new List<Rename>();
        var modified = new List<DiffFile>();
        var added = new List<string>();

        for (int i = 0; i < chosen.Count; i++)
        {
            string rel = chosen[i];
            string abs = AbsOf(corpus, rel);

            if (decoyRng.Next(1000) < decoyThreshold)
            {
                // Decoy: near-duplicate COPY at a new path, original left in place ⇒ an ADD, NOT a rename.
                string dupRel = RenamedPath(rel, "dup", i);
                string dupAbs = AbsOf(corpus, dupRel);
                Directory.CreateDirectory(Path.GetDirectoryName(dupAbs)!);
                File.Copy(abs, dupAbs, overwrite: true);
                AppendPerLine(dupAbs, 0.1, o.Seed, 10_000 + i, n => $" /*decoy:{i}:{n}*/");
                added.Add(dupRel);
                continue;
            }

            int target = band[i % band.Length];
            string toRel = RenamedPath(rel, "moved", i);
            string toAbs = AbsOf(corpus, toRel);
            Directory.CreateDirectory(Path.GetDirectoryName(toAbs)!);
            var (oldSha, oldSize) = HashFile(abs);
            File.Move(abs, toAbs);                             // the rename

            if (target >= 1000)
            {
                renames.Add(new Rename(rel, toRel, 1000));     // pure rename: identical bytes
                continue;
            }

            // rename+edit: change ceil((1-sim)*L) lines so realized similarity lands on the band.
            var lines = SplitLines(File.ReadAllText(toAbs));
            int toChange = Math.Min(lines.Count, (int)Math.Ceiling((1 - target / 1000.0) * lines.Count));
            var changed = ChangeFirstNLines(toAbs, toChange, i);
            var (newSha, newSize) = HashFile(toAbs);
            int realizedMilli = lines.Count == 0 ? 1000 : (int)Math.Round((lines.Count - toChange) * 1000.0 / lines.Count);
            var df = new DiffFile
            {
                Path = toRel, Reason = "content", OldSha = oldSha, NewSha = newSha, OldSize = oldSize, NewSize = newSize,
            };
            df.Hunks.AddRange(Coalesce(changed));
            modified.Add(df);
            renames.Add(new Rename(rel, toRel, realizedMilli));
        }

        string prevSha = TruthDigest.Compute(basem.Symbols);
        string diffSha = DiffDigest.Compute(modified, renames);
        WriteDiffDelta(corpus, basem, o, prevSha, diffSha, modified, renames, added);
        Console.WriteLine($"  {renames.Count} rename(s) ({renames.Count(r => r.SimilarityMilli >= 1000)} pure), " +
                          $"{added.Count} decoy add(s); diffTruthSha {diffSha[..12]}…");
        return 0;
    }

    /// <summary>Append a marker to the first <paramref name="n"/> lines (contiguous ⇒ one coalesced hunk),
    /// preserving terminators. Returns the 1-based changed line numbers.</summary>
    private static List<int> ChangeFirstNLines(string abs, int n, int idx)
    {
        var lines = SplitLines(File.ReadAllText(abs));
        var changed = new List<int>();
        var sb = new StringBuilder();
        for (int k = 0; k < lines.Count; k++)
        {
            string content = lines[k].content;
            if (k < n) { content += $" /*ren:{idx}:{k}*/"; changed.Add(k + 1); }
            sb.Append(content).Append(lines[k].term);
        }
        File.WriteAllText(abs, sb.ToString(), Encodings.Utf8NoBom);
        return changed;
    }

    /// <summary>A sibling path with a tagged stem (same dir + extension), forward-slash relative form.</summary>
    private static string RenamedPath(string rel, string tag, int i)
    {
        int slash = rel.LastIndexOf('/');
        string dir = slash >= 0 ? rel[..slash] : "";
        string file = slash >= 0 ? rel[(slash + 1)..] : rel;
        int dot = file.LastIndexOf('.');
        string stem = dot >= 0 ? file[..dot] : file;
        string ext = dot >= 0 ? file[dot..] : "";
        string name = $"{stem}_{tag}{i}{ext}";
        return dir.Length == 0 ? name : dir + "/" + name;
    }

    // ---- native 3-way (step 4) ------------------------------------------------------------------------

    /// <summary>
    /// Leave B pristine; copy it to two variant trees B_v1/B_v2 and edit each independently, with all edits on
    /// ODD base lines so every even line is a stable anchor (the stable-separator guarantee — region-level
    /// conflicts ≡ line-level). <c>--overlap-fraction f</c> = fraction of V2's edited lines that coincide with
    /// V1's ⇒ conflicts; the rest clean-merge. Emits B-delta-v1/v2.json (standard diff-deltas) + B-conflict.json
    /// with a conflictTruthSha. See docs/diff-delta-design.md §3-way.
    /// </summary>
    private static int RunThreeWay(MutateOptions o, string corpus, BaseManifest basem, List<string> chosen)
    {
        string trimmed = TrimDir(corpus);
        string v1Root = trimmed + "_v1", v2Root = trimmed + "_v2";
        CopyTree(corpus, v1Root);
        CopyTree(corpus, v2Root);

        int dth = (int)Math.Round(Math.Clamp(o.EditDensity, 0, 1) * 10000);
        int fth = (int)Math.Round(Math.Clamp(o.OverlapFraction, 0, 1) * 10000);
        var v1Files = new List<DiffFile>();
        var v2Files = new List<DiffFile>();
        var conflicts = new List<Conflict>();
        var clean = new List<CleanMerge>();

        for (int i = 0; i < chosen.Count; i++)
        {
            string rel = chosen[i];
            int L = SplitLines(File.ReadAllText(AbsOf(corpus, rel))).Count;
            var rV2 = Rng.For(o.Seed, Category.Mutate, 300_000 + i);
            var rShare = Rng.For(o.Seed, Category.Mutate, 400_000 + i);
            var rV1 = Rng.For(o.Seed, Category.Mutate, 500_000 + i);
            var shared = new HashSet<int>(); var v1only = new HashSet<int>(); var v2only = new HashSet<int>();
            for (int line = 1; line <= L; line += 2)           // ODD lines only ⇒ even lines are stable anchors
            {
                if (rV2.Next(10000) < dth) { if (rShare.Next(10000) < fth) shared.Add(line); else v2only.Add(line); }
                else if (rV1.Next(10000) < dth) v1only.Add(line);
            }

            var v1Edit = new HashSet<int>(shared); v1Edit.UnionWith(v1only);
            var v2Edit = new HashSet<int>(shared); v2Edit.UnionWith(v2only);
            AppendToLines(AbsOf(v1Root, rel), v1Edit, n => $" /*v1:{i}:{n}*/");
            AppendToLines(AbsOf(v2Root, rel), v2Edit, n => $" /*v2:{i}:{n}*/");

            var (oldSha, oldSize) = HashFile(AbsOf(corpus, rel));
            if (v1Edit.Count > 0)
            {
                var (ns, nz) = HashFile(AbsOf(v1Root, rel));
                var df = new DiffFile { Path = rel, Reason = "content", OldSha = oldSha, NewSha = ns, OldSize = oldSize, NewSize = nz };
                df.Hunks.AddRange(Coalesce(v1Edit.OrderBy(x => x).ToList()));
                v1Files.Add(df);
            }
            if (v2Edit.Count > 0)
            {
                var (ns, nz) = HashFile(AbsOf(v2Root, rel));
                var df = new DiffFile { Path = rel, Reason = "content", OldSha = oldSha, NewSha = ns, OldSize = oldSize, NewSize = nz };
                df.Hunks.AddRange(Coalesce(v2Edit.OrderBy(x => x).ToList()));
                v2Files.Add(df);
            }

            foreach (var line in shared.OrderBy(x => x))       // each shared odd line = a 1-line conflict region
                conflicts.Add(new Conflict(rel, line, 1, HunkOp.Replace, line, 1, HunkOp.Replace, line, 1));
            foreach (var line in v1only.OrderBy(x => x))
                clean.Add(new CleanMerge(rel, "v1", HunkOp.Replace, line, 1, line, 1));
            foreach (var line in v2only.OrderBy(x => x))
                clean.Add(new CleanMerge(rel, "v2", HunkOp.Replace, line, 1, line, 1));
        }

        string prevSha = TruthDigest.Compute(basem.Symbols);
        WriteDiffDelta(corpus, basem, o, prevSha, DiffDigest.Compute(v1Files, Array.Empty<Rename>()), v1Files, Array.Empty<Rename>(), Array.Empty<string>(), "v1");
        WriteDiffDelta(corpus, basem, o, prevSha, DiffDigest.Compute(v2Files, Array.Empty<Rename>()), v2Files, Array.Empty<Rename>(), Array.Empty<string>(), "v2");
        string conflictSha = ConflictDigest.Compute(conflicts, clean);
        WriteConflict(corpus, basem, o, conflictSha, conflicts, clean, Path.GetFileName(v1Root), Path.GetFileName(v2Root));

        Console.WriteLine($"mutate(3way): {chosen.Count} file(s), overlap={o.OverlapFraction:0.##}, seed {o.Seed} " +
                          $"→ {conflicts.Count} conflict(s), {clean.Count} clean; conflictTruthSha {conflictSha[..12]}…");
        Console.WriteLine($"  variants: {Path.GetFileName(v1Root)}/ + {Path.GetFileName(v2Root)}/");
        return 0;
    }

    /// <summary>Append <paramref name="marker"/>(line) to each 1-based line in <paramref name="lines"/>,
    /// preserving terminators (an in-place replace, line count unchanged).</summary>
    private static void AppendToLines(string abs, HashSet<int> lines, Func<int, string> marker)
    {
        var ls = SplitLines(File.ReadAllText(abs));
        var sb = new StringBuilder();
        for (int k = 0; k < ls.Count; k++)
        {
            string content = ls[k].content;
            if (lines.Contains(k + 1)) content += marker(k + 1);
            sb.Append(content).Append(ls[k].term);
        }
        File.WriteAllText(abs, sb.ToString(), Encodings.Utf8NoBom);
    }

    private static void CopyTree(string src, string dst)
    {
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, RelOf(src, dir).Replace('/', Path.DirectorySeparatorChar)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dst, RelOf(src, f).Replace('/', Path.DirectorySeparatorChar)), overwrite: true);
    }

    private static void WriteConflict(string corpus, BaseManifest basem, MutateOptions o, string conflictSha,
        IReadOnlyList<Conflict> conflicts, IReadOnlyList<CleanMerge> clean, string v1Tree, string v2Tree)
    {
        string path = $"{TrimDir(corpus)}-conflict.json";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", 1);
        w.WriteString("deltaKind", "conflict-3way");
        w.WriteNumber("baseSeed", basem.Seed);
        w.WriteNumber("editSeed", o.Seed);
        w.WriteNumber("overlapFraction", o.OverlapFraction);
        w.WriteString("baseManifestSha", basem.Sha256);
        w.WriteString("v1Tree", v1Tree);
        w.WriteString("v2Tree", v2Tree);
        w.WriteString("conflictTruthSha", conflictSha);
        w.WriteEndObject();

        w.WriteStartArray("conflicts");                        // new* coords reference the respective variant tree
        foreach (var c in conflicts)
        {
            w.WriteStartObject();
            w.WriteString("path", c.Path);
            w.WriteNumber("baseStart", c.BaseStart);
            w.WriteNumber("baseLines", c.BaseLines);
            w.WriteStartObject("v1"); w.WriteString("op", c.V1Op.Label()); w.WriteNumber("newStart", c.V1NewStart); w.WriteNumber("newLines", c.V1NewLines); w.WriteEndObject();
            w.WriteStartObject("v2"); w.WriteString("op", c.V2Op.Label()); w.WriteNumber("newStart", c.V2NewStart); w.WriteNumber("newLines", c.V2NewLines); w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("mergedClean");                      // each clean hunk's text lives in exactly one tree (side)
        foreach (var m in clean)
        {
            w.WriteStartObject();
            w.WriteString("path", m.Path);
            w.WriteString("side", m.Side);
            w.WriteString("op", m.Op.Label());
            w.WriteNumber("oldStart", m.OldStart); w.WriteNumber("oldLines", m.OldLines);
            w.WriteNumber("newStart", m.NewStart); w.WriteNumber("newLines", m.NewLines);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
        Console.WriteLine($"  {Path.GetFileName(path)}: {conflicts.Count} conflict(s), {clean.Count} clean-merge(s)");
    }

    /// <summary>SHA-256 (lowercase hex) of the raw file bytes + its size — CodeDiffer's size+hash prefilter oracle.</summary>
    private static (string sha, long size) HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long size = fs.Length;
        string sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return (sha, size);
    }

    /// <summary>Collapse a sorted ascending list of changed 1-based line numbers into coalesced replace hunks.</summary>
    private static List<Hunk> Coalesce(List<int> changedLines)
    {
        var hunks = new List<Hunk>();
        for (int i = 0; i < changedLines.Count;)
        {
            int start = changedLines[i], j = i;
            while (j + 1 < changedLines.Count && changedLines[j + 1] == changedLines[j] + 1) j++;
            int run = changedLines[j] - start + 1;
            hunks.Add(new Hunk(HunkOp.Replace, start, run, start, run));   // in-place edit: old==new coords/length
            i = j + 1;
        }
        return hunks;
    }

    /// <summary>Rel paths in the corpus matching the target population, sorted ordinally.</summary>
    private static List<string> SelectTargets(string corpus, string target, int giantMinMb)
    {
        long giantFloor = (long)giantMinMb * 1024 * 1024;
        var result = new List<string>();
        foreach (var abs in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(abs), MarkerName, StringComparison.Ordinal)) continue;
            bool match = target switch
            {
                "source"  => HasExt(abs, ".c", ".cc", ".cpp", ".cxx"),
                "headers" => HasExt(abs, ".h", ".hpp", ".hh", ".hxx"),
                "giant"   => new FileInfo(abs).Length >= giantFloor,
                "all"     => true,
                _         => false,
            };
            if (match) result.Add(RelOf(corpus, abs));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Rewrite a normal file appending <paramref name="suffix"/> to ~density of its lines — a real textual
    /// change a diff tool sees, preserving the tokens already on each line (so def sites and the symbol table
    /// stay put). Line terminators (LF/CRLF) and a missing final newline are preserved EXACTLY, so a rewrite
    /// that marks zero lines is a byte-for-byte no-op (caught by the honesty guard) rather than a spurious
    /// EOL/trailing-newline change. Returns the 1-based changed line numbers, ascending.
    /// </summary>
    private static List<int> AppendPerLine(string path, double density, int seed, int fileIndex, Func<long, string> suffix)
    {
        int threshold = (int)Math.Round(Math.Clamp(density, 0, 1) * 10000);
        var rng = Rng.For(seed, Category.Mutate, 100_000 + fileIndex);   // distinct stream per chosen file
        var changed = new List<int>();
        string text = File.ReadAllText(path);                            // normal files are < giantFloor
        var sb = new StringBuilder(text.Length + 64);
        int i = 0; long lineNo = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            string content = text[start..i];
            string term = "";                                            // preserve this line's exact terminator
            if (i < text.Length)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { term = "\r\n"; i += 2; }
                else { term = text[i].ToString(); i++; }
            }
            if (rng.Next(10000) < threshold) { content += suffix(lineNo); changed.Add((int)(lineNo + 1)); }
            sb.Append(content).Append(term);
            lineNo++;
        }
        File.WriteAllText(path, sb.ToString(), Encodings.Utf8NoBom);
        return changed;
    }

    /// <summary>
    /// Stream a giant file and mark every <paramref name="stride"/>-th line (0-based line k*stride ⇒ 1-based
    /// line 1+k*stride) — the exact pattern a <see cref="RunHunk"/> with rangeStart=1 reproduces. Keeps the
    /// 1 GB headers off the heap. Returns the total line count (the run-rule's inclusive rangeEnd).
    /// </summary>
    private static long ModifyGiant(string path, int stride, int fileIndex)
    {
        string tmp = path + ".mut.tmp";
        long lineNo = 0;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        using (var writer = new StreamWriter(tmp, false, Encodings.Utf8NoBom))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (lineNo % stride == 0) line += $" /*mut:{fileIndex}:{lineNo}*/";
                writer.Write(line);
                writer.Write('\n');
                lineNo++;
            }
        }
        File.Delete(path);
        File.Move(tmp, path);
        return lineNo;
    }

    // ---- edit-kind dispatch (step 2: reason classes) --------------------------------------------------

    /// <summary>The CLI edit mechanism mapped to the CodeDiffer `reason` classification it produces.</summary>
    private static string ReasonOf(string kind) => kind switch
    {
        "content" or "line-insert" or "line-delete" => "content",
        "eol"        => "eol",
        "whitespace" => "whitespace",
        "encoding"   => "encoding",
        "binary"     => "binary",
        "metadata"   => "metadata",
        _            => "content",
    };

    private readonly record struct EditResult(List<Hunk> Hunks, RunHunk? Run);
    private static EditResult NoHunks() => new(new List<Hunk>(), null);

    /// <summary>Apply one file's edit per the chosen kind; hash before/after; return its truth record (or null
    /// if the edit produced no actual byte change — the honesty guard).</summary>
    private static DiffFile? BuildDiffFile(string abs, string rel, string reason, MutateOptions o, int idx,
        long giantFloor, int stride)
    {
        var (oldSha, oldSize) = HashFile(abs);

        if (o.Kind == "metadata")
        {
            // Content-identical: NTFS has no POSIX mode, so this is a SYNTHETIC mode flip — oldSha == newSha,
            // only the metadata field differs. Proves CodeDiffer classifies metadata_only (not "modified").
            return new DiffFile
            {
                Path = rel, Reason = reason, OldSha = oldSha, NewSha = oldSha, OldSize = oldSize, NewSize = oldSize,
                Metadata = ("mode", "100644", "100755"),
            };
        }

        EditResult r = o.Kind switch
        {
            "content"     => oldSize >= giantFloor ? GiantContentEdit(abs, stride, idx)
                                                   : new EditResult(Coalesce(AppendPerLine(abs, o.EditDensity, o.Seed, idx, n => $" /*mut:{idx}:{n}*/")), null),
            "whitespace"  => new EditResult(Coalesce(AppendPerLine(abs, o.EditDensity, o.Seed, idx, _ => "   ")), null),
            "line-insert" => LineInsertEdit(abs, o.EditDensity, o.Seed, idx),
            "line-delete" => LineDeleteEdit(abs, o.EditDensity, o.Seed, idx),
            "eol"         => EolEdit(abs),
            "encoding"    => EncodingEdit(abs),
            "binary"      => BinaryEdit(abs, o.EditDensity, o.Seed, idx),
            _             => throw new ArgException($"unsupported --edit-kind '{o.Kind}'"),
        };

        var (newSha, newSize) = HashFile(abs);
        if (newSha == oldSha) return null;                     // honesty: no actual change ⇒ not recorded

        var df = new DiffFile
        {
            Path = rel, Reason = reason, OldSha = oldSha, NewSha = newSha, OldSize = oldSize, NewSize = newSize,
            Run = r.Run,
        };
        df.Hunks.AddRange(r.Hunks);
        return df;
    }

    private static EditResult GiantContentEdit(string abs, int stride, int idx)
    {
        long lines = ModifyGiant(abs, stride, idx);
        return new EditResult(new List<Hunk>(), new RunHunk(HunkOp.Replace, stride, 1, (int)Math.Min(lines, int.MaxValue), 1));
    }

    /// <summary>Insert a fixed block of filler lines before ~density of the lines → insert hunks that renumber
    /// everything after them (delta carries NEW coords). Inserted lines use LF (corpus is LF).</summary>
    private static EditResult LineInsertEdit(string abs, double density, int seed, int idx)
    {
        const int K = 2;
        var lines = SplitLines(File.ReadAllText(abs));
        var sel = Selection(lines.Count, density, seed, idx);
        var hunks = new List<Hunk>();
        var sb = new StringBuilder();
        int newLine = 0;
        for (int k = 0; k < lines.Count; k++)
        {
            if (sel[k])
            {
                int newStart = newLine + 1;
                for (int j = 0; j < K; j++) { sb.Append($"// mutate inserted {idx}:{k + 1}:{j}").Append('\n'); newLine++; }
                hunks.Add(new Hunk(HunkOp.Insert, k + 1, 0, newStart, K));
            }
            sb.Append(lines[k].content).Append(lines[k].term);
            newLine++;
        }
        File.WriteAllText(abs, sb.ToString(), Encodings.Utf8NoBom);
        return new EditResult(hunks, null);
    }

    /// <summary>Delete ~density of the lines (consecutive runs coalesced) → delete hunks that renumber what
    /// follows.</summary>
    private static EditResult LineDeleteEdit(string abs, double density, int seed, int idx)
    {
        var lines = SplitLines(File.ReadAllText(abs));
        var sel = Selection(lines.Count, density, seed, idx);
        var hunks = new List<Hunk>();
        var sb = new StringBuilder();
        int newLine = 0, k = 0;
        while (k < lines.Count)
        {
            if (sel[k])
            {
                int runStart = k + 1, j = k;
                while (j < lines.Count && sel[j]) j++;
                hunks.Add(new Hunk(HunkOp.Delete, runStart, j - k, newLine + 1, 0));
                k = j;                                          // skip emitting the deleted lines
            }
            else
            {
                sb.Append(lines[k].content).Append(lines[k].term);
                newLine++; k++;
            }
        }
        File.WriteAllText(abs, sb.ToString(), Encodings.Utf8NoBom);
        return new EditResult(hunks, null);
    }

    /// <summary>Flip every line terminator LF↔CRLF: bytes differ, ZERO textual hunks (the eol honesty case).</summary>
    private static EditResult EolEdit(string abs)
    {
        var sb = new StringBuilder();
        foreach (var (content, term) in SplitLines(File.ReadAllText(abs)))
            sb.Append(content).Append(term switch { "\n" => "\r\n", "\r\n" => "\n", "\r" => "\n", _ => term });
        File.WriteAllText(abs, sb.ToString(), Encodings.Utf8NoBom);
        return NoHunks();
    }

    /// <summary>Re-encode UTF-8(no BOM) → UTF-16LE(with BOM): bytes differ, decoded text identical, ZERO hunks.</summary>
    private static EditResult EncodingEdit(string abs)
    {
        string text = File.ReadAllText(abs);
        File.WriteAllText(abs, text, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return NoHunks();
    }

    /// <summary>Flip ~density of the raw bytes: a real binary change tracked by shas+sizes only (no text hunks).</summary>
    private static EditResult BinaryEdit(string abs, double density, int seed, int idx)
    {
        byte[] bytes = File.ReadAllBytes(abs);
        int threshold = (int)Math.Round(Math.Clamp(density, 0, 1) * 10000);
        var rng = Rng.For(seed, Category.Mutate, 100_000 + idx);
        for (int k = 0; k < bytes.Length; k++) if (rng.Next(10000) < threshold) bytes[k] ^= 0x5A;
        File.WriteAllBytes(abs, bytes);
        return NoHunks();
    }

    /// <summary>Split text into (content, terminator) pairs, preserving LF/CRLF and a missing final newline.</summary>
    private static List<(string content, string term)> SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            string content = text[start..i];
            string term = "";
            if (i < text.Length)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { term = "\r\n"; i += 2; }
                else { term = text[i].ToString(); i++; }
            }
            lines.Add((content, term));
        }
        return lines;
    }

    /// <summary>Deterministic per-line selection matching AppendPerLine's rng pattern (one draw per line, in order).</summary>
    private static bool[] Selection(int count, double density, int seed, int fileIndex)
    {
        int threshold = (int)Math.Round(Math.Clamp(density, 0, 1) * 10000);
        var rng = Rng.For(seed, Category.Mutate, 100_000 + fileIndex);
        var sel = new bool[count];
        for (int k = 0; k < count; k++) sel[k] = rng.Next(10000) < threshold;
        return sel;
    }

    private static void ShufflePaths(List<string> list, int seed, int stream)
    {
        var rng = Rng.For(seed, Category.Mutate, stream);
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
    }

    private static bool HasExt(string path, params string[] exts)
    {
        string ext = Path.GetExtension(path);
        foreach (var e in exts) if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- planning -------------------------------------------------------------------------------------

    private static List<Edit> BuildPlan(MutateOptions o, string corpus, BaseManifest basem)
    {
        // Removable/shiftable source files: func_i defs (i>=1 so removal leaves a dangling caller ripple).
        var srcPool = basem.Symbols
            .Where(kv => kv.Key.StartsWith("func_", StringComparison.Ordinal) && int.TryParse(kv.Key[5..], out int n) && n >= 1)
            .Select(kv => (name: kv.Key, site: SplitSite(kv.Value.Def)))
            .Select(x => new Edit(EditType.Remove, AbsOf(corpus, x.site.path), x.name, x.site.line))
            .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        Shuffle(srcPool, o.Seed, 1);

        var growPool = ScanRel(corpus, "hdr_*.h");
        var shrinkPool = ScanRel(corpus, "mut_shrink_*.h");
        var restreamPool = ScanRel(corpus, "mut_restream_*.h");

        var types = new List<EditType> { EditType.Remove, EditType.LineShift, EditType.Add };
        if (growPool.Count > 0) types.Add(EditType.Grow);
        if (shrinkPool.Count > 0) types.Add(EditType.Shrink);
        if (o.Restream && restreamPool.Count > 0) types.Add(EditType.Restream);

        var plan = new List<Edit>(o.Edits);
        int srcCur = 0, growCur = 0, shrinkCur = 0, restreamCur = 0;
        for (int k = 0; k < o.Edits; k++)
        {
            switch (types[k % types.Count])
            {
                case EditType.Remove:
                    plan.Add(Need(srcPool, ref srcCur, "remove") with { Type = EditType.Remove }); break;
                case EditType.LineShift:
                    plan.Add(Need(srcPool, ref srcCur, "line-shift") with { Type = EditType.LineShift }); break;
                case EditType.Add:
                    plan.Add(new Edit(EditType.Add, Path.Combine(corpus, $"mut_add_{k}.c"), $"mut_add_{k}", 2)); break;
                case EditType.Grow:
                    plan.Add(new Edit(EditType.Grow, AbsOf(corpus, growPool[growCur++ % growPool.Count]), null, 0)); break;
                case EditType.Shrink:
                    plan.Add(new Edit(EditType.Shrink, AbsOf(corpus, shrinkPool[shrinkCur++]), null, 0)); break;
                case EditType.Restream:
                    plan.Add(new Edit(EditType.Restream, AbsOf(corpus, restreamPool[restreamCur++]), null, 0)); break;
            }
        }
        return plan;

        static Edit Need(List<Edit> pool, ref int cur, string what)
        {
            if (cur >= pool.Count) throw new ArgException($"not enough source files for a '{what}' edit; generate a larger corpus (--cfiles) or fewer --edits.");
            return pool[cur++];
        }
    }

    // ---- applying one edit (disk + table) -------------------------------------------------------------

    private static FileOps ApplyEdit(Edit e, string corpus, Dictionary<string, SymbolEntry> work, bool touchDisk)
    {
        var ops = new FileOps();
        string rel = RelOf(corpus, e.Path);
        switch (e.Type)
        {
            case EditType.Remove:
            {
                // table: tombstone symbols defined here; drop ref-sites located here from other symbols.
                foreach (var name in work.Keys.ToList())
                    if (SplitSite(work[name].Def).path == rel) work.Remove(name);
                foreach (var s in work.Values)
                    s.Refs.RemoveAll(r => SplitSite(r).path == rel);
                if (touchDisk)
                {
                    foreach (var f in SiblingSet(e.Path)) if (File.Exists(f)) File.Delete(f);
                    foreach (var f in SiblingSet(e.Path)) ops.Removed.Add(RelOf(corpus, f));
                }
                else ops.Removed.Add(rel);
                break;
            }
            case EditType.LineShift:
            {
                int insertPos = e.DefLine;
                foreach (var s in work.Values)
                {
                    s.Def = ShiftSite(s.Def, rel, insertPos, LineShiftLines);
                    for (int i = 0; i < s.Refs.Count; i++) s.Refs[i] = ShiftSite(s.Refs[i], rel, insertPos, LineShiftLines);
                }
                if (touchDisk)
                {
                    var lines = new List<string>(File.ReadAllLines(e.Path));
                    int at = Math.Clamp(insertPos - 1, 0, lines.Count);
                    var pad = Enumerable.Range(0, LineShiftLines).Select(i => $"// mutate line-shift filler {i}");
                    lines.InsertRange(at, pad);
                    File.WriteAllText(e.Path, string.Join('\n', lines), Encodings.Utf8NoBom);
                }
                ops.Modified.Add(rel);
                break;
            }
            case EditType.Add:
            {
                string body = $"#include <stddef.h>\nint {e.Symbol}(int x) {{ return x + 1; }}\n"; // def on line 2
                work[e.Symbol!] = new SymbolEntry { Def = $"{rel}:{e.DefLine}" };
                if (touchDisk) File.WriteAllText(e.Path, body, Encodings.Utf8NoBom);
                ops.Added.Add(rel);
                break;
            }
            case EditType.Grow:
                if (touchDisk) GrowTo(e.Path, GrowTargetBytes);
                ops.Modified.Add(rel);
                break;
            case EditType.Shrink:
                if (touchDisk) OverwriteTo(e.Path, ShrinkTargetBytes);
                ops.Modified.Add(rel);
                break;
            case EditType.Restream:
                if (touchDisk) OverwriteTo(e.Path, RestreamTargetBytes);
                ops.Modified.Add(rel);
                break;
        }
        return ops;
    }

    // ---- delta emission -------------------------------------------------------------------------------

    private static void WriteDelta(string corpus, BaseManifest basem, MutateOptions o, int? step, string prevSha,
        Dictionary<string, SymbolEntry> before, Dictionary<string, SymbolEntry> after, FileOps ops)
    {
        string path = step is { } k ? $"{TrimDir(corpus)}-delta-{k}.json" : $"{TrimDir(corpus)}-delta.json";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", 1);
        w.WriteNumber("baseSeed", basem.Seed);
        w.WriteNumber("editSeed", o.Seed);
        if (step is { } s) w.WriteNumber("step", s);
        w.WriteString("baseManifestSha", basem.Sha256);
        w.WriteString("prevTruthSha", prevSha);
        w.WriteEndObject();

        w.WriteStartObject("fileOps");
        WriteArray(w, "added", ops.Added);
        WriteArray(w, "removed", ops.Removed);
        WriteArray(w, "modified", ops.Modified);
        if (ops.Renamed.Count > 0)
        {
            w.WriteStartArray("renamed");
            foreach (var (from, to) in ops.Renamed) { w.WriteStartObject(); w.WriteString("from", from); w.WriteString("to", to); w.WriteEndObject(); }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        // symbols overlay = the diff before→after (changed/added full entry, or "TOMBSTONE").
        w.WriteStartObject("symbols");
        int changed = 0, tombs = 0;
        foreach (var name in after.Keys.Concat(before.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal))
        {
            bool inA = after.TryGetValue(name, out var a);
            bool inB = before.TryGetValue(name, out var b);
            if (inA && (!inB || !Equal(a!, b!)))
            {
                w.WriteStartObject(name);
                w.WriteString("def", a!.Def);
                WriteArray(w, "refs", a.Refs);
                WriteArray(w, "edges", a.Edges);
                if (inB) { var removed = b!.Refs.Except(a.Refs).ToList(); if (removed.Count > 0) WriteArray(w, "removedSites", removed); }
                if (a.ExpectedMiss) w.WriteBoolean("expectedMiss", true);
                w.WriteEndObject();
                changed++;
            }
            else if (inB && !inA) { w.WriteString(name, "TOMBSTONE"); tombs++; }
        }
        w.WriteEndObject();

        w.WriteEndObject();
        w.Flush();
        Console.WriteLine($"  {Path.GetFileName(path)}: {changed} changed, {tombs} tombstoned, " +
                          $"+{ops.Added.Count}/-{ops.Removed.Count}/~{ops.Modified.Count} files");
    }

    // ---- diff-delta emission (bulk / diff-oracle mode) ------------------------------------------------

    private static void WriteDiffDelta(string corpus, BaseManifest basem, MutateOptions o, string prevSha,
        string diffSha, IReadOnlyList<DiffFile> files, IReadOnlyList<Rename> renames, IReadOnlyList<string> added,
        string? variant = null)
    {
        string path = variant is null ? $"{TrimDir(corpus)}-delta.json" : $"{TrimDir(corpus)}-delta-{variant}.json";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", 1);
        w.WriteString("deltaKind", "diff");            // distinguishes a diff-delta from a symbol-overlay delta
        w.WriteString("editKind", o.Kind);
        w.WriteNumber("baseSeed", basem.Seed);
        w.WriteNumber("editSeed", o.Seed);
        w.WriteString("target", o.Target);
        w.WriteNumber("editDensity", o.EditDensity);
        w.WriteString("baseManifestSha", basem.Sha256);
        w.WriteString("prevTruthSha", prevSha);
        w.WriteString("diffTruthSha", diffSha);
        w.WriteEndObject();

        w.WriteStartObject("fileOps");
        w.WriteStartArray("added");                    // decoy near-duplicate ADDs (rename false-positive traps)
        foreach (var a in added) w.WriteStringValue(a);
        w.WriteEndArray();
        w.WriteStartArray("removed"); w.WriteEndArray();
        w.WriteStartArray("modified");
        foreach (var f in files)
        {
            w.WriteStartObject();
            w.WriteString("path", f.Path);
            w.WriteString("reason", f.Reason);
            w.WriteString("oldSha", f.OldSha);
            w.WriteString("newSha", f.NewSha);
            w.WriteNumber("oldSize", f.OldSize);
            w.WriteNumber("newSize", f.NewSize);
            w.WriteStartArray("hunks");
            foreach (var h in f.Hunks)
            {
                w.WriteStartObject();
                w.WriteString("op", h.Op.Label());
                w.WriteNumber("oldStart", h.OldStart); w.WriteNumber("oldLines", h.OldLines);
                w.WriteNumber("newStart", h.NewStart); w.WriteNumber("newLines", h.NewLines);
                w.WriteEndObject();
            }
            if (f.Run is { } r)
            {
                w.WriteStartObject();
                w.WriteString("op", r.Op.Label());
                w.WriteString("kind", "run");
                w.WriteNumber("stride", r.Stride);
                w.WriteNumber("rangeStart", r.RangeStart);
                w.WriteNumber("rangeEnd", r.RangeEnd);
                w.WriteNumber("perHunk", r.PerHunk);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (f.Metadata is { } meta)                        // reason=metadata: content-identical, opt-in diff track
            {
                w.WriteStartObject("metadata");
                w.WriteString("field", meta.Field);
                w.WriteString("old", meta.Old);
                w.WriteString("new", meta.New);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("renamed");                      // {from,to,similarityMilli}; rename+edit hunks live
        foreach (var r in renames)                         // in `modified` keyed by the `to` path
        {
            w.WriteStartObject();
            w.WriteString("from", r.From);
            w.WriteString("to", r.To);
            w.WriteNumber("similarityMilli", r.SimilarityMilli);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject("symbols"); w.WriteEndObject(); // content-only edits ⇒ empty symbol overlay
        w.WriteEndObject();
        w.Flush();
        Console.WriteLine($"  {Path.GetFileName(path)}: {files.Count} modified, {renames.Count} renamed, {added.Count} added");
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static Dictionary<string, SymbolEntry> Clone(Dictionary<string, SymbolEntry> src)
    {
        var d = new Dictionary<string, SymbolEntry>(src.Count, StringComparer.Ordinal);
        foreach (var (k, v) in src)
            d[k] = new SymbolEntry
            {
                Def = v.Def,
                ExpectedMiss = v.ExpectedMiss,
                UnreachableRefs = v.UnreachableRefs is null ? null : new List<string>(v.UnreachableRefs),
            };
        foreach (var (k, v) in src) { d[k].Refs.AddRange(v.Refs); d[k].Edges.AddRange(v.Edges); }
        return d;
    }

    private static bool Equal(SymbolEntry a, SymbolEntry b) =>
        a.Def == b.Def && a.ExpectedMiss == b.ExpectedMiss &&
        a.Refs.Count == b.Refs.Count && !a.Refs.Except(b.Refs).Any() &&
        a.Edges.Count == b.Edges.Count && !a.Edges.Except(b.Edges).Any();

    private static (string path, int line) SplitSite(string site)
    {
        int c = site.LastIndexOf(':');
        return (site[..c], int.Parse(site[(c + 1)..]));
    }

    private static string ShiftSite(string site, string relF, int insertPos, int d)
    {
        var (p, line) = SplitSite(site);
        return (p == relF && line >= insertPos) ? $"{p}:{line + d}" : site;
    }

    private static void Merge(FileOps into, FileOps from)
    {
        into.Added.AddRange(from.Added); into.Removed.AddRange(from.Removed);
        into.Modified.AddRange(from.Modified); into.Renamed.AddRange(from.Renamed);
    }

    private static void Shuffle(List<Edit> list, int seed, int stream)
    {
        var rng = Rng.For(seed, Category.Mutate, stream);
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
    }

    private static List<string> ScanRel(string corpus, string pattern)
    {
        var list = Directory.EnumerateFiles(corpus, pattern, SearchOption.AllDirectories)
            .Select(f => RelOf(corpus, f)).ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static IEnumerable<string> SiblingSet(string cPath)
    {
        yield return cPath;
        string stem = cPath[..^Path.GetExtension(cPath).Length];
        foreach (var ext in new[] { ".o", ".lst", ".bak" }) yield return stem + ext;
    }

    private static void GrowTo(string path, long target)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20);
        var block = Encoding.ASCII.GetBytes(new string('/', 4094) + "\n"); // benign comment line
        while (fs.Length < target) fs.Write(block, 0, block.Length);
    }

    private static void OverwriteTo(string path, long target)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var block = Encoding.ASCII.GetBytes(new string('/', 4094) + "\n");
        while (fs.Length < target) fs.Write(block, 0, block.Length);
    }

    private static void WriteArray(Utf8JsonWriter w, string name, List<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }

    private static string AbsOf(string corpus, string rel) => Path.GetFullPath(Path.Combine(corpus, rel.Replace('/', Path.DirectorySeparatorChar)));
    private static string RelOf(string corpus, string abs) => PathUtil.Rel(corpus, abs);
    private static string TrimDir(string corpus) => corpus.TrimEnd(Path.DirectorySeparatorChar);

    private static string DefaultManifestPath(string corpus)
    {
        string parent = Path.GetDirectoryName(TrimDir(corpus)) ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(TrimDir(corpus));
        return Path.Combine(parent, $"{leaf}-manifest.json");
    }
}
