using System.Text.Json;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;

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
    // Max files whose lines we retain at once. Bounds verify memory on corpora with many large carrier
    // files; access is sequential-by-file so the effective hit rate stays high.
    private const int LineCacheCap = 16;

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

        // A mutate that died part-way leaves its journal: the tree then matches neither the base manifest nor any
        // delta, so whatever the sites below say, the corpus is not one the manifest describes.
        if (File.Exists(Path.Combine(corpus, Mutation.Mutator.JournalName)))
        {
            Console.Error.WriteLine($"FAIL: {Mutation.Mutator.JournalName} present: a mutate of this corpus was interrupted " +
                                    "(or is still running); regenerate the corpus");
            fail++;
        }

        // --- _meta ---
        // Not a manifest at all (no _meta or no symbols object) is bad input, like invalid JSON: exit 2.
        if (!root.TryGetProperty("_meta", out var meta)) { Console.Error.WriteLine("error: manifest has no _meta block"); return 2; }
        int ver = meta.TryGetProperty("manifestVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int vi) ? vi : -1;
        if (ver != 1) { Console.Error.WriteLine($"FAIL: manifestVersion {ver} != 1"); fail++; }
        else Console.WriteLine("  OK  _meta.manifestVersion == 1");

        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Object)
        { Console.Error.WriteLine("error: manifest has no symbols object"); return 2; }

        // Collect symbol names for the edge-chain check.
        var names = new HashSet<string>();
        foreach (var s in symbols.EnumerateObject()) names.Add(s.Name);

        // _meta.roots (oracle overlay): every declared entry point must be a real symbol.
        if (meta.TryGetProperty("roots", out var rootsEl) && rootsEl.ValueKind == JsonValueKind.Array)
        {
            int rootsOk = 0;
            foreach (var r in rootsEl.EnumerateArray())
            {
                string rn = r.GetString() ?? "";
                if (names.Contains(rn)) rootsOk++;
                else { Console.Error.WriteLine($"FAIL root '{rn}': not a declared symbol"); fail++; }
            }
            if (rootsOk > 0) Console.WriteLine($"  OK  _meta.roots ({rootsOk} declared entry point(s))");
        }

        var lineCache = new Dictionary<string, string[]>();
        var paths = new Generation.Containment(corpus);
        int defsOk = 0, refsOk = 0, gatedOk = 0, edgesOk = 0, missOk = 0, dupOk = 0, indirectOk = 0;

        // A file named VENDOR_missing_*.h must exist NOWHERE in the tree, its linked roots included (the
        // unresolved-include premise: an include path may span every root).
        bool anyVendorHeaderPresent = CorpusRoots(corpus)
            .Any(r => Directory.EnumerateFiles(r, "VENDOR_missing_*.h", NoLinks).Any());

        foreach (var sym in symbols.EnumerateObject())
        {
            string name = sym.Name;
            var entry = sym.Value;

            // Honest-miss (expectedMiss) symbols are macro-generated: the name must NOT appear literally at
            // the def site, and the generator macro must be present. They carry no refs/edges to check.
            if (entry.TryGetProperty("expectedMiss", out var emEl) && emEl.ValueKind == JsonValueKind.True)
            {
                if (!entry.TryGetProperty("def", out var emDef) || emDef.GetString() is not { } site)
                { Console.Error.WriteLine($"FAIL expectedMiss {name}: no def site"); fail++; continue; }
                if (HonestMissOk(paths, lineCache, site, name, out string mw)) missOk++;
                else { Console.Error.WriteLine($"FAIL expectedMiss {name}: {mw}"); fail++; }
                continue;
            }

            // def site contains the symbol token. A symbol without a string def is a FAIL, not a skip: a truncated
            // or hand-edited manifest must not pass by dropping its def sites.
            if (!entry.TryGetProperty("def", out var defEl) || defEl.ValueKind != JsonValueKind.String)
            { Console.Error.WriteLine($"FAIL def {name}: no def site"); fail++; }
            else if (SiteHasToken(paths, lineCache, defEl.GetString()!, name, out string why)) defsOk++;
            else { Console.Error.WriteLine($"FAIL def {name}: {why}"); fail++; }

            // each ref site contains the symbol token.
            if (entry.TryGetProperty("refs", out var refsEl))
                foreach (var r in refsEl.EnumerateArray())
                {
                    if (SiteHasToken(paths, lineCache, ManifestReader.Str(r), name, out string why)) refsOk++;
                    else { Console.Error.WriteLine($"FAIL ref {name} @ {r.GetString()}: {why}"); fail++; }
                }

            // edges point at symbols that exist (the func_i -> func_{i-1} chain is intact).
            if (entry.TryGetProperty("edges", out var edgesEl))
                foreach (var e in edgesEl.EnumerateArray())
                {
                    string target = ManifestReader.Str(e);
                    if (names.Contains(target)) edgesOk++;
                    else { Console.Error.WriteLine($"FAIL edge {name} -> {target}: target symbol missing"); fail++; }
                }

            // indirectEdges: a resolved target must be a declared symbol; an unresolved (external) target must
            // NOT be (closed-world: external ⇒ resolved:false, terminal). Soundness/tax is the consumer's job.
            if (entry.TryGetProperty("indirectEdges", out var ieEl) && ieEl.ValueKind == JsonValueKind.Array)
                foreach (var ie in ieEl.EnumerateArray())
                {
                    string target = ie.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                    bool resolved = !ie.TryGetProperty("resolved", out var rv) || rv.ValueKind != JsonValueKind.False;
                    bool present = names.Contains(target);
                    if (resolved && !present)
                    { Console.Error.WriteLine($"FAIL indirect {name} -> {target}: resolved target is not a declared symbol"); fail++; }
                    else if (!resolved && present)
                    { Console.Error.WriteLine($"FAIL indirect {name} -> {target}: unresolved target must not be a declared symbol"); fail++; }
                    else indirectOk++;
                }

            // unreachableRefs: gated call present, behind #ifdef VENDOR_OK, and the vendor header is absent.
            if (entry.TryGetProperty("unreachableRefs", out var unrefsEl))
                foreach (var u in unrefsEl.EnumerateArray())
                {
                    string site = ManifestReader.Str(u);
                    if (!SiteHasToken(paths, lineCache, site, name, out string why))
                    { Console.Error.WriteLine($"FAIL gated {name} @ {site}: {why}"); fail++; continue; }

                    string file = paths.Resolve(site[..site.LastIndexOf(':')])!;   // SiteHasToken resolved it
                    string text = File.ReadAllText(file);
                    if (!text.Contains("#ifdef VENDOR_OK"))
                    { Console.Error.WriteLine($"FAIL gated {name} @ {site}: not behind #ifdef VENDOR_OK"); fail++; continue; }
                    if (anyVendorHeaderPresent)
                    { Console.Error.WriteLine($"FAIL gated {name}: a VENDOR_missing_*.h header exists in the tree"); fail++; continue; }
                    gatedOk++;
                }
        }

        // dup-content: every path in a group must exist and hash to the recorded sha256 (i.e. be byte-
        // identical — the dedup target); every near-variant must exist and differ.
        if (root.TryGetProperty("dupGroups", out var dgEl) && dgEl.ValueKind == JsonValueKind.Object)
            foreach (var grp in dgEl.EnumerateObject())
            {
                string gname = grp.Name;
                if (!grp.Value.TryGetProperty("sha256", out var shaEl) || shaEl.GetString() is not { } sha ||
                    !grp.Value.TryGetProperty("paths", out var pathsEl) || pathsEl.ValueKind != JsonValueKind.Array)
                { Console.Error.WriteLine($"FAIL dup {gname}: malformed group (need sha256 + paths[])"); fail++; continue; }
                foreach (var p in pathsEl.EnumerateArray())
                {
                    if (FileSha(paths, ManifestReader.Str(p), out string got, out string why) && got == sha) dupOk++;
                    else { Console.Error.WriteLine($"FAIL dup {gname} @ {p.GetString()}: {(why.Length > 0 ? why : $"hash {got} != {sha}")}"); fail++; }
                }
                if (grp.Value.TryGetProperty("nearVariants", out var nvs))
                    foreach (var nv in nvs.EnumerateArray())
                    {
                        if (!FileSha(paths, ManifestReader.Str(nv), out string got, out string why))
                        { Console.Error.WriteLine($"FAIL dup-near {gname} @ {nv.GetString()}: {why}"); fail++; }
                        else if (got == sha)
                        { Console.Error.WriteLine($"FAIL dup-near {gname} @ {nv.GetString()}: identical to group (should differ)"); fail++; }
                    }
            }

        // indirectTruthSha: recompute the component digest from the manifest and assert it matches _meta —
        // a third independent reader (generator, this verifier, and the consumer) of the locked canonical form.
        if (meta.TryGetProperty("indirectTruthSha", out var itsEl) && itsEl.GetString() is { } declaredIts)
        {
            var parsed = ManifestReader.Load(manifestPath);
            string got = IndirectDigest.Compute(parsed.Symbols, parsed.Roots);
            if (got == declaredIts) Console.WriteLine("  OK  _meta.indirectTruthSha reproduces");
            else { Console.Error.WriteLine($"FAIL indirectTruthSha: manifest {declaredIts}, recomputed {got}"); fail++; }
        }

        Console.WriteLine($"  checked: {defsOk} defs, {refsOk} refs, {edgesOk} edges, {gatedOk} gated refs, {missOk} expected-miss, {dupOk} dup copies, {indirectOk} indirect edges");
        // A manifest that declares nothing checkable used to PASS, having proved nothing.
        if (fail == 0 && defsOk + refsOk + edgesOk + gatedOk + missOk + dupOk + indirectOk == 0)
        { Console.Error.WriteLine("error: the manifest declares nothing to check (no symbol sites, dup groups or indirect edges)"); return 2; }
        if (fail == 0) { Console.WriteLine($"verify: PASS ({names.Count} symbols)"); return 0; }
        Console.Error.WriteLine($"verify: FAIL ({fail} problem(s))");
        return 1;
    }

    /// <summary>Honest-miss check: the symbol name must NOT appear at the def line and the token-paste
    /// generator macro must — proving the symbol is macro-synthesized, not lexically present.</summary>
    private static bool HonestMissOk(Generation.Containment paths, Dictionary<string, string[]> cache, string site, string symbol, out string why)
    {
        int c = site.LastIndexOf(':');
        if (c < 0 || !int.TryParse(site[(c + 1)..], out int line)) { why = "malformed def site"; return false; }
        string rel = site[..c];
        if (paths.Resolve(rel) is not { } full) { why = $"path escapes corpus root: {rel}"; return false; }
        if (!cache.TryGetValue(full, out var lines))
        {
            if (!File.Exists(full)) { why = $"file missing: {rel}"; return false; }
            lines = File.ReadAllLines(full);
            // Bound the cache: sites are checked sequentially by file, so a small cap keeps the hit rate
            // while preventing retention of hundreds of multi-MB carrier files (broad-token) at once.
            if (cache.Count >= LineCacheCap) cache.Clear();
            cache[full] = lines;
        }
        if (line < 1 || line > lines.Length) { why = $"line {line} out of range"; return false; }
        string L = lines[line - 1];
        if (L.Contains(symbol, StringComparison.Ordinal)) { why = $"symbol appears literally at line {line} (not an honest miss)"; return false; }
        if (!L.Contains(Generation.PathologicalSymbolEmitter.GenMacro, StringComparison.Ordinal))
        { why = $"no {Generation.PathologicalSymbolEmitter.GenMacro} generator at line {line}"; return false; }
        why = "";
        return true;
    }

    /// <summary>Hex SHA-256 of a corpus-relative file (with containment check).</summary>
    private static bool FileSha(Generation.Containment paths, string rel, out string sha, out string why)
    {
        sha = ""; why = "";
        if (paths.Resolve(rel) is not { } full) { why = $"path escapes corpus root: {rel}"; return false; }
        if (!File.Exists(full)) { why = $"file missing: {rel}"; return false; }
        sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
        return true;
    }

    /// <summary>Assert the line named by "<rel>:<line>" exists and contains "<symbol>(".</summary>
    private static bool SiteHasToken(Generation.Containment paths, Dictionary<string, string[]> cache, string site,
        string symbol, out string why)
    {
        int c = site.LastIndexOf(':');
        if (c < 0) { why = "malformed site (no :line)"; return false; }
        string rel = site[..c];
        if (!int.TryParse(site[(c + 1)..], out int line)) { why = "malformed line number"; return false; }

        // Refuse paths that escape the corpus root (a malformed/hostile manifest with '..'); --linked-roots
        // siblings (../<corpus>_rootN/...) are part of the corpus.
        if (paths.Resolve(rel) is not { } full)
        { why = $"path escapes corpus root: {rel}"; return false; }

        if (!cache.TryGetValue(full, out var lines))
        {
            if (!File.Exists(full)) { why = $"file missing: {rel}"; return false; }
            lines = File.ReadAllLines(full);
            // Bound the cache: sites are checked sequentially by file, so a small cap keeps the hit rate
            // while preventing retention of hundreds of multi-MB carrier files (broad-token) at once.
            if (cache.Count >= LineCacheCap) cache.Clear();
            cache[full] = lines;
        }
        if (line < 1 || line > lines.Length) { why = $"line {line} out of range (file has {lines.Length})"; return false; }

        if (!lines[line - 1].Contains(symbol + "(")) { why = $"line {line} lacks '{symbol}('"; return false; }
        why = "";
        return true;
    }

    /// <summary>The corpus and its linked roots: the real (non-link) sibling dirs named <c>&lt;corpus&gt;_root&lt;N&gt;</c>.</summary>
    private static IEnumerable<string> CorpusRoots(string corpus)
    {
        string dir = corpus.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        yield return dir;
        string? parent = Path.GetDirectoryName(dir);
        if (parent is null) yield break;
        string prefix = Path.GetFileName(dir) + "_root";
        foreach (var sib in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            string tail = Path.GetFileName(sib)[prefix.Length..];
            if (tail.Length > 0 && tail.All(char.IsAsciiDigit)
                && !new DirectoryInfo(sib).Attributes.HasFlag(FileAttributes.ReparsePoint))
                yield return sib;
        }
    }

    /// <summary>Recursive walk that never follows a junction/symlink (the walk mutate uses), so a link inside the
    /// corpus can't pull an outside VENDOR_missing_*.h into the check.</summary>
    private static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Win32,
        IgnoreInaccessible = false,
    };

    private static string DefaultManifestPath(string corpus)
    {
        string parent = Path.GetDirectoryName(corpus.TrimEnd(Path.DirectorySeparatorChar))
                        ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(corpus.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(parent, $"{leaf}-manifest.json");
    }
}
