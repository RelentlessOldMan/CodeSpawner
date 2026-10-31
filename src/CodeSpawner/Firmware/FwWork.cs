using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Firmware;

/// <summary>
/// The bulk header carve (<c>--fw-bulk</c>, <c>--preset firmware-bulk</c>; docs/firmware-design.md "Bulk
/// shape"): big headers <c>fw/work/wreg_N.h</c> of register defines whose names line up across paste uses, so
/// tree-wide start and end pools keep many names that per-use pairing (rule 3) drops. The shape follows from the knobs alone, so the
/// construction counts in the manifest are exact, and verify recounts the kept and dropped ones from the truth lists.
/// <list type="bullet">
/// <item>An <b>exact</b> name is built by one use, <c>ZRA{id}W_R{id}</c> (A) or <c>KRB{id}W_Q{id}Z</c> (B), and is
/// kept. The uses form trees: the units call level 0, and each level's defines hold the next level's uses in their
/// bodies, so the pieces appear pass by pass.</item>
/// <item>A <b>cross decoy</b> <c>ZRA{a}W_Q{b}Z</c> has an A use's start piece and a B use's end piece. Global pools
/// keep it; per-use pairing (rule 3) drops it.</item>
/// <item>A <b>half decoy</b> has one side only, <c>ZRA{a}W_T{n}Z</c> or <c>ZQH{n}W_Q{b}Z</c>, and is dropped either
/// way.</item>
/// <item><b>Filler</b> <c>ZWF{h}N{i}Z</c> matches no piece.</item>
/// </list>
/// Uses take a paster <c>WRP{j}</c> directly, through a forwarder <c>WFW{f}</c> (a chain down to a paster), or
/// higher-order through an applier <c>WAP{k}(m, p, r) m(p, r)</c>. Each level has its own pasters, forwarders and
/// applier, since a use's children expand inside its expansion. Every one of them is used, so all are kept.
/// </summary>
internal sealed class FwWork
{
    public const string Dir = "work";
    private const long HeaderBytes = 30L << 20;   // ~31 MB (production-sized register headers)
    /// <summary>Every bulk-carve header is padded past this, so each is big.</summary>
    public const long MinHeaderBytes = FwSpec.MaxParseBytes + 1_000_000;

    public long Defines, Pasters, Forwarders;
    /// <summary>Appliers WAP{k} written: one for each level with a higher-order use (a level under 18 uses has none).</summary>
    public int Appliers;
    public int Growth, Depth, Headers;
    /// <summary>Longest forwarder chain (--fw-chain-max).</summary>
    public int ChainMax;
    public long Exact, CrossDecoys, HalfDecoys, Filler;
    /// <summary>The wide population.</summary>
    public FwWide Wide = null!;
    /// <summary>.c files outside the compile db, and assembly files.</summary>
    public int Uncovered, Asm;
    public long UsesDirect, UsesForwarded, UsesHigherOrder;
    /// <summary>Filler written past the plan to bring a header over maxParseBytes.</summary>
    public long ExtraFiller;
    /// <summary>Filler defines as written (the plan's plus the padding).</summary>
    public long FillerWritten => Filler + ExtraFiller;
    /// <summary>Uses per level (each use is one exact name, one start piece and one end piece).</summary>
    public long[] Level = [];
    /// <summary>Cross decoys whose two pieces are first both present after level k's uses.</summary>
    public long[] CrossByLevel = [];

    private long[][] _c = [];          // per header: nodes per level
    private long[] _base = [];         // per (h, k): its first node's index in level k
    private long[] _cumA = [], _cumB = [];   // per (h, k), flattened: A (B) nodes before it
    private long _na, _nb;

