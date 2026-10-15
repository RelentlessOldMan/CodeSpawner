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
        // An unknown version is no manifest this verifier can read: bad input, before any check runs on a guess.
        if (ver is not (1 or 2)) { Console.Error.WriteLine($"error: manifestVersion {(v.ValueKind == JsonValueKind.Undefined ? "missing" : v.GetRawText())} is not 1 or 2"); return 2; }
        Console.WriteLine($"  OK  _meta.manifestVersion == {ver}");
        bool v2 = ver == 2;

        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Object)
        { Console.Error.WriteLine("error: manifest has no symbols object"); return 2; }
        if (v2 && CppShapeError(meta, symbols) is { } shape) { Console.Error.WriteLine($"error: malformed v2 manifest: {shape}"); return 2; }

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
        int defsOk = 0, refsOk = 0, gatedOk = 0, edgesOk = 0, missOk = 0, dupOk = 0, indirectOk = 0, cppOk = 0;
        if (v2) fail += CheckCpp(meta, symbols, names, ref cppOk, paths, lineCache);

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
            // or hand-edited manifest must not pass by dropping its def sites. A v2 (C++) symbol is named in the source
            // by its simpleName (a whole word: `m_2`, `Cls_4` for a construction, `~Cls_4`), not by its qualified key.
            string? simple = null;
            if (v2)
            {
                if (entry.TryGetProperty("simpleName", out var snEl) && snEl.ValueKind == JsonValueKind.String && snEl.GetString()!.Length > 0)
                    simple = snEl.GetString()!;
                else { Console.Error.WriteLine($"FAIL {name}: a v2 symbol needs a simpleName"); fail++; continue; }
            }
            // A ctor shares its class's name, so its sites must name it outside a dtor's `~Cls`.
            bool ctor = v2 && entry.TryGetProperty("kind", out var kEl) && kEl.GetString() == "ctor";
            if (!entry.TryGetProperty("def", out var defEl) || defEl.ValueKind != JsonValueKind.String)
            { Console.Error.WriteLine($"FAIL def {name}: no def site"); fail++; }
            else if (SiteHasToken(paths, lineCache, defEl.GetString()!, simple ?? name, out string why, wholeWord: v2, notAfterTilde: ctor)) defsOk++;
            else { Console.Error.WriteLine($"FAIL def {name}: {why}"); fail++; }

            // each ref site contains the symbol token.
            if (entry.TryGetProperty("refs", out var refsEl))
                foreach (var r in refsEl.EnumerateArray())
                {
                    if (SiteHasToken(paths, lineCache, ManifestReader.Str(r), simple ?? name, out string why, wholeWord: v2, notAfterTilde: ctor)) refsOk++;
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
            var parsed = ManifestReader.Load(manifestPath, maxVersion: 2);
            string got = IndirectDigest.Compute(parsed.Symbols, parsed.Roots);
            if (got == declaredIts) Console.WriteLine("  OK  _meta.indirectTruthSha reproduces");
            else { Console.Error.WriteLine($"FAIL indirectTruthSha: manifest {declaredIts}, recomputed {got}"); fail++; }
        }

        // cppTruthSha (v2): required, and recomputed from the manifest like indirectTruthSha.
        if (v2)
        {
            if (meta.TryGetProperty("cppTruthSha", out var ctsEl) && ctsEl.GetString() is { } declaredCts)
            {
                var parsed = ManifestReader.Load(manifestPath, maxVersion: 2);
                string got = CppDigest.Compute(parsed.Symbols, parsed.StaticInitRoots);
                if (got == declaredCts) Console.WriteLine("  OK  _meta.cppTruthSha reproduces");
                else { Console.Error.WriteLine($"FAIL cppTruthSha: manifest {declaredCts}, recomputed {got}"); fail++; }
            }
            else { Console.Error.WriteLine("FAIL cppTruthSha: a v2 manifest must carry _meta.cppTruthSha"); fail++; }
        }

        Console.WriteLine($"  checked: {defsOk} defs, {refsOk} refs, {edgesOk} edges, {gatedOk} gated refs, {missOk} expected-miss, {dupOk} dup copies, {indirectOk} indirect edges" +
                          (v2 ? $", {cppOk} C++ facts" : ""));
        // A manifest that declares nothing checkable used to PASS, having proved nothing.
        if (fail == 0 && defsOk + refsOk + edgesOk + gatedOk + missOk + dupOk + indirectOk + cppOk == 0)
        { Console.Error.WriteLine("error: the manifest declares nothing to check (no symbol sites, dup groups or indirect edges)"); return 2; }
        if (fail == 0) { Console.WriteLine($"verify: PASS ({names.Count} symbols)"); return 0; }
        Console.Error.WriteLine($"verify: FAIL ({fail} problem(s))");
        return 1;
    }

    private static readonly HashSet<string> CppKinds = new(StringComparer.Ordinal) { "function", "method", "ctor", "dtor", "template", "instance", "class" };

    /// <summary>
    /// The shape of a v2 manifest: every field the C++ checks read has the type the schema gives it, symbol and class
    /// names are unique, and the v1-only C fields are absent. A wrong shape is bad input (exit 2), like invalid JSON:
    /// a string where an array belongs must not be skipped as if the field were empty.
    /// </summary>
    internal static string? CppShapeError(JsonElement meta, JsonElement symbols)
    {
        static bool Strs(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String);
        static bool Bool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;
        static string? Dup(JsonElement obj, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in obj.EnumerateObject()) if (!seen.Add(p.Name)) return $"{what} '{p.Name}' appears twice";
            return null;
        }

        if (!meta.TryGetProperty("classes", out var cls) || cls.ValueKind != JsonValueKind.Object) return "_meta.classes must be an object";
        if (Dup(cls, "_meta.classes key") is { } d1) return d1;
        foreach (var c in cls.EnumerateObject()) if (!Strs(c.Value)) return $"_meta.classes['{c.Name}'] must be an array of strings";
        foreach (var k in new[] { "roots", "staticInitRoots" })
            if (meta.TryGetProperty(k, out var a) && !Strs(a)) return $"_meta.{k} must be an array of strings";
        foreach (var k in new[] { "lang", "cppTruthSha" })
            if (meta.TryGetProperty(k, out var a) && a.ValueKind != JsonValueKind.String) return $"_meta.{k} must be a string";
        if (Dup(symbols, "symbol") is { } d2) return d2;

        foreach (var s in symbols.EnumerateObject())
        {
            var e = s.Value;
            string at = $"symbol '{s.Name}'";
            if (e.ValueKind != JsonValueKind.Object) return $"{at} must be an object";
            foreach (var k in new[] { "unreachableRefs", "indirectEdges", "expectedMiss", "bytes" })
                if (e.TryGetProperty(k, out _)) return $"{at}: '{k}' is a v1 (C) field";
            foreach (var k in new[] { "def", "kind", "simpleName", "instanceOf" })
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

    /// <summary>
    /// The C++ (v2) facts, D9 of docs/cpp-profile-design.md, checked against the manifest and the source (the shape is
    /// already known good). The hierarchy: _meta.classes is exactly the class symbols, acyclic, and each class's bases
    /// are the ones its `class` line names. Each symbol: simpleName is the key's last name, kind fits the key (and a
    /// template's def is under cpp/tmpl/), `pure` is set exactly on the `= 0;` declarations, defs are unique (an
    /// instance shares its template's), sites are canonical, refs hold no duplicate and not the def. Each edge kind
    /// points at the right kind (implicit: ctor/dtor, member pointer: method, staticInitRoots: ctor/dtor, instanceOf:
    /// template). Each virtual record: static is a method, cha is exactly the non-pure overrides below some receiver
    /// class at or below static's class that resolves the call to static, and rta is within cha (rta's own exactness
    /// needs the program's constructions, so the digest and the tests carry it). Returns the failure count.
    /// </summary>
    private static int CheckCpp(JsonElement meta, JsonElement symbols, HashSet<string> names, ref int ok,
        Generation.Containment paths, Dictionary<string, string[]> cache)
    {
        int fail = 0;
        void Fail(string m) { Console.Error.WriteLine("FAIL " + m); fail++; }
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
        // Topological order (Kahn), bases first; whatever is left over sits on a cycle.
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
            Fail($"_meta.classes: an inheritance cycle through {bases.Keys.First(c => pending[c] > 0)}");
            hierarchyOk = false;
        }
        else ok++;

        var kind = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in symbols.EnumerateObject()) kind[s.Name] = Prop(s.Value, "kind");
        foreach (var c in bases.Keys.Where(c => !names.Contains(c) || kind[c] != "class")) Fail($"_meta.classes: {c} is not a class symbol");

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

        foreach (var r in Arr(meta, "staticInitRoots"))
        {
            if (!names.Contains(r)) Fail($"staticInitRoot '{r}': not a declared symbol");
            else if (kind[r] is not ("ctor" or "dtor")) Fail($"staticInitRoot '{r}': a static object's root is its ctor or dtor, not a {kind[r]}");
            else ok++;
        }

        // --- virtual records ---
        // Resolve(c, n): the method a call of n through class c reaches: c's own, else the first base (in order, depth
        // first) that has one; a class's bases never share a name, so the order is unambiguous. Walked with a stack,
        // so a tall (hand-made) chain cannot overflow. Overrides(r, n): what cha is for a receiver r.
        var resolved = new Dictionary<(string, string), string?>();
        string? Resolve(string c, string n)
        {
            if (resolved.TryGetValue((c, n), out var hit)) return hit;
            string? got = null;
            var stack = new Stack<string>();
            stack.Push(c);
            while (stack.Count > 0 && got is null)
            {
                var x = stack.Pop();
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
