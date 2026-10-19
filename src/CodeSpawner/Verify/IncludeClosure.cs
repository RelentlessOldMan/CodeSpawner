using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeSpawner.Verify;

/// <summary>
/// Verify's own preprocessor for the include web (docs/include-web-design.md): it recomputes each unit's closure
/// from the corpus text, with no knowledge of the generator's model. It honours what the web relies on: include
/// guards, #if/#ifdef/#ifndef/#elif/#else/#endif (with <c>defined</c>, <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>,
/// integers and object-like macros), comments (a block comment may span lines; text in it is no directive),
/// backslash continuations, LF/CRLF/mixed line ends, quoted and angled includes (the includer's directory first for
/// a quoted one, then the -I dirs in order), relative paths, computed includes (a macro naming a quoted or angled
/// path) and #include_next (the search resumes after the dir the current file was found in), #pragma once, and an
/// active #error (a FAIL: a compiler would stop there). An include nesting deeper than 200 (where gcc and clang stop)
/// is a FAIL, not a stack overflow.
///
/// It is not a C preprocessor: function-like macros are never expanded, except <c>CAT(a, b)</c>, which pastes
/// <c>a_b</c> as cfg.h defines it; only names that some condition, computed include or TNAME needs (and the names
/// their bodies use, transitively) are tracked in headers (every #define still counts); and a template head is a non-directive line holding
/// <c>CAT(TNAME, x)</c>.
///
/// Each file is parsed once into events (runs of untracked defines merged into one), so a unit's walk costs its
/// directives, not its bytes.
/// </summary>
internal sealed class IncludeClosure
{
    public sealed record Instance(string Name, string Unit, int UnitLine, string File, int HeadLine, int Depth);

    public sealed class Result
    {
        public long Files, Bytes, Reads, ReadBytes, Defines;
        public int Depth, TemplateInstances;
        public readonly List<Instance> Instances = new();
        public string? Error;
    }

    private enum K : byte { DefRun, Define, Undef, If, Ifdef, Ifndef, Elif, Else, Endif, Include, Head, Once, Error }

    /// <summary>gcc's and clang's #include nesting limit.</summary>
    private const int MaxNesting = 200;

    private sealed class Ev
    {
        public K Kind;
        public int Line;
        public long N;            // DefRun: defines merged
        public string? Name;      // Define/Undef/Ifdef/Ifndef name, Include macro name, Head suffix
        public string? Text;      // Define body, If/Elif expression, Include path
        public bool FuncLike;     // Define
        public bool Angle, Next;  // Include
    }

    private sealed class PFile
    {
        public required List<Ev> Events;
        public string? Guard;
        public long Bytes;
    }

    private readonly string _corpus;
    private readonly string[] _dirs;                 // absolute -I dirs, in order
    private readonly HashSet<string> _units;
    private HashSet<string> _tracked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<PFile>> _parsed = new(PathCmp);
    private readonly ConcurrentDictionary<string, bool> _exists = new(PathCmp);
    private readonly Generation.Containment _paths;

    private static StringComparer PathCmp => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IncludeClosure(string corpus, IReadOnlyList<string> includeDirs, IEnumerable<string> unitRels, Generation.Containment paths)
    {
        _corpus = corpus;
        _paths = paths;
        _dirs = includeDirs.Select(d => Path.GetFullPath(Path.Combine(corpus, d))).ToArray();
        _units = new HashSet<string>(unitRels.Select(Full), PathCmp);
    }

    private string Full(string rel) => Path.GetFullPath(Path.Combine(_corpus, rel.Replace('/', Path.DirectorySeparatorChar)));
    private string Rel(string full) => Path.GetRelativePath(_corpus, full).Replace('\\', '/');

    /// <summary>Recompute every unit's closure (in parallel). Results are keyed by unit rel path.</summary>
    public Dictionary<string, Result> Run(IReadOnlyList<string> unitRels)
    {
        Discover();
        var results = new Result[unitRels.Count];
        Parallel.For(0, unitRels.Count, i => results[i] = Walk(unitRels[i]));
        var d = new Dictionary<string, Result>(StringComparer.Ordinal);
        for (int i = 0; i < unitRels.Count; i++) d[unitRels[i]] = results[i];
        return d;
    }

