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
    /// <summary>sha256 of the manifest file bytes — a delta records this to bind to its exact base.</summary>
    public required string Sha256 { get; init; }
    public required Dictionary<string, SymbolEntry> Symbols { get; init; }
}

/// <summary>Reads a v1 manifest (produced by <see cref="ManifestWriter"/>) back into a symbol table.</summary>
public static class ManifestReader
{
    public static BaseManifest Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
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
                if (p.Value.TryGetProperty("unreachableRefs", out var u) && u.ValueKind == JsonValueKind.Array)
                {
                    e.UnreachableRefs = new List<string>();
                    foreach (var x in u.EnumerateArray()) e.UnreachableRefs.Add(x.GetString()!);
                }
                if (p.Value.TryGetProperty("expectedMiss", out var em) && em.ValueKind == JsonValueKind.True)
                    e.ExpectedMiss = true;
                symbols[p.Name] = e;
            }

        return new BaseManifest
        {
            ManifestVersion = ver,
            Seed = meta.TryGetProperty("seed", out var s) ? s.GetInt32() : 0,
            GeneratorVersion = meta.TryGetProperty("generatorVersion", out var gv) ? gv.GetString()! : "",
            CorpusRoot = meta.TryGetProperty("corpusRoot", out var cr) ? cr.GetString()! : "",
            Sha256 = sha,
            Symbols = symbols,
        };
    }
}
