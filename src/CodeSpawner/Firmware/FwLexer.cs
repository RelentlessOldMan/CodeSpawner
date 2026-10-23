using System.Text;

namespace CodeSpawner.Firmware;

internal enum Tok : byte { Ident, Number, LParen, RParen, Comma, HashHash, Other }

internal delegate void SpanAction(ReadOnlySpan<byte> s);

/// <summary>Receives the tokens of a stream (docs/firmware-design.md, "Streams").</summary>
internal interface ITokenSink
{
    void Token(Tok kind, ReadOnlySpan<byte> text);
}

/// <summary>
/// The firmware analyzer's text layer: logical lines (with <c>\</c> continuations spliced), directive and define
/// recognition, a C token lexer in two modes, the raw identifier scan, and Carver's binary and density probes. It
/// works on bytes; a file is read whole (a UTF-8 BOM is skipped, UTF-16/32 text is decoded first).
/// </summary>
internal static class FwLexer
{
    public static bool IsIdStart(byte b) => (uint)((b | 0x20) - 'a') < 26 || b == '_';
    public static bool IsIdChar(byte b) => IsIdStart(b) || (uint)(b - '0') < 10;
    private static bool IsDigit(byte b) => (uint)(b - '0') < 10;
    private static bool IsSpace(byte b) => b == ' ' || b == '\t' || b == '\f' || b == '\v' || b == '\r';

    /// <summary>The offset just past a directive's leading <c>#</c> (or the digraph <c>%:</c>) and the blanks after
    /// it, leading blanks skipped; -1 if the line is no directive. A closed block comment counts as a blank.</summary>
    public static int DirectiveStart(ReadOnlySpan<byte> s)
    {
        int i = SkipGap(s, 0);
        if (i < s.Length && s[i] == '#') return SkipGap(s, i + 1);
        if (i + 1 < s.Length && s[i] == '%' && s[i + 1] == ':') return SkipGap(s, i + 2);
        return -1;
    }

    /// <summary>Past blanks and closed block comments from <paramref name="i"/>.</summary>
    public static int SkipGap(ReadOnlySpan<byte> s, int i)
    {
        while (i < s.Length)
        {
            if (IsSpace(s[i])) { i++; continue; }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int e = s[(i + 2)..].IndexOf("*/"u8);
                if (e < 0) return i;
                i += 2 + e + 2;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// Lexes one spliced logical line. <paramref name="shadow"/> false (min): comments are skipped and a string or
    /// char literal is one Other token, with <paramref name="inBlock"/> carried across lines. <paramref name="shadow"/>
    /// true: every byte is tokenized, comment and quote delimiters as Other, and <paramref name="inBlock"/> is untouched.
    /// </summary>
    public static void Lex(ReadOnlySpan<byte> s, ref bool inBlock, bool shadow, ITokenSink sink)
    {
        int i = 0, n = s.Length;
        if (!shadow && inBlock)
        {
            int e = s.IndexOf("*/"u8);
            if (e < 0) return;
            inBlock = false;
            i = e + 2;
        }
        while (i < n)
        {
            byte b = s[i];
            if (IsSpace(b) || b == '\n') { i++; continue; }
            if (IsIdStart(b))
            {
                int st = i++;
                while (i < n && IsIdChar(s[i])) i++;
                sink.Token(Tok.Ident, s[st..i]);
                continue;
            }
            if (IsDigit(b) || (b == '.' && i + 1 < n && IsDigit(s[i + 1])))
            {
                int st = i++;
                while (i < n)
                {
                    byte c = s[i];
                    if ((c == '+' || c == '-') && (s[i - 1] | 0x20) is (byte)'e' or (byte)'p') { i++; continue; }
                    if (IsIdChar(c) || c == '.') { i++; continue; }
                    break;
                }
                sink.Token(Tok.Number, s[st..i]);
                continue;
            }
            if (!shadow && b == '/' && i + 1 < n && s[i + 1] == '/') return;
            if (!shadow && b == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int e = s[(i + 2)..].IndexOf("*/"u8);
                if (e < 0) { inBlock = true; return; }
                i += 2 + e + 2;
                continue;
            }
            if (!shadow && (b == '"' || b == '\''))
            {
                int st = i++;
                while (i < n && s[i] != b) { if (s[i] == '\\' && i + 1 < n) i++; i++; }
                if (i < n) i++;
                sink.Token(Tok.Other, s[st..i]);
                continue;
            }
            switch (b)
            {
                case (byte)'(': sink.Token(Tok.LParen, s.Slice(i, 1)); i++; break;
                case (byte)')': sink.Token(Tok.RParen, s.Slice(i, 1)); i++; break;
                case (byte)',': sink.Token(Tok.Comma, s.Slice(i, 1)); i++; break;
                case (byte)'#' when i + 1 < n && s[i + 1] == '#': sink.Token(Tok.HashHash, s.Slice(i, 2)); i += 2; break;
                case (byte)'%' when i + 3 < n && s[i + 1] == ':' && s[i + 2] == '%' && s[i + 3] == ':':
                    sink.Token(Tok.HashHash, s.Slice(i, 4)); i += 4; break;   // the digraph `%:%:` is `##`
                default: sink.Token(Tok.Other, s.Slice(i, 1)); i++; break;
            }
        }
    }

    /// <summary>The raw identifier scan of rule 3: every <c>[A-Za-z0-9_]</c> run, leading digits stripped.</summary>
    public static void RawScan(ReadOnlySpan<byte> s, SpanAction each)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (!IsIdChar(s[i])) { i++; continue; }
            int st = i;
            while (i < n && IsIdChar(s[i])) i++;
            int a = st;
            while (a < i && IsDigit(s[a])) a++;
            if (a < i) each(s[a..i]);
        }
    }

