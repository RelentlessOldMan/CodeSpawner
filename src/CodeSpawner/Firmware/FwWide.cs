using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Firmware;

/// <summary>
/// The work carve's wide population (since 1.4.11; docs/firmware-design.md "Work shape"): the over-keep of a sound
/// static carve at work. A few thousand paste macros <c>KWP{j}</c> with short literal pieces are named, never called,
/// in the bodies of kept roots <c>KWT{j}</c>, so each is a wide pattern <c>pre*suf</c>. Under rule W (CodeCarver)
/// a name only a carved define's body names is named under max only, so what the patterns build is open, and so is
/// everything only their bodies reach.
/// <list type="bullet">
/// <item><b>Pieces.</b> Two-sided patterns are <c>Y{c}{c}</c> * <c>{c}{c}Y</c> (c a lowercase letter but q, or a
/// digit); one-sided ones are <c>Yq{c}{c}</c> * or * <c>{c}{c}qY</c>. The piece sets are prefix-free and
/// suffix-free, so each name matches its own pattern only.</item>
/// <item><b>Matched</b> (open): a pattern's piece(s) around an upper-case middle; a one-sided one's other end is
/// <c>Y_</c> or <c>_Y</c>.</item>
/// <item><b>Named-only</b> (open): <c>Y_{mid}_Y</c>, matched by no pattern, named in a matched define's body.</item>
/// <item><b>Near misses</b> (dropped): a two-sided pattern's start with a wrong end <c>{pre}{mid}{cc}Z</c>, a wrong
/// start with its end <c>Z{cc}{mid}{suf}</c>, or its end inside the name <c>{pre}{mid}{suf}Z</c>.</item>
/// <item><b>Body macros.</b> Paste macros <c>Y_WP{j}_Y</c> and forwarders <c>Y_WF{j}_Y</c> that matched bodies call
/// (open), and many more <c>ZWQP{j}Z</c> / <c>ZWQF{j}Z</c> that nothing names (dropped).</item>
/// <item><b>Edges</b> (kept, CodeCarver): a forwarder ending in a paster's name, called with arguments, and a body
/// calling a paster (<c>KWEQ{i} (x)</c>): both are calls, not rule W's guesses.</item>
/// </list>
/// </summary>
internal sealed class FwWide
{
    public const int Edges = 2;
    private const string C = "abcdefghijklmnoprstuvwxyz0123456789";   // piece chars: no q (one-sided pieces own it)
    private const int NC = 35, NP2 = NC * NC;

    /// <summary>Patterns: two-sided, prefix-only, suffix-only (also the wide pasters KWP and roots KWT).</summary>
    public long Two, Pre, Suf;
    public long Patterns => Two + Pre + Suf;
    public long Matched, Named, Near;
    public long UsedPasters, UnusedPasters, UsedForwarders, UnusedForwarders;
    public long Uses, PiecesS, PiecesE;
    public long Pieces => PiecesS + PiecesE;

    /// <summary>The work defines this population writes.</summary>
    public long Defines => 2 * Patterns + Matched + Named + Near + UsedPasters + UnusedPasters + UsedForwarders + UnusedForwarders + EdgeDefines;
    public const long EdgeDefines = Edges * 6;   // per edge: KWEP, KWEF, KWEA…_Q, KWEQ, KWEC, KWEB…_Q

