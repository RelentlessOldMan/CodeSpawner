using System.Buffers.Binary;
using System.Text;

namespace CodeSpawner.Firmware;

// The function truth (phase 2, docs/firmware-design.md "Functions"): C functions reached from the roots. min roots
// are firmware.roots, every name the assembly a build names says outside comments, the linker scripts' names, the
// prebuilt objects' undefined symbols, used/retain/constructor/destructor and sections a linker script KEEPs; max
// roots add the names in those assemblies' comments, all the assembly no build names, and sections only an IAR .icf
// or armlink .sct keeps. keep = reached under min; drop = not reached under max; open = the rest. main is the main
// db's world; union adds compile_commands.world2.json's.
internal sealed partial class FwSpec
{
    private sealed class FnNode
    {
        public required string Name;
        public required int File;
        public bool IsFunc, Attr;
        public readonly HashSet<string> Refs = new(StringComparer.Ordinal);
        public readonly List<string> Sections = new();
    }

    private sealed class FnGraph
    {
        public readonly Dictionary<string, FnNode> Nodes = new(StringComparer.Ordinal);   // name, or file:name for statics
        public readonly HashSet<(int File, string Sig)> Scanned = new();
    }

    private static readonly string[] AsmExts = [".s", ".asm", ".sx"];
    private static bool IsAsm(string p) => AsmExts.Contains(Path.GetExtension(p).ToLowerInvariant());
    private static bool IsLd(string p) => Path.GetExtension(p).ToLowerInvariant() is ".ld" or ".lds" or ".ldscript";
    private static bool IsLinkInfra(string p) => Path.GetExtension(p).ToLowerInvariant() is ".icf" or ".sct" or ".cmd";
    private static bool IsObj(string p) => Path.GetExtension(p).ToLowerInvariant() is ".o" or ".a";

    private void Functions(FwTruth t, string? listDir, bool checkAlphabet, SortedSet<string> files)
    {
        var main = new FnGraph();
        foreach (var u in _units) ScanUnit(main, u);
        var union = new FnGraph();
        foreach (var u in _units.Concat(ReadWorld2())) ScanUnit(union, u);

        // roots
        var min = new HashSet<string>(_roots!, StringComparer.Ordinal);
        var max = new HashSet<string>(StringComparer.Ordinal);
        var keepGlobs = new List<string>();
        var maxGlobs = new List<string>();
        var named = new HashSet<int>();
        foreach (var u in _units)
            if (IsAsm(u.File) && IsUnder(u.File, _fwRoot) && FileIndex(u.File) is int a and >= 0) named.Add(a);
        for (int f = 0; f < _fwCount; f++)
        {
            string p = _files[f];
            if (IsAsm(p))
            {
                bool isNamed = named.Contains(f);
                AsmScan(f, isNamed ? min : max, max, files, new HashSet<int>(), _units.FirstOrDefault(u => FileIndex(u.File) == f)?.IncDirs ?? new());
                files.Add(_rel[f]);
            }
            else if (IsLd(p)) { LdScan(f, min, keepGlobs); files.Add(_rel[f]); }
            else if (IsLinkInfra(p))
            {
                if (!p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) InfraGlobs(f, maxGlobs);
                files.Add(_rel[f]);
            }
            else if (IsObj(p)) { ObjUndefined(File.ReadAllBytes(p), min); files.Add(_rel[f]); }
            else if (Path.GetExtension(p).Equals(".inc", StringComparison.OrdinalIgnoreCase)) files.Add(_rel[f]);
        }
        max.UnionWith(min);

        var mainCls = Classify(main, min, max, keepGlobs, maxGlobs);
        var unionCls = Classify(union, min, max, keepGlobs, maxGlobs);

        (t.MustKeepFunctions, t.MustDropFunctions, t.OpenFunctions) = WriteFnLists(listDir, "main", mainCls);
        (t.UnionMustKeepFunctions, t.UnionMustDropFunctions, t.UnionOpenFunctions) = WriteFnLists(listDir, "union", unionCls);
        if (!checkAlphabet) return;
        foreach (var (name, cls) in unionCls)
        {
            int want = name.Length < 2 ? 0 : (name[0] == 'Z' || name[0] == 'Y') && name[^1] == 'Z' || name[0] == 'Z' && name[^1] == 'Y' ? 1
                : name[0] == 'Y' && name[^1] == 'Y' ? 2 : 0;
            if (want != cls && t.AlphabetErrors.Count < 50) t.AlphabetErrors.Add($"{name} (function): {ClassName(cls)}, alphabet says {ClassName(want)}");
        }
        string cases = listDir is null ? "" : Path.Combine(listDir, "cases.functions.tsv");
        if (listDir is not null && File.Exists(cases))
            foreach (var line in File.ReadLines(cases).Skip(1))
            {
                var name = line.Split('\t')[0];
                if (!unionCls.ContainsKey(name) && t.AlphabetErrors.Count < 50) t.AlphabetErrors.Add($"{name}: a function case row, but no graded function");
            }
    }