    /// <summary>Mirror of CodeCarver HeaderCarver.LooksBinary: a NUL in the first 8192 bytes, unless a UTF-16/32 BOM
    /// leads; an unreadable file counts as binary.</summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            int n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
            if (n >= 4 && buf[0] == 0 && buf[1] == 0 && buf[2] == 0xFE && buf[3] == 0xFF) return false;
            return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>A file's text as bytes for lexing: UTF-16/32 (by BOM) is decoded to UTF-8, a UTF-8 BOM is dropped.</summary>
    public static byte[] TextBytes(byte[] raw, out int start)
    {
        start = 0;
        Encoding? enc = null;
        if (raw.Length >= 4 && raw[0] == 0xFF && raw[1] == 0xFE && raw[2] == 0 && raw[3] == 0) enc = new UTF32Encoding(false, true);
        else if (raw.Length >= 4 && raw[0] == 0 && raw[1] == 0 && raw[2] == 0xFE && raw[3] == 0xFF) enc = new UTF32Encoding(true, true);
        else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) enc = Encoding.Unicode;
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) enc = Encoding.BigEndianUnicode;
        if (enc is not null)
        {
            int bom = enc.GetPreamble().Length;
            return Encoding.UTF8.GetBytes(enc.GetString(raw, bom, raw.Length - bom));
        }
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) start = 3;
        return raw;
    }

    // ---- density: a copy of CodeCarver MacroDensity (same reads, same counting) ----

    public const long DenseMinBytes = 1_000_000;
    public const int DensePrefixChars = 256 * 1024;

    /// <summary>Carver's verdict, plus whether a one-line change flips it (<c>edge</c>).</summary>
    public static (bool Dense, bool Edge) Density(string path)
    {
        // an unreadable file is not "not dense": the truth would be silently wrong, so the error goes to the caller
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buf = new char[DensePrefixChars];
        int read = sr.ReadBlock(buf, 0, buf.Length);
        if (read <= 0) return (false, false);
        var (nonBlank, defines) = DensityCounts(buf.AsSpan(0, read), read == buf.Length);
        bool dense = Verdict(nonBlank, defines);
        bool edge = Verdict(nonBlank, defines + 1) != dense || (defines > 0 && Verdict(nonBlank, defines - 1) != dense)
            || Verdict(nonBlank + 1, defines) != dense || (nonBlank > 0 && Verdict(nonBlank - 1, Math.Min(defines, nonBlank - 1)) != dense);
        return (dense, edge);
    }

    private static bool Verdict(long nonBlank, long defines) => nonBlank >= 50 && (double)defines / nonBlank >= 0.60;

    public static (long NonBlank, long Defines) DensityCounts(ReadOnlySpan<char> text, bool prefixTruncated)
    {
        int end = text.Length;
        if (prefixTruncated)
        {
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline >= 0) end = lastNewline + 1;
        }
        long nonBlank = 0, defines = 0;
        int i = 0;
        while (i < end)
        {
            int lineStart = i;
            while (i < end && text[i] != '\n') i++;
            int lineEnd = i;
            if (i < end) i++;
            int j = lineStart;
            while (j < lineEnd && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r')) j++;
            if (j >= lineEnd) continue;
            if (text[j] == '/' && j + 1 < lineEnd && (text[j + 1] == '/' || text[j + 1] == '*')) continue;
            nonBlank++;
            if (IsDensityDefine(text, j, lineEnd)) defines++;
        }
        return (nonBlank, defines);
    }

    private static bool IsDensityDefine(ReadOnlySpan<char> t, int start, int endExcl)
    {
        if (start >= endExcl || t[start] != '#') return false;
        int i = start + 1;
        while (i < endExcl && (t[i] == ' ' || t[i] == '\t')) i++;
        const string word = "define";
        if (i + word.Length > endExcl) return false;
        for (int k = 0; k < word.Length; k++) if (t[i + k] != word[k]) return false;
        int after = i + word.Length;
        return after >= endExcl || !(char.IsAsciiLetterOrDigit(t[after]) || t[after] == '_');
    }
}

