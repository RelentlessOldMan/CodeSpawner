using System.Text.Json;
using CodeSpawner.Cli;

namespace CodeSpawner.Verify;

/// <summary>
/// Tool-INDEPENDENT self-check: given a generated corpus + its manifest, assert the manifest accurately
/// describes the emitted files, with zero dependency on any indexer/carver. This is the contract's
/// guardian in its own repo — a generator change that breaks the manifest fails here, once, instead of
/// silently downstream. Relative paths are resolved against the supplied <c>--corpus</c> dir (NOT the
/// manifest's recorded corpusRoot), so a copied/SMB corpus validates against the same manifest.
/// </summary>
public static class ManifestVerifier
{
    public static int Run(VerifyOptions o)
    {
        string corpus = Path.GetFullPath(o.Corpus);
        string manifestPath = o.Manifest ?? DefaultManifestPath(corpus);

        if (!Directory.Exists(corpus)) { Console.Error.WriteLine($"error: corpus dir not found: {corpus}"); return 2; }
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"error: manifest not found: {manifestPath}"); return 2; }

        Console.WriteLine($"verify: corpus={corpus}");
        Console.WriteLine($"verify: manifest={manifestPath}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); }
        catch (JsonException ex) { Console.Error.WriteLine($"error: manifest is not valid JSON: {ex.Message}"); return 2; }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { Console.Error.WriteLine("error: manifest root is not an object"); return 2; }

        int fail = 0;

        // --- _meta ---
        if (!root.TryGetProperty("_meta", out var meta)) { Console.Error.WriteLine("FAIL: no _meta block"); return 1; }
        int ver = meta.TryGetProperty("manifestVersion", out var v) ? v.GetInt32() : -1;
        if (ver != 1) { Console.Error.WriteLine($"FAIL: manifestVersion {ver} != 1"); fail++; }
        else Console.WriteLine("  OK  _meta.manifestVersion == 1");

        if (!root.TryGetProperty("symbols", out var symbols)) { Console.Error.WriteLine("FAIL: no symbols block"); return 1; }

        // Collect symbol names for the edge-chain check.
        var names = new HashSet<string>();
        foreach (var s in symbols.EnumerateObject()) names.Add(s.Name);

        var lineCache = new Dictionary<string, string[]>();
        int defsOk = 0, refsOk = 0, gatedOk = 0, edgesOk = 0;

        // A file named VENDOR_missing_*.h must exist NOWHERE in the tree (the unresolved-include premise).
        bool anyVendorHeaderPresent =
            Directory.EnumerateFiles(corpus, "VENDOR_missing_*.h", SearchOption.AllDirectories).Any();

        foreach (var sym in symbols.EnumerateObject())
        {
            string name = sym.Name;
            var entry = sym.Value;

            // def site contains the symbol token.
            if (entry.TryGetProperty("def", out var defEl))
            {
                if (SiteHasToken(corpus, lineCache, defEl.GetString()!, name, out string why)) defsOk++;
                else { Console.Error.WriteLine($"FAIL def {name}: {why}"); fail++; }
            }

            // each ref site contains the symbol token.
            if (entry.TryGetProperty("refs", out var refsEl))
                foreach (var r in refsEl.EnumerateArray())
                {
                    if (SiteHasToken(corpus, lineCache, r.GetString()!, name, out string why)) refsOk++;
                    else { Console.Error.WriteLine($"FAIL ref {name} @ {r.GetString()}: {why}"); fail++; }
                }

            // edges point at symbols that exist (the func_i -> func_{i-1} chain is intact).
            if (entry.TryGetProperty("edges", out var edgesEl))
                foreach (var e in edgesEl.EnumerateArray())
                {
                    string target = e.GetString()!;
                    if (names.Contains(target)) edgesOk++;
                    else { Console.Error.WriteLine($"FAIL edge {name} -> {target}: target symbol missing"); fail++; }
                }

            // unreachableRefs: gated call present, behind #ifdef VENDOR_OK, and the vendor header is absent.
            if (entry.TryGetProperty("unreachableRefs", out var unrefsEl))
                foreach (var u in unrefsEl.EnumerateArray())
                {
                    string site = u.GetString()!;
                    if (!SiteHasToken(corpus, lineCache, site, name, out string why))
                    { Console.Error.WriteLine($"FAIL gated {name} @ {site}: {why}"); fail++; continue; }

                    string file = ResolveFile(corpus, site);
                    string text = File.ReadAllText(file);
                    if (!text.Contains("#ifdef VENDOR_OK"))
                    { Console.Error.WriteLine($"FAIL gated {name} @ {site}: not behind #ifdef VENDOR_OK"); fail++; continue; }
                    if (anyVendorHeaderPresent)
                    { Console.Error.WriteLine($"FAIL gated {name}: a VENDOR_missing_*.h header exists in the tree"); fail++; continue; }
                    gatedOk++;
                }
        }

        Console.WriteLine($"  checked: {defsOk} defs, {refsOk} refs, {edgesOk} edges, {gatedOk} gated refs");
        if (fail == 0) { Console.WriteLine($"verify: PASS ({names.Count} symbols)"); return 0; }
        Console.Error.WriteLine($"verify: FAIL ({fail} problem(s))");
        return 1;
    }

    /// <summary>Assert the line named by "<rel>:<line>" exists and contains "<symbol>(".</summary>
    private static bool SiteHasToken(string corpus, Dictionary<string, string[]> cache, string site,
        string symbol, out string why)
    {
        int c = site.LastIndexOf(':');
        if (c < 0) { why = "malformed site (no :line)"; return false; }
        string rel = site[..c];
        if (!int.TryParse(site[(c + 1)..], out int line)) { why = "malformed line number"; return false; }

        string full = Path.GetFullPath(Path.Combine(corpus, rel.Replace('/', Path.DirectorySeparatorChar)));
        // Refuse paths that escape the corpus root (a malformed/hostile manifest with '..').
        string prefix = corpus.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        { why = $"path escapes corpus root: {rel}"; return false; }

        if (!cache.TryGetValue(full, out var lines))
        {
            if (!File.Exists(full)) { why = $"file missing: {rel}"; return false; }
            lines = File.ReadAllLines(full);
            cache[full] = lines;
        }
        if (line < 1 || line > lines.Length) { why = $"line {line} out of range (file has {lines.Length})"; return false; }

        if (!lines[line - 1].Contains(symbol + "(")) { why = $"line {line} lacks '{symbol}('"; return false; }
        why = "";
        return true;
    }

    private static string ResolveFile(string corpus, string site)
    {
        string rel = site[..site.LastIndexOf(':')];
        return Path.GetFullPath(Path.Combine(corpus, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string DefaultManifestPath(string corpus)
    {
        string parent = Path.GetDirectoryName(corpus.TrimEnd(Path.DirectorySeparatorChar))
                        ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(corpus.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(parent, $"{leaf}-manifest.json");
    }
}