    public static FwWide Plan(GenOptions o, long budget, long defines)
    {
        var w = new FwWide();
        long patterns = Math.Max(3, o.Eff("fw-work-patterns", o.FwWorkPatterns));
        w.Pre = patterns * 15 / 100;
        w.Suf = patterns * 15 / 100;
        w.Two = patterns - w.Pre - w.Suf;
        if (w.Two > NP2 * (long)NP2 || w.Pre > NP2 || w.Suf > NP2)
            throw new ArgException($"--fw-work-patterns {patterns} is more than the piece sets hold");
        w.Near = (long)(defines * o.FwWorkNearPct / 100);
        w.Named = (long)(defines * o.FwWorkNamedPct / 100);
        long pasters = o.Eff("fw-work-body-pasters", o.FwWorkBodyPasters), forwarders = o.Eff("fw-work-body-forwarders", o.FwWorkBodyForwarders);
        w.Uses = o.Eff("fw-work-body-uses", o.FwWorkBodyUses);
        long pieces = o.Eff("fw-work-pieces", o.FwWorkPieces);
        // the bodies' uses call a tenth of the body macros (half the uses direct, half through a forwarder); the rest
        // nothing names
        long half = (w.Uses + 1) / 2;
        w.UsedPasters = Math.Min(Math.Min(pasters, Math.Max(1, pasters / 10)), half);
        w.UsedForwarders = Math.Min(Math.Min(forwarders, Math.Max(1, forwarders / 10)), w.Uses / 2);
        if (w.Uses == 0) w.UsedPasters = w.UsedForwarders = 0;
        w.UnusedPasters = pasters - w.UsedPasters;
        w.UnusedForwarders = w.UnusedPasters > 0 ? forwarders - w.UsedForwarders : 0;
        w.PiecesS = Math.Min(w.Uses, Math.Max(w.Uses > 0 ? 1 : 0, pieces / 2));
        w.PiecesE = Math.Min(w.Uses, Math.Max(w.Uses > 0 ? 1 : 0, pieces - pieces / 2));
        // the matched defines take the rest of the budget; each matched body names at most one named-only name and
        // holds at most one use
        long fixedDefs = 2 * w.Patterns + w.Named + w.Near + pasters + w.UsedForwarders + w.UnusedForwarders + EdgeDefines;
        if (fixedDefs + Math.Max(w.Named, w.Uses) > budget)
            throw new ArgException($"the work carve's wide population needs {fixedDefs + Math.Max(w.Named, w.Uses):N0} defines (patterns, body macros, "
                + $"near misses, named-only), but --fw-work-defines leaves {budget:N0}; raise it or lower --fw-work-patterns / -body-* / -near-pct / -named-pct");
        w.Matched = Math.Max(Math.Max(w.Named, w.Uses), budget - fixedDefs);
        w.Matched = Math.Max(w.Matched, w.Patterns);
        return w;
    }

    // ---------------- names ----------------

    private static readonly string[] Syl =
        ["CTRL", "STAT", "CFG", "EN", "IRQ", "DMA", "CLK", "PWR", "RST", "DATA", "BITS", "SEL", "OPM", "CNT", "TMR", "GPIO"];

    private static void B36(StringBuilder sb, long v)
    {
        Span<char> t = stackalloc char[14];
        int n = 0;
        do { long d = v % 36; t[n++] = (char)(d < 10 ? '0' + d : 'A' + d - 10); v /= 36; } while (v > 0);
        while (n > 0) sb.Append(t[--n]);
    }