/// <summary>Kind of a logical line.</summary>
internal enum LineKind : byte { Text, Define, Cond, Elif, Else, EndIf, OtherDirective }

/// <summary>
/// Walks a file's logical lines. A logical line is physical lines joined while one ends in <c>\</c> (before its
/// LF or CRLF); its spliced text has the continuations removed. A line is a directive when its first non-blank
/// byte is <c>#</c> and it does not start inside a block comment.
/// </summary>
internal ref struct LineWalker
{
    private readonly ReadOnlySpan<byte> _s;
    private int _pos;
    private byte[] _buf;
    public bool InBlock;     // block-comment state at the start of the next line (min lexing)

    public int Start, Length, TermBytes, Lines;   // physical extent of the current logical line, its terminators
    public int SplicedLength;
    public LineKind Kind;
    public bool StartsInComment;

    public LineWalker(ReadOnlySpan<byte> s, int start)
    {
        _s = s;
        _pos = start;
        _buf = new byte[256];
        InBlock = false;
        Start = Length = TermBytes = Lines = SplicedLength = 0;
        Kind = LineKind.Text;
        StartsInComment = false;
    }

    public ReadOnlySpan<byte> Spliced => _buf.AsSpan(0, SplicedLength);
    public ReadOnlySpan<byte> Raw => _s.Slice(Start, Length);

    public bool Next()
    {
        if (_pos >= _s.Length) return false;
        Start = _pos;
        TermBytes = 0;
        Lines = 0;
        SplicedLength = 0;
        while (true)
        {
            int nl = _s[_pos..].IndexOf((byte)'\n');
            int end = nl < 0 ? _s.Length : _pos + nl;           // index of '\n' or EOF
            int content = end;
            if (content > _pos && _s[content - 1] == '\r' && nl >= 0) content--;
            int term = nl < 0 ? 0 : end + 1 - content;
            Lines++;
            bool cont = nl >= 0 && content > _pos && _s[content - 1] == '\\';
            Append(_s[_pos..(cont ? content - 1 : content)]);
            TermBytes += term;
            _pos = nl < 0 ? _s.Length : end + 1;
            if (!cont || _pos >= _s.Length) break;
        }
        Length = _pos - Start;
        StartsInComment = InBlock;
        Kind = Classify(Spliced, InBlock);
        // carry the block-comment state to the next line
        bool ib = InBlock;
        FwLexer.Lex(Spliced, ref ib, false, NullSink.Instance);
        InBlock = ib;
        return true;
    }

    private void Append(ReadOnlySpan<byte> part)
    {
        if (SplicedLength + part.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, SplicedLength + part.Length));
        part.CopyTo(_buf.AsSpan(SplicedLength));
        SplicedLength += part.Length;
    }

    private static LineKind Classify(ReadOnlySpan<byte> s, bool inBlock)
    {
        if (inBlock) return LineKind.Text;
        int i = FwLexer.DirectiveStart(s);
        if (i < 0) return LineKind.Text;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        int st = i;
        while (i < s.Length && FwLexer.IsIdChar(s[i])) i++;
        var w = s[st..i];
        if (w.SequenceEqual("define"u8)) return LineKind.Define;
        if (w.SequenceEqual("if"u8) || w.SequenceEqual("ifdef"u8) || w.SequenceEqual("ifndef"u8)) return LineKind.Cond;
        if (w.SequenceEqual("elif"u8)) return LineKind.Elif;
        if (w.SequenceEqual("else"u8)) return LineKind.Else;
        if (w.SequenceEqual("endif"u8)) return LineKind.EndIf;
        return LineKind.OtherDirective;
    }
}

internal sealed class NullSink : ITokenSink
{
    public static readonly NullSink Instance = new();
    public void Token(Tok kind, ReadOnlySpan<byte> text) { }
}

