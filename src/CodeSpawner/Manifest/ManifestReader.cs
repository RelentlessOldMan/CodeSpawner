using System.Security.Cryptography;
using System.Text.Json;

namespace CodeSpawner.Manifest;

/// <summary>The parsed base manifest mutate composes deltas against.</summary>
public sealed class BaseManifest
{
    public required int ManifestVersion { get; init; }
    public required int Seed { get; init; }
    public required string GeneratorVersion { get; init; }
    public required string CorpusRoot { get; init; }
    /// <summary>The base's identity sha (<see cref="ManifestReader.IdentitySha"/>) — a delta records this as
    /// baseManifestSha to bind to its exact base.</summary>
    public required string Sha256 { get; init; }
    public required Dictionary<string, SymbolEntry> Symbols { get; init; }
    /// <summary>Declared entry-point symbol names (oracle overlay); empty when absent.</summary>
    public List<string> Roots { get; init; } = new();
}

/// <summary>Reads a v1 manifest (produced by <see cref="ManifestWriter"/>) back into a symbol table.</summary>
public static class ManifestReader
{
    /// <summary>
    /// baseManifestSha: sha256 of the manifest bytes with every CR byte dropped and the <c>_meta.corpusRoot</c>
    /// string value replaced by <c>""</c>. So the same corpus regenerated at another path, or on another OS, binds
    /// the same deltas; any other change to the manifest still changes it. (CR can only be whitespace in valid
    /// JSON — strings escape it.)
    /// </summary>
    public static string IdentitySha(byte[] bytes)
    {
        byte[] lf = Array.FindAll(bytes, b => b != (byte)'\r');
        var (start, end) = CorpusRootValueRange(lf);
        byte[] id = start < 0 ? lf : [.. lf.AsSpan(0, start), (byte)'"', (byte)'"', .. lf.AsSpan(end)];
        return Convert.ToHexString(SHA256.HashData(id)).ToLowerInvariant();
    }

    /// <summary>Byte range [start, end) of the _meta.corpusRoot string token, quotes included; (-1, -1) if absent.</summary>
    private static (int start, int end) CorpusRootValueRange(byte[] json)
    {
        var r = new Utf8JsonReader(json);
        bool inMeta = false;
        while (r.Read())
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            if (r.CurrentDepth == 1) { inMeta = r.ValueTextEquals("_meta"); continue; }
            if (inMeta && r.CurrentDepth == 2 && r.ValueTextEquals("corpusRoot"))
            {
                r.Read();
                return r.TokenType == JsonTokenType.String ? ((int)r.TokenStartIndex, (int)r.BytesConsumed) : (-1, -1);
            }
        }
        return (-1, -1);
    }

    public static BaseManifest Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string sha = IdentitySha(bytes);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        var meta = root.GetProperty("_meta");
        int ver = meta.TryGetProperty("manifestVersion", out var v) ? v.GetInt32() : -1;
        if (ver != 1) throw new InvalidOperationException($"manifest version {ver} != 1");

        var symbols = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);
        if (root.TryGetProperty("symbols", out var syms) && syms.ValueKind == JsonValueKind.Object)
            foreach (var p in syms.EnumerateObject())
            {
                var e = new SymbolEntry { Def = p.Value.GetProperty("def").GetString()! };
                if (p.Value.TryGetProperty("refs", out var r) && r.ValueKind == JsonValueKind.Array)
                    foreach (var x in r.EnumerateArray()) e.Refs.Add(x.GetString()!);
                if (p.Value.TryGetProperty("edges", out var g) && g.ValueKind == JsonValueKind.Array)
                    foreach (var x in g.EnumerateArray()) e.Edges.Add(x.GetString()!);
                if (p.Value.TryGetProperty("indirectEdges", out var ie) && ie.ValueKind == JsonValueKind.Array)
                    foreach (var x in ie.EnumerateArray())
                    {
                        IndirectViaExtensions.TryParse(x.TryGetProperty("via", out var vv) ? vv.GetString() ?? "" : "", out var via);
                        e.IndirectEdges.Add(new IndirectEdge
                        {
                            Target = x.GetProperty("target").GetString()!,
                            Via = via,
                            Dispatched = x.TryGetProperty("dispatched", out var d) && d.ValueKind == JsonValueKind.True,
                            Resolved = !x.TryGetProperty("resolved", out var rv) || rv.ValueKind != JsonValueKind.False,
                        });
                    }
                if (p.Value.TryGetProperty("unreachableRefs", out var u) && u.ValueKind == JsonValueKind.Array)
                {
                    e.UnreachableRefs = new List<string>();
                    foreach (var x in u.EnumerateArray()) e.UnreachableRefs.Add(x.GetString()!);
                }
                if (p.Value.TryGetProperty("expectedMiss", out var em) && em.ValueKind == JsonValueKind.True)
                    e.ExpectedMiss = true;
                if (p.Value.TryGetProperty("bytes", out var by) && by.ValueKind == JsonValueKind.Number)
                    e.Bytes = by.GetInt64();
                symbols[p.Name] = e;
            }

        var roots = new List<string>();
        if (meta.TryGetProperty("roots", out var rootsEl) && rootsEl.ValueKind == JsonValueKind.Array)
            foreach (var x in rootsEl.EnumerateArray()) roots.Add(x.GetString()!);

        return new BaseManifest
        {
            ManifestVersion = ver,
            Seed = meta.TryGetProperty("seed", out var s) ? s.GetInt32() : 0,
            GeneratorVersion = meta.TryGetProperty("generatorVersion", out var gv) ? gv.GetString()! : "",
            CorpusRoot = meta.TryGetProperty("corpusRoot", out var cr) ? cr.GetString()! : "",
            Sha256 = sha,
            Symbols = symbols,
            Roots = roots,
        };
    }
}