    public static FwWork? Plan(GenOptions o)
    {
        if (!o.FwWork) return null;
        var w = new FwWork
        {
            Defines = o.Eff("fw-bulk-defines", o.FwWorkDefines),
            Pasters = o.Eff("fw-bulk-pasters", o.FwWorkPasters, keepOne: true),
            Forwarders = o.Eff("fw-bulk-forwarders", o.FwWorkForwarders),
            Growth = o.FwWorkGrowth,
            Depth = o.FwWorkDepth,
            ChainMax = o.FwChainMax,
        };
        long exact = Math.Max(1, (long)(w.Defines * o.FwWorkKeepPct / 100));
        w.CrossDecoys = (long)(w.Defines * o.FwWorkDecoyPct / 100);
        w.HalfDecoys = (long)(w.Defines * o.FwWorkHalfPct / 100);
        w.Wide = FwWide.Plan(o, w.Defines - exact - w.CrossDecoys - w.HalfDecoys, w.Defines);
        w.Uncovered = o.Eff("fw-bulk-uncovered", o.FwWorkUncovered);
        w.Asm = Math.Max(2, o.Eff("fw-bulk-asm", o.FwWorkAsm));
        var wd = w.Wide;

        // ~80 bytes an exact define (its body holds its children's uses), ~40 a decoy, ~100 filler, ~150 a matched
        // define, ~140 a named-only or near-miss one, ~45 a macro
        long rest = Math.Max(0, w.Defines - exact - w.CrossDecoys - w.HalfDecoys - wd.Defines);
        long bytes = exact * 80 + (w.CrossDecoys + w.HalfDecoys) * 40 + rest * 100 + wd.Matched * 150 + (wd.Named + wd.Near) * 140
                     + (wd.Defines - wd.Matched - wd.Named - wd.Near) * 45;
        w.Headers = (int)Math.Clamp((bytes + HeaderBytes / 2) / HeaderBytes, 1, 10_000);

        // Level 1 is (growth - 1) x level 0, so the pieces grow x growth from pass 1 to pass 2; each later level
        // halves. A level smaller than the header count ends the tree.
        double s = 1, n = w.Growth - 1;
        for (int k = 1; k < w.Depth; k++, n /= 2) s += n;
        long n0 = Math.Max(w.Headers, (long)(exact / s));
        var levels = new List<long> { n0 };
        n = (double)n0 * (w.Growth - 1);
        for (int k = 1; k < w.Depth && (long)n >= w.Headers; k++, n /= 2) levels.Add((long)n);
        w.Level = levels.ToArray();
        w.Exact = w.Level.Sum();

        int K = w.Level.Length;
        w._c = new long[w.Headers][];
        w._base = new long[w.Headers * K];
        w._cumA = new long[w.Headers * K + 1];
        w._cumB = new long[w.Headers * K + 1];
        for (int h = 0; h < w.Headers; h++)
        {
            w._c[h] = new long[K];
            for (int k = 0; k < K; k++)
            {
                long c = Start(w.Level[k], h + 1, w.Headers) - Start(w.Level[k], h, w.Headers);
                int x = h * K + k;
                w._c[h][k] = c;
                w._base[x] = Start(w.Level[k], h, w.Headers);
                w._cumA[x + 1] = w._cumA[x] + (c + 1) / 2;
                w._cumB[x + 1] = w._cumB[x] + c / 2;
            }
        }
        w._na = w._cumA[^1];
        w._nb = w._cumB[^1];
        w.CrossDecoys = Math.Min(w.CrossDecoys, w._na * w._nb);
        if (w._na == 0 || w._nb == 0) w.HalfDecoys = 0;
        w.Filler = Math.Max(0, w.Defines - w.Exact - w.CrossDecoys - w.HalfDecoys - wd.Defines);

        // Each level has its own pasters, forwarders and applier: a use expands its children inside its own
        // expansion, where a macro it is in cannot expand again. A pool is cut to the uses that take it, so every
        // paster and forwarder is used.
        w._pBase = new long[K]; w._pN = new long[K]; w._fBase = new long[K]; w._fN = new long[K]; w._ap = new bool[K];
        long pTotal = w.Pasters, fTotal = w.Forwarders;
        w.Pasters = w.Forwarders = 0;
        for (int k = 0; k < K; k++)
        {
            long[] m = new long[3];
            for (long i = 0; i < w.Level[k]; i++) m[RawMode(i)]++;
            long f = Math.Min(Start(fTotal, k + 1, K) - Start(fTotal, k, K), m[1]);
            if (f == 0) { m[0] += m[1]; m[1] = 0; }
            long p = Math.Max(1, Math.Min(Start(pTotal, k + 1, K) - Start(pTotal, k, K), m[0] + m[2]));
            w._pBase[k] = w.Pasters; w._pN[k] = p; w.Pasters += p;
            w._fBase[k] = w.Forwarders; w._fN[k] = f; w.Forwarders += f;
            w.UsesDirect += m[0]; w.UsesForwarded += m[1]; w.UsesHigherOrder += m[2];
            w._ap[k] = m[2] > 0;
            if (w._ap[k]) w.Appliers++;
        }

        w.CrossByLevel = new long[K];
        for (long i = 0; i < w.CrossDecoys; i++)
        {
            var (ai, bi) = w.CrossPair(i);
            w.CrossByLevel[Math.Max(w.Nth(ai, a: true).k, w.Nth(bi, a: false).k)]++;
        }
        return w;
    }