    // An upper-case middle, unique per (tag, i): two or three register-word syllables, the tag, i in base 36.
    private static void Mid(StringBuilder sb, char tag, long i)
    {
        ulong x = Mix((ulong)i * 31 + tag);
        sb.Append(Syl[x % 16]).Append('_').Append(Syl[(x >> 4) % 16]);
        if ((x >> 8) % 2 == 1) sb.Append('_').Append(Syl[(x >> 9) % 16]);
        sb.Append('_').Append(tag);
        B36(sb, i);
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static string Two2(long v) => $"{C[(int)(v % NC)]}{C[(int)(v / NC % NC)]}";

    // Two-sided pattern t: a start and an end piece, the (a, b) pairs distinct.
    private static string TwoPre(long t) => "Y" + Two2(t % NP2);
    private static string TwoSuf(long t) => Two2((t * 7 + t / NP2) % NP2) + "Y";
    private static string PrePre(long t) => "Yq" + Two2(t);
    private static string SufSuf(long t) => Two2(t) + "qY";

    /// <summary>Pattern p's paste macro: <c>pre##x##suf</c>, <c>pre##x</c> or <c>x##suf</c>.</summary>
    public string PasterBody(long p) =>
        p < Two ? $"{TwoPre(p)}##x##{TwoSuf(p)}" : p < Two + Pre ? $"{PrePre(p - Two)}##x" : $"x##{SufSuf(p - Two - Pre)}";

    /// <summary>The pattern of wide paster <paramref name="p"/> as pre*suf.</summary>
    public string PatternText(long p) =>
        p < Two ? $"{TwoPre(p)}*{TwoSuf(p)}" : p < Two + Pre ? $"{PrePre(p - Two)}*" : $"*{SufSuf(p - Two - Pre)}";

    /// <summary>Matched name i, homed to pattern i % Patterns.</summary>
    public string MatchedName(long i)
    {
        long p = i % Patterns;
        var sb = new StringBuilder(48);
        if (p < Two) { sb.Append(TwoPre(p)); Mid(sb, 'M', i); sb.Append(TwoSuf(p)); }
        else if (p < Two + Pre) { sb.Append(PrePre(p - Two)); Mid(sb, 'M', i); sb.Append("_Y"); }
        else { sb.Append("Y_"); Mid(sb, 'M', i); sb.Append(SufSuf(p - Two - Pre)); }
        return sb.ToString();
    }

    public static string NamedName(long j)
    {
        var sb = new StringBuilder(40).Append("Y_");
        Mid(sb, 'N', j);
        return sb.Append("_Y").ToString();
    }

    /// <summary>Near miss i of two-sided pattern i % Two: right start (0), right end (1), or, one in sixteen, the end
    /// inside the name.</summary>
    public string NearName(long i)
    {
        long t = i % Two;
        string pre = TwoPre(t), suf = TwoSuf(t);
        var sb = new StringBuilder(48);
        if (i % 16 == 15) { sb.Append(pre); Mid(sb, 'X', i); return sb.Append(suf).Append('Z').ToString(); }
        if (i % 2 == 0) { sb.Append(pre); Mid(sb, 'X', i); return sb.Append(suf, 0, 2).Append('Z').ToString(); }
        sb.Append('Z').Append(pre, 1, 2);
        Mid(sb, 'X', i);
        return sb.Append(suf).ToString();
    }

    public static string WidePaster(long p) => $"KWP{p}";
    public static string Root(long p) => $"KWT{p}";


    // ---------------- text ----------------

    private static long Start(long n, long h, long parts) => (long)((Int128)n * h / parts);

    // A register field: base + offset, shifted and masked, then xored with a constant (~100 chars).
    private static void Reg(StringBuilder sb, long i, char tag)
    {
        ulong x = Mix((ulong)i * 131 + tag);
        sb.Append("(((*(volatile unsigned *)(0x").Append((0x40000000u | ((uint)x & 0x0FFFF000u)).ToString("X8"))
          .Append("u + 0x").Append(((uint)(x >> 32) & 0xFFCu).ToString("X4")).Append("u)) >> ").Append((int)(x >> 44) % 32)
          .Append(") & 0x").Append(((uint)(x >> 16)).ToString("X8")).Append("u ^ 0x").Append(((uint)(x >> 24)).ToString("X8")).Append("u)");
    }


    // A register-map comment closing a value line: reset value and access (~30 chars).
    private static readonly string[] Access = ["rw", "ro", "wo", "w1c", "rc"];
    private static string Note(StringBuilder sb, long i, char tag)
    {
        ulong x = Mix((ulong)i * 977 + tag);
        return sb.Append(" /* rst 0x").Append(((uint)x).ToString("x8")).Append(", ").Append(Access[(x >> 32) % 5])
                 .Append(", bit ").Append((int)(x >> 40) % 32).Append(" */").ToString();
    }

    private string UseText(long u)
    {
        string args = $"YU{u % PiecesS}_, _V{u % PiecesE}Y";
        return u % 2 == 1 && UsedForwarders > 0 ? $"Y_WF{u / 2 % UsedForwarders}_Y({args})" : $"Y_WP{u / 2 % UsedPasters}_Y({args})";
    }

    /// <summary>Matched define i: a register field, plus the named-only name and the use it holds, if any.</summary>
    public string MatchedLine(long i)
    {
        var sb = new StringBuilder(176).Append("#define ").Append(MatchedName(i)).Append(" (");
        Reg(sb, i, 'M');
        for (long j = Start(Named, i, Matched), e = Start(Named, i + 1, Matched); j < e; j++) sb.Append(" | ").Append(NamedName(j));
        for (long u = Start(Uses, i, Matched), e = Start(Uses, i + 1, Matched); u < e; u++) sb.Append(" + ").Append(UseText(u));
        return Note(sb.Append(')'), i, 'M');
    }

    public string NamedLine(long j)
    {
        var sb = new StringBuilder(144).Append("#define ").Append(NamedName(j)).Append(' ');
        Reg(sb, j, 'N');
        return Note(sb, j, 'N');
    }

    public string NearLine(long i)
    {
        var sb = new StringBuilder(144).Append("#define ").Append(NearName(i)).Append(' ');
        Reg(sb, i, 'X');
        return Note(sb, i, 'X');
    }

    /// <summary>The macro defines header <paramref name="h"/> of <paramref name="headers"/> holds: its share of the
    /// wide pasters and roots, the body paste macros and forwarders, and in header 0 the edges.</summary>
    public IEnumerable<string> MacroLines(int h, int headers)
    {
        for (long p = h; p < Patterns; p += headers)
        {
            yield return $"#define {WidePaster(p)}(x) {PasterBody(p)}";
            yield return $"#define {Root(p)} ({WidePaster(p)} + 0x{p:X}u)";
        }
        for (long j = h; j < UsedPasters; j += headers) yield return $"#define Y_WP{j}_Y(p, r) p##r";
        for (long j = h; j < UnusedPasters; j += headers) yield return $"#define ZWQP{j}Z(p, r) p##r";
        for (long f = h; f < UsedForwarders; f += headers) yield return $"#define Y_WF{f}_Y(p, r) Y_WP{f % UsedPasters}_Y(p, r)";
        for (long f = h; f < UnusedForwarders; f += headers) yield return $"#define ZWQF{f}Z(p, r) ZWQP{f % UnusedPasters}Z(p, r)";
        if (h != 0) yield break;
        for (int e = 0; e < Edges; e++)
        {
            // a forwarder ending in a paster's name, called with an argument
            yield return $"#define KWEP{e}(x) KWEA{e}##x##_Q";
            yield return $"#define KWEF{e} KWEP{e}";
            yield return $"#define KWEA{e}KWER{e}_Q {e + 1}";
            // a body calling a paster, a space before the (
            yield return $"#define KWEQ{e}(x) KWEB{e}##x##_Q";
            yield return $"#define KWEC{e} (KWEQ{e} (KWES{e}) + 1)";
            yield return $"#define KWEB{e}KWES{e}_Q {e + 2}";
        }
    }

    /// <summary>The value lines of header <paramref name="h"/>: its matched, named-only and near-miss defines,
    /// interleaved.</summary>
    public IEnumerable<string> ValueLines(int h, int headers)
    {
        long Share(long n) => n <= h ? 0 : (n - h + headers - 1) / headers;
        long nm = Share(Matched), nn = Share(Named), nr = Share(Near);
        long ni = 0, ri = 0;
        for (long q = 0; q < nm; q++)
        {
            yield return MatchedLine(q * headers + h);
            for (long until = Start(nn, q + 1, nm); ni < until; ni++) yield return NamedLine(ni * headers + h);
            for (long until = Start(nr, q + 1, nm); ri < until; ri++) yield return NearLine(ri * headers + h);
        }
    }

    /// <summary>Unit text: the roots unit <paramref name="u"/> names in <c>#if 0</c>, and in unit 0 the edges' calls.</summary>
    public void UnitText(int u, int units, StringBuilder scope, StringBuilder fn)
    {
        int n = 0;
        for (long p = u; p < Patterns; p += units)
        {
            if (n == 0) scope.Append($"#if 0\nunsigned kwt{u}_{p} = ");
            else scope.Append("\n\t+ ");
            scope.Append(Root(p));
            if (++n == 16) { scope.Append(";\n#endif\n"); n = 0; }
        }
        if (n > 0) scope.Append(";\n#endif\n");
        if (u != 0) return;
        for (int e = 0; e < Edges; e++) fn.Append($"int fwe{e}(void)\n{{\n\treturn KWEF{e}(KWER{e}) + KWEC{e};\n}}\n");
    }

    /// <summary>cases.tsv rows (the index gives the header: index % headers): a few of each wide kind.</summary>
    public IEnumerable<(string Name, string Case, long Index)> Cases()
    {
        foreach (long p in new[] { 0, Two, Two + Pre })
        {
            if (p >= Patterns || p < 0) continue;
            string side = p < Two ? "two-sided" : p < Two + Pre ? "prefix-only" : "suffix-only";
            yield return (WidePaster(p), $"work-wide-paster ({side} {PatternText(p)})", p);
            yield return (Root(p), "work-wide-root (names its paster, kept)", p);
            if (p < Matched) yield return (MatchedName(p), $"work-wide-matched ({side}, open)", p);
        }
        for (long j = 0; j < Math.Min(2, Named); j++) yield return (NamedName(j), "work-wide-named-only (open)", j);
        foreach (long i in new long[] { 0, 1, 15 })
            if (i < Near)
                yield return (NearName(i), i == 15 ? "work-near-miss (end inside, dropped)" : i == 0 ? "work-near-miss (right start, dropped)"
                    : "work-near-miss (right end, dropped)", i);
        if (UsedPasters > 0) yield return ("Y_WP0_Y", "work-body-paster (used, open)", 0);
        if (UnusedPasters > 0) yield return ("ZWQP0Z", "work-body-paster (unused, dropped)", 0);
        if (UsedForwarders > 0) yield return ("Y_WF0_Y", "work-body-forwarder (used, open)", 0);
        if (UnusedForwarders > 0) yield return ("ZWQF0Z", "work-body-forwarder (unused, dropped)", 0);
        yield return ("KWEP0", "work-edge (forwarder ending in a paster, called with args, kept)", 0);
        yield return ("KWEA0KWER0_Q", "work-edge (built through that forwarder, kept)", 0);
        yield return ("KWEQ0", "work-edge (body calls a paster with a space before (, kept)", 0);
        yield return ("KWEB0KWES0_Q", "work-edge (built by that call, kept)", 0);
    }

    /// <summary>What a wide-population name is, by its spelling, or null.</summary>
    public static string? Classify(string n)
    {
        if (n.StartsWith("KWP", StringComparison.Ordinal) && Digits(n, 3)) return "widePaster";
        if (n.StartsWith("KWT", StringComparison.Ordinal) && Digits(n, 3)) return "root";
        if (n.StartsWith("KWE", StringComparison.Ordinal)) return "edge";
        if (n.StartsWith("Y_WP", StringComparison.Ordinal) && n.EndsWith("_Y", StringComparison.Ordinal)) return "usedPaster";
        if (n.StartsWith("Y_WF", StringComparison.Ordinal) && n.EndsWith("_Y", StringComparison.Ordinal)) return "usedForwarder";
        if (n.StartsWith("ZWQP", StringComparison.Ordinal) && n.EndsWith('Z')) return "unusedPaster";
        if (n.StartsWith("ZWQF", StringComparison.Ordinal) && n.EndsWith('Z')) return "unusedForwarder";
        int m = n.LastIndexOf("_M", StringComparison.Ordinal), x = n.LastIndexOf("_X", StringComparison.Ordinal), k = n.LastIndexOf("_N", StringComparison.Ordinal);
        if (n.StartsWith("Y_", StringComparison.Ordinal) && n.EndsWith("_Y", StringComparison.Ordinal) && k > 0 && m < k && x < k) return "named";
        if (m > 0 && m > x && m > k && n[0] == 'Y' && n[^1] == 'Y') return "matched";
        if (x > 0 && x > m && x > k && (n[0] == 'Y' && n[^1] == 'Z' || n[0] == 'Z' && n[^1] == 'Y')) return "near";
        return null;
    }

    private static bool Digits(string n, int at)
    {
        if (at >= n.Length) return false;
        for (int i = at; i < n.Length; i++) if (!char.IsAsciiDigit(n[i])) return false;
        return true;
    }
}
