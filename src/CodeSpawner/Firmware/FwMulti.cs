using System.Text;

namespace CodeSpawner.Firmware;

internal sealed partial class FwSpec
{
    /// <summary>The fw-relative files one target reads: its units and their include closures (its dead include
    /// lines not followed), its link line's script with its INCLUDEs, and its objects.</summary>
    internal static HashSet<string> UsedFiles(string root, FwScope scope)
    {
        var s = new FwSpec(root) { _scope = scope, _eval = new FwCond(scope) };
        try
        {
            s.EnumerateFw();
            s.ReadDb();
            s.ScopeDefines();
            s.ClassifyFiles(new FwTruth());
            s.Closure();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (int f in s._closure) if (f < s._fwCount) used.Add(s._rel[f]);
            foreach (var u in s._units)
                if (IsUnder(u.File, s._fwRoot) && s.FileIndex(u.File) is int f and >= 0) used.Add(s._rel[f]);
            used.UnionWith(scope.ScriptClosure);
            used.UnionWith(scope.Objects);
            return used;
        }
        finally { foreach (var h in s._handles.Values) h.Dispose(); }
    }

    internal static (FwList, FwList, FwList) WriteFunctionLists(string? dir, string side, SortedDictionary<string, int> cls) => WriteFnLists(dir, side, cls);
}

/// <summary>
/// The truth of a multi-target corpus (docs/firmware-design.md "Multi-target"): each target's, computed alone
/// against its own db and link line, then the union: keep in any target, drop in every target that grades the
/// name, else open; a name no target grades is open.
/// </summary>
internal static class FwMulti
{
    public static readonly string[] Names = ["ta", "tb", "tc"];
    public static string DbPath(string t) => $"fw-build/{t}/compile_commands.json";
    public static string LogPath(string t) => $"fw-build/{t}/build.log";
    public static string Root(string t) => $"targets/{t}/startup.S:Reset_Handler";

    /// <summary>Qualified names whose class the spelling of their bare part does not give (a static's other copy).</summary>
    private static readonly Dictionary<string, int> Override = new(StringComparer.Ordinal)
    {
        ["src/fw_attr.c:kfwdup"] = 1,
        ["src/handlers.c:kfwshd"] = 1,
    };

    /// <summary>A name's per-target suffix (<c>__KDO</c>: keep in the first target, drop in the second, open in the third), or null.</summary>
    private static string? Suffix(string bare, int n)
    {
        if (bare.Length <= n + 2 || bare[^(n + 2)] != '_' || bare[^(n + 1)] != '_') return null;
        var s = bare[^n..];
        return s.All(c => c is 'K' or 'D' or 'O') ? s : null;
    }

    private static int Letter(char c) => c == 'K' ? 0 : c == 'D' ? 1 : 2;

    /// <summary>The class target <paramref name="i"/> of <paramref name="n"/> should give a name.</summary>
    public static int Expected(string name, int i, int n)
    {
        if (Override.TryGetValue(name, out int o)) return o;
        string bare = name[(name.LastIndexOf(':') + 1)..];
        return Suffix(bare, n) is { } s ? Letter(s[i]) : FwSpec.Alphabet(Encoding.Latin1.GetBytes(bare));
    }

    /// <summary>The union class a name should have, given which targets grade it (none: open).</summary>
    public static int UnionExpected(string name, bool[] graded) =>
        Union(Enumerable.Range(0, graded.Length).Where(i => graded[i]).Select(i => Expected(name, i, graded.Length)));

    // keep in any, drop in all, else open (no grade: open)
    private static int Union(IEnumerable<int> cls)
    {
        bool any = false, allDrop = true;
        foreach (int c in cls)
        {
            if (c == 0) return 0;
            any = true;
            allDrop &= c == 1;
        }
        return any && allDrop ? 1 : 2;
    }

