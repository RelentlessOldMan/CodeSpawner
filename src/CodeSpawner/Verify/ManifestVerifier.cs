using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Where FAIL and error lines go: stderr, or (tests) a writer of the caller's flow alone, so a test can read
    // which check fired without swapping the process-wide Console under tests running in parallel.
    private static readonly AsyncLocal<TextWriter?> ErrOverride = new();
    private static TextWriter Err => ErrOverride.Value ?? Console.Error;

    internal static int Run(VerifyOptions o, TextWriter err)
    {
        ErrOverride.Value = err;
        try { return Run(o); }
        finally { ErrOverride.Value = null; }
    }

    public static int Run(VerifyOptions o)
    {
        // A JSON value of the wrong kind where the checks below read another (an object where an array belongs) is
        // bad input, like invalid JSON, not a failure of the corpus: exit 2, never 1 part-way through the checks.
        try { return RunChecks(o); }
        catch (InvalidOperationException ex) { Err.WriteLine($"error: malformed manifest: {ex.Message}"); return 2; }
    }

    private static int RunChecks(VerifyOptions o)
    {
        string corpus = Path.GetFullPath(o.Corpus);
        string manifestPath = o.Manifest ?? DefaultManifestPath(corpus);

        if (!Directory.Exists(corpus)) { Err.WriteLine($"error: corpus dir not found: {corpus}"); return 2; }
        if (!File.Exists(manifestPath)) { Err.WriteLine($"error: manifest not found: {manifestPath}"); return 2; }

        Console.WriteLine($"verify: corpus={corpus}");
        Console.WriteLine($"verify: manifest={manifestPath}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); }
        catch (JsonException ex) { Err.WriteLine($"error: manifest is not valid JSON: {ex.Message}"); return 2; }
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { Err.WriteLine("error: manifest root is not an object"); return 2; }
        // System.Text.Json keeps the last of two equal keys; a reader that keeps the first would see another manifest
        // than the one checked here.
        if (DuplicateKey(root, "") is { } dup) { Err.WriteLine($"error: manifest key {dup} appears twice"); return 2; }

        int fail = 0;

        // A mutate that died part-way leaves its journal: the tree then matches neither the base manifest nor any
        // delta, so whatever the sites below say, the corpus is not one the manifest describes.
        if (File.Exists(Path.Combine(corpus, Mutation.Mutator.JournalName)))
        {
            Err.WriteLine($"FAIL: {Mutation.Mutator.JournalName} present: a mutate of this corpus was interrupted " +
                                    "(or is still running); regenerate the corpus");
            fail++;
        }

        // --- _meta ---
        // Not a manifest at all (no _meta or no symbols object) is bad input, like invalid JSON: exit 2.
        if (!root.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object) { Err.WriteLine("error: manifest has no _meta object"); return 2; }
        int ver = meta.TryGetProperty("manifestVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int vi) ? vi : -1;
        // An unknown version is no manifest this verifier can read: bad input, before any check runs on a guess.
        if (ver is not (1 or 2)) { Err.WriteLine($"error: manifestVersion {(v.ValueKind == JsonValueKind.Undefined ? "missing" : v.GetRawText())} is not 1 or 2"); return 2; }
        Console.WriteLine($"  OK  _meta.manifestVersion == {ver}");
        bool v2 = ver == 2;

        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Object)
        { Err.WriteLine("error: manifest has no symbols object"); return 2; }
        if (MetaShapeError(meta) is { } metaShape) { Err.WriteLine($"error: malformed manifest: {metaShape}"); return 2; }
        if (WebShapeError(root, meta, symbols) is { } webShape) { Err.WriteLine($"error: malformed manifest: {webShape}"); return 2; }
        if (v2 && CppShapeError(root, meta, symbols) is { } shape) { Err.WriteLine($"error: malformed v2 manifest: {shape}"); return 2; }

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
                else { Err.WriteLine($"FAIL root '{rn}': not a declared symbol"); fail++; }
            }
            if (rootsOk > 0) Console.WriteLine($"  OK  _meta.roots ({rootsOk} declared entry point(s))");
        }

        var lineCache = new Dictionary<string, string[]>();
        var paths = new Generation.Containment(corpus);
        int defsOk = 0, refsOk = 0, gatedOk = 0, edgesOk = 0, missOk = 0, dupOk = 0, indirectOk = 0, cppOk = 0, webOk = 0;
        if (v2) fail += CheckCpp(corpus, meta, symbols, names, ref cppOk, paths, lineCache);
        if (root.TryGetProperty("closures", out var closuresEl)) fail += CheckWeb(corpus, meta, closuresEl, symbols, paths, ref webOk);

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
                { Err.WriteLine($"FAIL expectedMiss {name}: no def site"); fail++; continue; }
                if (HonestMissOk(paths, lineCache, site, name, out string mw)) missOk++;
                else { Err.WriteLine($"FAIL expectedMiss {name}: {mw}"); fail++; }
                continue;
            }

            // def site contains the symbol token. A symbol without a string def is a FAIL, not a skip: a truncated
            // or hand-edited manifest must not pass by dropping its def sites. A v2 (C++) symbol is named in the source
            // by its simpleName (a whole word: `m_2`, `Cls_4` for a construction, `~Cls_4`), not by its qualified key.
            string? simple = null;
            if (v2)
            {
                if (entry.TryGetProperty("simpleName", out var snEl) && snEl.ValueKind == JsonValueKind.String && snEl.GetString()!.Length > 0)
                    simple = snEl.GetString()!;
                else { Err.WriteLine($"FAIL {name}: a v2 symbol needs a simpleName"); fail++; continue; }
            }
            // A ctor shares its class's name, so its sites must name it outside a dtor's `~Cls`.
            bool ctor = v2 && entry.TryGetProperty("kind", out var kEl) && kEl.GetString() == "ctor";
            // A web template instance is named by token pasting: its def is the unit's #include line (CheckWeb checks
            // where it is defined); its refs are ordinary call sites.
            bool instance = entry.TryGetProperty("templateHead", out JsonElement _);
            if (!entry.TryGetProperty("def", out var defEl) || defEl.ValueKind != JsonValueKind.String)
            { Err.WriteLine($"FAIL def {name}: no def site"); fail++; }
            else if (instance)
            {
                if (TryReadLine(paths, lineCache, defEl.GetString()!, out string dl, out int _, out string dwhy) && dl.TrimStart().StartsWith("#include", StringComparison.Ordinal)) defsOk++;
                else { Err.WriteLine($"FAIL def {name}: {(dwhy.Length > 0 ? dwhy : "not an #include line")}"); fail++; }
            }
            else if (SiteHasToken(paths, lineCache, defEl.GetString()!, simple ?? name, out string why, wholeWord: v2, notAfterTilde: ctor)) defsOk++;
            else { Err.WriteLine($"FAIL def {name}: {why}"); fail++; }

            // each ref site contains the symbol token.
            if (entry.TryGetProperty("refs", out var refsEl))
                foreach (var r in refsEl.EnumerateArray())
                {
                    if (SiteHasToken(paths, lineCache, ManifestReader.Str(r), simple ?? name, out string why, wholeWord: v2, notAfterTilde: ctor)) refsOk++;
                    else { Err.WriteLine($"FAIL ref {name} @ {r.GetString()}: {why}"); fail++; }
                }

            // edges point at symbols that exist (the func_i -> func_{i-1} chain is intact).
            if (entry.TryGetProperty("edges", out var edgesEl))
                foreach (var e in edgesEl.EnumerateArray())
                {
                    string target = ManifestReader.Str(e);
                    if (names.Contains(target)) edgesOk++;
                    else { Err.WriteLine($"FAIL edge {name} -> {target}: target symbol missing"); fail++; }
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
                    { Err.WriteLine($"FAIL indirect {name} -> {target}: resolved target is not a declared symbol"); fail++; }
                    else if (!resolved && present)
                    { Err.WriteLine($"FAIL indirect {name} -> {target}: unresolved target must not be a declared symbol"); fail++; }
                    else indirectOk++;
                }

            // unreachableRefs: gated call present, behind #ifdef VENDOR_OK, and the vendor header is absent.
            if (entry.TryGetProperty("unreachableRefs", out var unrefsEl))
                foreach (var u in unrefsEl.EnumerateArray())
                {
                    string site = ManifestReader.Str(u);
                    if (!SiteHasToken(paths, lineCache, site, name, out string why))
                    { Err.WriteLine($"FAIL gated {name} @ {site}: {why}"); fail++; continue; }

                    string file = paths.Resolve(site[..site.LastIndexOf(':')])!;   // SiteHasToken resolved it
                    string text = File.ReadAllText(file);
                    if (!text.Contains("#ifdef VENDOR_OK"))
                    { Err.WriteLine($"FAIL gated {name} @ {site}: not behind #ifdef VENDOR_OK"); fail++; continue; }
                    if (anyVendorHeaderPresent)
                    { Err.WriteLine($"FAIL gated {name}: a VENDOR_missing_*.h header exists in the tree"); fail++; continue; }
                    gatedOk++;
                }
        }

        // dup-content: every path in a group must exist and hash to the recorded sha256 (i.e. be byte-
        // identical — the dedup target); every near-variant must exist and differ.
        if (root.TryGetProperty("dupGroups", out var dgEl) && dgEl.ValueKind == JsonValueKind.Object)
            foreach (var grp in dgEl.EnumerateObject())
            {
                string gname = grp.Name;
                if (grp.Value.ValueKind != JsonValueKind.Object
                    || !grp.Value.TryGetProperty("sha256", out var shaEl) || shaEl.ValueKind != JsonValueKind.String
                    || !grp.Value.TryGetProperty("paths", out var pathsEl) || pathsEl.ValueKind != JsonValueKind.Array
                    || grp.Value.TryGetProperty("nearVariants", out var nvEl) && nvEl.ValueKind != JsonValueKind.Array)
                { Err.WriteLine($"FAIL dup {gname}: malformed group (need sha256 + paths[], nearVariants[] optional)"); fail++; continue; }
                string sha = shaEl.GetString()!;
                foreach (var p in pathsEl.EnumerateArray())
                {
                    if (FileSha(paths, ManifestReader.Str(p), out string got, out string why) && got == sha) dupOk++;
                    else { Err.WriteLine($"FAIL dup {gname} @ {p.GetString()}: {(why.Length > 0 ? why : $"hash {got} != {sha}")}"); fail++; }
                }
                if (grp.Value.TryGetProperty("nearVariants", out var nvs))
                    foreach (var nv in nvs.EnumerateArray())
                    {
                        if (!FileSha(paths, ManifestReader.Str(nv), out string got, out string why))
                        { Err.WriteLine($"FAIL dup-near {gname} @ {nv.GetString()}: {why}"); fail++; }
                        else if (got == sha)
                        { Err.WriteLine($"FAIL dup-near {gname} @ {nv.GetString()}: identical to group (should differ)"); fail++; }
                    }
            }

        // indirectTruthSha: recompute the component digest from the manifest and assert it matches _meta —
        // a third independent reader (generator, this verifier, and the consumer) of the locked canonical form.
        if (meta.TryGetProperty("indirectTruthSha", out var itsEl) && itsEl.GetString() is { } declaredIts)
        {
            var parsed = ManifestReader.Load(manifestPath, maxVersion: 2);
            string got = IndirectDigest.Compute(parsed.Symbols, parsed.Roots);
            if (got == declaredIts) Console.WriteLine("  OK  _meta.indirectTruthSha reproduces");
            else { Err.WriteLine($"FAIL indirectTruthSha: manifest {declaredIts}, recomputed {got}"); fail++; }
        }

        // cppTruthSha (v2): required, and recomputed from the manifest like indirectTruthSha.
        if (v2)
        {
            if (meta.TryGetProperty("cppTruthSha", out var ctsEl) && ctsEl.GetString() is { } declaredCts)
            {
                var parsed = ManifestReader.Load(manifestPath, maxVersion: 2);
                string got = CppDigest.Compute(parsed.Symbols, parsed.StaticInitRoots);
                if (got == declaredCts) Console.WriteLine("  OK  _meta.cppTruthSha reproduces");
                else { Err.WriteLine($"FAIL cppTruthSha: manifest {declaredCts}, recomputed {got}"); fail++; }
            }
            else { Err.WriteLine("FAIL cppTruthSha: a v2 manifest must carry _meta.cppTruthSha"); fail++; }
        }

        Console.WriteLine($"  checked: {defsOk} defs, {refsOk} refs, {edgesOk} edges, {gatedOk} gated refs, {missOk} expected-miss, {dupOk} dup copies, {indirectOk} indirect edges" +
                          (v2 ? $", {cppOk} C++ facts" : "") + (webOk > 0 ? $", {webOk} include-web facts" : ""));
        // A manifest that declares nothing checkable used to PASS, having proved nothing.
        if (fail == 0 && defsOk + refsOk + edgesOk + gatedOk + missOk + dupOk + indirectOk + cppOk + webOk == 0)
        { Err.WriteLine("error: the manifest declares nothing to check (no symbol sites, dup groups or indirect edges)"); return 2; }
        if (fail == 0) { Console.WriteLine($"verify: PASS ({names.Count} symbols)"); return 0; }
        Err.WriteLine($"verify: FAIL ({fail} problem(s))");
        return 1;
    }

    private static readonly HashSet<string> CppKinds = new(StringComparer.Ordinal) { "function", "method", "ctor", "dtor", "template", "instance", "class" };

    /// <summary>The first object key (as a path) that appears twice anywhere under <paramref name="e"/>, or null.</summary>
    internal static string? DuplicateKey(JsonElement e, string at)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                string here = at.Length == 0 ? $"'{p.Name}'" : $"{at}.'{p.Name}'";
                if (!seen.Add(p.Name)) return here;
                if (DuplicateKey(p.Value, here) is { } d) return d;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var x in e.EnumerateArray()) if (DuplicateKey(x, $"{at}[{i++}]") is { } d) return d;
        }
        return null;
    }

    /// <summary>The _meta scalars every reader takes (both versions): a wrong type is bad input up front, not an
    /// exception after the checks have printed.</summary>
    internal static string? MetaShapeError(JsonElement meta)
    {
        if (meta.TryGetProperty("seed", out var s) && !(s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out _)))
            return $"_meta.seed {s.GetRawText()} is not a 32-bit integer";
        foreach (var k in new[] { "generatorVersion", "corpusRoot", "indirectTruthSha", "cppTruthSha" })
            if (meta.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.String) return $"_meta.{k} must be a string";
        if (meta.TryGetProperty("includeDirs", out var d)
            && !(d.ValueKind == JsonValueKind.Array && d.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0)))
            return "_meta.includeDirs must be an array of non-empty strings";
        return null;
    }

    internal static readonly string[] ClosureFields = ["files", "bytes", "reads", "readBytes", "depth", "defines", "templateInstances"];

    /// <summary>The include web's shape: <c>closures</c> maps a unit to its seven non-negative integers and needs
    /// <c>_meta.includeDirs</c>; a template instance carries <c>templateHead</c> and <c>includedVia</c> as strings,
    /// both or neither, and only next to closures (its truth is recomputed from them).</summary>
    internal static string? WebShapeError(JsonElement root, JsonElement meta, JsonElement symbols)
    {
        bool has = root.TryGetProperty("closures", out var cl);
        if (has)
        {
            if (cl.ValueKind != JsonValueKind.Object) return "closures must be an object";
            if (!meta.TryGetProperty("includeDirs", out _)) return "closures need _meta.includeDirs";
            foreach (var u in cl.EnumerateObject())
            {
                if (u.Value.ValueKind != JsonValueKind.Object) return $"closures.'{u.Name}' must be an object";
                foreach (var f in ClosureFields)
                    if (!u.Value.TryGetProperty(f, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out long x) || x < 0)
                        return $"closures.'{u.Name}'.{f} must be a non-negative integer";
            }
        }
        foreach (var s in symbols.EnumerateObject())
        {
            if (s.Value.ValueKind != JsonValueKind.Object) continue;
            bool th = s.Value.TryGetProperty("templateHead", out var a), iv = s.Value.TryGetProperty("includedVia", out var b);
            if (!th && !iv) continue;
            if (th != iv) return $"symbol '{s.Name}' needs both templateHead and includedVia, or neither";
            if (a.ValueKind != JsonValueKind.String || b.ValueKind != JsonValueKind.String) return $"symbol '{s.Name}': templateHead and includedVia must be strings";
            if (!has) return $"symbol '{s.Name}' is a template instance, but the manifest has no closures";
        }
        return null;
    }

    /// <summary>
    /// The include web: recompute every unit's closure with verify's own preprocessor (<see cref="IncludeClosure"/>)
    /// and compare all seven numbers; then match the template instances it finds against the manifest's instance
    /// symbols, both ways: each instance declared once, with the def, templateHead and includedVia the walk sees.
    /// </summary>
    private static int CheckWeb(string corpus, JsonElement meta, JsonElement closures, JsonElement symbols,
        Generation.Containment paths, ref int ok)
    {
        int fail = 0;
        var dirs = meta.GetProperty("includeDirs").EnumerateArray().Select(x => x.GetString()!).ToList();
        foreach (var d in dirs)
            if (paths.Resolve(d) is null) { Err.WriteLine($"FAIL includeDirs: {d} escapes the corpus root"); return 1; }
        var units = closures.EnumerateObject().Select(u => u.Name).ToList();
        foreach (var u in units)
            if (paths.Resolve(u) is not { } f || !File.Exists(f)) { Err.WriteLine($"FAIL closure {u}: unit file missing or outside the corpus"); return 1; }

        var walk = new IncludeClosure(corpus, dirs, units, paths).Run(units);
        var found = new Dictionary<string, IncludeClosure.Instance>(StringComparer.Ordinal);
        foreach (var u in closures.EnumerateObject())
        {
            var r = walk[u.Name];
            if (r.Error is not null) { Err.WriteLine($"FAIL closure {u.Name}: {r.Error}"); fail++; continue; }
            long[] got = [r.Files, r.Bytes, r.Reads, r.ReadBytes, r.Depth, r.Defines, r.TemplateInstances];
            for (int k = 0; k < ClosureFields.Length; k++)
            {
                long want = u.Value.GetProperty(ClosureFields[k]).GetInt64();
                if (want == got[k]) ok++;
                else { Err.WriteLine($"FAIL closure {u.Name}: {ClosureFields[k]} {want}, recomputed {got[k]}"); fail++; }
            }
            foreach (var x in r.Instances)
                if (!found.TryAdd(x.Name, x)) { Err.WriteLine($"FAIL instance {x.Name}: defined twice ({found[x.Name].Unit}:{found[x.Name].UnitLine} and {x.Unit}:{x.UnitLine})"); fail++; }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in symbols.EnumerateObject())
        {
            if (!s.Value.TryGetProperty("templateHead", out var th)) continue;
            declared.Add(s.Name);
            if (!found.TryGetValue(s.Name, out var x)) { Err.WriteLine($"FAIL instance {s.Name}: no unit's closure defines it"); fail++; continue; }
            string def = s.Value.TryGetProperty("def", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString()! : "";
            string via = s.Value.GetProperty("includedVia").GetString()!;
            if (def != $"{x.Unit}:{x.UnitLine}") { Err.WriteLine($"FAIL instance {s.Name}: def {def}, the walk has {x.Unit}:{x.UnitLine}"); fail++; }
            else if (th.GetString() != $"{x.File}:{x.HeadLine}") { Err.WriteLine($"FAIL instance {s.Name}: templateHead {th.GetString()}, the walk has {x.File}:{x.HeadLine}"); fail++; }
            else if (via != x.File) { Err.WriteLine($"FAIL instance {s.Name}: includedVia {via}, the walk has {x.File}"); fail++; }
            else ok++;
        }
        foreach (var n in found.Keys.Where(n => !declared.Contains(n)).Order(StringComparer.Ordinal))
        { Err.WriteLine($"FAIL instance {n}: the walk finds it at {found[n].Unit}:{found[n].UnitLine}, the manifest does not declare it"); fail++; }
        if (fail == 0) Console.WriteLine($"  OK  include web: {units.Count} closure(s), {found.Count} template instance(s) recomputed");
        return fail;
    }

    /// <summary>
    /// The shape of a v2 manifest: every field the C++ checks read has the type the schema gives it, symbol and class
    /// names are unique, and the v1-only C fields are absent. A wrong shape is bad input (exit 2), like invalid JSON:
    /// a string where an array belongs must not be skipped as if the field were empty.
    /// </summary>
    internal static string? CppShapeError(JsonElement root, JsonElement meta, JsonElement symbols)
    {
        static bool Strs(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String);
        static bool Bool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

        // (Duplicate keys, anywhere in the manifest, are refused before this runs.)
        if (!meta.TryGetProperty("classes", out var cls) || cls.ValueKind != JsonValueKind.Object) return "_meta.classes must be an object";
        foreach (var c in cls.EnumerateObject()) if (!Strs(c.Value)) return $"_meta.classes['{c.Name}'] must be an array of strings";
        foreach (var k in new[] { "roots", "staticInitRoots" })
            if (meta.TryGetProperty(k, out var a) && !Strs(a)) return $"_meta.{k} must be an array of strings";
        foreach (var k in new[] { "lang", "cppTruthSha" })
            if (meta.TryGetProperty(k, out var a) && a.ValueKind != JsonValueKind.String) return $"_meta.{k} must be a string";
        if (root.TryGetProperty("dupGroups", out _)) return "'dupGroups' is a v1 (C) field";
        if (meta.TryGetProperty("indirectTruthSha", out _)) return "_meta.indirectTruthSha is a v1 (C) field";

        foreach (var s in symbols.EnumerateObject())
        {
            var e = s.Value;
            string at = $"symbol '{s.Name}'";
            if (e.ValueKind != JsonValueKind.Object) return $"{at} must be an object";
            foreach (var k in new[] { "unreachableRefs", "indirectEdges", "expectedMiss", "bytes" })
                if (e.TryGetProperty(k, out _)) return $"{at}: '{k}' is a v1 (C) field";
            if (!e.TryGetProperty("def", out var df) || df.ValueKind != JsonValueKind.String) return $"{at} needs a string 'def'";
            foreach (var k in new[] { "kind", "simpleName", "instanceOf" })
                if (e.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.String) return $"{at}: '{k}' must be a string";
            if (e.TryGetProperty("pure", out var pu) && !Bool(pu)) return $"{at}: 'pure' must be a boolean";
            foreach (var k in new[] { "refs", "edges", "implicitEdges" })
                if (e.TryGetProperty(k, out var v) && !Strs(v)) return $"{at}: '{k}' must be an array of strings";
            if (e.TryGetProperty("virtualEdges", out var ve))
            {
                if (ve.ValueKind != JsonValueKind.Array) return $"{at}: 'virtualEdges' must be an array";
                foreach (var v in ve.EnumerateArray())
                    if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("static", out var st) || st.ValueKind != JsonValueKind.String
                        || !v.TryGetProperty("cha", out var c) || !Strs(c) || !v.TryGetProperty("rta", out var r) || !Strs(r))
                        return $"{at}: each virtualEdges record needs a string 'static' and string arrays 'cha' and 'rta'";
            }
            if (e.TryGetProperty("memberPtrEdges", out var me))
            {
                if (me.ValueKind != JsonValueKind.Array) return $"{at}: 'memberPtrEdges' must be an array";
                foreach (var m in me.EnumerateArray())
                    if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String
                        || !m.TryGetProperty("via", out var v) || v.ValueKind != JsonValueKind.String
                        || !m.TryGetProperty("dispatched", out var dp) || !Bool(dp))
                        return $"{at}: each memberPtrEdges record needs string 'target' and 'via' and a boolean 'dispatched'";
            }
        }
        return null;
    }

    // A site as the generator writes it: a forward-slash relative path (no '.' or '..' segment, no drive, no
    // backslash) and a positive line number with nothing else around it.
    private static readonly Regex CanonicalSite = new(@"^(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*:[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex ClassLine = new(@"^class (\w+)( final)?(?: : (public [\w:]+(?:, public [\w:]+)*))? \{$", RegexOptions.Compiled);
    private static readonly Regex TemplateArgs = new("<[^<>]*>", RegexOptions.Compiled);
    // The generated program's line shapes. A declaration (no body): `void m_1();`, `virtual void m_1() override;`,
    // `Cls_3();`, `~Cls_3();` (calls are always qualified or through a pointer, so never look like these). A
    // definition: a function, method, ctor or dtor body opening on its line, a `class` line, or a pure `= 0;`
    // (`int main()` is not a symbol, so not one).
    private static readonly Regex DeclLine = new(@"^\s*(?:(?:virtual )?void \w+\(\)(?: override)?(?: final)?|~?\w+\(\));$", RegexOptions.Compiled);
    private static readonly Regex DefLine = new(@"^(?:\s*(?:virtual )?void [\w:~]+\(.*\)(?: override)?(?: final)? \{|\s*(?:\w+::)?~?\w+\(\) \{|class \w+.* \{|\s*virtual void \w+\(\) = 0;)$", RegexOptions.Compiled);

    /// <summary>
    /// The C++ (v2) facts, D9 of docs/cpp-profile-design.md, checked against the manifest and the source (the shape is
    /// already known good). The hierarchy: _meta.classes is exactly the class symbols, acyclic, and each class's bases
    /// are the ones its `class` line names. Each symbol: simpleName is the key's last name, kind fits the key (and a
    /// template's def is under cpp/tmpl/), `pure` is set exactly on the `= 0;` declarations, defs are unique (an
    /// instance shares its template's), sites are canonical, refs hold no duplicate and not the def. Each edge kind
    /// points at the right kind (implicit: ctor/dtor, member pointer: method, staticInitRoots: ctor/dtor, instanceOf:
    /// template). Each virtual record: static is a method, cha is exactly the non-pure overrides below some receiver
    /// class at or below static's class that resolves the call to static, and rta is within cha (rta's own exactness
    /// needs the program's constructions; verify does not check it). Sites: a def is a definition line in its
    /// owner's files (a member's in its class's .hpp/.cpp, a function's outside every class and template file), no
    /// site is a declaration (the rt/*.hpp prototypes, an in-class declaration), and a ref is never another symbol's
    /// def line (a class symbol, a type use, is exempt). Completeness: every definition line under cpp/ is some
    /// symbol's def, so a deleted symbol fails. Edges never target a class or a template. Returns the failure count.
    /// </summary>
    private static int CheckCpp(string corpus, JsonElement meta, JsonElement symbols, HashSet<string> names, ref int ok,
        Generation.Containment paths, Dictionary<string, string[]> cache)
    {
        int fail = 0;
        void Fail(string m) { Err.WriteLine("FAIL " + m); fail++; }
        string? LineAt(string site) => TryReadLine(paths, cache, site, out string t, out _, out _) ? t.Trim() : null;
        static string Strip(string key) => TemplateArgs.Replace(key, "");
        static string Owner(string key) { key = Strip(key); int i = key.LastIndexOf("::", StringComparison.Ordinal); return i < 0 ? "" : key[..i]; }
        static string Last(string key) { key = Strip(key); int i = key.LastIndexOf("::", StringComparison.Ordinal); return i < 0 ? key : key[(i + 2)..]; }
        string? Prop(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static List<string> Arr(JsonElement e, string key) =>
            e.TryGetProperty(key, out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : new();

        if (Prop(meta, "lang") != "cpp") Fail($"_meta.lang '{Prop(meta, "lang")}': a v2 manifest is lang \"cpp\"");

        // --- the hierarchy ---
        var bases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in meta.GetProperty("classes").EnumerateObject()) bases[c.Name] = c.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
        bool hierarchyOk = true;
        foreach (var (c, bs) in bases)
            foreach (var b in bs.Where(b => !bases.ContainsKey(b))) { Fail($"_meta.classes: {c}'s base {b} is not a class"); hierarchyOk = false; }
        // Topological order (Kahn), bases first; whatever is left over sits on a cycle or below one.
        var children = bases.Keys.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (c, bs) in bases)
        {
            pending[c] = bs.Count(bases.ContainsKey);
            foreach (var b in bs.Where(bases.ContainsKey)) children[b].Add(c);
        }
        var topo = new List<string>();
        var ready = new Queue<string>(bases.Keys.Where(c => pending[c] == 0));
        while (ready.Count > 0)
        {
            var c = ready.Dequeue();
            topo.Add(c);
            foreach (var d in children[c]) if (--pending[d] == 0) ready.Enqueue(d);
        }
        if (topo.Count < bases.Count)
        {
            // A left-over class still waits on a left-over base, so walking up through those must repeat: the repeated
            // stretch is the cycle.
            var path = new List<string> { bases.Keys.First(c => pending[c] > 0) };
            while (path.IndexOf(path[^1]) == path.Count - 1)
                path.Add(bases[path[^1]].First(b => bases.ContainsKey(b) && pending[b] > 0));
            Fail($"_meta.classes: an inheritance cycle {string.Join(" -> ", path.Skip(path.IndexOf(path[^1])))}");
            hierarchyOk = false;
        }
        else ok++;

        var kind = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in symbols.EnumerateObject()) kind[s.Name] = Prop(s.Value, "kind");
        foreach (var c in bases.Keys.Where(c => !names.Contains(c) || kind[c] != "class")) Fail($"_meta.classes: {c} is not a class symbol");

        // Where definitions live: every def site, and each class's file stem (cpp/ns_1/cls_4 for cls_4.hpp/.cpp).
        static string FileOf(string site) => site[..Math.Max(0, site.LastIndexOf(':'))];
        static string StemOf(string file) { int i = file.LastIndexOf('.'); return i > file.LastIndexOf('/') ? file[..i] : file; }
        var allDefs = new HashSet<string>(StringComparer.Ordinal);
        var classStem = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in symbols.EnumerateObject())
            if (Prop(s.Value, "def") is { } d)
            {
                allDefs.Add(d);
                if (kind[s.Name] == "class") classStem[s.Name] = StemOf(FileOf(d));
            }
        var classStems = classStem.Values.ToHashSet(StringComparer.Ordinal);

        // --- per symbol ---
        var defOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var methodsOf = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var pure = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in symbols.EnumerateObject())
        {
            string name = s.Name;
            var e = s.Value;
            string? k = kind[name];
            if (k is null || !CppKinds.Contains(k)) { Fail($"{name}: kind '{k}' is not one of {string.Join("|", CppKinds)}"); continue; }
            ok++;
            string? def = Prop(e, "def");
            string? sn = Prop(e, "simpleName");
            if (sn is not null && sn != Last(name)) Fail($"{name}: simpleName '{sn}' is not the key's last name '{Last(name)}'");

            // kind against the key's shape: a template argument makes an instance; a key under a class is a member
            // (ctor / dtor by name); otherwise a function, or a template when defined in a template file.
            string own = Owner(name), last = Last(name);
            string ownSimple = Last(own);
            string want = name.Contains('<') ? "instance"
                : bases.ContainsKey(name) ? "class"
                : bases.ContainsKey(own) ? (last == ownSimple ? "ctor" : last == "~" + ownSimple ? "dtor" : "method")
                : def is not null && def.StartsWith("cpp/tmpl/", StringComparison.Ordinal) ? "template" : "function";
            if (k != want) Fail($"{name}: kind '{k}' does not fit its key and def (expected '{want}')");
            if (k == "method" && bases.ContainsKey(own))
            {
                if (!methodsOf.TryGetValue(own, out var ms)) methodsOf[own] = ms = new(StringComparer.Ordinal);
                ms[last] = name;
            }

            string? inst = Prop(e, "instanceOf");
            if (k == "instance")
            {
                if (inst is null || !names.Contains(inst) || kind[inst] != "template") Fail($"instance {name}: instanceOf '{inst}' is not a declared template");
                else if (Prop(symbols.GetProperty(inst), "def") != def) Fail($"instance {name}: def {def} is not its template's def");
                else ok++;
            }
            else if (inst is not null) Fail($"{name}: only an instance has instanceOf");

            // Sites: canonical, unique defs (an instance shares its template's), no duplicate ref, the def not a ref.
            var refs = Arr(e, "refs");
            foreach (var site in refs.Prepend(def).OfType<string>().Where(x => !CanonicalSite.IsMatch(x)))
                Fail($"{name}: site '{site}' is not canonical (a/b/file.ext:line)");
            if (def is not null && k != "instance")
            {
                if (defOwner.TryGetValue(def, out var other)) Fail($"{name}: def {def} is also the def of {other}");
                else defOwner[def] = name;
            }
            if (def is not null && refs.Contains(def)) Fail($"{name}: its def {def} is listed as a ref");
            if (refs.Count != refs.Distinct().Count()) Fail($"{name}: a ref is listed twice");

            // Where the sites sit. A class symbol's name is a type use on any line, declarations included.
            if (k != "class" && def is not null)
            {
                bool placed = true;
                foreach (var site in refs.Prepend(def))
                    if (FileOf(site).StartsWith("cpp/rt/", StringComparison.Ordinal) && FileOf(site).EndsWith(".hpp", StringComparison.Ordinal)
                        || LineAt(site) is { } sl && DeclLine.IsMatch(sl))
                    { Fail($"{name}: site {site} is a declaration, neither a def nor a ref"); placed = false; }
                foreach (var site in refs.Where(allDefs.Contains))
                { Fail($"{name}: ref {site} is a definition line (a def, not a call or use)"); placed = false; }
                string defStem = StemOf(FileOf(def));
                if (LineAt(def) is { } dl2 && !DefLine.IsMatch(dl2)) { Fail($"{name}: def {def} is not a definition line"); placed = false; }
                if (k is "method" or "ctor" or "dtor" && classStem.TryGetValue(own, out var cs) && defStem != cs)
                { Fail($"{name}: def {def} is outside its class's files ({cs}.hpp/.cpp)"); placed = false; }
                if (k == "function" && (classStems.Contains(defStem) || def.StartsWith("cpp/tmpl/", StringComparison.Ordinal)))
                { Fail($"{name}: a function's def {def} sits in a class or template file"); placed = false; }
                if (placed) ok++;
            }
            foreach (var t in Arr(e, "edges").Where(t => names.Contains(t) && kind[t] is "class" or "template"))
                Fail($"edge {name} -> {t}: a call targets a function, method, ctor, dtor or instance, not a {kind[t]}");

            // pure: exactly the methods declared `= 0;`.
            bool isPure = e.TryGetProperty("pure", out var pu) && pu.ValueKind == JsonValueKind.True;
            bool declaresPure = def is not null && LineAt(def) is { } dl && dl.EndsWith("= 0;", StringComparison.Ordinal);
            if (isPure && k != "method") Fail($"{name}: only a method can be pure");
            else if (isPure != declaresPure && k == "method")
                Fail(isPure ? $"{name}: pure, but its def line {def} is not a '= 0;' declaration" : $"{name}: its def {def} declares it '= 0;' but it is not marked pure");
            else ok++;
            if (isPure) pure.Add(name);

            if (k == "class")
            {
                // A class symbol is a def + type-use refs only, and its `class` line names exactly its _meta.classes bases.
                if (new[] { "edges", "implicitEdges", "virtualEdges", "memberPtrEdges" }.Any(p => e.TryGetProperty(p, out var a) && a.GetArrayLength() > 0))
                    Fail($"class {name}: a class symbol has no edges");
                if (def is not null && LineAt(def) is { } cl && bases.TryGetValue(name, out var bs))
                {
                    var m = ClassLine.Match(cl);
                    var named = m.Success && m.Groups[3].Success ? m.Groups[3].Value.Split(", ").Select(x => x["public ".Length..]).ToList() : new List<string>();
                    if (!m.Success || m.Groups[1].Value != last) Fail($"class {name}: def {def} is not its `class {last}` line");
                    else if (!named.SequenceEqual(bs)) Fail($"class {name}: _meta.classes bases [{string.Join(", ", bs)}] are not the ones its class line names [{string.Join(", ", named)}]");
                    else ok++;
                }
            }

            foreach (var t in Arr(e, "implicitEdges"))
            {
                if (!names.Contains(t)) Fail($"implicit {name} -> {t}: target symbol missing");
                else if (kind[t] is not ("ctor" or "dtor")) Fail($"implicit {name} -> {t}: an implicit call targets a ctor or dtor, not a {kind[t]}");
                else ok++;
            }
            if (e.TryGetProperty("memberPtrEdges", out var me))
                foreach (var m in me.EnumerateArray())
                {
                    string tn = Prop(m, "target")!, via = Prop(m, "via")!;
                    if (!names.Contains(tn)) Fail($"memberPtr {name} -> {tn}: target symbol missing");
                    else if (kind[tn] != "method") Fail($"memberPtr {name} -> {tn}: a member pointer targets a method, not a {kind[tn]}");
                    else if (via is not ("table" or "arg")) Fail($"memberPtr {name} -> {tn}: via '{via}' is not table|arg");
                    else ok++;
                }
        }

        // Completeness: every definition line in the program is some symbol's def (main excepted: not a symbol).
        int missing = 0;
        string cppDir = Path.Combine(corpus, "cpp");
        if (Directory.Exists(cppDir))
            foreach (var file in Directory.EnumerateFiles(cppDir, "*", NoLinks).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(corpus, file).Replace('\\', '/');
                int n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    if (!DefLine.IsMatch(line) || allDefs.Contains($"{rel}:{n}")) continue;
                    if (++missing <= 20) Fail($"{rel}:{n} defines something no symbol's def names: {line.Trim()}");
                }
            }
        if (missing > 20) Fail($"... {missing - 20} more definition lines no symbol's def names");
        else if (missing == 0) ok++;

        // No list names a target twice (the generator dedups; a reader counting edges would count one twice).
        foreach (var s in symbols.EnumerateObject())
        {
            foreach (var key in new[] { "edges", "implicitEdges" })
                if (Arr(s.Value, key) is var l && l.Count != l.Distinct().Count()) Fail($"{s.Name}: a target is listed twice in {key}");
            if (s.Value.TryGetProperty("memberPtrEdges", out var mpe)
                && mpe.EnumerateArray().Select(m => $"{Prop(m, "target")}|{Prop(m, "via")}|{m.GetProperty("dispatched").GetRawText()}").ToList() is var mk
                && mk.Count != mk.Distinct().Count())
                Fail($"{s.Name}: a member-pointer record is listed twice");
        }
        foreach (var key in new[] { "roots", "staticInitRoots" })
            if (Arr(meta, key) is var l && l.Count != l.Distinct().Count()) Fail($"_meta.{key}: an entry is listed twice");

        foreach (var r in Arr(meta, "staticInitRoots"))
        {
            if (!names.Contains(r)) Fail($"staticInitRoot '{r}': not a declared symbol");
            else if (kind[r] is not ("ctor" or "dtor")) Fail($"staticInitRoot '{r}': a static object's root is its ctor or dtor, not a {kind[r]}");
            else ok++;
        }

        // --- virtual records ---
        // Resolve(c, n): the method a call of n through class c reaches: c's own, else the first base (in order, depth
        // first) that has one; a class's bases never share a name, so the order is unambiguous. Walked with a stack,
        // so a tall (hand-made) chain cannot overflow, visiting each class once, so a lattice of diamonds stays linear
        // (a class reached again had nothing the first time). Overrides(r, n): what cha is for a receiver r.
        var resolved = new Dictionary<(string, string), string?>();
        string? Resolve(string c, string n)
        {
            if (resolved.TryGetValue((c, n), out var hit)) return hit;
            string? got = null;
            var stack = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            stack.Push(c);
            while (stack.Count > 0 && got is null)
            {
                var x = stack.Pop();
                if (!visited.Add(x)) continue;
                if (methodsOf.TryGetValue(x, out var ms) && ms.TryGetValue(n, out var own)) got = own;
                else for (int i = bases[x].Count - 1; i >= 0; i--) stack.Push(bases[x][i]);
            }
            return resolved[(c, n)] = got;
        }
        IEnumerable<string> Subtree(string c)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { c };
            var stack = new Stack<string>();
            stack.Push(c);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                yield return x;
                foreach (var d in children[x]) if (seen.Add(d)) stack.Push(d);
            }
        }
        var overrides = new Dictionary<(string, string), HashSet<string>>();
        HashSet<string> Overrides(string r, string n)
        {
            if (!overrides.TryGetValue((r, n), out var set))
                overrides[(r, n)] = set = Subtree(r).Select(c => Resolve(c, n)).OfType<string>().Where(x => !pure.Contains(x)).ToHashSet(StringComparer.Ordinal);
            return set;
        }
        foreach (var s in symbols.EnumerateObject())
        {
            if (!s.Value.TryGetProperty("virtualEdges", out var ve)) continue;
            foreach (var v in ve.EnumerateArray())
            {
                string st = Prop(v, "static")!;
                string at = $"virtual {s.Name} [{st}]";
                if (!names.Contains(st) || kind[st] != "method") { Fail($"{at}: static is not a declared method"); continue; }
                var cha = Arr(v, "cha");
                var rta = Arr(v, "rta");
                bool good = true;
                foreach (var t in cha)
                {
                    if (!names.Contains(t) || kind[t] != "method") { Fail($"{at}: cha target '{t}' is not a declared method"); good = false; }
                    else if (pure.Contains(t)) { Fail($"{at}: cha target '{t}' is pure (no body to dispatch to)"); good = false; }
                }
                if (cha.Count != cha.Distinct().Count()) { Fail($"{at}: a cha target is listed twice"); good = false; }
                foreach (var t in rta.Where(t => !cha.Contains(t))) { Fail($"{at}: rta target '{t}' is not in cha"); good = false; }
                if (rta.Count != rta.Distinct().Count()) { Fail($"{at}: an rta target is listed twice"); good = false; }
                string o = Owner(st), n = Last(st);
                if (!good || !hierarchyOk || !bases.ContainsKey(o)) continue;
                // The receiver is not recorded: any class at or below static's class that resolves the call to static
                // could be it. cha must be exactly the override set of one of them (static's own class first).
                var want = cha.ToHashSet(StringComparer.Ordinal);
                if (Overrides(o, n).SetEquals(want) || Subtree(o).Any(r => r != o && Resolve(r, n) == st && Overrides(r, n).SetEquals(want))) ok++;
                else Fail($"{at}: cha is not the set of non-pure overrides below any receiver at or below {o} (static's class gives [{string.Join(", ", Overrides(o, n).Order(StringComparer.Ordinal))}])");
            }
        }
        return fail;
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

    /// <summary>Assert the line named by "<rel>:<line>" exists and contains "<symbol>(" (v1), or, with
    /// <paramref name="wholeWord"/> (v2), <paramref name="symbol"/> as a whole word.</summary>
    private static bool SiteHasToken(Generation.Containment paths, Dictionary<string, string[]> cache, string site,
        string symbol, out string why, bool wholeWord = false, bool notAfterTilde = false)
    {
        if (!TryReadLine(paths, cache, site, out string text, out int line, out why)) return false;
        if (wholeWord ? !HasWord(text, symbol, notAfterTilde) : !text.Contains(symbol + "("))
        { why = wholeWord ? $"line {line} lacks the word '{symbol}'" : $"line {line} lacks '{symbol}('"; return false; }
        return true;
    }

    /// <summary>The text of the line named by "<rel>:<line>", inside the corpus root.</summary>
    private static bool TryReadLine(Generation.Containment paths, Dictionary<string, string[]> cache, string site,
        out string text, out int line, out string why)
    {
        text = "";
        line = 0;
        int c = site.LastIndexOf(':');
        if (c < 0) { why = "malformed site (no :line)"; return false; }
        string rel = site[..c];
        if (!int.TryParse(site[(c + 1)..], out line)) { why = "malformed line number"; return false; }

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
        text = lines[line - 1];
        why = "";
        return true;
    }

    /// <summary>True when <paramref name="word"/> occurs in <paramref name="text"/> with no identifier character on
    /// either side (a leading '~' already bounds a dtor name on the left). With <paramref name="notAfterTilde"/>, an
    /// occurrence right after a '~' (the dtor of a class named <paramref name="word"/>) does not count.</summary>
    internal static bool HasWord(string text, string word, bool notAfterTilde = false)
    {
        static bool Ident(char ch) => char.IsAsciiLetterOrDigit(ch) || ch == '_';
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            int end = i + word.Length;
            bool left = i == 0 || !Ident(word[0]) || (!Ident(text[i - 1]) && !(notAfterTilde && text[i - 1] == '~'));
            if (left && (end == text.Length || !Ident(text[end]))) return true;
        }
        return false;
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
