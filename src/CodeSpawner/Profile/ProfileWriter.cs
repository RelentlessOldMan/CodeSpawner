using System.Text.Json;

namespace CodeSpawner.Profile;

/// <summary>
/// Serializes a <see cref="ProfileModel"/> to profileVersion 1 with a streaming <see cref="Utf8JsonWriter"/>
/// (AOT-safe, no reflection). Posture gates content-derived fields: <c>structure-only</c> omits class /
/// trigram / density / includes; <c>class-labeled</c> emits those but drops numeric content stats;
/// <c>content-stats</c> emits everything. The file is numbers + enum labels only.
/// </summary>
public static class ProfileWriter
{
    public static void Write(ProfileModel m, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });

        bool content = m.Posture.ReadsContent();
        bool stats = m.Posture.EmitsNumericStats();

        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("profileVersion", ProfileModel.ProfileVersion);
        w.WriteString("scannedAt", m.ScannedAt);
        w.WriteNumber("minCluster", m.MinCluster);
        w.WriteString("posture", m.Posture.Label());
        w.WriteEndObject();

        w.WriteStartObject("totals");
        w.WriteNumber("files", m.TotalFiles);
        w.WriteNumber("bytes", m.TotalBytes);
        w.WriteNumber("parsedSourceBytes", m.ParsedSourceBytes);
        w.WriteNumber("totalIndexedBytes", m.TotalIndexedBytes);
        if (content)
        {
            w.WriteNumber("distinctTrigramEstimate", m.DistinctTrigramEstimate);
            w.WriteNumber("trigramOccurrences", m.TrigramOccurrences);
        }
        w.WriteEndObject();

        w.WriteStartArray("sizeHistogram");
        foreach (var b in m.SizeHistogram)
        {
            w.WriteStartObject();
            w.WriteString("bucket", b.Bucket);
            w.WriteNumber("files", b.Files);
            w.WriteNumber("bytes", b.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("extensions");
        foreach (var e in m.Extensions)
        {
            w.WriteStartObject();
            w.WriteString("ext", e.Ext);
            w.WriteNumber("files", e.Files);
            w.WriteNumber("bytes", e.Bytes);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("headers");
        w.WriteStartObject("sizeBuckets");
        w.WriteNumber("gt20MB", m.Headers.Gt20MB);
        w.WriteNumber("1to20MB", m.Headers.Between1And20MB);
        w.WriteNumber("lt1MB", m.Headers.Lt1MB);
        w.WriteEndObject();
        if (stats && m.Headers.DefineFractionHistogramGe1MB is { } dh)
        {
            w.WriteStartArray("defineFractionHistogram_ge1MB");
            foreach (var b in dh)
            {
                w.WriteStartObject();
                w.WriteString("range", b.Bucket);
                w.WriteNumber("count", b.Files);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        if (content && m.Includes is { } inc)
        {
            w.WriteStartObject("includes");
            WriteLabeled(w, "fanoutHistogram", inc.FanoutHistogram);
            w.WriteNumber("unresolvedIncludeRate", Round(inc.UnresolvedIncludeRate));
            w.WriteNumber("duplicateBasenameAmbiguity", inc.DuplicateBasenameAmbiguity);
            w.WriteNumber("hops", inc.Hops);
            w.WriteEndObject();
        }

        w.WriteStartObject("dirs");
        w.WriteNumber("count", m.Dirs.Count);
        WriteLabeled(w, "depthHistogram", m.Dirs.DepthHistogram);
        WriteLabeled(w, "fanoutHistogram", m.Dirs.FanoutHistogram);
        WriteLabeled(w, "filesPerDirHistogram", m.Dirs.FilesPerDirHistogram);
        w.WriteEndObject();

        w.WriteStartArray("archetypes");
        foreach (var a in m.Archetypes)
        {
            w.WriteStartObject();
            w.WriteString("label", a.Label);
            w.WriteString("extension", a.Extension);
            w.WriteNumber("count", a.Count);
            if (content)
            {
                w.WriteString("class", a.Class.Label());
                w.WriteString("parseCost", a.Class.Cost().Label());
            }
            w.WriteStartObject("sizeDistribution");
            w.WriteNumber("p50", a.SizeDistribution.P50);
            w.WriteNumber("p90", a.SizeDistribution.P90);
            w.WriteNumber("max", a.SizeDistribution.Max);
            w.WriteEndObject();
            if (content && a.Trigram is { } tg)
            {
                w.WriteStartObject("trigram");
                w.WriteNumber("distinctEstimate", tg.DistinctEstimate);
                w.WriteNumber("occurrences", tg.Occurrences);
                w.WriteEndObject();
            }
            if (content && a.SymbolDensity is { } sd)
            {
                w.WriteStartObject("symbolDensity");
                w.WriteString("functionsPerKB", sd.FunctionsPerKB.Label());
                w.WriteString("callsPerFunction", sd.CallsPerFunction.Label());
                w.WriteString("globalRefsPerFile", sd.GlobalRefsPerFile.Label());
                w.WriteEndObject();
            }
            if (stats && a.Content is { } cb)
            {
                w.WriteStartObject("content");
                w.WriteNumber("defineFrac", Round(cb.DefineFrac));
                w.WriteNumber("commentFrac", Round(cb.CommentFrac));
                w.WriteNumber("blankFrac", Round(cb.BlankFrac));
                w.WriteNumber("includeFrac", Round(cb.IncludeFrac));
                w.WriteNumber("identUniqueRatio", Round(cb.IdentUniqueRatio));
                w.WriteNumber("avgIdentLen", Round(cb.AvgIdentLen));
                w.WriteNumber("avgLineLen", Round(cb.AvgLineLen));
                w.WriteNumber("maxLineLen", cb.MaxLineLen);
                w.WriteString("encoding", cb.Encoding);
                w.WriteBoolean("bom", cb.Bom);
                w.WriteString("newline", cb.Newline);
                if (a.SymbolDensity is { ExactFunctionsPerKB: { } } sde)
                {
                    w.WriteStartObject("symbolDensityExact");
                    w.WriteNumber("functionsPerKB", Round(sde.ExactFunctionsPerKB!.Value));
                    w.WriteNumber("callsPerFunction", Round(sde.ExactCallsPerFunction!.Value));
                    w.WriteNumber("globalRefsPerFile", Round(sde.ExactGlobalRefsPerFile!.Value));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (content && m.ParsedSourceByClass.Count > 0)
        {
            w.WriteStartArray("parsedSourceByClass");
            foreach (var p in m.ParsedSourceByClass)
            {
                w.WriteStartObject();
                w.WriteString("class", p.Class.Label());
                w.WriteNumber("bytes", p.Bytes);
                w.WriteString("parseCost", p.Class.Cost().Label());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteLabeled(Utf8JsonWriter w, string name, LabeledHistogram h)
    {
        w.WriteStartObject(name);
        foreach (var (label, count) in h.Entries) w.WriteNumber(label, count);
        w.WriteEndObject();
    }

    // Bucket ratios to 3 decimals so no exact per-file value leaks (privacy: rounding/bucketing).
    private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
