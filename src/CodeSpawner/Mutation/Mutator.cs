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

        Console.WriteLine($"mutate(bulk): target={o.Target} files={chosen.Count}/{pool.Count} " +
                          $"density={o.EditDensity:0.###} seed {o.Seed} base {basem.Sha256[..12]}…");

        long giantFloor = (long)o.GiantMinMb * 1024 * 1024;
        int stride = Math.Max(1, (int)Math.Round(1.0 / Math.Clamp(o.EditDensity, 1e-6, 1.0)));
        var files = new List<DiffFile>();
        long touched = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            string abs = AbsOf(corpus, chosen[i]);
            var (oldSha, oldSize) = HashFile(abs);
            DiffFile df;
            if (oldSize >= giantFloor)
            {
                // Giant file: deterministic stride → a single compact run-rule instead of a huge hunk list.
                long lines = ModifyGiant(abs, stride, i);
                var (newSha, newSize) = HashFile(abs);
                touched += (lines + stride - 1) / stride;
                df = new DiffFile
                {
                    Path = chosen[i], OldSha = oldSha, NewSha = newSha, OldSize = oldSize, NewSize = newSize,
                    Run = new RunHunk(HunkOp.Replace, stride, 1, (int)Math.Min(lines, int.MaxValue), 1),
                };
            }
            else
            {
                // Normal file: random per-line selection; coalesce contiguous changed lines into replace hunks.
                var changed = ModifyContent(abs, o.EditDensity, o.Seed, i);
                var (newSha, newSize) = HashFile(abs);
                if (newSha == oldSha) continue;                // honesty: never record a file with no actual change
                touched += changed.Count;
                df = new DiffFile
                {
                    Path = chosen[i], OldSha = oldSha, NewSha = newSha, OldSize = oldSize, NewSize = newSize,
                };
                df.Hunks.AddRange(Coalesce(changed));
            }
            files.Add(df);
        }

        // Content-only edits preserve def sites, so the symbol table (and prevTruthSha) is unchanged: the 2-way
        // delta carries file-level + hunk-level truth and its own _meta.diffTruthSha (docs/diff-delta-design.md).
        string prevSha = TruthDigest.Compute(basem.Symbols);
        string diffSha = DiffDigest.Compute(files, Array.Empty<Rename>());
        WriteDiffDelta(corpus, basem, o, prevSha, diffSha, files);
        Console.WriteLine($"  ~{touched} line(s) changed across {files.Count} file(s); diffTruthSha {diffSha[..12]}…");
        return 0;
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
    /// Stream a normal file line-by-line and append a deterministic marker comment to ~density of its lines —
    /// a real textual change a diff tool sees, preserving the tokens already on each line (so def sites and the
    /// symbol table stay put). Returns the 1-based line numbers changed, ascending (for coalescing into hunks).
    /// </summary>
    private static List<int> ModifyContent(string path, double density, int seed, int fileIndex)
    {
        int threshold = (int)Math.Round(Math.Clamp(density, 0, 1) * 10000);
        var rng = Rng.For(seed, Category.Mutate, 100_000 + fileIndex);   // distinct stream per chosen file
        var changed = new List<int>();
        string tmp = path + ".mut.tmp";
        long lineNo = 0;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        using (var writer = new StreamWriter(tmp, false, Encodings.Utf8NoBom))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (rng.Next(10000) < threshold) { line += $" /*mut:{fileIndex}:{lineNo}*/"; changed.Add((int)(lineNo + 1)); }
                writer.Write(line);
                writer.Write('\n');
                lineNo++;
            }
        }
        File.Delete(path);
        File.Move(tmp, path);
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
        string diffSha, IReadOnlyList<DiffFile> files)
    {
        string path = $"{TrimDir(corpus)}-delta.json";
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", 1);
        w.WriteString("deltaKind", "diff");            // distinguishes a diff-delta from a symbol-overlay delta
        w.WriteNumber("baseSeed", basem.Seed);
        w.WriteNumber("editSeed", o.Seed);
        w.WriteString("target", o.Target);
        w.WriteNumber("editDensity", o.EditDensity);
        w.WriteString("baseManifestSha", basem.Sha256);
        w.WriteString("prevTruthSha", prevSha);
        w.WriteString("diffTruthSha", diffSha);
        w.WriteEndObject();

        w.WriteStartObject("fileOps");
        w.WriteStartArray("added"); w.WriteEndArray();
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
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("renamed"); w.WriteEndArray();   // populated in step 3 (rename/move)
        w.WriteEndObject();

        w.WriteStartObject("symbols"); w.WriteEndObject(); // content-only edits ⇒ empty symbol overlay
        w.WriteEndObject();
        w.Flush();
        Console.WriteLine($"  {Path.GetFileName(path)}: ~{files.Count} file(s) modified");
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
