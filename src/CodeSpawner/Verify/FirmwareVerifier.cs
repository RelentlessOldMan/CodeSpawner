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
    public static int Check(string corpus, JsonElement fw, string? referenceCarve, TextWriter err, ref int ok)
    {
        var n = new int[1];
        int fail = Core(corpus, fw, referenceCarve, err, n);
        ok += n[0];
        return fail;
    }

    private static int Core(string corpus, JsonElement fw, string? referenceCarve, TextWriter err, int[] okCount)
    {
        if (fw.ValueKind != JsonValueKind.Object) { err.WriteLine("FAIL firmware: not an object"); return 1; }
        int fail = 0;
        FwTruth t;
        try { t = FwSpec.Compute(corpus, null, checkAlphabet: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { err.WriteLine($"FAIL firmware: the tree cannot be read ({ex.Message})"); return 1; }

        void Eq(string what, string? want, string got)
        {
            if (want == got) okCount[0]++;
            else { err.WriteLine($"FAIL firmware {what}: manifest says {want ?? "(missing)"}, the tree gives {got}"); fail++; }
        }
        string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        JsonElement Obj(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
        string Arr(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(", ", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object
                ? $"{Str(x, "path")}{(x.TryGetProperty("edge", out var g) && g.ValueKind == JsonValueKind.True ? " (edge)" : "")}"
                : x.GetString()))
            : "(missing)";

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
        Eq("bigHeaders", Arr(fw, "bigHeaders"), string.Join(", ", t.BigHeaders));
        Eq("denseHeaders", Arr(fw, "denseHeaders"), string.Join(", ", t.Dense.Select(d => d.Path + (d.Edge ? " (edge)" : ""))));
        Eq("wholeIncludedFromOutside", Arr(fw, "wholeIncludedFromOutside"), string.Join(", ", t.WholeFromOutside));

        var paths = new Generation.Containment(corpus);
        if (Str(fw, "cases") is { } cases && paths.Resolve(cases) is { } cp && File.Exists(cp)) okCount[0]++;
        else { err.WriteLine($"FAIL firmware cases: {Str(fw, "cases") ?? "(missing)"} is not a file of the corpus"); fail++; }
        if (Obj(fw, "hostile") is { ValueKind: JsonValueKind.Array } hostile)
            foreach (var h in hostile.EnumerateArray())
            {
                if (Str(h, "path") is { } hp && paths.Resolve(hp) is { } hf && File.Exists(hf)) okCount[0]++;
                else { err.WriteLine($"FAIL firmware hostile: {Str(h, "path") ?? "(no path)"} is not a file of the corpus"); fail++; }
            }

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
        if (Obj(fw, "work") is { ValueKind: JsonValueKind.Object } work)
        {
            var main = Obj(builds, "main");
            string? keepRel = Str(Obj(main, "mustKeepDefines"), "path"), dropRel = Str(Obj(main, "mustDropDefines"), "path");
            string? keepF = keepRel is null ? null : paths.Resolve(keepRel), dropF = dropRel is null ? null : paths.Resolve(dropRel);
            if (keepF is null || dropF is null || !File.Exists(keepF) || !File.Exists(dropF))
            { err.WriteLine("FAIL firmware work: the main build's truth lists are missing"); fail++; }
            else
            {
                var kc = WorkCounts(keepF);
                var dc = WorkCounts(dropF);
                Eq("work.exactKept", Num(work, "exactKept"), kc.Exact.ToString());
                Eq("work.pasters", Num(work, "pasters"), kc.Pasters.ToString());
                Eq("work.forwarders", Num(work, "forwarders"), kc.Forwarders.ToString());
                Eq("work.appliers", Num(work, "appliers"), kc.Appliers.ToString());
                Eq("work.crossDecoys", Num(work, "crossDecoys"), dc.Cross.ToString());
                Eq("work.halfDecoys", Num(work, "halfDecoys"), dc.Half.ToString());
                Eq("work.filler", Num(work, "filler"), dc.Filler.ToString());
                Eq("work: decoys and filler kept", "0", (kc.Cross + kc.Half + kc.Filler).ToString());
                Eq("work: exact names dropped", "0", dc.Exact.ToString());
            }
        }

        if (referenceCarve is not null)
        {
            if (fail > 0) { err.WriteLine("FAIL firmware: no reference carve of a corpus that fails verify"); return fail + 1; }
            string? keepRel = Str(Obj(Obj(builds, "main"), "mustKeepDefines"), "path");
            WriteCarve(corpus, Path.GetFullPath(referenceCarve), t, paths.Resolve(keepRel!)!);
            Console.WriteLine($"  OK  firmware reference carve written to {Path.GetFullPath(referenceCarve)}");
        }
        return fail;
    }

    private sealed record WorkCount(long Exact, long Cross, long Half, long Filler, long Pasters, long Forwarders, long Appliers);

    private static WorkCount WorkCounts(string list)
    {
        long e = 0, c = 0, h = 0, f = 0, pa = 0, fo = 0, ap = 0;
        using var r = new StreamReader(OpenList(list), Encoding.UTF8);
        for (string? n; (n = r.ReadLine()) is not null;)
        {
            switch (FwWork.Classify(n))
            {
                case "exact": e++; break;
                case "cross": c++; break;
                case "half": h++; break;
                case "filler": f++; break;
                default:
                    if (Numbered(n, "WRP")) pa++;
                    else if (Numbered(n, "WFW")) fo++;
                    else if (Numbered(n, "WAP")) ap++;
                    break;
            }
        }
        return new WorkCount(e, c, h, f, pa, fo, ap);
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
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        foreach (var d in new[] { FwSpec.FwDir, FirmwareEmitter.ExtDir })
        {
            string src = Path.Combine(corpus, d);
            if (!Directory.Exists(src)) continue;
            foreach (var f in Directory.EnumerateFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
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
                w.WriteStringValue(s.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? dest + s[root.Length..] : s);
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