/// <summary>A parsed <c>#define</c>: its name, parameters and body tokens (min lexing).</summary>
internal sealed class DefineParse : ITokenSink
{
    public int NameStart, NameLen;
    public bool IsFunc;
    public bool Variadic;                     // ends in `...` (named or not)
    public readonly List<byte[]> Params = new();
    public byte[]? VariadicName;              // __VA_ARGS__ or the named variadic param
    public readonly List<(Tok Kind, int Start, int Len)> Body = new();
    public int BodyStart;                     // offset of the body in the spliced line
    public bool EndsInComment;                // the logical line ends inside an open block comment
    private bool _collecting;
    private byte[] _tok = new byte[256];
    private int _tokUsed;

    /// <summary>Parses a define line's spliced text; false if it has no name.</summary>
    public bool Parse(ReadOnlySpan<byte> s)
    {
        Params.Clear(); Body.Clear(); _tokUsed = 0; IsFunc = false; Variadic = false; VariadicName = null; EndsInComment = false;
        int i = FwLexer.DirectiveStart(s);
        if (i < 0) return false;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        i += 6; // "define"
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\f' || s[i] == '\v')) i++;
        // a comment between `define` and the name (`#define /*x*/NAME`)
        while (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
        {
            int e = s[(i + 2)..].IndexOf("*/"u8);
            if (e < 0) return false;
            i += 2 + e + 2;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        }
        if (i >= s.Length || !FwLexer.IsIdStart(s[i])) return false;
        NameStart = i;
        while (i < s.Length && FwLexer.IsIdChar(s[i])) i++;
        NameLen = i - NameStart;
        if (i < s.Length && s[i] == '(')
        {
            IsFunc = true;
            i++;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                if (i < s.Length && s[i] == ')') { i++; break; }
                if (i + 2 < s.Length && s[i] == '.' && s[i + 1] == '.' && s[i + 2] == '.')
                {
                    Variadic = true;
                    VariadicName = "__VA_ARGS__"u8.ToArray();
                    i += 3;
                    continue;
                }
                if (i < s.Length && FwLexer.IsIdStart(s[i]))
                {
                    int st = i;
                    while (i < s.Length && FwLexer.IsIdChar(s[i])) i++;
                    var p = s[st..i].ToArray();
                    while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
                    if (i + 2 < s.Length && s[i] == '.' && s[i + 1] == '.' && s[i + 2] == '.')
                    {
                        Variadic = true;
                        VariadicName = p;
                        i += 3;
                    }
                    Params.Add(p);
                    continue;
                }
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length) i++; // malformed: skip
            }
            if (Variadic && VariadicName is not null && !Params.Any(p => p.AsSpan().SequenceEqual(VariadicName)))
                Params.Add(VariadicName);
        }
        BodyStart = i;
        _collecting = true;
        bool ib = false;
        FwLexer.Lex(s[i..], ref ib, false, this);
        _collecting = false;
        EndsInComment = ib;
        return true;
    }

    public void Token(Tok kind, ReadOnlySpan<byte> text)
    {
        if (!_collecting) return;
        if (_tokUsed + text.Length > _tok.Length) Array.Resize(ref _tok, Math.Max(_tok.Length * 2, _tokUsed + text.Length));
        text.CopyTo(_tok.AsSpan(_tokUsed));
        Body.Add((kind, _tokUsed, text.Length));
        _tokUsed += text.Length;
    }

    /// <summary>The text of body token <paramref name="k"/>.</summary>
    public ReadOnlySpan<byte> Text(int k) => _tok.AsSpan(Body[k].Start, Body[k].Len);

    public bool IsParam(ReadOnlySpan<byte> id)
    {
        foreach (var p in Params) if (id.SequenceEqual(p)) return true;
        return false;
    }

    /// <summary>Rule 1: some <c>##</c> has a parameter beside it, other than <c>, ## __VA_ARGS__</c>.</summary>
    public bool PastesParam()
    {
        for (int k = 0; k < Body.Count; k++)
        {
            if (Body[k].Kind != Tok.HashHash) continue;
            bool l = k > 0 && Body[k - 1].Kind == Tok.Ident && IsParam(Text(k - 1));
            bool r = k + 1 < Body.Count && Body[k + 1].Kind == Tok.Ident && IsParam(Text(k + 1));
            if (r && k > 0 && Body[k - 1].Kind == Tok.Comma && VariadicName is not null && Text(k + 1).SequenceEqual(VariadicName))
                r = false;
            if (l || r) return true;
        }
        return false;
    }
}
