using System.Text;

namespace CodeSpawner.Firmware;

/// <summary>Whether a line is in one build's preprocessed text: <c>Live</c> it is, <c>Dead</c> it is not,
/// <c>Unknown</c> a define in the tree decides (only max counts it).</summary>
internal enum LineState : byte { Live, Unknown, Dead }

/// <summary>
/// One target of a multi-target corpus (docs/firmware-design.md "Multi-target"): its db and build log, the link
/// line's script, library dirs and objects, and the files other targets use that it does not (it ignores them).
/// </summary>
internal sealed class FwScope
{
    public required string Name;
    public required int Index;
    public required int Targets;
    public required string DbPath, LogPath;
    /// <summary>fw-relative: the -T script, its INCLUDE closure, the objects and archives on the link line.</summary>
    public required string Script;
    public required HashSet<string> ScriptClosure, Objects;
    /// <summary>Where the target writes its every carved name and class, for the union.</summary>
    public string? ClsPath;
    /// <summary>fw-relative files only other targets use.</summary>
    public HashSet<string> Ignore = new(StringComparer.Ordinal);
    /// <summary>Names an <c>#if</c> tests that the tree #defines or #undefs (guards excepted).</summary>
    public HashSet<string> TreeDefined = new(StringComparer.Ordinal);
    /// <summary>Every -D of the target's units: the value when they all agree, null when they do not.</summary>
    public Dictionary<string, string?> Defines = new(StringComparer.Ordinal);
    /// <summary>The -D names some units give and others do not.</summary>
    public HashSet<string> Partial = new(StringComparer.Ordinal);

    /// <summary>Reads <c>ld.lld ... -T script -L dir ... objects -o x.elf</c> from a build log (paths corpus-relative).</summary>
    public static FwScope FromLog(string root, string name, int index, int targets, string dbPath, string logPath)
    {
        string fwRoot = Path.Combine(root, FwSpec.FwDir);
        string log = Path.Combine(root, logPath);
        if (!File.Exists(log)) throw new FwDbException($"{logPath} is missing");
        string? link = File.ReadLines(log).LastOrDefault(l => l.TrimStart().StartsWith("ld.lld ", StringComparison.Ordinal));
        if (link is null) throw new FwDbException($"{logPath} has no ld.lld line");
        var args = link.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? script = null;
        var libDirs = new List<string>();
        var objs = new HashSet<string>(StringComparer.Ordinal);
        string Rel(string p) => Path.GetRelativePath(fwRoot, Path.GetFullPath(Path.Combine(root, p))).Replace('\\', '/');
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-T" && i + 1 < args.Length) script = Rel(args[++i]);
            else if (a == "-L" && i + 1 < args.Length) libDirs.Add(args[++i]);
            else if (a == "-o" && i + 1 < args.Length) i++;
            else if (!a.StartsWith('-') && (a.EndsWith(".o", StringComparison.Ordinal) || a.EndsWith(".a", StringComparison.Ordinal)))
                objs.Add(Rel(a));
        }
        if (script is null) throw new FwDbException($"{logPath}: the ld.lld line has no -T script");
        // the script's INCLUDE closure: the script's own dir, then each -L dir
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var q = new Queue<string>();
        q.Enqueue(script);
        while (q.Count > 0)
        {
            string s = q.Dequeue();
            if (!closure.Add(s)) continue;
            string full = Path.Combine(fwRoot, s);
            if (!File.Exists(full)) continue;
            foreach (var line in File.ReadLines(full))
            {
                var t = line.Trim();
                if (!t.StartsWith("INCLUDE ", StringComparison.Ordinal)) continue;
                string inc = t[8..].Trim().Trim('"');
                foreach (var d in libDirs.Prepend(Path.GetDirectoryName(Path.Combine(FwSpec.FwDir, s))!))
                {
                    string cand = Path.Combine(root, d, inc);
                    if (File.Exists(cand)) { q.Enqueue(Rel(Path.GetRelativePath(root, cand))); break; }
                }
            }
        }
        return new FwScope
        {
            Name = name, Index = index, Targets = targets, DbPath = dbPath, LogPath = logPath,
            Script = script, ScriptClosure = closure, Objects = objs,
        };
    }

    /// <summary>A name's value in the target: a number, null when unknown; <paramref name="defined"/> whether it is.</summary>
    public Tri3 Value(string x, out long v)
    {
        v = 0;
        if (Defines.TryGetValue(x, out var s))
        {
            if (s is null || Partial.Contains(x) || TreeDefined.Contains(x)) return Tri3.Unknown;
            return FwCond.TryNumber(s, out v) ? Tri3.True : Tri3.Unknown;
        }
        if (Partial.Contains(x) || TreeDefined.Contains(x) || Reserved(x)) return Tri3.Unknown;
        return Tri3.False;   // nothing defines it: reads 0
    }

    public Tri3 Defined(string x)
    {
        if (Partial.Contains(x) || TreeDefined.Contains(x) || Reserved(x)) return Tri3.Unknown;
        return Defines.ContainsKey(x) ? Tri3.True : Tri3.False;
    }

    // the compiler's own names (__GNUC__, _WIN32): not the tree's to decide
    private static bool Reserved(string x) => x.Length >= 2 && x[0] == '_' && (x[1] == '_' || char.IsAsciiLetterUpper(x[1]));
}

