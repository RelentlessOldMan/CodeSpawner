using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeSpawner.Firmware;

// The function truth (phase 2, docs/firmware-design.md "Functions"): C functions reached from the roots, as the
// documented link decides (ld.lld --gc-sections, C built -ffunction-sections -fdata-sections, no -u/-e). The graph's
// nodes are input sections: each C function and global, each section of the assembly a build names, each section of
// a prebuilt object. An input section is kept whole once any symbol in it is reached. min roots are firmware.roots,
// the linker scripts' names, retain and sections a linker script KEEPs; max roots add every name the assembly says
// (comments and dead branches too), all the assembly no build names, used/constructor/destructor, sections only an
// IAR .icf, armlink .sct or TI .cmd keeps or a `sym = .;` bracket holds, function names in headers too big to scan,
// and edges a weak reference or an unreadable piece of asm makes. keep = reached under min; drop = not reached under
// max; open = the rest. main is the main db's world; union adds compile_commands.world2.json's.
internal sealed partial class FwSpec
{
    private sealed class FnNode
    {
        public required string Name;
        public required int Tu;              // the unit or object whose names it resolves in (-1: none)
        public bool IsFunc, Retain, MaxRoot, ArMember;
        public readonly HashSet<(int Tu, string Name)> Refs = new();
        public readonly HashSet<(int Tu, string Name)> MaxRefs = new();   // followed only under max
        public readonly HashSet<string> Edges = new(StringComparer.Ordinal);   // node keys
        public readonly List<string> Sections = new();
    }

    private sealed class FnGraph
    {
        // C: name, or tu:name for a static; assembly and objects: #s…, #a…, #o…
        public readonly Dictionary<string, FnNode> Nodes = new(StringComparer.Ordinal);
        // an assembly or object symbol -> the nodes defining it (a local one as tu:name)
        public readonly Dictionary<string, List<string>> Labels = new(StringComparer.Ordinal);
        public readonly HashSet<(int Start, string Sig)> Units = new();
        public readonly HashSet<string> MaxNames = new(StringComparer.Ordinal);
        public readonly HashSet<int> KHeaders = new();

        public void Label(string name, string key)
        {
            if (!Labels.TryGetValue(name, out var l)) Labels[name] = l = new();
            if (!l.Contains(key)) l.Add(key);
        }
    }

    private static readonly string[] AsmExts = [".s", ".asm", ".sx"];
    private static bool IsAsm(string p) => AsmExts.Contains(Path.GetExtension(p).ToLowerInvariant());
    private static bool IsCppAsm(string p) => Path.GetExtension(p) is ".S" or ".sx" or ".SX";
    private static bool IsLd(string p) => Path.GetExtension(p).ToLowerInvariant() is ".ld" or ".lds" or ".ldscript";
    private static bool IsLinkInfra(string p) => Path.GetExtension(p).ToLowerInvariant() is ".icf" or ".sct" or ".cmd";
    private static bool IsObj(string p) => Path.GetExtension(p).ToLowerInvariant() is ".o" or ".a";

