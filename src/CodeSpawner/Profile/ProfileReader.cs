using System.Text.Json;

namespace CodeSpawner.Profile;

/// <summary>Reads a profileVersion 1 profile (produced by <see cref="ProfileWriter"/>) back for regeneration.</summary>
public static class ProfileReader
{
    public static ProfileModel Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        var meta = root.GetProperty("_meta");
        int ver = meta.TryGetProperty("profileVersion", out var v) ? v.GetInt32() : -1;
        if (ver != ProfileModel.ProfileVersion)
            throw new InvalidOperationException($"profile version {ver} != {ProfileModel.ProfileVersion}");

        var m = new ProfileModel
        {
            MinCluster = meta.TryGetProperty("minCluster", out var mc) ? mc.GetInt32() : 5,
            Posture = meta.TryGetProperty("posture", out var p) ? ParsePosture(p.GetString()) : PrivacyPosture.ClassLabeled,
            ScannedAt = meta.TryGetProperty("scannedAt", out var sa) ? sa.GetString() ?? "" : "",
        };

        if (root.TryGetProperty("totals", out var totals))
        {
            m.TotalFiles = GetLong(totals, "files");
            m.TotalBytes = GetLong(totals, "bytes");
            m.ParsedSourceBytes = GetLong(totals, "parsedSourceBytes");
            m.TotalIndexedBytes = GetLong(totals, "totalIndexedBytes");
        }

        if (root.TryGetProperty("dirs", out var dirs))
        {
            var ds = new DirStats { Count = GetLong(dirs, "count") };
            ReadLabeled(dirs, "depthHistogram", ds.DepthHistogram);
            ReadLabeled(dirs, "fanoutHistogram", ds.FanoutHistogram);
            ReadLabeled(dirs, "filesPerDirHistogram", ds.FilesPerDirHistogram);
            m.Dirs = ds;
        }

        if (root.TryGetProperty("archetypes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                ContentClass cls = ContentClass.GenericCode;
                if (a.TryGetProperty("class", out var ce) && ContentClasses.TryParse(ce.GetString() ?? "", out var parsed))
                    cls = parsed;

                var sd = a.GetProperty("sizeDistribution");
                SymbolDensity? density = null;
                if (a.TryGetProperty("symbolDensity", out var dEl))
                    density = new SymbolDensity
                    {
                        FunctionsPerKB = ParseBand(dEl, "functionsPerKB"),
                        CallsPerFunction = ParseBand(dEl, "callsPerFunction"),
                        GlobalRefsPerFile = ParseBand(dEl, "globalRefsPerFile"),
                    };

                m.Archetypes.Add(new Archetype
                {
                    Label = a.GetProperty("label").GetString() ?? "a",
                    Extension = a.TryGetProperty("extension", out var ex) ? ex.GetString() ?? ".txt" : ".txt",
                    Count = GetLong(a, "count"),
                    Class = cls,
                    SizeDistribution = new SizeDistribution
                    {
                        P50 = GetLong(sd, "p50"),
                        P90 = GetLong(sd, "p90"),
                        Max = GetLong(sd, "max"),
                    },
                    SymbolDensity = density,
                });
            }

        return m;
    }

    private static PrivacyPosture ParsePosture(string? s) => s switch
    {
        "structure-only" => PrivacyPosture.StructureOnly,
        "content-stats" => PrivacyPosture.ContentStats,
        _ => PrivacyPosture.ClassLabeled,
    };

    private static DensityBand ParseBand(JsonElement e, string name) =>
        e.TryGetProperty(name, out var b)
            ? b.GetString() switch { "high" => DensityBand.High, "med" => DensityBand.Med, _ => DensityBand.Low }
            : DensityBand.Low;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static void ReadLabeled(JsonElement parent, string name, LabeledHistogram h)
    {
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in obj.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Number) h.Add(prop.Name, prop.Value.GetInt64());
    }
}