    public static FwTruth Compute(string root, string? listDir, bool checkAlphabet, int targets)
    {
        root = Path.GetFullPath(root);
        var names = Names[..targets];
        var scopes = names.Select((t, i) => FwScope.FromLog(root, t, i, targets, DbPath(t), LogPath(t))).ToList();

        // the names an #if tests that the tree defines: every C file of the corpus but its truth
        var cfiles = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(p => p.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetRelativePath(root, p).Replace('\\', '/').StartsWith(FirmwareEmitter.TruthDir + "/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var treeDefined = FwCond.TreeDefined(cfiles);
        foreach (var sc in scopes) sc.TreeDefined = treeDefined;

        // a target ignores what only other targets use
        var used = scopes.Select(sc => FwSpec.UsedFiles(root, sc)).ToList();
        for (int i = 0; i < targets; i++)
        {
            for (int j = 0; j < targets; j++) if (j != i) scopes[i].Ignore.UnionWith(used[j]);
            scopes[i].Ignore.ExceptWith(used[i]);
        }

        string tmp = listDir ?? Directory.CreateTempSubdirectory("codespawner-multi-").FullName;
        try
        {
            var builds = new List<(string, FwTruth)>();
            var all = new FwTruth();
            foreach (var sc in scopes)
            {
                sc.ClsPath = Path.Combine(tmp, $"{sc.Name}.defcls.tmp");
                var t = FwSpec.Compute(root, listDir, checkAlphabet, [Root(sc.Name)], sc);
                builds.Add((sc.Name, t));
                all.AlphabetErrors.AddRange(t.AlphabetErrors);
            }
            var first = builds[0].Item2;
            all.TreeSha = first.TreeSha;
            all.BigHeaders = first.BigHeaders;
            all.BigHeadersKept = first.BigHeadersKept;
            all.PasteMacros = first.PasteMacros;
            all.ForwardingMacros = first.ForwardingMacros;
            all.TotalDefineLines = first.TotalDefineLines;
            all.BytesBefore = first.BytesBefore;
            all.Dense = first.Dense;
            all.DenseHeaders = first.DenseHeaders;
            all.WholeFromOutside = builds.SelectMany(b => b.Item2.WholeFromOutside).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            all.WholeIncludedFromOutside = all.WholeFromOutside.Count;
            all.Builds = builds;

            var caseNames = new HashSet<string>(StringComparer.Ordinal);
            string casesTsv = listDir is null ? "" : Path.Combine(listDir, "cases.tsv");
            bool checkCases = checkAlphabet && listDir is not null && File.Exists(casesTsv);
            if (checkCases) foreach (var line in File.ReadLines(casesTsv).Skip(1)) caseNames.Add(line.Split('\t')[0]);
            UnionDefines(all, listDir, checkAlphabet, scopes, caseNames);
            if (checkCases)
                foreach (var n in caseNames.Order(StringComparer.Ordinal))
                    if (all.AlphabetErrors.Count < 50) all.AlphabetErrors.Add($"{n}: a case row, but no carved file defines it");

            Functions(all, listDir, checkAlphabet, root, builds);

            var files = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (_, t) in builds) files.UnionWith(t.KeepFiles!);
            using (var fw = new ListWriter(listDir, "mustKeepFiles", "union"))
            {
                foreach (var p in files) fw.Add(Encoding.UTF8.GetBytes(p));
                all.MustKeepFiles = fw.Finish();
            }
            return all;
        }
        finally
        {
            foreach (var sc in scopes) if (sc.ClsPath is { } p) try { File.Delete(p); } catch (IOException) { }
            if (listDir is null) try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    // a k-way merge of the targets' (name, class) streams, all in ordinal order
    private static void UnionDefines(FwTruth all, string? listDir, bool checkAlphabet, List<FwScope> scopes, HashSet<string> caseNames)
    {
        int n = scopes.Count;
        var readers = scopes.Select(sc => new StreamReader(sc.ClsPath!, Encoding.Latin1, false, 1 << 16)).ToArray();
        try
        {
            var cur = new (string Name, int Cls)?[n];
            void Advance(int i)
            {
                string? l = readers[i].ReadLine();
                cur[i] = l is null ? null : (l[..l.LastIndexOf('\t')], l[^1] - '0');
            }
            for (int i = 0; i < n; i++) Advance(i);
            using var keepW = new ListWriter(listDir, "mustKeepDefines", "union");
            using var dropW = new ListWriter(listDir, "mustDropDefines", "union");
            using var openW = new ListWriter(listDir, "openDefines", "union");
            var graded = new bool[n];
            var cls = new List<int>();
            while (true)
            {
                string? name = null;
                for (int i = 0; i < n; i++)
                    if (cur[i] is { } c && (name is null || string.CompareOrdinal(c.Name, name) < 0)) name = c.Name;
                if (name is null) break;
                cls.Clear();
                for (int i = 0; i < n; i++)
                {
                    graded[i] = false;
                    if (cur[i] is not { } c || c.Name != name) continue;
                    if (c.Cls != 9) { graded[i] = true; cls.Add(c.Cls); }
                    Advance(i);
                }
                caseNames.Remove(name);
                int u = Union(cls);
                (u == 0 ? keepW : u == 1 ? dropW : openW).Add(Encoding.Latin1.GetBytes(name));
                if (checkAlphabet && UnionExpected(name, graded) is int want && want != u && all.AlphabetErrors.Count < 50)
                    all.AlphabetErrors.Add($"union: {name}: {FwSpec.ClassName(u)}, alphabet says {FwSpec.ClassName(want)}");
            }
            all.MustKeepCount = keepW.Count;
            all.MustDropCount = dropW.Count;
            all.OpenCount = openW.Count;
            all.MustKeepDefines = keepW.Finish();
            all.MustDropDefines = dropW.Finish();
            all.OpenDefines = openW.Finish();
        }
        finally { foreach (var r in readers) r.Dispose(); }
    }

    // a function name defined in more than one file is <file>:<name> everywhere
    private static void Functions(FwTruth all, string? listDir, bool checkAlphabet, string root, List<(string Name, FwTruth T)> builds)
    {
        int n = builds.Count;
        var filesOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, t) in builds)
            foreach (var (file, name) in t.FnCls!.Keys)
            {
                if (!filesOf.TryGetValue(name, out var s)) filesOf[name] = s = new(StringComparer.Ordinal);
                s.Add(file);
            }
        string Key(string file, string name) => filesOf[name].Count > 1 ? $"{file}:{name}" : name;
        var per = new List<SortedDictionary<string, int>>();
        for (int i = 0; i < n; i++)
        {
            var (tn, t) = builds[i];
            var d = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var ((file, name), c) in t.FnCls!) d[Key(file, name)] = c;
            per.Add(d);
            (t.MustKeepFunctions, t.MustDropFunctions, t.OpenFunctions) = FwSpec.WriteFunctionLists(listDir, tn, d);
            if (checkAlphabet)
                foreach (var (k, c) in d)
                    if (Expected(k, i, n) is int want && want != c && all.AlphabetErrors.Count < 50)
                        all.AlphabetErrors.Add($"{tn}: {k} (function): {FwSpec.ClassName(c)}, alphabet says {FwSpec.ClassName(want)}");
        }
        var union = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var graded = new bool[n];
        foreach (var k in per.SelectMany(d => d.Keys).Distinct())
        {
            for (int i = 0; i < n; i++) graded[i] = per[i].ContainsKey(k);
            union[k] = Union(Enumerable.Range(0, n).Where(i => graded[i]).Select(i => per[i][k]));
            if (checkAlphabet && UnionExpected(k, graded) is int want && want != union[k] && all.AlphabetErrors.Count < 50)
                all.AlphabetErrors.Add($"union: {k} (function): {FwSpec.ClassName(union[k])}, alphabet says {FwSpec.ClassName(want)}");
        }
        (all.MustKeepFunctions, all.MustDropFunctions, all.OpenFunctions) = FwSpec.WriteFunctionLists(listDir, "union", union);
        (all.UnionMustKeepFunctions, all.UnionMustDropFunctions, all.UnionOpenFunctions) = (all.MustKeepFunctions, all.MustDropFunctions, all.OpenFunctions);
        if (!checkAlphabet) return;
        string cases = Path.Combine(root, FirmwareEmitter.FunctionCasesPath);
        if (File.Exists(cases))
            foreach (var line in File.ReadLines(cases).Skip(1))
            {
                var name = line.Split('\t')[0];
                if (!union.ContainsKey(name) && all.AlphabetErrors.Count < 50) all.AlphabetErrors.Add($"{name}: a function case row, but no graded function");
            }
    }
}