    private static long Start(long n, long h, long parts) => (long)((Int128)n * h / parts);

    // even local index: A; odd: B
    private static bool IsA(long l) => l % 2 == 0;
    private static string Id(int h, int k, long l) => $"{h}H{k}L{l}";
    private static string StartPiece(int h, int k, long l) => IsA(l) ? $"ZRA{Id(h, k, l)}W" : $"KRB{Id(h, k, l)}W";
    private static string EndPiece(int h, int k, long l) => IsA(l) ? $"_R{Id(h, k, l)}" : $"_Q{Id(h, k, l)}Z";
    public static string ExactName(int h, int k, long l) => StartPiece(h, k, l) + EndPiece(h, k, l);

    private long[] _pBase = [], _pN = [], _fBase = [], _fN = [];   // per level: its pasters and forwarders
    private bool[] _ap = [];   // per level: it has an applier (a higher-order use)

    // 0 direct, 1 through a forwarder, 2 higher-order; by index in the level: 10 / 7 / 3 of every 20
    private static int RawMode(long g) => g % 20 < 10 ? 0 : g % 20 < 17 ? 1 : 2;
    private int Mode(int k, long g) => RawMode(g) is var m && m == 1 && _fN[k] == 0 ? 0 : m;

    // index among the level's uses that take a paster (modes 0 and 2), or a forwarder (mode 1)
    private long PasterIndex(int k, long g) => _fN[k] > 0 ? g / 20 * 13 + (g % 20 < 10 ? g % 20 : g % 20 - 7) : g;
    private static long ForwarderIndex(long g) => g / 20 * 7 + g % 20 - 10;

    private string UseText(int h, int k, long l)
    {
        long g = _base[h * Level.Length + k] + l;
        string a = StartPiece(h, k, l), b = EndPiece(h, k, l);
        return Mode(k, g) switch
        {
            0 => $"WRP{_pBase[k] + PasterIndex(k, g) % _pN[k]}({a}, {b})",
            1 => $"WFW{_fBase[k] + ForwarderIndex(g) % _fN[k]}({a}, {b})",
            _ => $"WAP{k}(WRP{_pBase[k] + PasterIndex(k, g) % _pN[k]}, {a}, {b})",
        };
    }