    // ---- phase 1: which names need tracking ----

    // Every file a unit could reach (each include taken whatever the conditions say, a computed include by every value
    // its macro is given anywhere), scanned for the names conditions, computed includes and template heads use.
    private void Discover()
    {
        var seen = new HashSet<string>(PathCmp);
        var queue = new List<string>();
        foreach (var u in _units) if (seen.Add(u)) queue.Add(u);
        foreach (var d in _dirs)
            if (Directory.Exists(d))
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    if (seen.Add(Path.GetFullPath(f))) queue.Add(Path.GetFullPath(f));

        var names = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        names["TNAME"] = 0;
        var strDefs = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal);
        // The names each macro's bodies use: a tracked macro's body is evaluated, so its names need tracking too.
        var uses = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal);
        var incs = new ConcurrentBag<(string From, string Spec, bool Angle, bool Macro)>();
        var macroIncs = new List<(string From, string Spec)>();
        while (queue.Count > 0)
        {
            Parallel.ForEach(queue, f =>
            {
                if (!Exists(f)) return;
                Scan(File.ReadAllBytes(f), keepAll: true, onDirective: (kind, name, text, angle, _) =>
                {
                    switch (kind)
                    {
                        case K.If: case K.Elif: foreach (var id in Idents(text!)) names[id] = 0; break;
                        case K.Ifdef: case K.Ifndef: names[name!] = 0; break;
                        case K.Define when !string.IsNullOrEmpty(text) && (text[0] == '"' || text[0] == '<'):
                            strDefs.GetOrAdd(name!, _ => new()).Add(text); break;
                        case K.Define when !string.IsNullOrEmpty(text):
                            foreach (var id in Idents(text).Distinct()) uses.GetOrAdd(name!, _ => new()).Add(id);
                            break;
                        case K.Include:
                            if (name is not null) { names[name] = 0; incs.Add((f, name, false, true)); }
                            else incs.Add((f, text!, angle, false));
                            break;
                    }
                }, onHead: null);
            });
            queue = new List<string>();
            // A computed include is retried every round: its macro may be defined in a file found later.
            foreach (var (from, spec, angle, macro) in incs)
                if (macro) macroIncs.Add((from, spec));
                else Follow(from, spec, angle);
            foreach (var (from, spec) in macroIncs)
                if (strDefs.TryGetValue(spec, out var vals))
                    foreach (var v in vals) Follow(from, v.Trim('"', '<', '>'), v[0] == '<');
            incs.Clear();
        }

        var tracked = new HashSet<string>(names.Keys, StringComparer.Ordinal);
        var work = new Stack<string>(tracked);
        while (work.Count > 0)
            if (uses.TryGetValue(work.Pop(), out var used))
                foreach (var id in used)
                    if (tracked.Add(id)) work.Push(id);
        _tracked = tracked;

        void Follow(string from, string spec, bool angle)
        {
            foreach (var cand in Candidates(spec, angle, from))
                if (Contained(cand) && Exists(cand) && seen.Add(cand)) queue.Add(cand);
        }
    }

    private IEnumerable<string> Candidates(string spec, bool angle, string from)
    {
        if (!angle) yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, spec));
        foreach (var d in _dirs) yield return Path.GetFullPath(Path.Combine(d, spec));
    }

    private bool Exists(string full) => _exists.GetOrAdd(full, File.Exists);

    private static readonly Regex Ident = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    // The identifiers in a directive's text: not the letters of a number (0x1Fu) or the text of a string.
    private static IEnumerable<string> Idents(string text)
    {
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c; i++) if (text[i] == '\\') i++;
                i++;
            }
            else if (char.IsAsciiDigit(c) || c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.')) i++;
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int a = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (text[a..i] != "defined") yield return text[a..i];
            }
            else i++;
        }
    }

    // ---- phase 2: events ----

    private PFile Parse(string full) => _parsed.GetOrAdd(full, f => new Lazy<PFile>(() =>
    {
        byte[] b = File.ReadAllBytes(f);
        bool keepAll = _units.Contains(f);
        var ev = new List<Ev>();
        Scan(b, keepAll, (kind, name, text, angle, next) =>
        {
            // (line is patched in by Scan through LastLine)
            if (kind == K.DefRun)
            {
                if (ev.Count > 0 && ev[^1].Kind == K.DefRun) ev[^1].N++;
                else ev.Add(new Ev { Kind = K.DefRun, N = 1, Line = _line.Value });
                return;
            }
            var e = new Ev { Kind = kind, Line = _line.Value, Name = name, Text = text, Angle = angle, Next = next };
            if (kind == K.Define) { e.FuncLike = _funcLike.Value; }
            ev.Add(e);
        }, onHead: (suffix, line) => ev.Add(new Ev { Kind = K.Head, Name = suffix, Line = line }));
        return new PFile { Events = ev, Guard = GuardOf(ev), Bytes = b.LongLength };
    })).Value;

    // The include-guard idiom: the first event #ifndef G, the second #define G, and the #ifndef's #endif is the last.
    private static string? GuardOf(List<Ev> ev)
    {
        if (ev.Count < 3 || ev[0].Kind != K.Ifndef || ev[1].Kind != K.Define || ev[1].Name != ev[0].Name || ev[^1].Kind != K.Endif) return null;
        int depth = 0;
        for (int i = 0; i < ev.Count; i++)
        {
            if (ev[i].Kind is K.If or K.Ifdef or K.Ifndef) depth++;
            else if (ev[i].Kind == K.Endif && --depth == 0) return i == ev.Count - 1 ? ev[0].Name : null;
        }
        return null;
    }

    // Scan state shared with the callbacks (per thread: files parse in parallel).
    private static readonly ThreadLocal<int> _line = new();
    private static readonly ThreadLocal<bool> _funcLike = new();

    private delegate void DirectiveSink(K kind, string? name, string? text, bool angle, bool next);

    private static readonly byte[] TnameBytes = Encoding.ASCII.GetBytes("TNAME");
    private static readonly Regex HeadRx = new(@"\bCAT\s*\(\s*TNAME\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)", RegexOptions.Compiled);

    /// <summary>Split <paramref name="b"/> into logical lines (continuations joined, comments replaced by a space, a
    /// block comment's newlines swallowed) and report each directive. An untracked #define is reported as a DefRun
    /// (a count, no name): <paramref name="keepAll"/> reports every one.</summary>
    private void Scan(byte[] b, bool keepAll, DirectiveSink onDirective, Action<string, int>? onHead)
    {
        var buf = new byte[256];
        int n = b.Length, i = 0, line = 1;
        while (i < n)
        {
            int start = line, len = 0;
            while (i < n)
            {
                byte c = b[i];
                if (c == '\\' && i + 1 < n && (b[i + 1] == '\n' || b[i + 1] == '\r' && i + 2 < n && b[i + 2] == '\n'))
                { i += b[i + 1] == '\n' ? 2 : 3; line++; continue; }
                if (c == '\n') { i++; line++; break; }
                if (c == '\r' && i + 1 < n && b[i + 1] == '\n') { i += 2; line++; break; }
                if (c == '/' && i + 1 < n && b[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(b[i] == '*' && i + 1 < n && b[i + 1] == '/')) { if (b[i] == '\n') line++; i++; }
                    i = Math.Min(n, i + 2);
                    Put(ref buf, ref len, (byte)' ');
                    continue;
                }
                if (c == '/' && i + 1 < n && b[i + 1] == '/')
                {
                    // Lines are spliced before comments are seen: a backslash-newline carries the comment on.
                    while (i < n && b[i] != '\n' && !(b[i] == '\r' && i + 1 < n && b[i + 1] == '\n'))
                    {
                        if (b[i] == '\\' && i + 1 < n && (b[i + 1] == '\n' || b[i + 1] == '\r' && i + 2 < n && b[i + 2] == '\n'))
                        { i += b[i + 1] == '\n' ? 2 : 3; line++; continue; }
                        i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    Put(ref buf, ref len, c); i++;
                    while (i < n && b[i] != c && b[i] != '\n')
                    {
                        if (b[i] == '\\' && i + 1 < n && b[i + 1] != '\n') Put(ref buf, ref len, b[i++]);
                        Put(ref buf, ref len, b[i++]);
                    }
                    if (i < n && b[i] == c) Put(ref buf, ref len, b[i++]);
                    continue;
                }
                Put(ref buf, ref len, c); i++;
            }
            Line(buf.AsSpan(0, len), start, keepAll, onDirective, onHead);
        }
    }

    private static void Put(ref byte[] buf, ref int len, byte c)
    {
        if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
        buf[len++] = c;
    }

    private void Line(ReadOnlySpan<byte> s, int line, bool keepAll, DirectiveSink sink, Action<string, int>? onHead)
    {
        int p = SkipWs(s, 0);
        if (p >= s.Length || s[p] != '#')
        {
            if (onHead is not null && s.IndexOf(TnameBytes) >= 0)
            {
                var m = HeadRx.Match(Encoding.ASCII.GetString(s));
                if (m.Success) onHead(m.Groups[1].Value, line);
            }
            return;
        }
        p = SkipWs(s, p + 1);
        int w = p;
        while (p < s.Length && IsId(s[p])) p++;
        var word = s[w..p];
        p = SkipWs(s, p);
        _line.Value = line;
        if (word.SequenceEqual("define"u8))
        {
            int a = p;
            while (p < s.Length && IsId(s[p])) p++;
            if (p == a) return;
            var name = s[a..p];
            if (!keepAll && !Tracked(name)) { sink(K.DefRun, null, null, false, false); return; }
            _funcLike.Value = p < s.Length && s[p] == '(';
            sink(K.Define, Str(name), Str(s[p..]).Trim(), false, false);
        }
        else if (word.SequenceEqual("undef"u8))
        {
            int a = p;
            while (p < s.Length && IsId(s[p])) p++;
            if (p > a && (keepAll || Tracked(s[a..p]))) sink(K.Undef, Str(s[a..p]), null, false, false);
        }
        else if (word.SequenceEqual("include"u8) || word.SequenceEqual("include_next"u8))
        {
            bool next = word.Length == "include_next".Length;
            var rest = Str(s[p..]).Trim();
            if (rest.Length >= 2 && rest[0] == '"' && rest.IndexOf('"', 1) > 0) sink(K.Include, null, rest[1..rest.IndexOf('"', 1)], false, next);
            else if (rest.Length >= 2 && rest[0] == '<' && rest.IndexOf('>') > 0) sink(K.Include, null, rest[1..rest.IndexOf('>')], true, next);
            else if (rest.Length > 0) sink(K.Include, Ident.Match(rest).Value, null, false, next);
        }
        else if (word.SequenceEqual("if"u8)) sink(K.If, null, Str(s[p..]).Trim(), false, false);
        else if (word.SequenceEqual("elif"u8)) sink(K.Elif, null, Str(s[p..]).Trim(), false, false);
        else if (word.SequenceEqual("ifdef"u8) || word.SequenceEqual("ifndef"u8))
        {
            int a = p;
            while (p < s.Length && IsId(s[p])) p++;
            sink(word.Length == 5 ? K.Ifdef : K.Ifndef, Str(s[a..p]), null, false, false);
        }
        else if (word.SequenceEqual("else"u8)) sink(K.Else, null, null, false, false);
        else if (word.SequenceEqual("endif"u8)) sink(K.Endif, null, null, false, false);
        else if (word.SequenceEqual("error"u8)) sink(K.Error, null, Str(s[p..]).Trim(), false, false);
        else if (word.SequenceEqual("pragma"u8))
        {
            int a = p;
            while (p < s.Length && IsId(s[p])) p++;
            if (s[a..p].SequenceEqual("once"u8) && SkipWs(s, p) == s.Length) sink(K.Once, null, null, false, false);
        }
        // #warning, other pragmas, #line, and the null directive: nothing the closure counts.
    }

    private bool Tracked(ReadOnlySpan<byte> name)
    {
        if (_tracked.Count == 0) return false;
        Span<char> c = stackalloc char[name.Length];
        for (int k = 0; k < name.Length; k++) c[k] = (char)name[k];
        return _tracked.Contains(new string(c));
    }

    private static int SkipWs(ReadOnlySpan<byte> s, int p)
    {
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\f' || s[p] == '\v')) p++;
        return p;
    }

    private static bool IsId(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    private static string Str(ReadOnlySpan<byte> s) => Encoding.Latin1.GetString(s);

    // ---- the walk ----

    private sealed class Macro(string body, bool funcLike)
    {
        public string Body = body;
        public bool FuncLike = funcLike;
    }

    private sealed class State
    {
        public readonly Dictionary<string, Macro> Macros = new(StringComparer.Ordinal);
        public readonly HashSet<string> Seen = new(PathCmp);
        public readonly HashSet<string> Once = new(PathCmp);   // files that executed #pragma once
        public required Result R;
        public required string Unit;
    }

    private Result Walk(string unitRel)
    {
        var st = new State { R = new Result(), Unit = unitRel };
        try { Enter(st, Full(unitRel), -1, 0, 0); }
        catch (WalkError e) { st.R.Error = e.Message; }
        return st.R;
    }

    private sealed class WalkError(string m) : Exception(m);

    private void Enter(State st, string full, int foundIdx, int depth, int topLine)
    {
        var pf = Parse(full);
        if (pf.Guard is not null && st.Macros.ContainsKey(pf.Guard)) return;   // skipped by its guard: not a read
        if (st.Once.Contains(full)) return;                                     // #pragma once: likewise
        if (depth > MaxNesting) throw new WalkError($"{Rel(full)}: #include nested deeper than {MaxNesting}");
        var r = st.R;
        r.Reads++; r.ReadBytes += pf.Bytes;
        if (st.Seen.Add(full)) { r.Files++; r.Bytes += pf.Bytes; }
        if (depth > r.Depth) r.Depth = depth;

        // Conditional stack: (this group active, some branch already taken, parent active).
        var stack = new Stack<(bool Active, bool Taken, bool Parent)>();
        bool active = true;
        (string Name, int Line)? head = null;
        foreach (var e in pf.Events)
        {
            switch (e.Kind)
            {
                case K.If:
                case K.Ifdef:
                case K.Ifndef:
                {
                    bool v = active && (e.Kind == K.If ? Eval(st, e.Text!, full, e.Line) != 0
                        : e.Kind == K.Ifdef ? st.Macros.ContainsKey(e.Name!) : !st.Macros.ContainsKey(e.Name!));
                    stack.Push((v, v, active));
                    active = v;
                    continue;
                }
                case K.Elif:
                {
                    if (stack.Count == 0) throw new WalkError($"{Rel(full)}:{e.Line}: #elif without #if");
                    var (_, taken, parent) = stack.Pop();
                    bool v = parent && !taken && Eval(st, e.Text!, full, e.Line) != 0;
                    stack.Push((v, taken || v, parent));
                    active = v;
                    continue;
                }
                case K.Else:
                {
                    if (stack.Count == 0) throw new WalkError($"{Rel(full)}:{e.Line}: #else without #if");
                    var (_, taken, parent) = stack.Pop();
                    bool v = parent && !taken;
                    stack.Push((v, true, parent));
                    active = v;
                    continue;
                }
                case K.Endif:
                    if (stack.Count == 0) throw new WalkError($"{Rel(full)}:{e.Line}: #endif without #if");
                    active = stack.Pop().Parent;
                    continue;
            }
            if (!active) continue;
            switch (e.Kind)
            {
                case K.DefRun: r.Defines += e.N; break;
                case K.Define: r.Defines++; st.Macros[e.Name!] = new Macro(e.Text!, e.FuncLike); break;
                case K.Undef: st.Macros.Remove(e.Name!); break;
                case K.Once: st.Once.Add(full); break;
                case K.Error: throw new WalkError($"{Rel(full)}:{e.Line}: #error {e.Text}");
                case K.Head:
                    if (head is null) head = (Expand(st, $"CAT(TNAME, {e.Name})", 0), e.Line);
                    break;
                case K.Include:
                {
                    string spec = e.Text ?? "";
                    bool angle = e.Angle;
                    if (e.Name is not null)
                    {
                        if (!st.Macros.TryGetValue(e.Name, out var m) || m.FuncLike)
                            throw new WalkError($"{Rel(full)}:{e.Line}: #include {e.Name}: not an object-like macro here");
                        string v = m.Body.Trim();
                        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') { spec = v[1..^1]; angle = false; }
                        else if (v.Length >= 2 && v[0] == '<' && v[^1] == '>') { spec = v[1..^1]; angle = true; }
                        else throw new WalkError($"{Rel(full)}:{e.Line}: #include {e.Name}: expands to {v}, not a path");
                    }
                    var (target, idx) = Resolve(spec, angle, e.Next, full, foundIdx)
                        ?? throw new WalkError($"{Rel(full)}:{e.Line}: #include {(angle ? $"<{spec}>" : $"\"{spec}\"")} not found");
                    Enter(st, target, idx, depth + 1, depth == 0 ? e.Line : topLine);
                    break;
                }
            }
        }
        if (stack.Count != 0) throw new WalkError($"{Rel(full)}: #if without #endif");
        if (head is { } h)
        {
            r.TemplateInstances++;
            r.Instances.Add(new Instance(h.Name, st.Unit, topLine, Rel(full), h.Line, depth));
        }
    }

    private (string Full, int Idx)? Resolve(string spec, bool angle, bool next, string from, int foundIdx)
    {
        int first = 0;
        if (next && foundIdx >= 0) first = foundIdx + 1;   // #include_next: after the dir this file was found in
        else if (!angle)
        {
            string c = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, spec));
            if (Contained(c) && Exists(c)) return (c, -1);
        }
        for (int k = first; k < _dirs.Length; k++)
        {
            string c = Path.GetFullPath(Path.Combine(_dirs[k], spec));
            if (Contained(c) && Exists(c)) return (c, k);
        }
        return null;
    }

    // An include may not leave the corpus (or pass through a link in it).
    // Containment caches what it has checked and is not thread-safe: one lock, and a cache of our own in front of it.
    private bool Contained(string full) => _contained.GetOrAdd(full, f => { lock (_paths) return _paths.Resolve(Rel(f)) is { } p && PathCmp.Equals(p, f); });
    private readonly ConcurrentDictionary<string, bool> _contained = new(PathCmp);

    // ---- #if and macro expansion ----

    private long Eval(State st, string expr, string file, int line)
    {
        var toks = Tokens(expr);
        int p = 0;
        long v = Or(st, toks, ref p, 0, file, line);
        if (p != toks.Count) throw new WalkError($"{Rel(file)}:{line}: cannot evaluate #if {expr}");
        return v;
    }

    private static List<string> Tokens(string s)
    {
        var t = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsAsciiLetter(c) || c == '_' || char.IsAsciiDigit(c))
            {
                int a = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                t.Add(s[a..i]);
                continue;
            }
            if (i + 1 < s.Length && (s.AsSpan(i, 2) is "&&" or "||" or "==" or "!=")) { t.Add(s.Substring(i, 2)); i += 2; continue; }
            t.Add(c.ToString()); i++;
        }
        return t;
    }

    private long Or(State st, List<string> t, ref int p, int d, string f, int l)
    {
        long v = And(st, t, ref p, d, f, l);
        while (p < t.Count && t[p] == "||") { p++; long w = And(st, t, ref p, d, f, l); v = v != 0 || w != 0 ? 1 : 0; }
        return v;
    }

    private long And(State st, List<string> t, ref int p, int d, string f, int l)
    {
        long v = Eq(st, t, ref p, d, f, l);
        while (p < t.Count && t[p] == "&&") { p++; long w = Eq(st, t, ref p, d, f, l); v = v != 0 && w != 0 ? 1 : 0; }
        return v;
    }

    private long Eq(State st, List<string> t, ref int p, int d, string f, int l)
    {
        long v = Unary(st, t, ref p, d, f, l);
        while (p < t.Count && t[p] is "==" or "!=")
        {
            bool eq = t[p++] == "==";
            long w = Unary(st, t, ref p, d, f, l);
            v = (v == w) == eq ? 1 : 0;
        }
        return v;
    }

    private long Unary(State st, List<string> t, ref int p, int d, string f, int l)
    {
        if (p >= t.Count) throw new WalkError($"{Rel(f)}:{l}: #if expression ends early");
        string x = t[p++];
        if (x == "!") return Unary(st, t, ref p, d, f, l) == 0 ? 1 : 0;
        if (x == "(")
        {
            long v = Or(st, t, ref p, d, f, l);
            if (p >= t.Count || t[p++] != ")") throw new WalkError($"{Rel(f)}:{l}: unbalanced ( in #if");
            return v;
        }
        if (x == "defined")
        {
            bool paren = p < t.Count && t[p] == "(";
            if (paren) p++;
            if (p >= t.Count) throw new WalkError($"{Rel(f)}:{l}: defined needs a name");
            string name = t[p++];
            if (paren && (p >= t.Count || t[p++] != ")")) throw new WalkError($"{Rel(f)}:{l}: unbalanced defined(");
            return st.Macros.ContainsKey(name) ? 1 : 0;
        }
        if (char.IsAsciiDigit(x[0]))
            return Number(x) ?? throw new WalkError($"{Rel(f)}:{l}: #if number {x} is not one this verifier reads");
        if (char.IsAsciiLetter(x[0]) || x[0] == '_')
        {
            if (!st.Macros.TryGetValue(x, out var m)) return 0;   // an undefined name is 0
            if (m.FuncLike || d > 32) throw new WalkError($"{Rel(f)}:{l}: #if uses {x}, which this verifier cannot expand");
            var bt = Tokens(m.Body);
            if (bt.Count == 0) throw new WalkError($"{Rel(f)}:{l}: #if uses {x}, which is empty");
            int q = 0;
            long v = Or(st, bt, ref q, d + 1, f, l);
            if (q != bt.Count) throw new WalkError($"{Rel(f)}:{l}: #if uses {x} = {m.Body}, which this verifier cannot evaluate");
            return v;
        }
        throw new WalkError($"{Rel(f)}:{l}: unexpected '{x}' in #if");
    }

    // An integer literal: decimal, 0x hex, 0b binary or 0 octal, with u/l suffixes; null if malformed or past 63 bits.
    private static long? Number(string x)
    {
        string num = x.TrimEnd('u', 'U', 'l', 'L');
        (string digits, int radix) = num.Length > 2 && num[0] == '0' && num[1] is 'x' or 'X' ? (num[2..], 16)
            : num.Length > 2 && num[0] == '0' && num[1] is 'b' or 'B' ? (num[2..], 2)
            : num.Length > 1 && num[0] == '0' ? (num[1..], 8)
            : (num, 10);
        ulong v = 0;
        foreach (char c in digits)
        {
            int dv = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiHexDigit(c) ? (c | 0x20) - 'a' + 10 : 99;
            if (dv >= radix || v > (ulong.MaxValue - (ulong)dv) / (ulong)radix) return null;
            v = v * (ulong)radix + (ulong)dv;
        }
        return digits.Length == 0 || v > long.MaxValue ? null : (long)v;
    }

    /// <summary>Expand object-like macros and <c>CAT(a, b)</c> (pastes <c>a_b</c>) in <paramref name="text"/>.</summary>
    private static string Expand(State st, string text, int d)
    {
        if (d > 32) return text;
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (!(char.IsAsciiLetter(c) || c == '_')) { sb.Append(c); i++; continue; }
            int a = i;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string id = text[a..i];
            int j = i;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (id == "CAT" && j < text.Length && text[j] == '(')
            {
                int depth = 0, comma = -1, k = j;
                for (; k < text.Length; k++)
                {
                    if (text[k] == '(') depth++;
                    else if (text[k] == ')' && --depth == 0) break;
                    else if (text[k] == ',' && depth == 1 && comma < 0) comma = k;
                }
                if (k < text.Length && comma > 0)
                {
                    sb.Append(Expand(st, text[(j + 1)..comma], d + 1).Trim()).Append('_').Append(Expand(st, text[(comma + 1)..k], d + 1).Trim());
                    i = k + 1;
                    continue;
                }
            }
            if (st.Macros.TryGetValue(id, out var m) && !m.FuncLike) sb.Append(Expand(st, m.Body, d + 1));
            else sb.Append(id);
        }
        return sb.ToString();
    }
}