internal enum Tri3 : byte { False, True, Unknown }

/// <summary>
/// Closed-world #if for one target: conditions evaluate on its -Ds, a name nothing defines is 0, a name the tree
/// defines is unknown. <see cref="Line"/> follows a file's lines and says which are in the build.
/// </summary>
internal sealed class FwCond
{
    private readonly FwScope _s;
    // per open #if: the enclosing state, a branch surely taken, a branch maybe taken, this branch's state
    private readonly List<(LineState Parent, bool Done, bool Maybe, LineState State)> _stack = new();

    public FwCond(FwScope s) => _s = s;

    public void Reset() => _stack.Clear();

    private LineState Cur => _stack.Count == 0 ? LineState.Live : _stack[^1].State;

    private static LineState Under(LineState parent, Tri3 c) =>
        parent == LineState.Dead || c == Tri3.False ? LineState.Dead : c == Tri3.Unknown || parent == LineState.Unknown ? LineState.Unknown : LineState.Live;

    /// <summary>The state of one logical line (a directive line is its enclosing region's).</summary>
    public LineState Line(LineKind k, ReadOnlySpan<byte> spliced)
    {
        switch (k)
        {
            case LineKind.Cond:
            {
                var p = Cur;
                var c = p == LineState.Dead ? Tri3.False : Cond(spliced);
                _stack.Add((p, c == Tri3.True, c != Tri3.False, Under(p, c)));
                return p;
            }
            case LineKind.Elif when _stack.Count > 0:
            case LineKind.Else when _stack.Count > 0:
            {
                var (p, done, maybe, _) = _stack[^1];
                Tri3 c = done || p == LineState.Dead ? Tri3.False : k == LineKind.Else ? Tri3.True : Cond(spliced);
                // an earlier branch maybe taken: this one only maybe
                var eff = c == Tri3.True && maybe ? Tri3.Unknown : c;
                _stack[^1] = (p, done || c == Tri3.True, maybe || c != Tri3.False, Under(p, eff));
                return p;
            }
            case LineKind.EndIf when _stack.Count > 0:
            {
                var p = _stack[^1].Parent;
                _stack.RemoveAt(_stack.Count - 1);
                return p;
            }
            default:
                return Cur;
        }
    }