    // The i-th A (or B) node over the whole tree, header-major then level.
    private (int h, int k, long l) Nth(long i, bool a)
    {
        var cum = a ? _cumA : _cumB;
        int lo = 0, hi = cum.Length - 2;   // last x with cum[x] <= i
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (cum[mid] <= i) lo = mid; else hi = mid - 1;
        }
        long j = i - cum[lo];
        return (lo / Level.Length, lo % Level.Length, a ? 2 * j : 2 * j + 1);
    }

    // Distinct (A, B) pairs for i < na * nb.
    private (long a, long b) CrossPair(long i) => (i % _na, (i / _na + i % _na) % _nb);

    public string CrossDecoy(long i)
    {
        var (ai, bi) = CrossPair(i);
        var (ha, ka, la) = Nth(ai, a: true);
        var (hb, kb, lb) = Nth(bi, a: false);
        return StartPiece(ha, ka, la) + EndPiece(hb, kb, lb);
    }

    public string HalfDecoy(long i)
    {
        if (i % 2 == 0) { var (h, k, l) = Nth(i / 2 % _na, a: true); return $"{StartPiece(h, k, l)}_T{i}Z"; }
        var (hb, kb, lb) = Nth(i / 2 % _nb, a: false);
        return $"ZQH{i}W{EndPiece(hb, kb, lb)}";
    }

    /// <summary>What a bulk-carve name is, by its spelling: exact, cross, half, filler, or null (not a bulk-carve name).</summary>
    public static string? Classify(string name)
    {
        if (name.StartsWith("KRB", StringComparison.Ordinal)) return name.Contains("W_Q", StringComparison.Ordinal) ? "exact" : null;
        if (name.StartsWith("ZQH", StringComparison.Ordinal)) return name.Contains("W_Q", StringComparison.Ordinal) ? "half" : null;
        if (name.StartsWith("ZWF", StringComparison.Ordinal)) return "filler";
        if (!name.StartsWith("ZRA", StringComparison.Ordinal)) return null;
        int w = name.IndexOf("W_", StringComparison.Ordinal);
        if (w < 0 || w + 2 >= name.Length) return null;
        return name[w + 2] switch { 'R' => "exact", 'Q' => "cross", 'T' => "half", _ => null };
    }

    public static string HeaderFile(int h) => $"wreg_{h}.h";

    /// <summary>The bulk-carve headers unit <paramref name="u"/> calls into: with at least as many units as headers,
    /// header <c>u % Headers</c>; otherwise every header whose number is <paramref name="u"/> mod the unit count.</summary>
    public IEnumerable<int> HeadersOfUnit(int u, int units)
    {
        if (units >= Headers) { yield return u % Headers; yield break; }
        for (int h = u; h < Headers; h += units) yield return h;
    }

    /// <summary>Appends the functions holding unit <paramref name="u"/>'s level-0 uses: of each header it serves,
    /// the level-0 nodes whose index falls to it.</summary>
    public void UnitText(int u, int units, StringBuilder scope, StringBuilder fn)
    {
        Wide.UnitText(u, units, scope, fn);
        int fi = 0;
        foreach (int h in HeadersOfUnit(u, units))
        {
            long share = units >= Headers ? (units - h + Headers - 1) / Headers : 1;
            long mine = units >= Headers ? u / Headers : 0;
            int n = 0;
            for (long l = mine; l < _c[h][0]; l += share)
            {
                if (n == 0) fn.Append($"int fwk{u}_{fi++}(void)\n{{\n\treturn ");
                else fn.Append("\n\t\t+ ");
                fn.Append(UseText(h, 0, l));
                if (++n == 16) { fn.Append(";\n}\n"); n = 0; }
            }
            if (n > 0) fn.Append(";\n}\n");
        }
    }

    /// <summary>Writes header <paramref name="h"/>'s text through <paramref name="line"/>: header 0 also holds the
    /// pasters and forwarders (the others include it). Exact defines, decoys and filler are interleaved, and filler
    /// pads the header past <see cref="MinHeaderBytes"/>.</summary>
    public void Write(int h, Action<string> line, Action<long> fillerOne, Func<long> bytes)
    {
        string guard = $"FW_WREG_{h}_H";
        line($"/* wreg_{h}.h: generated register map (work shape) */");
        line($"#ifndef {guard}");
        line($"#define {guard}");
        if (h == 0)
        {
            for (long j = 0; j < Pasters; j++) line($"#define WRP{j}(p, r) p##r");
            // a level's forwarder f goes to its paster f, or past the pool to its forwarder f - pool; a chain is cut
            // at --fw-chain-max links (forwarder f, f / pool a multiple of it, goes to paster f % pool), so a big
            // forwarder pool over a small paster pool cannot nest past what a preprocessor (or the truth) recurses
            for (int k = 0; k < Level.Length; k++)
                for (long f = 0; f < _fN[k]; f++)
                    line(f / _pN[k] % ChainMax == 0 ? $"#define WFW{_fBase[k] + f}(p, r) WRP{_pBase[k] + f % _pN[k]}(p, r)"
                        : $"#define WFW{_fBase[k] + f}(p, r) WFW{_fBase[k] + f - _pN[k]}(p, r)");
            // an applier no use takes would be an unused macro the truth drops, against its K spelling
            for (int k = 0; k < Level.Length; k++) if (_ap[k]) line($"#define WAP{k}(m, p, r) m(p, r)");
        }
        else line("#include \"wreg_0.h\"");
        foreach (var s in Wide.MacroLines(h, Headers)) line(s);

        int K = Level.Length;
        long nodes = 0;
        foreach (var c in _c[h]) nodes += c;
        long cross = Share(CrossDecoys, h), half = Share(HalfDecoys, h), fill = Share(Filler, h);
        long ci = 0, hi = 0, fl = 0, q = 0;
        var sb = new StringBuilder();
        for (int k = 0; k < K; k++)
            for (long l = 0; l < _c[h][k]; l++, q++)
            {
                sb.Clear().Append("#define ").Append(ExactName(h, k, l)).Append(' ');
                long from = 0, to = 0;
                if (k + 1 < K)
                {
                    long c = _c[h][k], cn = _c[h][k + 1];
                    from = Start(cn, l, c);
                    to = Start(cn, l + 1, c);
                }
                if (to > from)
                {
                    sb.Append('(');
                    for (long x = from; x < to; x++) sb.Append(UseText(h, k + 1, x)).Append(" + ");
                    sb.Append("1)");
                }
                else sb.Append('1');
                line(sb.ToString());
                for (long until = Start(cross, q + 1, nodes); ci < until; ci++) line($"#define {CrossDecoy(ci * Headers + h)} 1");
                for (long until = Start(half, q + 1, nodes); hi < until; hi++) line($"#define {HalfDecoy(hi * Headers + h)} 1");
                for (long until = Start(fill, q + 1, nodes); fl < until; fl++) fillerOne(fl);
            }
        foreach (var s in Wide.ValueLines(h, Headers)) line(s);
        while (bytes() < MinHeaderBytes) fillerOne(fl++);
        line("#endif");
        Interlocked.Add(ref ExtraFiller, fl - fill);
    }

    /// <summary>cases.tsv rows: per level the first A and B exact names, and a few of each decoy.</summary>
    public IEnumerable<(string Name, string Case, string Site)> Cases()
    {
        string Site(int h) => $"{Dir}/{HeaderFile(h)}";
        for (int k = 0; k < Level.Length; k++)
            for (long l = 0; l < Math.Min(2, _c[0][k]); l++)
                yield return (ExactName(0, k, l), $"work-exact (level {k})", Site(0));
        for (long i = 0; i < Math.Min(3, CrossDecoys); i++) yield return (CrossDecoy(i), "work-cross-decoy", Site((int)(i % Headers)));
        for (long i = 0; i < Math.Min(2, HalfDecoys); i++) yield return (HalfDecoy(i), "work-half-decoy", Site((int)(i % Headers)));
        foreach (var r in Wide.Cases()) yield return (r.Name, r.Case, Site((int)(r.Index % Headers)));
    }

    // items i < n with i % Headers == h
    private long Share(long n, int h) => n <= h ? 0 : (n - h + Headers - 1) / Headers;
}