    private List<Unit> ReadWorld2()
    {
        string p = Path.Combine(_root, FirmwareEmitter.DbWorld2Path);
        if (!File.Exists(p)) return new();
        int before = _units.Count;
        try { ReadDb(File.ReadAllBytes(p)); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException)
        { throw new FwDbException($"{FirmwareEmitter.DbWorld2Path} is not a compile db this generator writes ({ex.Message})"); }
        var w2 = _units.GetRange(before, _units.Count - before);
        _units.RemoveRange(before, _units.Count - before);
        return w2;
    }

    private static (FwList, FwList, FwList) WriteFnLists(string? dir, string side, SortedDictionary<string, int> cls)
    {
        using var k = new ListWriter(dir, "mustKeepFunctions", side);
        using var d = new ListWriter(dir, "mustDropFunctions", side);
        using var o = new ListWriter(dir, "openFunctions", side);
        foreach (var (name, c) in cls) (c == 0 ? k : c == 1 ? d : o).Add(Encoding.ASCII.GetBytes(name));
        return (k.Finish(), d.Finish(), o.Finish());
    }

    // name -> class (0 keep, 1 drop, 2 open), ordinal order
    private static SortedDictionary<string, int> Classify(FnGraph g, HashSet<string> min, HashSet<string> max, List<string> keepGlobs, List<string> maxGlobs)
    {
        var rMin = Reach(g, min, n => n.Attr || n.Sections.Any(s => keepGlobs.Any(x => Glob(x, s))));
        var rMax = Reach(g, max, n => n.Attr || n.Sections.Any(s => keepGlobs.Any(x => Glob(x, s)) || maxGlobs.Any(x => Glob(x, s))));
        var cls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, n) in g.Nodes)
        {
            if (!n.IsFunc) continue;
            int c = rMin.Contains(key) ? 0 : rMax.Contains(key) ? 2 : 1;
            // one name, several definitions (statics in two files): the most kept wins
            if (!cls.TryGetValue(n.Name, out int was) || Rank(c) < Rank(was)) cls[n.Name] = c;
        }
        return cls;
        static int Rank(int c) => c == 0 ? 0 : c == 2 ? 1 : 2;
    }

    private static HashSet<string> Reach(FnGraph g, HashSet<string> roots, Func<FnNode, bool> isRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var q = new Queue<string>();
        foreach (var r in roots) if (g.Nodes.ContainsKey(r) && seen.Add(r)) q.Enqueue(r);
        foreach (var (key, n) in g.Nodes) if (isRoot(n) && seen.Add(key)) q.Enqueue(key);
        while (q.Count > 0)
        {
            var n = g.Nodes[q.Dequeue()];
            foreach (var r in n.Refs)
            {
                string loc = $"{n.File}:{r}";
                string? to = g.Nodes.ContainsKey(loc) ? loc : g.Nodes.ContainsKey(r) ? r : null;
                if (to is not null && seen.Add(to)) q.Enqueue(to);
            }
        }
        return seen;
    }

    private static bool Glob(string pat, string s)
    {
        int p = 0, i = 0, star = -1, mark = 0;
        while (i < s.Length)
        {
            if (p < pat.Length && (pat[p] == '?' || pat[p] == s[i])) { p++; i++; }
            else if (p < pat.Length && pat[p] == '*') { star = p++; mark = i; }
            else if (star >= 0) { p = star + 1; i = ++mark; }
            else return false;
        }
        while (p < pat.Length && pat[p] == '*') p++;
        return p == pat.Length;
    }

    // ---------------- C ----------------

    private void ScanUnit(FnGraph g, Unit u)
    {
        if (!IsCExt(u.File) || !File.Exists(u.File) || !IsUnder(u.File, _fwRoot)) return;
        int start = FileIndex(u.File);
        if (start < 0) return;
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in u.DefineArgs)
        {
            int eq = a.IndexOf('=');
            defs[eq < 0 ? a : a[..eq]] = eq < 0 ? "1" : a[(eq + 1)..];
        }
        string sig = string.Join("\0", u.DefineArgs);
        var seen = new HashSet<int> { start };
        var q = new Queue<int>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            int f = q.Dequeue();
            if (f < _fwCount && !_isK[f] && !_binary[f] && g.Scanned.Add((f, sig))) CScan(g, f, defs);
            foreach (var (quoted, name) in Includes(f))
            {
                int h = ResolveInclude(f, quoted, name, u);
                if (h >= 0 && seen.Add(h)) q.Enqueue(h);
            }
        }
    }

    private enum TK { Id, Str, P }

    private void CScan(FnGraph g, int f, Dictionary<string, string> defs)
    {
        var toks = CTokens(Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])), defs);
        var decl = new List<(TK K, string S)>();
        int paren = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var (k, s) = toks[i];
            if (k == TK.P && s == "(") paren++;
            else if (k == TK.P && s == ")") paren--;
            bool eq = decl.Any(x => x.K == TK.P && x.S == "=");
            if (k == TK.P && s == ";" && paren <= 0)
            {
                if (eq) Global(g, f, decl);
                decl.Clear(); paren = 0;
                continue;
            }
            if (k == TK.P && s == "{" && paren <= 0)
            {
                int end = Match(toks, i);
                if (eq) { decl.AddRange(toks.GetRange(i, end - i + 1)); i = end; continue; }
                if (FuncName(decl) is { } name)
                {
                    var n = Node(g, f, name, decl, isFunc: true);
                    for (int j = i + 1; j < end; j++) if (toks[j].K == TK.Id) n.Refs.Add(toks[j].S);
                    decl.Clear();
                }
                // a struct or enum body is part of its declaration
                i = end;
                continue;
            }
            decl.Add((k, s));
        }
    }

    private static int Match(List<(TK K, string S)> toks, int open)
    {
        int d = 0;
        for (int j = open; j < toks.Count; j++)
        {
            if (toks[j].K != TK.P) continue;
            if (toks[j].S == "{") d++;
            else if (toks[j].S == "}" && --d == 0) return j;
        }
        return toks.Count - 1;
    }

    // `... name ( params ) [__attribute__((...))]` -> name
    private static string? FuncName(List<(TK K, string S)> d)
    {
        int e = d.Count - 1;
        while (e >= 0)
        {
            if (d[e].K != TK.P || d[e].S != ")") return null;
            int depth = 0, b = e;
            for (; b >= 0; b--)
            {
                if (d[b].K != TK.P) continue;
                if (d[b].S == ")") depth++;
                else if (d[b].S == "(" && --depth == 0) break;
            }
            if (b <= 0 || d[b - 1].K != TK.Id) return null;
            if (d[b - 1].S is "__attribute__" or "__attribute") { e = b - 2; continue; }
            return d[b - 1].S;
        }
        return null;
    }

    private static void Global(FnGraph g, int f, List<(TK K, string S)> d)
    {
        int eq = d.FindIndex(x => x.K == TK.P && x.S == "=");
        int j = eq - 1;
        while (j >= 0 && d[j].K == TK.P && d[j].S is "[" or "]") j--;
        while (j >= 0 && d[j].K != TK.Id) j--;
        if (j < 0) return;
        var n = Node(g, f, d[j].S, d, isFunc: false);
        for (int k = eq + 1; k < d.Count; k++) if (d[k].K == TK.Id) n.Refs.Add(d[k].S);
    }

    private static FnNode Node(FnGraph g, int f, string name, List<(TK K, string S)> decl, bool isFunc)
    {
        bool isStatic = decl.Any(x => x.K == TK.Id && x.S == "static");
        string key = isStatic ? $"{f}:{name}" : name;
        if (!g.Nodes.TryGetValue(key, out var n)) g.Nodes[key] = n = new FnNode { Name = name, File = f };
        n.IsFunc |= isFunc;
        for (int i = 0; i < decl.Count; i++)
        {
            if (decl[i].K != TK.Id || decl[i].S is not ("__attribute__" or "__attribute")) continue;
            int depth = 0;
            for (int j = i + 1; j < decl.Count; j++)
            {
                var (k, s) = decl[j];
                if (k == TK.P && s == "(") depth++;
                else if (k == TK.P && s == ")") { if (--depth == 0) break; }
                else if (k == TK.Id && s.Trim('_') is "used" or "retain" or "constructor" or "destructor") n.Attr = true;
                else if (k == TK.Id && s.Trim('_') == "section" && j + 2 < decl.Count && decl[j + 2].K == TK.Str) n.Sections.Add(decl[j + 2].S);
            }
        }
        return n;
    }

    /// <summary>C tokens of the live text: comments, directives and dead conditional branches out; a condition this
    /// can't decide from the unit's -D is live both ways.</summary>
    private static List<(TK K, string S)> CTokens(string text, Dictionary<string, string> defs)
    {
        text = text.Replace("\\\r\n", "").Replace("\\\n", "");
        var toks = new List<(TK, string)>();
        var stack = new List<(bool Parent, bool Done, bool Live)>();
        bool live = true, lineStart = true;
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? n : e + 2;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; continue; }
            if (lineStart && (c == '#' || c == '%' && i + 1 < n && text[i + 1] == ':'))
            {
                i += c == '#' ? 1 : 2;
                var sb = new StringBuilder();
                while (i < n && text[i] != '\n')
                {
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '*')
                    {
                        int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = e < 0 ? n : e + 2;
                        sb.Append(' ');
                        continue;
                    }
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; break; }
                    sb.Append(text[i++]);
                }
                Directive(sb.ToString().Trim(), defs, stack, ref live);
                continue;
            }
            lineStart = false;
            if (c == '"' || c == '\'')
            {
                int st = ++i;
                while (i < n && text[i] != c && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                if (live && c == '"') toks.Add((TK.Str, text[st..Math.Min(i, n)]));
                i++;
                continue;
            }
            if (char.IsAsciiDigit(c)) { while (i < n && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++; continue; }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int st = i;
                while (i < n && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (live) toks.Add((TK.Id, text[st..i]));
                continue;
            }
            if (live) toks.Add((TK.P, c.ToString()));
            i++;
        }
        return toks;
    }

    private enum Tri { False, True, Unknown }

    private static void Directive(string d, Dictionary<string, string> defs, List<(bool Parent, bool Done, bool Live)> stack, ref bool live)
    {
        int k = 0;
        while (k < d.Length && char.IsAsciiLetter(d[k])) k++;
        string kw = d[..k], rest = d[k..].Trim();
        switch (kw)
        {
            case "if" or "ifdef" or "ifndef":
            {
                Tri c = kw == "if" ? Eval(rest, defs) : defs.ContainsKey(rest) ? (kw == "ifdef" ? Tri.True : Tri.False) : Tri.Unknown;
                stack.Add((live, c == Tri.True, live && c != Tri.False));
                break;
            }
            case "elif" when stack.Count > 0:
            {
                var (parent, done, _) = stack[^1];
                Tri c = done ? Tri.False : Eval(rest, defs);
                stack[^1] = (parent, done || c == Tri.True, parent && !done && c != Tri.False);
                break;
            }
            case "else" when stack.Count > 0:
            {
                var (parent, done, _) = stack[^1];
                stack[^1] = (parent, true, parent && !done);
                break;
            }
            case "endif" when stack.Count > 0:
                stack.RemoveAt(stack.Count - 1);
                break;
            default:
                return;
        }
        live = stack.Count == 0 || stack[^1].Live;
    }

    private static Tri Eval(string e, Dictionary<string, string> defs)
    {
        e = e.Trim();
        if (long.TryParse(e, out long v)) return v != 0 ? Tri.True : Tri.False;
        if (e.StartsWith("defined", StringComparison.Ordinal))
        {
            string x = e[7..].Trim().Trim('(', ')').Trim();
            return IsIdent(x) && defs.ContainsKey(x) ? Tri.True : Tri.Unknown;
        }
        if (IsIdent(e)) return defs.TryGetValue(e, out var dv) && long.TryParse(dv, out long n) ? (n != 0 ? Tri.True : Tri.False) : Tri.Unknown;
        int eq = e.IndexOf("==", StringComparison.Ordinal);
        if (eq > 0)
        {
            string a = e[..eq].Trim(), b = e[(eq + 2)..].Trim();
            if (IsIdent(a) && long.TryParse(b, out long want) && defs.TryGetValue(a, out var av) && long.TryParse(av, out long have))
                return have == want ? Tri.True : Tri.False;
        }
        return Tri.Unknown;
    }

    private static bool IsIdent(string s) =>
        s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static IEnumerable<string> Idents(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c)) { while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++; continue; }
            if (!char.IsAsciiLetter(c) && c != '_') { i++; continue; }
            int st = i;
            while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
            yield return s[st..i];
        }
    }

    // ---------------- assembly ----------------

    /// <summary>The names an assembly file says: outside comments into <paramref name="code"/>, inside into
    /// <paramref name="comment"/>. Follows .include and #include (a header's lines starting with '.'), expands the
    /// file's .macro invocations and its one-parameter cpp paste macros.</summary>
    private void AsmScan(int f, HashSet<string> code, HashSet<string> comment, SortedSet<string> files, HashSet<int> seen, List<string> incDirs)
    {
        if (!seen.Add(f)) return;
        files.Add(_rel[f]);
        var lines = Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])).Split('\n');
        var macros = new Dictionary<string, (string[] Params, List<string> Body)>(StringComparer.Ordinal);
        var pastes = new Dictionary<string, (string Param, string Body)>(StringComparer.Ordinal);
        bool inBlock = false;
        List<string>? body = null;
        foreach (var raw in lines)
        {
            // comments out
            var sb = new StringBuilder();
            string line = raw.TrimEnd('\r');
            for (int i = 0; i < line.Length; i++)
            {
                if (inBlock)
                {
                    int e = line.IndexOf("*/", i, StringComparison.Ordinal);
                    comment.UnionWith(Idents(e < 0 ? line[i..] : line[i..e]));
                    if (e < 0) { i = line.Length; break; }
                    inBlock = false;
                    i = e + 1;
                    continue;
                }
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; continue; }
                if (line[i] == '@' || line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    comment.UnionWith(Idents(line[i..]));
                    break;
                }
                if (line[i] == '"')
                {
                    int e = line.IndexOf('"', i + 1);
                    if (e < 0) e = line.Length - 1;
                    sb.Append(line, i, e - i + 1);
                    i = e;
                    continue;
                }
                sb.Append(line[i]);
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) continue;
            code.UnionWith(Idents(s));

            if (body is not null)
            {
                if (s.StartsWith(".endm", StringComparison.Ordinal)) body = null;
                else body.Add(s);
                continue;
            }
            if (s.StartsWith(".macro", StringComparison.Ordinal))
            {
                var parts = s[6..].Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                body = new List<string>();
                macros[parts[0]] = (parts[1..].Select(x => x.Split('=')[0]).ToArray(), body);
                continue;
            }
            if (s.StartsWith('#'))
            {
                string d = s[1..].TrimStart();
                if (d.StartsWith("define", StringComparison.Ordinal) && PasteMacro(d[6..]) is var (pn, pp, pb)) pastes[pn] = (pp, pb);
                else if (d.StartsWith("include", StringComparison.Ordinal) && Quoted(d[7..]) is { } inc && ResolveAsmInclude(f, inc, incDirs) is int h and >= 0)
                {
                    files.Add(_rel[h]);
                    if (IsCExt(_files[h]))
                        foreach (var hl in File.ReadAllLines(_files[h])) { if (hl.TrimStart().StartsWith('.')) code.UnionWith(Idents(hl)); }
                    else AsmScan(h, code, comment, files, seen, incDirs);
                }
                continue;
            }
            if (s.StartsWith(".include", StringComparison.Ordinal))
            {
                if (Quoted(s[8..]) is { } inc && ResolveAsmInclude(f, inc, incDirs) is int h and >= 0) AsmScan(h, code, comment, files, seen, incDirs);
                continue;
            }
            // a .macro invocation: the body with its parameters substituted
            var words = s.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && macros.TryGetValue(words[0], out var m))
                foreach (var bl in m.Body)
                {
                    string x = bl;
                    for (int k = 0; k < m.Params.Length; k++) x = x.Replace("\\" + m.Params[k], k + 1 < words.Length ? words[k + 1] : "");
                    code.UnionWith(Idents(x.Replace("\\()", "")));
                }
            // a cpp paste macro: NAME(arg) -> its body with the parameter pasted
            foreach (var (pn, (pp, pb)) in pastes)
            {
                int at = 0;
                while ((at = s.IndexOf(pn + "(", at, StringComparison.Ordinal)) >= 0)
                {
                    int close = s.IndexOf(')', at);
                    if (close < 0) break;
                    string arg = s[(at + pn.Length + 1)..close].Trim();
                    code.UnionWith(Idents(System.Text.RegularExpressions.Regex.Replace(pb, $@"\b{pp}\b", arg).Replace("##", "").Replace(" ", "")));
                    at = close;
                }
            }
        }
    }

    // `NAME(p) body-with-##` -> (NAME, p, body)
    private static (string, string, string)? PasteMacro(string d)
    {
        d = d.Trim();
        int lp = d.IndexOf('(');
        if (lp <= 0 || !IsIdent(d[..lp])) return null;
        int rp = d.IndexOf(')', lp);
        if (rp < 0) return null;
        string p = d[(lp + 1)..rp].Trim(), body = d[(rp + 1)..].Trim();
        if (!IsIdent(p) || !body.Contains("##")) return null;
        return (d[..lp], p, body);
    }

    private static string? Quoted(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '"') return null;
        int e = s.IndexOf('"', 1);
        return e < 0 ? null : s[1..e];
    }

    private int ResolveAsmInclude(int from, string name, List<string> incDirs)
    {
        foreach (var d in incDirs.Prepend(Path.GetDirectoryName(_files[from])!))
        {
            string p;
            try { p = Path.GetFullPath(Path.Combine(d, name)); } catch { continue; }
            if (IsUnder(p, _fwRoot) && File.Exists(p)) return FileIndex(p);
        }
        return -1;
    }

    // ---------------- linker scripts, prebuilt objects ----------------

    private static string StripBlockComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int b = s.IndexOf("/*", i, StringComparison.Ordinal);
            if (b < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, b - i).Append(' ');
            int e = s.IndexOf("*/", b + 2, StringComparison.Ordinal);
            i = e < 0 ? s.Length : e + 2;
        }
        return sb.ToString();
    }

    /// <summary>A GNU linker script: every name it says (ENTRY, EXTERN, PROVIDE, assignments) is a root, and every
    /// KEEP's section patterns keep the sections they match.</summary>
    private void LdScan(int f, HashSet<string> min, List<string> keepGlobs)
    {
        string s = StripBlockComments(Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])));
        min.UnionWith(Idents(s));
        int at = 0;
        while ((at = s.IndexOf("KEEP", at, StringComparison.Ordinal)) >= 0)
        {
            int open = s.IndexOf('(', at);
            at += 4;
            if (open < 0) break;
            int depth = 0, close = open;
            for (; close < s.Length; close++)
            {
                if (s[close] == '(') depth++;
                else if (s[close] == ')' && --depth == 0) break;
            }
            string inner = s[(open + 1)..Math.Min(close, s.Length)];
            int lp = inner.IndexOf('('), rp = inner.LastIndexOf(')');
            if (lp >= 0 && rp > lp) inner = inner[(lp + 1)..rp];
            keepGlobs.AddRange(inner.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>An IAR .icf or armlink .sct: the section names it says (each starts with '.').</summary>
    private void InfraGlobs(int f, List<string> globs)
    {
        string s = StripBlockComments(Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])));
        foreach (var w in s.Split([' ', '\t', '\r', '\n', '(', ')', '{', '}', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 1 && w[0] == '.') globs.Add(w);
    }

    /// <summary>The undefined global symbols of an ELF32 little-endian object, or of each member of an ar archive.</summary>
    internal static void ObjUndefined(byte[] b, HashSet<string> into)
    {
        if (b.Length >= 8 && Encoding.ASCII.GetString(b, 0, 8) == "!<arch>\n")
        {
            int at = 8;
            while (at + 60 <= b.Length)
            {
                string name = Encoding.ASCII.GetString(b, at, 16).TrimEnd();
                if (!int.TryParse(Encoding.ASCII.GetString(b, at + 48, 10).Trim(), out int size) || size < 0 || at + 60 + size > b.Length) return;
                if (name != "/" && name != "//") ElfUndefined(b.AsSpan(at + 60, size), into);
                at += 60 + size + (size & 1);
            }
            return;
        }
        ElfUndefined(b, into);
    }

    private static void ElfUndefined(ReadOnlySpan<byte> e, HashSet<string> into)
    {
        if (e.Length < 52 || e[0] != 0x7F || e[1] != 'E' || e[2] != 'L' || e[3] != 'F' || e[4] != 1 || e[5] != 1) return;
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(e[32..]);
        int shent = BinaryPrimitives.ReadUInt16LittleEndian(e[46..]), shnum = BinaryPrimitives.ReadUInt16LittleEndian(e[48..]);
        if (shent < 40 || shoff + (long)shent * shnum > e.Length) return;
        for (int i = 0; i < shnum; i++)
        {
            var sh = e.Slice((int)shoff + i * shent, 40);
            if (BinaryPrimitives.ReadUInt32LittleEndian(sh[4..]) != 2) continue;   // SHT_SYMTAB
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(sh[16..]), size = BinaryPrimitives.ReadUInt32LittleEndian(sh[20..]);
            uint link = BinaryPrimitives.ReadUInt32LittleEndian(sh[24..]);
            if (link >= shnum || off + (long)size > e.Length) continue;
            var str = e.Slice((int)shoff + (int)link * shent, 40);
            uint soff = BinaryPrimitives.ReadUInt32LittleEndian(str[16..]), ssize = BinaryPrimitives.ReadUInt32LittleEndian(str[20..]);
            if (soff + (long)ssize > e.Length) continue;
            var strtab = e.Slice((int)soff, (int)ssize);
            for (uint k = 16; k + 16 <= size; k += 16)
            {
                var sym = e.Slice((int)(off + k), 16);
                uint nm = BinaryPrimitives.ReadUInt32LittleEndian(sym);
                int bind = sym[12] >> 4;
                if (BinaryPrimitives.ReadUInt16LittleEndian(sym[14..]) != 0 || bind == 0 || nm == 0 || nm >= strtab.Length) continue;
                int z = strtab[(int)nm..].IndexOf((byte)0);
                if (z > 0) into.Add(Encoding.ASCII.GetString(strtab.Slice((int)nm, z)));
            }
        }
    }
}