    /// <summary>An #if/#ifdef/#ifndef/#elif line's condition.</summary>
    public Tri3 Cond(ReadOnlySpan<byte> line)
    {
        string s = StripComments(Encoding.Latin1.GetString(line)).Trim();
        if (s.StartsWith('#')) s = s[1..].TrimStart();
        else if (s.StartsWith("%:", StringComparison.Ordinal)) s = s[2..].TrimStart();
        int i = 0;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
        string kw = s[..i], rest = s[i..].Trim();
        return kw switch
        {
            "ifdef" or "elifdef" => _s.Defined(FirstIdent(rest)),
            "ifndef" or "elifndef" => Not(_s.Defined(FirstIdent(rest))),
            _ => Eval(rest),
        };
    }

    private static Tri3 Not(Tri3 t) => t == Tri3.Unknown ? t : t == Tri3.True ? Tri3.False : Tri3.True;

    private static string FirstIdent(string s)
    {
        int i = 0;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[..i];
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) break;
                i = e + 1;
                sb.Append(' ');
                continue;
            }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static bool TryNumber(string s, out long v)
    {
        s = s.Trim().TrimEnd('u', 'U', 'l', 'L');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        return long.TryParse(s, System.Globalization.NumberStyles.None, null, out v);
    }

    // ---------------- expression ----------------

    // a value, or unknown (null)
    private List<string> _t = new();
    private int _p;

    private Tri3 Eval(string e)
    {
        _t = Tokens(e);
        _p = 0;
        if (_t.Count == 0) return Tri3.Unknown;
        long? v;
        try { v = Ternary(); }
        catch (FormatException) { return Tri3.Unknown; }
        if (_p != _t.Count || v is null) return Tri3.Unknown;
        return v != 0 ? Tri3.True : Tri3.False;
    }

    private static List<string> Tokens(string e)
    {
        var l = new List<string>();
        int i = 0;
        while (i < e.Length)
        {
            char c = e[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int st = i;
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                while (i < e.Length && (char.IsAsciiLetterOrDigit(e[i]) || e[i] == '_')) i++;
                l.Add(e[st..i]);
                continue;
            }
            string two = i + 1 < e.Length ? e.Substring(i, 2) : "";
            if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=" or "<<" or ">>") { l.Add(two); i += 2; continue; }
            l.Add(c.ToString());
            i++;
        }
        return l;
    }

    private string? Peek => _p < _t.Count ? _t[_p] : null;
    private bool Take(string x) { if (Peek == x) { _p++; return true; } return false; }

    private long? Ternary()
    {
        var c = Or();
        if (!Take("?")) return c;
        var a = Ternary();
        if (!Take(":")) throw new FormatException();
        var b = Ternary();
        return c is null ? (a is not null && a == b ? a : null) : c != 0 ? a : b;
    }

    private long? Or()
    {
        var a = And();
        while (Take("||"))
        {
            var b = And();
            a = a is not null && a != 0 || b is not null && b != 0 ? 1 : a is null || b is null ? null : 0;
        }
        return a;
    }

    private long? And()
    {
        var a = Bin(0);
        while (Take("&&"))
        {
            var b = Bin(0);
            a = a == 0 || b == 0 ? 0 : a is null || b is null ? null : 1;
        }
        return a;
    }

    private static readonly string[][] Levels =
    [
        ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
    ];

    private long? Bin(int level)
    {
        if (level == Levels.Length) return Unary();
        var a = Bin(level + 1);
        while (Peek is { } op && Levels[level].Contains(op))
        {
            _p++;
            var b = Bin(level + 1);
            if (a is null || b is null) { a = null; continue; }
            long x = a.Value, y = b.Value;
            a = op switch
            {
                "|" => x | y, "^" => x ^ y, "&" => x & y, "==" => x == y ? 1 : 0, "!=" => x != y ? 1 : 0,
                "<" => x < y ? 1 : 0, ">" => x > y ? 1 : 0, "<=" => x <= y ? 1 : 0, ">=" => x >= y ? 1 : 0,
                "<<" => x << (int)(y & 63), ">>" => x >> (int)(y & 63), "+" => x + y, "-" => x - y, "*" => x * y,
                "/" => y == 0 ? null : x / y, _ => y == 0 ? null : x % y,
            };
        }
        return a;
    }

    private long? Unary()
    {
        if (Take("!")) { var v = Unary(); return v is null ? null : v == 0 ? 1 : 0; }
        if (Take("-")) { var v = Unary(); return v is null ? null : -v; }
        if (Take("+")) return Unary();
        if (Take("~")) { var v = Unary(); return v is null ? null : ~v; }
        if (Take("("))
        {
            var v = Ternary();
            if (!Take(")")) throw new FormatException();
            return v;
        }
        string t = Peek ?? throw new FormatException();
        _p++;
        if (t == "defined")
        {
            bool paren = Take("(");
            string x = Peek ?? throw new FormatException();
            _p++;
            if (paren && !Take(")")) throw new FormatException();
            var d = _s.Defined(x);
            return d == Tri3.Unknown ? null : d == Tri3.True ? 1 : 0;
        }
        if (char.IsAsciiDigit(t[0])) return TryNumber(t, out long n) ? n : throw new FormatException();
        if (t[0] == '\'') throw new FormatException();
        if (!char.IsAsciiLetter(t[0]) && t[0] != '_') throw new FormatException();
        // a function-like macro call: unknown
        if (Peek == "(")
        {
            int depth = 0;
            do { if (Peek == "(") depth++; else if (Peek == ")") depth--; _p++; } while (depth > 0 && _p < _t.Count);
            return null;
        }
        var r = _s.Value(t, out long val);
        return r == Tri3.Unknown ? null : r == Tri3.True ? val : 0;
    }

    // ---------------- the tree's defines ----------------

    /// <summary>The names an #if in <paramref name="files"/> tests that one of them #defines or #undefs, less header
    /// guards (<c>#ifndef X</c> then <c>#define X</c>).</summary>
    public static HashSet<string> TreeDefined(IReadOnlyList<string> files)
    {
        var tested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
            Walk(f, (k, kw, rest, _) =>
            {
                if (k is LineKind.Cond or LineKind.Elif)
                    foreach (var x in Tokens(rest)) if (char.IsAsciiLetter(x[0]) || x[0] == '_') tested.Add(x);
            });
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
            Walk(f, (k, kw, rest, guard) =>
            {
                if (k != LineKind.Define && kw != "undef") return;
                string x = FirstIdent(rest);
                if (tested.Contains(x) && x != guard) defined.Add(x);
            });
        return defined;
    }

    // each directive line: kind, keyword, the rest, and the guard name when the line just before was `#ifndef X`
    private static void Walk(string file, Action<LineKind, string, string, string?> on)
    {
        var bytes = File.ReadAllBytes(file);
        if (FwLexer.LooksBinary(file)) return;
        var text = FwLexer.TextBytes(bytes, out int st);
        var w = new LineWalker(text, st);
        string? prevIfndef = null;
        while (w.Next())
        {
            if (w.Kind == LineKind.Text) { if (w.Spliced.Trim(" \t\r"u8).Length > 0) prevIfndef = null; continue; }
            string s = StripComments(Encoding.Latin1.GetString(w.Spliced)).Trim();
            s = s.StartsWith('#') ? s[1..].TrimStart() : s.StartsWith("%:", StringComparison.Ordinal) ? s[2..].TrimStart() : s;
            string kw = FirstIdent(s), rest = s[kw.Length..].Trim();
            on(w.Kind, kw, rest, prevIfndef);
            prevIfndef = kw == "ifndef" ? FirstIdent(rest)
                : kw == "if" && rest.Replace(" ", "") is var r && r.StartsWith("!defined", StringComparison.Ordinal) ? FirstIdent(r[8..].Trim('(', ')')) : null;
        }
    }
}
