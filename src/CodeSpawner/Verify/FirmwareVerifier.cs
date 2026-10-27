using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpawner.Firmware;

namespace CodeSpawner.Verify;

/// <summary>
/// The manifest's <c>firmware</c> object (docs/firmware-design.md): the truth is recomputed from the tree and every
/// count, file list and truth-list hash must match; the truth lists on disk must hash to what the manifest says.
/// <c>--reference-carve DIR</c> also writes the carve the truth describes (every carved define that is not must-keep
/// removed), so the truth can be compiled.
/// </summary>
internal static class FirmwareVerifier
{
    public static int Check(string corpus, JsonElement fw, bool wantWork, string? referenceCarve, TextWriter err, ref int ok)
    {
        var n = new int[1];
        int fail = Core(corpus, fw, wantWork, referenceCarve, err, n);
        ok += n[0];
        return fail;
    }

    private static int Core(string corpus, JsonElement fw, bool wantWork, string? referenceCarve, TextWriter err, int[] okCount)
    {
        if (fw.ValueKind != JsonValueKind.Object) { err.WriteLine("FAIL firmware: not an object"); return 1; }
        FwTruth t;
        try { t = FwSpec.Compute(corpus, null, checkAlphabet: true); }
        catch (FwDbException ex) { err.WriteLine($"FAIL firmware compileDb: {ex.Message}"); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { err.WriteLine($"FAIL firmware: the tree cannot be read ({ex.Message})"); return 1; }
        // the names' spelling says their class (Z..Z must-drop, Y..Y open, else must-keep): a name the truth puts
        // elsewhere is in a corpus the generator would have refused to write
        foreach (var a in t.AlphabetErrors) err.WriteLine($"FAIL firmware alphabet: {a}");
        int fail = t.AlphabetErrors.Count;
        if (fail == 0) okCount[0]++;

        void Eq(string what, string? want, string got)
        {
            if (want == got) okCount[0]++;
            else { err.WriteLine($"FAIL firmware {what}: manifest says {want ?? "(missing)"}, the tree gives {got}"); fail++; }
        }
        string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        JsonElement Obj(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
        // a list, element by element: ["a, b"] is not ["a", "b"], and a dense entry's edge must be a bool
        void ListEq(string what, JsonElement e, string k, IReadOnlyList<string> got, Func<JsonElement, string?>? item = null)
        {
            if (!(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array))
            { err.WriteLine($"FAIL firmware {what}: manifest has no list, the tree gives {got.Count} entries"); fail++; return; }
            var want = v.EnumerateArray().Select(x => item is null ? (x.ValueKind == JsonValueKind.String ? x.GetString() : null) : item(x)).ToList();
            if (want.Count != got.Count) { err.WriteLine($"FAIL firmware {what}: manifest lists {want.Count}, the tree gives {got.Count}"); fail++; return; }
            for (int i = 0; i < got.Count; i++)
                if (want[i] != got[i]) { err.WriteLine($"FAIL firmware {what}[{i}]: manifest says {want[i] ?? "(not a string)"}, the tree gives {got[i]}"); fail++; return; }
            okCount[0]++;
        }

        Eq("root", Str(fw, "root"), FwSpec.FwDir);
        Eq("compileDb", Str(fw, "compileDb"), FwSpec.DbPath);
        Eq("maxParseBytes", Num(fw, "maxParseBytes"), FwSpec.MaxParseBytes.ToString());
        Eq("treeSha256", Str(fw, "treeSha256"), t.TreeSha);
        var c = Obj(fw, "counts");
        foreach (var (k, v) in new (string, long)[]
        {
            ("bigHeadersKept", t.BigHeadersKept), ("denseHeaders", t.DenseHeaders), ("wholeIncludedFromOutside", t.WholeIncludedFromOutside),
            ("pasteMacros", t.PasteMacros), ("forwardingMacros", t.ForwardingMacros), ("totalDefineLines", t.TotalDefineLines),
            ("mustKeep", t.MustKeepCount), ("mustDrop", t.MustDropCount), ("open", t.OpenCount), ("bytesBefore", t.BytesBefore),
            ("definesDroppedMin", t.DefinesDroppedMin), ("definesDroppedMax", t.DefinesDroppedMax),
            ("bytesAfterMin", t.BytesAfterMin), ("bytesAfterMax", t.BytesAfterMax),
        })
            Eq($"counts.{k}", Num(c, k), v.ToString());
        ListEq("bigHeaders", fw, "bigHeaders", t.BigHeaders);
        ListEq("denseHeaders", fw, "denseHeaders", t.Dense.Select(d => $"{d.Path} edge={(d.Edge ? "true" : "false")}").ToList(),
            x => Str(x, "path") is { } p && x.TryGetProperty("edge", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? $"{p} edge={(g.ValueKind == JsonValueKind.True ? "true" : "false")}" : null);
        ListEq("wholeIncludedFromOutside", fw, "wholeIncludedFromOutside", t.WholeFromOutside);

        // the emitter's own counts, recounted from the tree: units (fw/src/m*/u*.c), traps (fw/src/traps/trap_*.h) and
        // cases (the rows of cases.tsv); families leaves no trace of its own in the tree and is checked for shape only
        string fwSrc = Path.Combine(corpus, FwSpec.FwDir, "src");
        long units = Directory.Exists(fwSrc)
            ? Directory.EnumerateDirectories(fwSrc, "m*").Sum(d => Directory.EnumerateFiles(d, "u*.c").LongCount(f => Numbered(Path.GetFileNameWithoutExtension(f), "u")))
            : 0;
        string trapDir = Path.Combine(fwSrc, "traps");
        long traps = Directory.Exists(trapDir) ? Directory.EnumerateFiles(trapDir, "trap_*.h").LongCount() : 0;
        Eq("counts.units", Num(c, "units"), units.ToString());
        Eq("counts.traps", Num(c, "traps"), traps.ToString());
        if (Num(c, "families") is { } fam && long.TryParse(fam, out long famN) && famN >= 0) okCount[0]++;
        else { err.WriteLine($"FAIL firmware counts.families: {Num(c, "families") ?? "(missing)"} is not a count"); fail++; }

        var paths = new Generation.Containment(corpus);
        if (Str(fw, "cases") is { } cases && paths.Resolve(cases) is { } cp && File.Exists(cp))
        {
            var (rows, bad) = CaseRows(cp, corpus);
            if (bad is not null) { err.WriteLine($"FAIL firmware cases ({cases}): {bad}"); fail++; }
            else okCount[0]++;
            Eq("counts.cases", Num(c, "cases"), rows.ToString());
        }
        else { err.WriteLine($"FAIL firmware cases: {Str(fw, "cases") ?? "(missing)"} is not a file of the corpus"); fail++; }
        if (Obj(fw, "hostile") is { ValueKind: JsonValueKind.Array } hostile)
            foreach (var h in hostile.EnumerateArray())
            {
                if (Str(h, "path") is { } hp && paths.Resolve(hp) is { } hf && File.Exists(hf) && Str(h, "kind") is { Length: > 0 }) okCount[0]++;
                else { err.WriteLine($"FAIL firmware hostile: {Str(h, "path") ?? "(no path)"} is not a file of the corpus with a kind"); fail++; }
            }
        else { err.WriteLine("FAIL firmware hostile: missing (an array of { path, kind })"); fail++; }

        var builds = Obj(fw, "builds");
        foreach (var b in new[] { "main", "union" })
        {
            var lists = Obj(builds, b);
            foreach (var (k, l) in new[] { ("mustKeepDefines", t.MustKeepDefines), ("mustDropDefines", t.MustDropDefines),
                         ("openDefines", t.OpenDefines), ("mustKeepFunctions", t.MustKeepFunctions), ("mustKeepFiles", t.MustKeepFiles) })
            {
                var e = Obj(lists, k);
                Eq($"builds.{b}.{k}.sha256", Str(e, "sha256"), l.Sha256);
                Eq($"builds.{b}.{k}.count", Num(e, "count"), l.Count.ToString());
                string? rel = Str(e, "path");
                string? full = rel is null ? null : paths.Resolve(rel);
                if (full is null || !File.Exists(full)) { err.WriteLine($"FAIL firmware builds.{b}.{k}: {rel ?? "(no path)"} is not a file of the corpus"); fail++; continue; }
                Eq($"builds.{b}.{k} ({rel}) on disk", l.Sha256, ListSha(full));
            }
        }

        // the work carve: its class counts, recounted from the main build's lists by the names' spelling
        bool workDir = Directory.Exists(Path.Combine(corpus, FwSpec.FwDir, FwWork.Dir));
        if (Obj(fw, "work") is not { ValueKind: JsonValueKind.Object } && (wantWork || workDir))
        { err.WriteLine($"FAIL firmware work: missing, but {(wantWork ? "_meta.gen.fwWork is true" : $"{FwSpec.FwDir}/{FwWork.Dir}/ exists")}"); fail++; }
        if (Obj(fw, "work") is { ValueKind: JsonValueKind.Object } work)
        {
            fail += CheckWorkShape(corpus, work, err, okCount);
            var tree = Obj(work, "tree");
            var main = Obj(builds, "main");
            string? keepRel = Str(Obj(main, "mustKeepDefines"), "path"), dropRel = Str(Obj(main, "mustDropDefines"), "path"),
                openRel = Str(Obj(main, "openDefines"), "path");
            string? keepF = keepRel is null ? null : paths.Resolve(keepRel), dropF = dropRel is null ? null : paths.Resolve(dropRel),
                openF = openRel is null ? null : paths.Resolve(openRel);
            if (keepF is null || dropF is null || openF is null || !File.Exists(keepF) || !File.Exists(dropF) || !File.Exists(openF))
            { err.WriteLine("FAIL firmware work: the main build's truth lists are missing"); fail++; }
            else
            {
                var kc = WorkCounts(keepF);
                var oc = WorkCounts(openF);
                var dc = WorkCounts(dropF);
                Eq("work.tree.exactKept", Num(tree, "exactKept"), kc.Exact.ToString());
                Eq("work.tree.pasters", Num(tree, "pasters"), kc.Pasters.ToString());
                Eq("work.tree.forwarders", Num(tree, "forwarders"), kc.Forwarders.ToString());
                Eq("work.tree.appliers", Num(tree, "appliers"), kc.Appliers.ToString());
                Eq("work.tree.crossDecoys", Num(tree, "crossDecoys"), dc.Cross.ToString());
                Eq("work.tree.halfDecoys", Num(tree, "halfDecoys"), dc.Half.ToString());
                Eq("work.tree.filler", Num(tree, "filler"), dc.Filler.ToString());
                Eq("work: tree decoys and filler kept or open", "0", (kc.Cross + kc.Half + kc.Filler + oc.Cross + oc.Half + oc.Filler).ToString());
                Eq("work: tree exact names not kept", "0", (dc.Exact + oc.Exact).ToString());
                // the wide population (1.4.11): each kind in its one class
                long W(WorkCount c, string k) => c.Wide.GetValueOrDefault(k);
                Eq("work.exactUsed", Num(work, "exactUsed"),
                    (kc.Exact + kc.Pasters + kc.Forwarders + kc.Appliers + W(kc, "widePaster") + W(kc, "root") + W(kc, "edge")).ToString());
                Eq("work.patterns (wide pasters kept)", Num(work, "patterns"), W(kc, "widePaster").ToString());
                Eq("work.patterns (roots kept)", Num(work, "patterns"), W(kc, "root").ToString());
                Eq("work.edges", Num(work, "edges"), W(kc, "edge").ToString());
                Eq("work.openByPattern", Num(work, "openByPattern"), W(oc, "matched").ToString());
                Eq("work.namedOnly", Num(work, "namedOnly"), W(oc, "named").ToString());
                Eq("work.pasteMacrosUsed", Num(work, "pasteMacrosUsed"), W(oc, "usedPaster").ToString());
                Eq("work.forwardersUsed", Num(work, "forwardersUsed"), W(oc, "usedForwarder").ToString());
                Eq("work.openByBody", Num(work, "openByBody"), (W(oc, "named") + W(oc, "usedPaster") + W(oc, "usedForwarder")).ToString());
                Eq("work.nearMissDropped", Num(work, "nearMissDropped"), W(dc, "near").ToString());
                Eq("work.pasteMacros", Num(work, "pasteMacros"), (W(oc, "usedPaster") + W(dc, "unusedPaster")).ToString());
                Eq("work.forwarders", Num(work, "forwarders"), (W(oc, "usedForwarder") + W(dc, "unusedForwarder")).ToString());
                long misplaced = 0;
                foreach (var k in new[] { "matched", "named", "usedPaster", "usedForwarder" }) misplaced += W(kc, k) + W(dc, k);
                foreach (var k in new[] { "widePaster", "root", "edge" }) misplaced += W(oc, k) + W(dc, k);
                foreach (var k in new[] { "near", "unusedPaster", "unusedForwarder" }) misplaced += W(kc, k) + W(oc, k);
                Eq("work: wide names outside their class", "0", misplaced.ToString());
                // byLevel[k].uses: the kept exact names of level k (a use's id is <h>H<k>L<l>)
                if (Obj(tree, "byLevel") is { ValueKind: JsonValueKind.Array } bl)
                {
                    Eq("work.tree.byLevel: levels", bl.GetArrayLength().ToString(), kc.ByLevel.Count.ToString());
                    int k = 0;
                    long live = 0;
                    foreach (var e in bl.EnumerateArray())
                    {
                        Eq($"work.tree.byLevel[{k}].uses", Num(e, "uses"), (k < kc.ByLevel.Count ? kc.ByLevel[k] : 0).ToString());
                        live += k < dc.CrossByLevel.Count ? dc.CrossByLevel[k] : 0;
                        Eq($"work.tree.byLevel[{k}].crossDecoysLiveSoFar", Num(e, "crossDecoysLiveSoFar"), live.ToString());
                        k++;
                    }
                }
            }
        }

        if (referenceCarve is not null)
        {
            if (fail > 0) { err.WriteLine("FAIL firmware: no reference carve of a corpus that fails verify"); return fail + 1; }
            string? keepRel = Str(Obj(Obj(builds, "main"), "mustKeepDefines"), "path");
            string dest = Path.GetFullPath(referenceCarve);
            try { WriteCarve(corpus, dest, t, paths.Resolve(keepRel!)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                err.WriteLine($"FAIL firmware reference carve: {ex.Message}");
                try { if (File.Exists(Path.Combine(dest, CarveMarker))) Directory.Delete(dest, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return fail + 1;
            }
            Console.WriteLine($"  OK  firmware reference carve written to {dest}");
        }
        return fail;
    }

    private sealed record WorkCount(long Exact, long Cross, long Half, long Filler, long Pasters, long Forwarders, long Appliers,
        List<long> ByLevel, List<long> CrossByLevel, Dictionary<string, long> Wide);

    private static WorkCount WorkCounts(string list)
    {
        long e = 0, c = 0, h = 0, f = 0, pa = 0, fo = 0, ap = 0;
        List<long> byLevel = new(), crossByLevel = new();
        var wide = new Dictionary<string, long>(StringComparer.Ordinal);
        static void Bump(List<long> l, int k) { while (l.Count <= k) l.Add(0); l[k]++; }
        using var r = new StreamReader(OpenList(list), Encoding.UTF8);
        for (string? n; (n = r.ReadLine()) is not null;)
        {
            switch (FwWork.Classify(n))
            {
                case "exact": e++; if (LevelAt(n, 3) is int k) Bump(byLevel, k); break;
                case "cross":
                    c++;
                    // ZRA<idA>W_Q<idB>Z: live from the deeper of its two pieces' levels
                    int q = n.IndexOf("W_Q", StringComparison.Ordinal);
                    if (LevelAt(n, 3) is int ka && LevelAt(n, q + 3) is int kb) Bump(crossByLevel, Math.Max(ka, kb));
                    break;
                case "half": h++; break;
                case "filler": f++; break;
                default:
                    if (Numbered(n, "WRP")) pa++;
                    else if (Numbered(n, "WFW")) fo++;
                    else if (Numbered(n, "WAP")) ap++;
                    else if (FwWide.Classify(n) is { } wk) wide[wk] = wide.GetValueOrDefault(wk) + 1;
                    break;
            }
        }
        return new WorkCount(e, c, h, f, pa, fo, ap, byLevel, crossByLevel, wide);
    }

    /// <summary>The level k of the use id <c>&lt;h&gt;H&lt;k&gt;L&lt;l&gt;</c> starting at <paramref name="at"/>, or null.</summary>
    private static int? LevelAt(string n, int at)
    {
        int hh = at;
        while (hh < n.Length && char.IsAsciiDigit(n[hh])) hh++;
        if (hh == at || hh >= n.Length || n[hh] != 'H') return null;
        int k = hh + 1, kk = k;
        while (kk < n.Length && char.IsAsciiDigit(n[kk])) kk++;
        if (kk == k || kk >= n.Length || n[kk] != 'L' || kk - k > 4) return null;
        return int.Parse(n.AsSpan(k, kk - k));
    }

    /// <summary>The work object's own arithmetic and its headers on disk (the class counts are recounted from the
    /// lists in <c>Core</c>).</summary>
    private static int CheckWorkShape(string corpus, JsonElement work, TextWriter err, int[] okCount)
    {
        int fail = 0;
        long? N(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt64(out long x) ? x : null;
        void Is(string what, long? want, long? got)
        {
            if (want is not null && want == got) okCount[0]++;
            else { err.WriteLine($"FAIL firmware work.{what}: manifest says {want?.ToString() ?? "(missing)"}, expected {got?.ToString() ?? "(unknown)"}"); fail++; }
        }
        string wantDir = FwSpec.FwDir + "/" + FwWork.Dir;
        string? dir = work.TryGetProperty("dir", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
        if (dir == wantDir) okCount[0]++;
        else { err.WriteLine($"FAIL firmware work.dir: manifest says {dir ?? "(missing)"}, expected {wantDir}"); fail++; }
        string full = Path.Combine(corpus, FwSpec.FwDir, FwWork.Dir);
        long headers = Directory.Exists(full) ? Directory.EnumerateFiles(full, "wreg_*.h").LongCount() : 0;
        Is("headers", N(work, "headers"), headers);
        // the flat counts (1.4.11)
        long? Files(string sub, params string[] exts)
        {
            string d = Path.Combine(corpus, FwSpec.FwDir, sub);
            return Directory.Exists(d) ? Directory.EnumerateFiles(d).LongCount(p => exts.Any(x => p.EndsWith(x, StringComparison.OrdinalIgnoreCase))) : 0;
        }
        Is("uncoveredFiles", N(work, "uncoveredFiles"), Files("src/uncovered", ".c"));
        Is("asmFiles", N(work, "asmFiles"), Files("asm", ".s", ".asm"));
        Is("patterns (twoSided + prefixOnly + suffixOnly)", N(work, "patterns"),
            N(work, "patternsTwoSided") + N(work, "patternsPrefixOnly") + N(work, "patternsSuffixOnly"));
        Is("pieces (start + end)", N(work, "pieces"), N(work, "piecesStart") + N(work, "piecesEnd"));
        var work0 = work;
        work = work.TryGetProperty("tree", out var tv) && tv.ValueKind == JsonValueKind.Object ? tv : default;
        if (work.ValueKind != JsonValueKind.Object) { err.WriteLine("FAIL firmware work.tree: missing"); return fail + 1; }
        long? exact = N(work, "exactKept"), cross = N(work, "crossDecoys"), half = N(work, "halfDecoys"), filler = N(work, "filler");
        Is("tree.defines", N(work, "defines"), exact + cross + half + filler);
        Is("defines (tree + exactUsed - exactKept + open + near + body macros)", N(work0, "defines"),
            N(work, "defines") + N(work0, "exactUsed") - exact + N(work0, "openByPattern") + N(work0, "namedOnly") + N(work0, "nearMissDropped")
            + N(work0, "pasteMacros") + N(work0, "forwarders"));
        Is("tree.keptUnderGlobalPools", N(work, "keptUnderGlobalPools"), exact + cross);
        var uses = work.TryGetProperty("uses", out var u) ? u : default;
        Is("tree.uses (direct + forwarded + higherOrder)", N(uses, "direct") + N(uses, "forwarded") + N(uses, "higherOrder"), exact);
        long? levels = N(work, "levels");
        int bl = work.TryGetProperty("byLevel", out var b) && b.ValueKind == JsonValueKind.Array ? b.GetArrayLength() : -1;
        Is("tree.levels", levels, bl < 0 ? null : bl);
        Is("tree.expectedPasses", N(work, "expectedPasses"), levels + 1);
        if (bl < 0) return fail;
        long cum = 0, crossCum = 0;
        int i = 0;
        foreach (var e in b.EnumerateArray())
        {
            long? lu = N(e, "uses");
            cum += lu ?? 0;
            Is($"tree.byLevel[{i}].level", N(e, "level"), i);
            Is($"tree.byLevel[{i}].startPieces", N(e, "startPieces"), lu);
            Is($"tree.byLevel[{i}].endPieces", N(e, "endPieces"), lu);
            Is($"tree.byLevel[{i}].piecesSoFar", N(e, "piecesSoFar"), cum);
            long? live = N(e, "crossDecoysLiveSoFar");
            if (live is not null && live >= crossCum) okCount[0]++;
            else { err.WriteLine($"FAIL firmware work.tree.byLevel[{i}].crossDecoysLiveSoFar: {live?.ToString() ?? "(missing)"} falls below level {i - 1}'s {crossCum}"); fail++; }
            crossCum = live ?? crossCum;
            i++;
        }
        Is("tree.byLevel: last piecesSoFar", cum, exact);
        Is("tree.byLevel: last crossDecoysLiveSoFar", crossCum, cross);
        return fail;
    }

    /// <summary>cases.tsv's data rows, or what is wrong with it: the header <c>name\tcase\tsite</c>, then rows of
    /// three non-empty fields whose site (relative to fw/, ../ for one outside it) is a file of the corpus.</summary>
    private static (long Rows, string? Bad) CaseRows(string path, string corpus)
    {
        using var r = new StreamReader(path, Encoding.UTF8);
        if (r.ReadLine() != "name\tcase\tsite") return (0, "the first line is not the header name<TAB>case<TAB>site");
        long rows = 0;
        var sites = new Dictionary<string, bool>(StringComparer.Ordinal);
        var paths = new Generation.Containment(corpus);
        for (string? line; (line = r.ReadLine()) is not null;)
        {
            rows++;
            var f = line.Split('\t');
            if (f.Length != 3 || f.Any(string.IsNullOrEmpty)) return (rows, $"row {rows} is not three non-empty fields");
            if (!sites.TryGetValue(f[2], out bool ok))
                sites[f[2]] = ok = !Path.IsPathRooted(f[2]) && paths.Resolve(FwSpec.FwDir + "/" + f[2]) is { } full && File.Exists(full);
            if (!ok) return (rows, $"row {rows}: site {f[2]} is not a file of the corpus");
        }
        return (rows, null);
    }

    private static bool Numbered(string n, string stem) =>
        n.Length > stem.Length && n.StartsWith(stem, StringComparison.Ordinal) && n.AsSpan(stem.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    private static Stream OpenList(string path)
    {
        Stream s = File.OpenRead(path);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(s, CompressionMode.Decompress) : s;
    }

    // A list's sha256 is of its text (uncompressed).
    private static string ListSha(string path)
    {
        using var s = OpenList(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The file a reference carve leaves in its directory, so a later carve may replace it.</summary>
    public const string CarveMarker = ".codespawner-carve";

    /// <summary>
    /// Why <paramref name="dest"/> may not take a reference carve of <paramref name="corpus"/>, or null. A carve
    /// replaces its directory, so it must not be (or hold, or sit in) the corpus, nor a folder the generator would
    /// never delete, and an existing non-empty directory must be an earlier carve (it carries <see cref="CarveMarker"/>).
    /// </summary>
    public static string? CarveDestProblem(string corpus, string dest)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpus));
        string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest));
        char s = Path.DirectorySeparatorChar;
        if (string.Equals(c, d, cmp) || c.StartsWith(d + s, cmp)) return $"'{d}' is the corpus or holds it";
        if (d.StartsWith(c + s, cmp)) return $"'{d}' is inside the corpus";
        if (File.Exists(d)) return $"'{d}' is a file";
        if (!Directory.Exists(d)) return null;
        if (Generation.OutputDir.UnsafeToDelete(d) is { } why) return $"'{d}' {why}: refusing to replace it";
        if (Directory.EnumerateFileSystemEntries(d).Any() && !File.Exists(Path.Combine(d, CarveMarker)))
            return $"'{d}' is a non-empty directory that is not a reference carve: refusing to replace it";
        return null;
    }

    /// <summary>
    /// Copies fw/ and ext/ to <paramref name="dest"/>, removing from each carved file every define line whose name is
    /// not must-keep (a define line that leaves a block comment open is replaced by <c>/*</c>, so the comment still
    /// swallows what it did), and writes the compile db with the corpus root replaced by <paramref name="dest"/>.
    /// </summary>
    private static void WriteCarve(string corpus, string dest, FwTruth t, string keepList)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        using (var r = new StreamReader(OpenList(keepList), Encoding.UTF8))
            for (string? line; (line = r.ReadLine()) is not null;) keep.Add(line);
        // CarveDestProblem allowed only a missing, empty or earlier-carve directory
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, CarveMarker), $"CodeSpawner {Program.Version} reference carve\n");
        foreach (var d in new[] { FwSpec.FwDir, FirmwareEmitter.ExtDir })
        {
            string src = Path.Combine(corpus, d);
            if (!Directory.Exists(src)) continue;
            foreach (var f in Directory.EnumerateFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                string to = Path.Combine(dest, Path.GetRelativePath(corpus, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        }
        var dp = new DefineParse();
        foreach (var rel in t.Carved)
        {
            string to = Path.Combine(dest, FwSpec.FwDir, rel);
            byte[] raw = File.ReadAllBytes(to);
            var text = FwLexer.TextBytes(raw, out int st);
            using var o = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            o.Write(text.AsSpan(0, st));
            var w = new LineWalker(text, st);
            while (w.Next())
            {
                if (w.Kind == LineKind.Define && dp.Parse(w.Spliced)
                    && !keep.Contains(Encoding.UTF8.GetString(w.Spliced.Slice(dp.NameStart, dp.NameLen))))
                {
                    if (dp.EndsInComment) { o.Write("/*"u8); o.Write(text.AsSpan(w.Start + w.Length - w.TermBytes, w.TermBytes)); }
                    continue;
                }
                o.Write(w.Raw);
            }
        }
        string db = Path.Combine(corpus, FwSpec.DbPath);
        if (File.Exists(db))
        {
            string to = Path.Combine(dest, FwSpec.DbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpus));
            using var doc = JsonDocument.Parse(File.ReadAllBytes(db));
            using var fs = File.Create(to);
            using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            Rewrite(doc.RootElement, jw, root, dest);
        }
    }

    /// <summary>Every occurrence of <paramref name="root"/> that is a whole path prefix (followed by a separator, a quote,
    /// a blank or the end), anywhere in <paramref name="s"/> (a joined <c>-I&lt;root&gt;/…</c> or a <c>command</c> string
    /// too), replaced by <paramref name="dest"/>.</summary>
    internal static string RebaseRoot(string s, string root, string dest)
    {
        var sb = new StringBuilder();
        int at = 0;
        for (int i; (i = s.IndexOf(root, at, StringComparison.OrdinalIgnoreCase)) >= 0;)
        {
            int end = i + root.Length;
            bool whole = end == s.Length || s[end] is '\\' or '/' or '"' or ' ' or '\t';
            sb.Append(s, at, i - at).Append(whole ? dest : s.Substring(i, root.Length));
            at = end;
        }
        return sb.Append(s, at, s.Length - at).ToString();
    }

    private static void Rewrite(JsonElement e, Utf8JsonWriter w, string root, string dest)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) Rewrite(x, w, root, dest);
                w.WriteEndArray();
                break;
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject()) { w.WritePropertyName(p.Name); Rewrite(p.Value, w, root, dest); }
                w.WriteEndObject();
                break;
            case JsonValueKind.String:
                string s = e.GetString()!;
                w.WriteStringValue(RebaseRoot(s, root, dest));
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