    private void Functions(FwTruth t, string? listDir, bool checkAlphabet, SortedSet<string> files)
    {
        // roots, and the assembly and object sections (one set for both worlds)
        var min = new HashSet<string>(_roots!, StringComparer.Ordinal);
        var max = new HashSet<string>(StringComparer.Ordinal);
        var keepGlobs = new List<string>();
        var maxGlobs = new List<string>();
        var bin = new FnGraph();
        var asmUnits = new Dictionary<int, Unit>();
        foreach (var u in _units)
            if (IsAsm(u.File) && IsUnder(u.File, _fwRoot) && FileIndex(u.File) is int a and >= 0) asmUnits.TryAdd(a, u);
        for (int f = 0; f < _fwCount; f++)
        {
            string p = _files[f];
            if (IsAsm(p))
            {
                // assembly no build names is not linked: every name it says only max reaches
                bool linked = asmUnits.TryGetValue(f, out var u);
                var gas = new Gas(this, linked ? bin : new FnGraph(), f, max, files, u?.IncDirs ?? new(), u is null ? new() : Defs(u));
                gas.File(f, IsCppAsm(p));
                gas.Finish();
                files.Add(_rel[f]);
            }
            else if (IsLd(p)) { LdScan(f, min, keepGlobs, maxGlobs); files.Add(_rel[f]); }
            else if (IsLinkInfra(p)) { InfraGlobs(f, maxGlobs); files.Add(_rel[f]); }
            else if (IsObj(p)) { ObjNodes(bin, f, File.ReadAllBytes(p), max); files.Add(_rel[f]); }
            else if (Path.GetExtension(p).Equals(".inc", StringComparison.OrdinalIgnoreCase)) files.Add(_rel[f]);
        }
        max.UnionWith(min);

        var main = new FnGraph();
        foreach (var u in _units) ScanUnit(main, u);
        var union = new FnGraph();
        foreach (var u in _units.Concat(ReadWorld2())) ScanUnit(union, u);

        // function names in headers too big to scan are open (Carver ruling Q2)
        var kNames = KHeaderNames(union.KHeaders, union.Nodes.Values.Where(n => n.IsFunc).Select(n => n.Name));
        foreach (var g in new[] { main, union })
        {
            foreach (var (k, n) in bin.Nodes) g.Nodes[k] = n;
            foreach (var (name, l) in bin.Labels) foreach (var k in l) g.Label(name, k);
            foreach (int h in g.KHeaders) if (kNames.TryGetValue(h, out var s)) g.MaxNames.UnionWith(s);
            GroupSections(g);
        }

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
        // gen and verify both: every case row names a graded function
        string cases = Path.Combine(_root, FirmwareEmitter.FunctionCasesPath);
        if (File.Exists(cases))
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
        try { ReadDb(File.ReadAllBytes(p), FirmwareEmitter.DbWorld2Path); }
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
        static bool In(FnNode n, List<string> globs) => n.Sections.Any(s => globs.Any(x => Glob(x, s)));
        // KEEP never pulls an archive member in: only its being needed does
        var rMin = Reach(g, min, false, n => n.Retain || !n.ArMember && In(n, keepGlobs));
        var rMax = Reach(g, max.Concat(g.MaxNames), true, n => n.Retain || n.MaxRoot || In(n, keepGlobs) || In(n, maxGlobs));
        var cls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, n) in g.Nodes)
        {
            if (!n.IsFunc) continue;
            int c = rMin.Contains(key) ? 0 : rMax.Contains(key) ? 2 : 1;
            // one name, several definitions (statics in two units): the most kept wins
            if (!cls.TryGetValue(n.Name, out int was) || Rank(c) < Rank(was)) cls[n.Name] = c;
        }
        return cls;
        static int Rank(int c) => c == 0 ? 0 : c == 2 ? 1 : 2;
    }

    /// <summary>The nodes a name means from unit or object <paramref name="tu"/>: its own static or local label
    /// shadows the rest; else the C global and every assembly or object symbol of that name.</summary>
    private static IEnumerable<string> Targets(FnGraph g, int tu, string name)
    {
        if (tu >= 0)
        {
            string loc = $"{tu}:{name}";
            if (g.Nodes.ContainsKey(loc)) { yield return loc; yield break; }
            if (g.Labels.TryGetValue(loc, out var ll)) { foreach (var k in ll) yield return k; yield break; }
        }
        if (g.Nodes.ContainsKey(name)) yield return name;
        if (g.Labels.TryGetValue(name, out var l)) foreach (var k in l) yield return k;
    }

    private static HashSet<string> Reach(FnGraph g, IEnumerable<string> roots, bool max, Func<FnNode, bool> isRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var q = new Queue<string>();
        void Go(string k) { if (g.Nodes.ContainsKey(k) && seen.Add(k)) q.Enqueue(k); }
        foreach (var r in roots) foreach (var k in Targets(g, -1, r)) Go(k);
        foreach (var (key, n) in g.Nodes) if (isRoot(n)) Go(key);
        while (q.Count > 0)
        {
            var n = g.Nodes[q.Dequeue()];
            foreach (var e in n.Edges) Go(e);
            foreach (var (tu, r) in n.Refs) foreach (var k in Targets(g, tu, r)) Go(k);
            if (max) foreach (var (tu, r) in n.MaxRefs) foreach (var k in Targets(g, tu, r)) Go(k);
        }
        return seen;
    }

    /// <summary>C nodes one unit places in the same named section share one input section: reaching one reaches all.</summary>
    private static void GroupSections(FnGraph g)
    {
        var by = new Dictionary<(int, string), List<string>>();
        foreach (var (key, n) in g.Nodes)
        {
            if (key.StartsWith('#')) continue;
            foreach (var s in n.Sections)
            {
                if (!by.TryGetValue((n.Tu, s), out var l)) by[(n.Tu, s)] = l = new();
                l.Add(key);
            }
        }
        foreach (var l in by.Values)
            for (int i = 1; i < l.Count; i++) { g.Nodes[l[0]].Edges.Add(l[i]); g.Nodes[l[i]].Edges.Add(l[0]); }
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

    private static Dictionary<string, string> Defs(Unit u)
    {
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in u.DefineArgs)
        {
            int eq = a.IndexOf('=');
            defs[eq < 0 ? a : a[..eq]] = eq < 0 ? "1" : a[(eq + 1)..];
        }
        return defs;
    }

    // ---------------- C ----------------

    /// <summary>One C unit: its include closure's macros (and its -D), then each closure file's code with them
    /// expanded. Statics are the unit's own (tu:name), so a header's static inline is each includer's.</summary>
    private void ScanUnit(FnGraph g, Unit u)
    {
        if (!IsCExt(u.File) || !File.Exists(u.File) || !IsUnder(u.File, _fwRoot)) return;
        int tu = FileIndex(u.File);
        if (tu < 0 || !g.Units.Add((tu, string.Join("\0", u.DefineArgs)))) return;
        var defs = Defs(u);
        var closure = new List<int>();
        var seen = new HashSet<int> { tu };
        var q = new Queue<int>();
        q.Enqueue(tu);
        while (q.Count > 0)
        {
            int f = q.Dequeue();
            if (f < _fwCount && !_binary[f]) { if (_isK[f]) g.KHeaders.Add(f); else closure.Add(f); }
            foreach (var (quoted, name) in Includes(f))
            {
                int h = ResolveInclude(f, quoted, name, u);
                if (h >= 0 && seen.Add(h)) q.Enqueue(h);
            }
        }
        var mac = new Macros();
        foreach (var (k, v) in defs) mac.Add(new MacroDef { Name = k, Body = Lex(v, false) });
        var scans = closure.Select(f => CFile(f, defs)).ToList();
        foreach (var s in scans) foreach (var d in s.Defines) mac.Add(d);
        foreach (var s in scans)
        {
            Decls(g, tu, mac.Expand(s.Tokens), mac, u);
            foreach (var (x, y) in s.PragmaWeak) Node(g, tu, x, [], false, mac, u).Refs.Add((tu, y));
        }
    }

    private enum CK { Id, Str, P, Num, Chr, Opaque }

    /// <summary>A token; <c>Sp</c>: whitespace before it; <c>H</c>: the macros it came out of (not expanded again).</summary>
    private readonly record struct CTok(CK K, string S, bool Sp = false, string[]? H = null)
    {
        public bool Is(string p) => K == CK.P && S == p;
        public string Text => K == CK.Str ? "\"" + S + "\"" : S;
    }

    private const string DirMark = "#\n";

    private sealed class CScanned
    {
        public readonly List<CTok> Tokens = new();
        public readonly List<MacroDef> Defines = new();
        public readonly List<(string, string)> PragmaWeak = new();
    }

    private CScanned CFile(int f, Dictionary<string, string> defs)
    {
        var r = new CScanned();
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])).Replace("\\\r\n", "").Replace("\\\n", "");
        var stack = new List<(bool Parent, bool Done, bool Live)>();
        bool live = true, lineStart = true, sp = false;
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { lineStart = true; sp = true; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { sp = true; i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? n : e + 2;
                sp = true;
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
                // a directive line ends any macro call still looking for its "(" (as clang reads it)
                if (r.Tokens.Count > 0 && !r.Tokens[^1].Is(DirMark)) r.Tokens.Add(new CTok(CK.P, DirMark));
                Directive(sb.ToString().Trim(), defs, stack, ref live, (kw, rest) =>
                {
                    if (kw == "define" && ParseDefine(rest, false) is { } d) r.Defines.Add(d);
                    else if (kw == "pragma" && Regex.Match(rest, @"^weak\s+([A-Za-z_]\w*)\s*=\s*([A-Za-z_]\w*)") is { Success: true } m)
                        r.PragmaWeak.Add((m.Groups[1].Value, m.Groups[2].Value));
                });
                continue;
            }
            lineStart = false;
            var t = LexOne(text, ref i, sp, false);
            sp = false;
            if (live) r.Tokens.Add(t);
        }
        return r;
    }

    /// <summary>One token at <paramref name="i"/> (not whitespace or a comment). Digraphs read as what they spell
    /// (the bracket ones only in C).</summary>
    private static CTok LexOne(string s, ref int i, bool sp, bool asm)
    {
        int n = s.Length, st = i;
        char c = s[i];
        if (c == '"')
        {
            i++;
            while (i < n && s[i] != '"' && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
            var str = s[(st + 1)..Math.Min(i, n)];
            i = Math.Min(i + 1, n);
            return new CTok(CK.Str, str, sp);
        }
        if (c == '\'')
        {
            i++;
            while (i < n && s[i] != '\'' && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
            i = Math.Min(i + 1, n);
            return new CTok(CK.Chr, s[st..i], sp);
        }
        if (char.IsAsciiDigit(c) || c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1]))
        {
            i++;
            while (i < n && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.'
                             || (s[i] is '+' or '-' && s[i - 1] is 'e' or 'E' or 'p' or 'P'))) i++;
            return new CTok(CK.Num, s[st..i], sp);
        }
        if (char.IsAsciiLetter(c) || c == '_')
        {
            while (i < n && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return new CTok(CK.Id, s[st..i], sp);
        }
        string two = i + 1 < n ? s.Substring(i, 2) : "";
        if (two == "##") { i += 2; return new CTok(CK.P, "##", sp); }
        if (two == "%:")
        {
            if (i + 3 < n && s[i + 2] == '%' && s[i + 3] == ':') { i += 4; return new CTok(CK.P, "##", sp); }
            i += 2;
            return new CTok(CK.P, "#", sp);
        }
        if (!asm && two is "<%" or "%>" or "<:" or ":>")
        {
            i += 2;
            return new CTok(CK.P, two switch { "<%" => "{", "%>" => "}", "<:" => "[", _ => "]" }, sp);
        }
        i++;
        return new CTok(CK.P, c.ToString(), sp);
    }

    private static List<CTok> Lex(string s, bool asm)
    {
        var l = new List<CTok>();
        int i = 0;
        bool sp = false;
        while (i < s.Length)
        {
            if (s[i] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v') { sp = true; i++; continue; }
            l.Add(LexOne(s, ref i, sp, asm));
            sp = false;
        }
        return l;
    }

    private static string Rebuild(List<CTok> toks)
    {
        var sb = new StringBuilder();
        foreach (var t in toks) { if (t.Sp && sb.Length > 0) sb.Append(' '); sb.Append(t.Text); }
        return sb.ToString();
    }

    private static readonly HashSet<string> AsmKw = new(StringComparer.Ordinal) { "asm", "__asm__", "__asm" };
    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal) { "const", "volatile", "restrict", "__restrict", "__restrict__" };

    private void Decls(FnGraph g, int tu, List<CTok> toks, Macros mac, Unit u)
    {
        var decl = new List<CTok>();
        int paren = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Is("(")) paren++;
            else if (t.Is(")")) paren--;
            if (t.Is(";") && paren <= 0)
            {
                int eq = decl.FindIndex(x => x.Is("="));
                if (decl.Count > 0 && decl[0].K == CK.Id && AsmKw.Contains(decl[0].S)) TopAsm(g, tu, decl, u);
                else if (eq >= 0) Global(g, tu, decl, eq, mac, u);
                // a prototype's attributes count (used, retain, section, alias)
                else if (FuncName(decl) is { } p && decl.Any(x => x.K == CK.Id && x.S is "__attribute__" or "__attribute"))
                    Node(g, tu, p, decl, false, mac, u);
                decl.Clear();
                paren = 0;
                continue;
            }
            if (t.Is("{") && paren <= 0)
            {
                int end = Match(toks, i);
                if (decl.Any(x => x.Is("="))) { decl.AddRange(toks.GetRange(i, end - i + 1)); i = end; continue; }
                if (FuncName(decl) is { } name)
                {
                    var n = Node(g, tu, name, decl, true, mac, u);
                    Body(n, tu, toks, i + 1, end, mac);
                    decl.Clear();
                }
                // a struct or enum body is part of its declaration
                i = end;
                continue;
            }
            decl.Add(t);
        }
    }

    /// <summary>A function body's references. Inline asm (Carver ruling Q3): a name a branch or data directive
    /// names is a reference; any other name in the string only max follows.</summary>
    private static void Body(FnNode n, int tu, List<CTok> toks, int from, int to, Macros mac)
    {
        for (int j = from; j < to; j++)
        {
            var t = toks[j];
            if (t.K == CK.Id && AsmKw.Contains(t.S))
            {
                int k = j + 1;
                while (k < to && toks[k].K == CK.Id && toks[k].S is "volatile" or "__volatile__" or "__volatile" or "goto" or "inline") k++;
                if (k < to && toks[k].Is("("))
                {
                    var sb = new StringBuilder();
                    int m = k + 1;
                    for (; m < to && toks[m].K == CK.Str; m++) sb.Append(toks[m].S);
                    foreach (var (name, strong) in InlineAsmNames(sb.ToString())) (strong ? n.Refs : n.MaxRefs).Add((tu, name));
                    j = m - 1;   // the operands after ':' are C expressions: read as code
                    continue;
                }
            }
            if (t.K == CK.Id) n.Refs.Add((tu, t.S));
            else if (t.K == CK.Opaque) foreach (var x in mac.Closure(t.S)) n.MaxRefs.Add((tu, x));
        }
    }

    private static readonly Regex AsmName = new(@"(?<![\w.$])[A-Za-z_.$][\w.$]*", RegexOptions.CultureInvariant);
    private static IEnumerable<string> AsmIdents(string s) =>
        AsmName.Matches(s).Select(m => m.Value).Where(x => x != "." && !x.All(c => c == '.'));

    private static readonly HashSet<string> DataDirs = new(StringComparer.Ordinal)
    {
        ".word", ".long", ".4byte", ".int", ".short", ".hword", ".2byte", ".byte", ".quad", ".8byte", ".dc.a", ".dc.l",
        ".dc.w", ".inst", ".inst.n", ".inst.w", ".reloc", ".uleb128", ".sleb128", ".value",
    };

    // `bl foo` / `.word foo` / `ldr r0, =foo` in an inline asm string: strong; other names: only max
    private static IEnumerable<(string, bool)> InlineAsmNames(string s)
    {
        s = s.Replace("\\n", "\n").Replace("\\t", " ").Replace("\\r", "\n").Replace("\\\"", "\"");
        foreach (var raw in s.Split('\n', ';'))
        {
            string line = raw;
            int at = line.IndexOf('@');
            if (at >= 0) { foreach (var x in AsmIdents(line[at..])) yield return (x, false); line = line[..at]; }
            line = Regex.Replace(line, @"^\s*(?:[A-Za-z_.$][\w.$]*|\d+)\s*:", "").Trim();
            if (line.Length == 0) continue;
            int sp = line.IndexOfAny([' ', '\t']);
            string w = (sp < 0 ? line : line[..sp]).ToLowerInvariant(), rest = sp < 0 ? "" : line[sp..];
            bool strong = DataDirs.Contains(w) || w is "b" or "bl" or "blx" or "b.w" or "b.n" or "bl.w" or "cbz" or "cbnz"
                          || w.Length is 3 or 5 && w[0] == 'b' && w[1..3] is "eq" or "ne" or "cs" or "cc" or "hs" or "lo" or "mi" or "pl" or "vs" or "vc" or "hi" or "ls" or "ge" or "lt" or "gt" or "le" or "al";
            foreach (var x in AsmIdents(rest)) yield return (x, strong);
            if (!strong && w.StartsWith("ldr", StringComparison.Ordinal) && rest.IndexOf('=') is int eq and >= 0)
                foreach (var x in AsmIdents(rest[(eq + 1)..])) yield return (x, true);
        }
    }

    // file-scope `__asm__("...")`: assembled into the unit's own object
    private void TopAsm(FnGraph g, int tu, List<CTok> decl, Unit u)
    {
        var sb = new StringBuilder();
        foreach (var t in decl) if (t.K == CK.Str) sb.Append(t.S);
        string s = sb.ToString().Replace("\\n", "\n").Replace("\\t", " ").Replace("\\\"", "\"");
        var gas = new Gas(this, g, tu, g.MaxNames, null, u.IncDirs, Defs(u));
        foreach (var line in s.Split('\n')) gas.Raw(line);
        gas.Finish();
    }

    private static int Match(List<CTok> toks, int open)
    {
        int d = 0;
        for (int j = open; j < toks.Count; j++)
        {
            if (toks[j].K != CK.P) continue;
            if (toks[j].S == "{") d++;
            else if (toks[j].S == "}" && --d == 0) return j;
        }
        return toks.Count - 1;
    }

    private static int MatchBack(List<CTok> d, int close, string open, string shut)
    {
        int depth = 0;
        for (int b = close; b >= 0; b--)
        {
            if (d[b].Is(shut)) depth++;
            else if (d[b].Is(open) && --depth == 0) return b;
        }
        return -1;
    }

    // `... name ( params ) [__attribute__((...))]` -> name
    private static string? FuncName(List<CTok> d)
    {
        int e = d.Count - 1;
        while (e >= 0)
        {
            if (!d[e].Is(")")) return null;
            int b = MatchBack(d, e, "(", ")");
            if (b <= 0 || d[b - 1].K != CK.Id) return null;
            if (d[b - 1].S is "__attribute__" or "__attribute") { e = b - 2; continue; }
            return d[b - 1].S;
        }
        return null;
    }

    // `T name[...] attrs = init;` or `T (*const name)(...) attrs = init;`
    private static void Global(FnGraph g, int tu, List<CTok> d, int eq, Macros mac, Unit u)
    {
        string? name = null;
        for (int k = 0; k + 1 < eq && name is null; k++)
        {
            if (!d[k].Is("(") || !d[k + 1].Is("*")) continue;
            int j = k + 2;
            while (j < eq && d[j].K == CK.Id && Qualifiers.Contains(d[j].S)) j++;
            if (j < eq && d[j].K == CK.Id) name = d[j].S;
        }
        for (int j = eq - 1; j >= 0 && name is null; j--)
        {
            if (d[j].Is("]")) { j = MatchBack(d, j, "[", "]"); if (j < 0) return; continue; }
            if (d[j].Is(")"))
            {
                int b = MatchBack(d, j, "(", ")");
                if (b < 0) return;
                j = b > 0 && d[b - 1].K == CK.Id && d[b - 1].S is "__attribute__" or "__attribute" ? b - 1 : b;
                continue;
            }
            if (d[j].K == CK.Id && !Qualifiers.Contains(d[j].S)) name = d[j].S;
        }
        if (name is null) return;
        var n = Node(g, tu, name, d, false, mac, u);
        for (int k = eq + 1; k < d.Count; k++)
        {
            if (d[k].K == CK.Id) n.Refs.Add((tu, d[k].S));
            else if (d[k].K == CK.Opaque) foreach (var x in mac.Closure(d[k].S)) n.MaxRefs.Add((tu, x));
        }
    }

    private static FnNode Node(FnGraph g, int tu, string name, List<CTok> decl, bool isFunc, Macros mac, Unit u)
    {
        bool isStatic = decl.Any(x => x.K == CK.Id && x.S == "static");
        string key = isStatic ? $"{tu}:{name}" : name;
        if (!g.Nodes.TryGetValue(key, out var n)) g.Nodes[key] = n = new FnNode { Name = name, Tu = tu };
        n.IsFunc |= isFunc;
        for (int i = 0; i < decl.Count; i++)
        {
            if (decl[i].K != CK.Id || decl[i].S is not ("__attribute__" or "__attribute")) continue;
            int depth = 0;
            for (int j = i + 1; j < decl.Count; j++)
            {
                var (k, s, _, _) = decl[j];
                if (k == CK.P && s == "(") depth++;
                else if (k == CK.P && s == ")") { if (--depth == 0) break; }
                else if (k != CK.Id) continue;
                else if (s.Trim('_') == "retain") n.Retain = true;
                // used only stops the compiler dropping it; --gc-sections still may (Carver ruling Q6)
                else if (s.Trim('_') == "used") n.MaxRoot = true;
                // a constructor's pointer sits in .init_array: kept if a script KEEPs it, else open (ruling Q7)
                else if (s.Trim('_') == "constructor") { n.Sections.Add(".init_array"); n.MaxRoot = true; }
                else if (s.Trim('_') == "destructor") { n.Sections.Add(".fini_array"); n.MaxRoot = true; }
                else if (s.Trim('_') is "section" or "alias" && j + 2 < decl.Count && decl[j + 1].Is("(") && decl[j + 2].K == CK.Str)
                {
                    if (s.Trim('_') == "section") n.Sections.Add(decl[j + 2].S);
                    else n.Refs.Add((tu, decl[j + 2].S));
                }
            }
        }
        return n;
    }

    private enum Tri { False, True, Unknown }

    private static void Directive(string d, Dictionary<string, string> defs, List<(bool Parent, bool Done, bool Live)> stack, ref bool live,
                                  Action<string, string>? onLive = null)
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
                if (live) onLive?.Invoke(kw, rest);
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

    // ---------------- macros ----------------

    private sealed class MacroDef
    {
        public required string Name;
        public bool IsFunc, Variadic;
        public string[] Params = [];
        public required List<CTok> Body;
        public string Sig => (IsFunc ? "(" + string.Join(",", Params) + (Variadic ? "..." : "") + ")" : "") + Rebuild(Body);
    }

    /// <summary><c>NAME body</c> or <c>NAME(params) body</c> (the text after <c>#define</c>).</summary>
    private static MacroDef? ParseDefine(string d, bool asm)
    {
        int i = 0;
        while (i < d.Length && (char.IsAsciiLetterOrDigit(d[i]) || d[i] == '_')) i++;
        if (i == 0 || char.IsAsciiDigit(d[0])) return null;
        var m = new MacroDef { Name = d[..i], Body = null! };
        if (i < d.Length && d[i] == '(')
        {
            int close = d.IndexOf(')', i);
            if (close < 0) return null;
            var ps = new List<string>();
            foreach (var raw in d[(i + 1)..close].Split(','))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                if (p == "...") { m.Variadic = true; ps.Add("__VA_ARGS__"); }
                else if (p.EndsWith("...", StringComparison.Ordinal)) { m.Variadic = true; ps.Add(p[..^3].Trim()); }
                else ps.Add(p);
            }
            m.IsFunc = true;
            m.Params = ps.ToArray();
            i = close + 1;
        }
        m.Body = Lex(d[i..].Trim(), asm);
        if (m.Body.Count > 0) m.Body[0] = m.Body[0] with { Sp = false };
        return m;
    }

    /// <summary>A unit's macro table and expander (C99 6.10.3: arguments pre-expanded, # and ##, rescan with the
    /// macro hidden). A name with two different definitions (both #if branches live) is not expanded: it is an opaque
    /// token, and every name its definitions can reach is a max-only reference. So is every macro once the unit's
    /// expansion budget is spent.</summary>
    private sealed class Macros
    {
        private readonly Dictionary<string, MacroDef> _one = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MacroDef>> _all = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _closure = new(StringComparer.Ordinal);
        private int _budget = 4_000_000;

        public void Add(MacroDef d)
        {
            if (!_all.TryGetValue(d.Name, out var l)) { _all[d.Name] = l = new(); _one[d.Name] = d; }
            else if (!l.Any(x => x.Sig == d.Sig)) _ambiguous.Add(d.Name);
            if (!l.Any(x => x.Sig == d.Sig)) l.Add(d);
        }

        public bool Has(string name) => _all.ContainsKey(name);

        /// <summary>Every identifier a macro's definitions can reach, through the macros they name.</summary>
        public HashSet<string> Closure(string name)
        {
            if (_closure.TryGetValue(name, out var c)) return c;
            c = new HashSet<string>(StringComparer.Ordinal);
            var q = new Queue<string>();
            q.Enqueue(name);
            var seen = new HashSet<string>(StringComparer.Ordinal) { name };
            while (q.Count > 0)
                foreach (var d in _all.GetValueOrDefault(q.Dequeue()) ?? new())
                    foreach (var t in d.Body)
                        if (t.K == CK.Id && !d.Params.Contains(t.S))
                        {
                            c.Add(t.S);
                            if (_all.ContainsKey(t.S) && seen.Add(t.S)) q.Enqueue(t.S);
                        }
            return _closure[name] = c;
        }

        public List<CTok> Expand(List<CTok> input)
        {
            var stack = new List<CTok>(input.Count);
            for (int i = input.Count - 1; i >= 0; i--) stack.Add(input[i]);
            var o = new List<CTok>(input.Count);
            while (stack.Count > 0)
            {
                var t = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (t.Is(DirMark)) continue;
                if (t.K != CK.Id || !_one.TryGetValue(t.S, out var m) || t.H is { } h && h.Contains(t.S)) { o.Add(t); continue; }
                if (_ambiguous.Contains(t.S) || _budget <= 0) { o.Add(t with { K = CK.Opaque }); continue; }
                List<List<CTok>>? args = null;
                if (m.IsFunc)
                {
                    if (stack.Count == 0 || !stack[^1].Is("(")) { o.Add(t); continue; }
                    args = Args(stack);
                    if (args is null) { o.Add(t); continue; }
                }
                string[] hs = t.H is null ? [t.S] : [.. t.H, t.S];
                var r = Subst(m, args, hs);
                _budget -= r.Count + 1;
                if (r.Count > 0) r[0] = r[0] with { Sp = t.Sp };
                for (int i = r.Count - 1; i >= 0; i--) stack.Add(r[i]);
            }
            return o;
        }

        // the arguments of a call whose '(' is on top of the stack, popped; null (stack untouched) if never closed
        private static List<List<CTok>>? Args(List<CTok> stack)
        {
            int depth = 0, at = stack.Count - 1;
            for (; at >= 0; at--)
            {
                if (stack[at].Is("(")) depth++;
                else if (stack[at].Is(")") && --depth == 0) break;
            }
            if (at < 0) return null;
            var args = new List<List<CTok>> { new() };
            depth = 0;
            for (int i = stack.Count - 2; i > at; i--)
            {
                var x = stack[i];
                if (x.Is(DirMark)) continue;
                if (x.Is("(")) depth++;
                else if (x.Is(")")) depth--;
                if (x.Is(",") && depth == 0) { args.Add(new()); continue; }
                args[^1].Add(x);
            }
            stack.RemoveRange(at, stack.Count - at);
            return args;
        }

        private List<CTok> Subst(MacroDef m, List<List<CTok>>? args, string[] hs)
        {
            var body = m.Body;
            var r = new List<CTok>();
            bool paste = false;
            List<CTok> Arg(int p)
            {
                if (args is null) return new();
                if (m.Variadic && p == m.Params.Length - 1)
                {
                    var va = new List<CTok>();
                    for (int k = p; k < args.Count; k++)
                    {
                        if (k > p) va.Add(new CTok(CK.P, ","));
                        va.AddRange(args[k]);
                    }
                    return va;
                }
                return p < args.Count ? args[p] : new();
            }
            void Put(List<CTok> toks, bool sp)
            {
                for (int k = 0; k < toks.Count; k++)
                {
                    var x = toks[k];
                    x = x with { H = x.H is null ? hs : [.. x.H, .. hs] };
                    if (k == 0) x = x with { Sp = sp };
                    if (paste && k == 0)
                    {
                        paste = false;
                        if (r.Count > 0)
                        {
                            var a = r[^1];
                            string s = a.Text + x.Text;
                            r[^1] = new CTok(IsIdent(s) ? CK.Id : char.IsAsciiDigit(s[0]) ? CK.Num : a.K, s, a.Sp, hs);
                            continue;
                        }
                    }
                    r.Add(x);
                }
                if (toks.Count == 0 && paste) paste = false;   // a placemarker: nothing to paste onto
            }
            for (int i = 0; i < body.Count; i++)
            {
                var b = body[i];
                int pi = b.K == CK.Id ? Array.IndexOf(m.Params, b.S) : -1;
                if (b.Is("##")) { paste = r.Count > 0; continue; }
                if (m.IsFunc && b.Is("#") && i + 1 < body.Count && body[i + 1].K == CK.Id && Array.IndexOf(m.Params, body[i + 1].S) is int sp and >= 0)
                {
                    Put([new CTok(CK.Str, Rebuild(Arg(sp)).Replace("\\", "\\\\").Replace("\"", "\\\""))], b.Sp);
                    i++;
                    continue;
                }
                if (pi >= 0)
                {
                    bool raw = i + 1 < body.Count && body[i + 1].Is("##") || i > 0 && body[i - 1].Is("##");
                    // GNU `, ## __VA_ARGS__`: an empty one takes the comma with it
                    if (raw && m.Variadic && pi == m.Params.Length - 1 && Arg(pi).Count == 0 && paste && r.Count > 0 && r[^1].Is(","))
                    { r.RemoveAt(r.Count - 1); paste = false; continue; }
                    Put(raw ? Arg(pi) : Expand(Arg(pi)), b.Sp);
                    continue;
                }
                Put([b], b.Sp);
            }
            return r;
        }
    }

    // ---------------- assembly ----------------

    private int ResolveAsmInclude(int from, string name, List<string> incDirs, bool quoted)
    {
        IEnumerable<string> dirs = quoted ? incDirs.Prepend(Path.GetDirectoryName(_files[from])!) : incDirs;
        foreach (var d in dirs)
        {
            string p;
            try { p = Path.GetFullPath(Path.Combine(d, name)); } catch { continue; }
            if (IsUnder(p, _fwRoot) && File.Exists(p)) return FileIndex(p);
        }
        return -1;
    }

    private static string? Quoted(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '"') return null;
        int e = s.IndexOf('"', 1);
        return e < 0 ? null : s[1..e];
    }

    /// <summary>One assembled object: cpp (for .S/.sx: #if, #define, #include, comments), then gas (comments,
    /// .macro and .irp/.irpc/.rept expanded, .include followed, .if* on what it can decide). Each section is a
    /// node holding the references its statements make; each label and alias points at where it is defined. Every
    /// name it says goes into <c>max</c>; those in comments and dead branches only there (Carver ruling Q1).</summary>
    private sealed class Gas
    {
        private readonly FwSpec _s;
        private readonly FnGraph _g;
        private readonly int _obj;
        private readonly HashSet<string> _max;
        private readonly SortedSet<string>? _files;
        private readonly List<string> _incDirs;
        private readonly Dictionary<string, string> _defs;
        private readonly Macros _cpp = new();
        private readonly Dictionary<string, (string[] P, string[] Def, bool Va, List<string> Body)> _macros = new(StringComparer.Ordinal);
        private string _sec = ".text";
        private string _prev = ".text";
        private readonly Stack<string> _push = new();
        private readonly List<FnNode> _mine = new();
        private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
        private readonly HashSet<string> _global = new(StringComparer.Ordinal), _weak = new(StringComparer.Ordinal);
        private readonly HashSet<int> _seen = new();
        private readonly Stack<int> _file = new();
        private readonly List<(bool Parent, bool Done, bool Live)> _cond = new();
        private List<string>? _block;
        private int _blockDepth;
        private string _blockKind = "", _blockHead = "";
        private bool _inBlock, _armasm;
        private int _counter;

        public Gas(FwSpec s, FnGraph g, int obj, HashSet<string> max, SortedSet<string>? files, List<string> incDirs, Dictionary<string, string> defs)
        {
            (_s, _g, _obj, _max, _files, _incDirs) = (s, g, obj, max, files, incDirs);
            _defs = new Dictionary<string, string>(defs, StringComparer.Ordinal) { ["__ASSEMBLER__"] = "1" };
        }

        private bool Live => _cond.Count == 0 || _cond[^1].Live;

        public void File(int f, bool cpp)
        {
            if (!_seen.Add(f)) return;
            _files?.Add(_s._rel[f]);
            _file.Push(f);
            bool was = _armasm;
            _armasm = Path.GetExtension(_s._files[f]).Equals(".asm", StringComparison.OrdinalIgnoreCase);
            string text = Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(_s._files[f]));
            if (cpp) Cpp(text, f, false);
            else foreach (var line in text.Split('\n')) Raw(line);
            _armasm = was;
            _file.Pop();
        }

        // the cpp pass: directives, comments and dead branches out (their names into max), macros expanded
        private void Cpp(string text, int f, bool cHeader)
        {
            text = text.Replace("\\\r\n", "").Replace("\\\n", "");
            var stack = new List<(bool Parent, bool Done, bool Live)>();
            bool live = true, inBlock = false;
            foreach (var raw in text.Split('\n'))
            {
                var sb = new StringBuilder();
                string line = raw.TrimEnd('\r');
                for (int i = 0; i < line.Length; i++)
                {
                    if (inBlock)
                    {
                        int e = line.IndexOf("*/", i, StringComparison.Ordinal);
                        _max.UnionWith(AsmIdents(e < 0 ? line[i..] : line[i..e]));
                        if (e < 0) break;
                        inBlock = false;
                        i = e + 1;
                        sb.Append(' ');
                        continue;
                    }
                    if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; continue; }
                    if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') { _max.UnionWith(AsmIdents(line[i..])); break; }
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
                string s = sb.ToString();
                string trimmed = s.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    bool before = live;
                    Directive(trimmed[1..].Trim(), _defs, stack, ref live, (kw, rest) =>
                    {
                        if (kw == "define") { if (ParseDefine(rest, true) is { } d) _cpp.Add(d); }
                        else if (kw == "include") Include(rest, f);
                    });
                    if (!before) _max.UnionWith(AsmIdents(trimmed));
                    continue;
                }
                if (!live) { _max.UnionWith(AsmIdents(s)); continue; }
                if (s.Trim().Length == 0) continue;
                string x = Rebuild(_cpp.Expand(Lex(s, true)));
                // a C header's lines that are not assembler (prototypes, typedefs) never reach gas
                if (cHeader && !x.TrimStart().StartsWith('.')) { _max.UnionWith(AsmIdents(x)); continue; }
                Raw(x);
            }
        }

        private void Include(string rest, int from)
        {
            rest = rest.Trim();
            bool quoted = rest.StartsWith('"');
            string? name = quoted ? Quoted(rest) : rest.StartsWith('<') && rest.IndexOf('>') is int e and > 0 ? rest[1..e] : null;
            if (name is null) return;
            int h = _s.ResolveAsmInclude(from, name, _incDirs, quoted);
            if (h < 0 || !_seen.Add(h)) return;
            _files?.Add(_s._rel[h]);
            _file.Push(h);
            Cpp(Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(_s._files[h])), h, IsCExt(_s._files[h]));
            _file.Pop();
        }

        /// <summary>One source line for gas: its comments out (into max), then each ';'-separated statement.</summary>
        public void Raw(string raw)
        {
            string line = raw.TrimEnd('\r');
            var sb = new StringBuilder();
            var stmts = new List<string>();
            for (int i = 0; i < line.Length; i++)
            {
                if (_inBlock)
                {
                    int e = line.IndexOf("*/", i, StringComparison.Ordinal);
                    _max.UnionWith(AsmIdents(e < 0 ? line[i..] : line[i..e]));
                    if (e < 0) { i = line.Length; break; }
                    _inBlock = false;
                    i = e + 1;
                    continue;
                }
                char c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { _inBlock = true; i++; continue; }
                if (c == '@' || c == '/' && i + 1 < line.Length && line[i + 1] == '/' || _armasm && c == ';'
                    || c == '#' && sb.ToString().Trim().Length == 0 && stmts.Count == 0)
                {
                    _max.UnionWith(AsmIdents(line[i..]));
                    break;
                }
                if (c == '"')
                {
                    int e = line.IndexOf('"', i + 1);
                    while (e > 0 && line[e - 1] == '\\') e = line.IndexOf('"', e + 1);
                    if (e < 0) e = line.Length - 1;
                    sb.Append(line, i, e - i + 1);
                    i = e;
                    continue;
                }
                if (c == ';') { stmts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            stmts.Add(sb.ToString());
            foreach (var s in stmts) Line(s, 0);
        }

        private FnNode Sec()
        {
            string key = $"#s{_obj}:{_sec}";
            if (!_g.Nodes.TryGetValue(key, out var n))
            {
                _g.Nodes[key] = n = new FnNode { Name = key, Tu = _obj };
                n.Sections.Add(_sec);
                _mine.Add(n);
            }
            return n;
        }

        private void Use(string text, bool strong)
        {
            var n = Sec();
            foreach (var x in AsmIdents(text)) { (strong ? n.Refs : n.MaxRefs).Add((_obj, x)); _max.Add(x); }
        }

        private void Alias(string name, string expr, bool weak)
        {
            string key = $"#a{_obj}:{name}";
            if (!_g.Nodes.TryGetValue(key, out var n)) { _g.Nodes[key] = n = new FnNode { Name = key, Tu = _obj }; _mine.Add(n); }
            foreach (var x in AsmIdents(expr)) { (weak ? n.MaxRefs : n.Refs).Add((_obj, x)); _max.Add(x); }
            _labels[name] = key;
            _max.Add(name);
        }

        private static readonly Regex LabelRx = new(@"^\s*([A-Za-z_.$][\w.$]*|\d+)\s*:(?![:=])", RegexOptions.CultureInvariant);
        private static readonly Regex AssignRx = new(@"^([A-Za-z_$][\w.$]*)\s*==?\s*(.+)$", RegexOptions.CultureInvariant);

        private static string Word(string s)
        {
            int i = 0;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',') i++;
            return s[..i];
        }

        private void Line(string stmt, int depth)
        {
            string s = stmt.Trim();
            if (s.Length == 0) return;
            string w = Word(s), lw = w.ToLowerInvariant(), rest = s[w.Length..].Trim();
            if (_block is not null)
            {
                bool isMacro = _blockKind == ".macro";
                if (isMacro ? lw == ".macro" : lw is ".irp" or ".irpc" or ".rept") _blockDepth++;
                else if (isMacro ? lw == ".endm" : lw == ".endr")
                {
                    if (--_blockDepth == 0) { EndBlock(depth); return; }
                }
                _block.Add(s);
                return;
            }
            // gas conditionals: what it can decide (.ifb/.ifnb/.ifc/.ifnc), the rest live both ways
            if (lw.StartsWith(".if", StringComparison.Ordinal))
            {
                Tri c = lw switch
                {
                    ".ifb" => rest.Length == 0 ? Tri.True : Tri.False,
                    ".ifnb" => rest.Length != 0 ? Tri.True : Tri.False,
                    ".ifc" or ".ifnc" when rest.Split(',') is { Length: 2 } p => (p[0].Trim() == p[1].Trim()) == (lw == ".ifc") ? Tri.True : Tri.False,
                    _ => Tri.Unknown,
                };
                bool live = Live;
                _cond.Add((live, c == Tri.True, live && c != Tri.False));
                return;
            }
            if (lw is ".else" or ".elseif" && _cond.Count > 0)
            {
                var (parent, done, _) = _cond[^1];
                _cond[^1] = lw == ".else" ? (parent, true, parent && !done) : (parent, done, parent && !done);
                return;
            }
            if (lw == ".endif") { if (_cond.Count > 0) _cond.RemoveAt(_cond.Count - 1); return; }
            if (!Live) { _max.UnionWith(AsmIdents(s)); return; }

            for (var m = LabelRx.Match(s); m.Success; m = LabelRx.Match(s))
            {
                string name = m.Groups[1].Value;
                if (!char.IsAsciiDigit(name[0])) { _labels[name] = Sec().Name; _max.Add(name); }
                s = s[m.Length..].Trim();
            }
            if (s.Length == 0) return;
            w = Word(s);
            lw = w.ToLowerInvariant();
            rest = s[w.Length..].Trim().TrimStart(',').Trim();
            if (!w.StartsWith('.') && AssignRx.Match(s) is { Success: true } am) { Alias(am.Groups[1].Value, am.Groups[2].Value, false); return; }
            switch (lw)
            {
                case ".macro":
                {
                    _block = new();
                    _blockDepth = 1;
                    _blockKind = ".macro";
                    _blockHead = rest;
                    return;
                }
                case ".irp" or ".irpc" or ".rept":
                    _block = new();
                    _blockDepth = 1;
                    _blockKind = lw;
                    _blockHead = rest;
                    return;
                case ".endm" or ".endr" or ".exitm" or ".purgem":
                    return;
                case ".include":
                    if (Quoted(rest) is { } inc && _file.Count > 0 && _s.ResolveAsmInclude(_file.Peek(), inc, _incDirs, true) is int h and >= 0) File(h, false);
                    return;
                case ".section" or ".pushsection":
                {
                    string name = rest.StartsWith('"') ? Quoted(rest) ?? "" : Word(rest);
                    if (name.Length == 0) return;
                    if (lw == ".pushsection") _push.Push(_sec);
                    _prev = _sec;
                    _sec = name;
                    return;
                }
                case ".popsection":
                    if (_push.Count > 0) { _prev = _sec; _sec = _push.Pop(); }
                    return;
                case ".previous":
                    (_sec, _prev) = (_prev, _sec);
                    return;
                case ".text" or ".data" or ".bss":
                    _prev = _sec;
                    _sec = lw;
                    return;
                case ".global" or ".globl" or ".extern":
                    foreach (var x in AsmIdents(rest)) { _global.Add(x); _max.Add(x); }
                    return;
                case ".weak":
                    foreach (var x in AsmIdents(rest)) { _global.Add(x); _weak.Add(x); _max.Add(x); }
                    return;
                case ".weakref" or ".set" or ".equ" or ".equiv" or ".eqv" or ".thumb_set":
                {
                    int comma = rest.IndexOf(',');
                    if (comma <= 0) return;
                    Alias(rest[..comma].Trim(), rest[(comma + 1)..], lw == ".weakref");
                    return;
                }
                case ".type" or ".size" or ".syntax" or ".cpu" or ".thumb" or ".arm" or ".code" or ".code16" or ".code32" or ".fpu"
                    or ".eabi_attribute" or ".align" or ".p2align" or ".balign" or ".balignw" or ".balignl" or ".file" or ".ident"
                    or ".hidden" or ".protected" or ".internal" or ".thumb_func" or ".func" or ".endfunc" or ".ltorg" or ".pool"
                    or ".loc" or ".end" or ".arch" or ".arch_extension" or ".object_arch" or ".local" or ".altmacro" or ".noaltmacro"
                    or ".list" or ".nolist" or ".title" or ".sbttl" or ".psize" or ".eject" or ".fill" or ".space" or ".skip"
                    or ".zero" or ".org" or ".ascii" or ".asciz" or ".string" or ".incbin" or ".err" or ".error" or ".warning"
                    or ".print" or ".comm" or ".lcomm" or ".subsection":
                    return;
            }
            if (lw.StartsWith(".cfi_", StringComparison.Ordinal)) return;
            if (lw.StartsWith('.')) { Use(rest, DataDirs.Contains(lw)); return; }
            if (_macros.ContainsKey(w)) { Invoke(w, rest, depth); return; }
            // an instruction: its operands are references
            Use(rest, true);
        }

        private void EndBlock(int depth)
        {
            var body = _block!;
            string kind = _blockKind, head = _blockHead;
            _block = null;
            if (kind == ".macro")
            {
                var parts = head.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;
                var ps = new List<string>();
                var defs = new List<string>();
                bool va = false;
                foreach (var p in parts[1..])
                {
                    string x = p;
                    string def = "";
                    int eq = x.IndexOf('=');
                    if (eq >= 0) { def = x[(eq + 1)..]; x = x[..eq]; }
                    int q = x.IndexOf(':');
                    if (q >= 0) { if (x[(q + 1)..] == "vararg") va = true; x = x[..q]; }
                    ps.Add(x);
                    defs.Add(def);
                }
                _macros[parts[0]] = (ps.ToArray(), defs.ToArray(), va, body);
                return;
            }
            if (depth > 32) { foreach (var l in body) _max.UnionWith(AsmIdents(l)); return; }
            if (kind == ".rept") { foreach (var l in body) Line(l, depth + 1); return; }
            int c = head.IndexOfAny([',', ' ', '\t']);
            string sym = c < 0 ? head : head[..c].Trim();
            string vals = c < 0 ? "" : head[(c + 1)..].Trim();
            IEnumerable<string> each = kind == ".irpc" ? vals.Select(ch => ch.ToString())
                : vals.Contains(',') ? vals.Split(',').Select(v => v.Trim()) : vals.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var v in each)
                foreach (var l in body) Line(l.Replace("\\" + sym, v).Replace("\\()", ""), depth + 1);
        }

        private void Invoke(string name, string rest, int depth)
        {
            var (ps, defs, va, body) = _macros[name];
            if (depth > 32) { foreach (var l in body) _max.UnionWith(AsmIdents(l)); return; }
            var args = rest.Contains(',') ? rest.Split(',').Select(a => a.Trim()).ToList()
                : rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            var val = new string[ps.Length];
            for (int i = 0; i < ps.Length; i++) val[i] = defs[i];
            int pos = 0;
            foreach (var a in args)
            {
                int eq = a.IndexOf('=');
                if (eq > 0 && Array.IndexOf(ps, a[..eq].Trim()) is int k and >= 0) { val[k] = a[(eq + 1)..].Trim(); continue; }
                if (pos >= ps.Length) break;
                if (va && pos == ps.Length - 1) { val[pos] = string.Join(", ", args.Skip(args.IndexOf(a))); break; }
                val[pos++] = a;
            }
            _counter++;
            // longest parameter first: `\ab` is not `\a` then "b"
            var order = Enumerable.Range(0, ps.Length).OrderByDescending(i => ps[i].Length).ToArray();
            foreach (var l in body)
            {
                string x = l;
                foreach (int i in order) x = x.Replace("\\" + ps[i], val[i]);
                Line(x.Replace("\\()", "").Replace("\\@", _counter.ToString(System.Globalization.CultureInfo.InvariantCulture)), depth + 1);
            }
        }

        /// <summary>Labels into the graph (a .global/.weak one by name, else as obj:name); a reference to a .weak
        /// name this object never defines does not pull anything in (Carver condition 4): max only.</summary>
        public void Finish()
        {
            var weakUndef = _weak.Where(w => !_labels.ContainsKey(w)).ToHashSet(StringComparer.Ordinal);
            foreach (var n in _mine)
                foreach (var r in n.Refs.Where(r => weakUndef.Contains(r.Name)).ToList()) { n.Refs.Remove(r); n.MaxRefs.Add(r); }
            foreach (var (name, key) in _labels) _g.Label(_global.Contains(name) ? name : $"{_obj}:{name}", key);
        }
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

    private static readonly Regex KeepRx = new(@"\bKEEP\s*\(", RegexOptions.CultureInvariant);
    private const string DotAssign = @"(?:PROVIDE(?:_HIDDEN)?\s*\(\s*)?[A-Za-z_.$][\w.$]*\s*=\s*\.\s*\)?\s*;";
    private static readonly Regex BracketRx = new(DotAssign + @"\s*((?!KEEP\b)[^;{}()\s][^;{}()]*\([^;{}]*?\))(?=\s*" + DotAssign + ")", RegexOptions.CultureInvariant);

    /// <summary>A GNU linker script: every name it says (ENTRY, EXTERN, PROVIDE, assignments) is a root; each
    /// KEEP's section patterns keep the sections they match; patterns a <c>sym = .;</c> bracket holds without KEEP
    /// are max roots (Carver ruling Q5).</summary>
    private void LdScan(int f, HashSet<string> min, List<string> keepGlobs, List<string> maxGlobs)
    {
        string s = StripBlockComments(Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])));
        min.UnionWith(Idents(s));
        foreach (Match m in KeepRx.Matches(s))
        {
            int open = m.Index + m.Length - 1, depth = 0, close = open;
            for (; close < s.Length; close++)
            {
                if (s[close] == '(') depth++;
                else if (s[close] == ')' && --depth == 0) break;
            }
            keepGlobs.AddRange(SpecGlobs(s[(open + 1)..Math.Min(close, s.Length)]));
        }
        foreach (Match m in BracketRx.Matches(s)) maxGlobs.AddRange(SpecGlobs(m.Groups[1].Value));
    }

    private static readonly Regex SortRx = new(@"\b(?:SORT(?:_BY_NAME|_BY_ALIGNMENT|_BY_INIT_PRIORITY|_NONE)?|REVERSE)\b", RegexOptions.CultureInvariant);

    /// <summary>The section patterns of an input section description <c>filepat(secpat ...)</c>: its last
    /// top-level group, SORT and EXCLUDE_FILE wrappers removed.</summary>
    private static IEnumerable<string> SpecGlobs(string spec)
    {
        spec = Regex.Replace(spec, @"EXCLUDE_FILE\s*\([^)]*\)", " ");
        int depth = 0, start = -1, lastStart = -1, lastEnd = -1;
        for (int i = 0; i < spec.Length; i++)
        {
            if (spec[i] == '(') { if (depth++ == 0) start = i; }
            else if (spec[i] == ')' && depth > 0 && --depth == 0) { lastStart = start; lastEnd = i; }
        }
        if (lastStart < 0) return [];
        string inner = SortRx.Replace(spec[(lastStart + 1)..lastEnd], " ").Replace('(', ' ').Replace(')', ' ');
        return inner.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>An IAR .icf, armlink .sct or TI .cmd: the section names it says (each starts with '.'). The
    /// documented link reads none of them, so they are max roots only.</summary>
    private void InfraGlobs(int f, List<string> globs)
    {
        string s = StripBlockComments(Encoding.Latin1.GetString(File.ReadAllBytes(_files[f])));
        foreach (var w in s.Split([' ', '\t', '\r', '\n', '(', ')', '{', '}', ',', ';', '"', '='], StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 1 && w[0] == '.') globs.Add(w);
    }

    private sealed record ElfSec(string Name, uint Type, ulong Flags, long Off, long Size, uint Link, uint Info, long EntSize);
    private sealed record ElfSym(string Name, int Bind, int Shndx);

    /// <summary>An ELF relocatable (32 or 64 bit, either byte order): sections, symbols and relocations (target
    /// section, symbol index). Null if it is not one, or is cut short.</summary>
    private static (List<ElfSec> Secs, List<ElfSym> Syms, List<(int Target, int Sym)> Rels)? ElfRead(byte[] b, int at, int len)
    {
        if (len < 52 || at < 0 || at + (long)len > b.Length || b[at] != 0x7F || b[at + 1] != 'E' || b[at + 2] != 'L' || b[at + 3] != 'F') return null;
        bool is64 = b[at + 4] == 2, be = b[at + 5] == 2;
        if (b[at + 4] is not (1 or 2) || b[at + 5] is not (1 or 2) || is64 && len < 64) return null;

        ulong U(int off, int n)
        {
            if (off < 0 || off + n > len) throw new IndexOutOfRangeException();
            var s = b.AsSpan(at + off, n);
            return n switch
            {
                2 => be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                4 => be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => be ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
            };
        }
        try
        {
            long shoff = (long)(is64 ? U(40, 8) : U(32, 4));
            int shent = (int)U(is64 ? 58 : 46, 2), shnum = (int)U(is64 ? 60 : 48, 2), shstr = (int)U(is64 ? 62 : 50, 2);
            if (shent < (is64 ? 64 : 40) || shoff < 0 || shoff + (long)shent * shnum > len) return null;
            var raw = new List<(uint Name, uint Type, ulong Flags, long Off, long Size, uint Link, uint Info, long Ent)>();
            for (int i = 0; i < shnum; i++)
            {
                int h = (int)shoff + i * shent;
                raw.Add(is64
                    ? ((uint)U(h, 4), (uint)U(h + 4, 4), U(h + 8, 8), (long)U(h + 24, 8), (long)U(h + 32, 8), (uint)U(h + 40, 4), (uint)U(h + 44, 4), (long)U(h + 56, 8))
                    : ((uint)U(h, 4), (uint)U(h + 4, 4), U(h + 8, 4), (long)U(h + 16, 4), (long)U(h + 20, 4), (uint)U(h + 24, 4), (uint)U(h + 28, 4), (long)U(h + 36, 4)));
            }
            string Str(int sec, uint off)
            {
                if (sec <= 0 || sec >= raw.Count) return "";
                var r = raw[sec];
                if (r.Type == 8 || r.Off < 0 || r.Off + r.Size > len || off >= r.Size) return "";
                var s = b.AsSpan(at + (int)(r.Off + off), (int)(r.Size - off));
                int z = s.IndexOf((byte)0);
                return Encoding.ASCII.GetString(z < 0 ? s : s[..z]);
            }
            var secs = raw.Select(r => new ElfSec(Str(shstr, r.Name), r.Type, r.Flags, r.Off, r.Size, r.Link, r.Info, r.Ent)).ToList();
            var syms = new List<ElfSym>();
            int symSec = secs.FindIndex(x => x.Type == 2);   // SHT_SYMTAB
            if (symSec >= 0)
            {
                var st = secs[symSec];
                int size = is64 ? 24 : 16;
                if (st.Off < 0 || st.Off + st.Size > len) return null;
                for (long k = 0; k + size <= st.Size; k += size)
                {
                    int o = (int)(st.Off + k);
                    uint nm = (uint)U(o, 4);
                    int info = b[at + o + (is64 ? 4 : 12)];
                    int shndx = (int)U(o + (is64 ? 6 : 14), 2);
                    syms.Add(new ElfSym(Str((int)st.Link, nm), info >> 4, shndx));
                }
            }
            var rels = new List<(int, int)>();
            foreach (var r in secs)
            {
                if (r.Type is not (4 or 9) || r.Off < 0 || r.Off + r.Size > len) continue;   // SHT_RELA, SHT_REL
                int size = is64 ? (r.Type == 4 ? 24 : 16) : (r.Type == 4 ? 12 : 8);
                for (long k = 0; k + size <= r.Size; k += size)
                {
                    ulong info = is64 ? U((int)(r.Off + k + 8), 8) : U((int)(r.Off + k + 4), 4);
                    rels.Add(((int)r.Info, (int)(is64 ? info >> 32 : info >> 8)));
                }
            }
            return (secs, syms, rels);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>The members of an ar archive (GNU/SysV or BSD names; a thin archive's from the files it names), or
    /// the file itself: (name, bytes, offset, length).</summary>
    private static IEnumerable<(string Name, byte[] B, int At, int Len)> Members(byte[] b, string? path)
    {
        bool thin = b.Length >= 8 && Encoding.ASCII.GetString(b, 0, 8) == "!<thin>\n";
        if (b.Length < 8 || !thin && Encoding.ASCII.GetString(b, 0, 8) != "!<arch>\n") { yield return ("", b, 0, b.Length); yield break; }
        int at = 8;
        string longNames = "";
        while (at + 60 <= b.Length)
        {
            string name = Encoding.ASCII.GetString(b, at, 16).TrimEnd();
            if (!int.TryParse(Encoding.ASCII.GetString(b, at + 48, 10).Trim(), out int size) || size < 0) yield break;
            int data = at + 60;
            bool inline = !thin || name is "/" or "//" or "/SYM64/";
            if (inline && data + (long)size > b.Length) yield break;
            if (name == "//") longNames = Encoding.ASCII.GetString(b, data, size);
            else if (name is not ("/" or "/SYM64/" or "__.SYMDEF" or "__.SYMDEF SORTED"))
            {
                int skip = 0;
                if (name.StartsWith("#1/", StringComparison.Ordinal) && int.TryParse(name[3..], out int bl) && bl <= size)
                {
                    skip = bl;
                    name = Encoding.ASCII.GetString(b, data, bl).TrimEnd('\0');
                }
                else if (name.Length > 1 && name[0] == '/' && int.TryParse(name[1..], out int lo) && lo < longNames.Length)
                {
                    int end = longNames.IndexOf('\n', lo);
                    name = (end < 0 ? longNames[lo..] : longNames[lo..end]).TrimEnd('/');
                }
                else name = name.TrimEnd('/');
                if (name is not ("__.SYMDEF" or "__.SYMDEF SORTED"))
                {
                    if (!thin) yield return (name, b, data + skip, size - skip);
                    else if (path is not null && Path.Combine(Path.GetDirectoryName(path)!, name) is var mp && File.Exists(mp))
                    {
                        var mb = File.ReadAllBytes(mp);
                        yield return (name, mb, 0, mb.Length);
                    }
                }
            }
            at = data + (inline ? size + (size & 1) : 0);
        }
    }

    /// <summary>The undefined global symbols of an ELF object, or of each member of an ar archive.</summary>
    internal static void ObjUndefined(byte[] b, HashSet<string> into)
    {
        foreach (var (_, mb, at, len) in Members(b, null))
            if (ElfRead(mb, at, len) is var (_, syms, _))
                foreach (var s in syms) if (s.Shndx == 0 && s.Bind != 0 && s.Name.Length > 0) into.Add(s.Name);
    }

    /// <summary>A prebuilt object's (each archive member's) allocated sections as nodes: its global symbols point at
    /// their section, each relocation is a reference from the section it patches (a weak undefined one only for
    /// max), SHF_GNU_RETAIN is a root. Anything not ELF: every identifier-like string in it only max reaches.</summary>
    private void ObjNodes(FnGraph g, int f, byte[] b, HashSet<string> max)
    {
        bool ar = b.Length >= 8 && Encoding.ASCII.GetString(b, 0, 8) is "!<arch>\n" or "!<thin>\n";
        int m = 0;
        foreach (var (_, mb, at, len) in Members(b, _files[f]))
        {
            string ok = $"#o{f}.{m++}";
            if (ElfRead(mb, at, len) is not var (secs, syms, rels))
            {
                max.UnionWith(Idents(Encoding.Latin1.GetString(mb, at, len)).Where(x => x.Length > 1));
                continue;
            }
            string? Key(int shndx) => shndx > 0 && shndx < secs.Count && (secs[shndx].Flags & 2) != 0 ? $"{ok}:{shndx}" : null;
            for (int i = 1; i < secs.Count; i++)
            {
                if (Key(i) is not { } k) continue;
                var n = g.Nodes[k] = new FnNode { Name = k, Tu = -1, ArMember = ar, Retain = (secs[i].Flags & 0x200000) != 0 };
                n.Sections.Add(secs[i].Name);
            }
            foreach (var s in syms)
                if (s.Bind is 1 or 2 && s.Name.Length > 0 && Key(s.Shndx) is { } k) { g.Label(s.Name, k); max.Add(s.Name); }
            foreach (var (target, si) in rels)
            {
                if (Key(target) is not { } from || si <= 0 || si >= syms.Count) continue;
                var s = syms[si];
                var n = g.Nodes[from];
                if (s.Bind == 0) { if (Key(s.Shndx) is { } to) n.Edges.Add(to); continue; }
                if (s.Name.Length == 0) continue;
                (s.Shndx == 0 && s.Bind == 2 ? n.MaxRefs : n.Refs).Add((-1, s.Name));
                max.Add(s.Name);
            }
        }
    }

    /// <summary>The function names each of <paramref name="headers"/> says anywhere in it (one pass over the
    /// bytes; names hashed so most identifiers cost one lookup).</summary>
    private Dictionary<int, HashSet<string>> KHeaderNames(IEnumerable<int> headers, IEnumerable<string> names)
    {
        const ulong Basis = 14695981039346656037, Prime = 1099511628211;
        var byHash = new Dictionary<ulong, List<string>>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            ulong h = Basis;
            foreach (char c in name) h = (h ^ c) * Prime;
            if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new();
            l.Add(name);
        }
        var res = new Dictionary<int, HashSet<string>>();
        var buf = new byte[1 << 20];
        var tok = new byte[256];
        foreach (int f in headers.OrderBy(x => x))
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            using var fs = new FileStream(_files[f], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            ulong h = Basis;
            int len = 0;
            bool ident = false, num = false;
            void End()
            {
                if (ident && len <= tok.Length && byHash.TryGetValue(h, out var l))
                    foreach (var c in l) if (c.Length == len && Encoding.ASCII.GetString(tok, 0, len) == c) found.Add(c);
                ident = num = false;
                h = Basis;
                len = 0;
            }
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++)
                {
                    byte c = buf[i];
                    bool idc = c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or >= (byte)'0' and <= (byte)'9';
                    if (!idc) { if (ident || num) End(); continue; }
                    if (!ident && !num) { if (c is >= (byte)'0' and <= (byte)'9') { num = true; continue; } ident = true; }
                    if (num) continue;
                    if (len < tok.Length) tok[len] = c;
                    len++;
                    h = (h ^ c) * Prime;
                }
            End();
            if (found.Count > 0) res[f] = found;
        }
        return res;
    }
}
