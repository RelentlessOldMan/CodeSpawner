using System.Text;
using System.Text.Json;
using CodeSpawner.Cli;
using CodeSpawner.Generation;

namespace CodeSpawner.Firmware;

/// <summary>A hostile file of the firmware tree (corpus-relative path) and what makes it hostile.</summary>
internal sealed record FwHostile(string Path, string Kind);

internal sealed class FirmwareResult
{
    public required FwTruth Truth { get; init; }
    public List<FwHostile> Hostile { get; } = new();
    public long Units { get; set; }
    public long Families { get; set; }
    public long Traps { get; set; }
    public long Cases { get; set; }
    /// <summary>The work-shaped carve, when <c>--fw-work</c> is on.</summary>
    public FwWork? Work { get; set; }
}

/// <summary>
/// The firmware corpus (docs/firmware-design.md): big generated register headers full of paste macros, dense
/// headers, closure-root units and trap files under <c>fw/</c>, a compile db under <c>fw-build/</c>, headers outside
/// the root under <c>ext/</c>. Names carry their class in their spelling (Z…Z must drop, Y…Y open, the rest must
/// keep); the truth is computed afterwards from the written tree by <see cref="FwSpec"/>, and gen fails when a name's
/// class and spelling disagree, so a design slip in here can never ship as truth.
/// </summary>
internal static class FirmwareEmitter
{
    public const string ExtDir = "ext", BuildDir = "fw-build", TruthDir = "fw-truth";
    /// <summary>The corpus-root directories the firmware corpus owns (mutate leaves them alone: the truth names their bytes).</summary>
    public static readonly string[] Dirs = [FwSpec.FwDir, BuildDir, TruthDir, ExtDir];
    public const string CasesPath = TruthDir + "/cases.tsv";

    /// <summary>regs_0.h is one byte past maxParseBytes; table_big.c is exactly maxParseBytes; dense_small.h one byte
    /// under the dense floor.</summary>
    public const long BoundaryBig = FwSpec.MaxParseBytes + 1, BoundaryTable = FwSpec.MaxParseBytes, BoundarySmall = FwLexer.DenseMinBytes - 1;
    private const long BigSrcBytes = 21_000_000, DenseBytes = 1_100_000;
    private const int Kinds = 32, Traps = 8, CasesPerKind = 3, FilesPerDir = 100;

    private static readonly string[] KindNames =
    [
        "direct", "cat-prefix", "cat-suffix", "multi-line-call", "forwarder-chain", "alias", "higher-order",
        "computed-call", "wanted-body", "beside-paste", "object-expansion", "if0-call", "comment-seed", "cat3-split",
        "redefinition", "kept-header-body", "comment-call-open", "string-call-open", "numeric-object", "call-arg",
        "nested-call-edge", "number-suffix", "empty-edge", "call-edge", "digraph", "higher-order-forwarder",
        "use-pairs", "uncalled-wildcard", "expanded-arg", "empty-expansion", "empty-arg", "higher-order-pairs",
    ];

    // Kinds whose big-header text is define lines only, so a dense header can host them.
    private static bool PureDefines(int kind) => kind is 0 or 1 or 2 or 5 or 7 or 8 or 10 or 13 or >= 18;

    private sealed class Plan
    {
        public required GenOptions O;
        public required string Root;
        public int Big, Files, Units, Dx, ChainMax;
        public long Families, Pasters;
        public long BigBytes;
        public FwWork? Work;
        public readonly List<int> UnitFile = new();   // unit -> file index
        public string Fw(string rel) => Path.Combine(Root, FwSpec.FwDir, rel);

        public int Kind(long f) => (int)(f % Kinds);
        public int UnitOf(long f) => (int)(f % Units);
        public int BigOf(int u) => u % Big;
        public int DxOf(int u) => Dx > 0 ? u % Dx : -1;
        public bool DenseHosted(long f) => Dx > 0 && PureDefines(Kind(f)) && f / Kinds % 4 == 1;
        public bool LongChain(long f) => Kind(f) == 4 && f / Kinds % 50 == 0;
        public int ChainLen(long f) => LongChain(f) ? ChainMax : 2 + (int)(f / Kinds % 4);
        public bool SplitChain(long f) => LongChain(f) && Dx > 0;
        public int DirOf(int file) => file / FilesPerDir;
        public bool IsHeaderFile(int file) => file % 4 == 3;
        public int HeaderOfDir(int d) => d * FilesPerDir + 3 < Files ? d * FilesPerDir + 3 : -1;
        public bool KeptHeaderBody(long f) => Kind(f) == 15 && HeaderOfDir(DirOf(UnitFile[UnitOf(f)])) >= 0;
        public string UnitRel(int u) { int i = UnitFile[u]; return $"src/m{DirOf(i)}/u{i}.c"; }
        public string HdrRel(int i) => $"src/m{DirOf(i)}/k{i}.h";
        // Traps: compiled parts sit in unit t % Units, host defines in that unit's big header.
        public int TrapUnit(int t) => t % Units;
    }

    public static FirmwareResult Emit(GenOptions o, string root, ParallelOptions par)
    {
        var p = new Plan
        {
            O = o,
            Root = root,
            Big = o.Eff("fw-big-headers", o.FwBigHeaders, keepOne: true),
            Files = Math.Max(4, o.Eff("fw-files", o.FwFiles)),
            Dx = o.Eff("fw-dense-headers", o.FwDenseHeaders),
            Families = o.Eff("fw-families", o.FwFamilies),
            Pasters = o.Eff("fw-pasters", o.FwPasters),
            ChainMax = o.FwChainMax,
            BigBytes = (long)o.FwBigMb << 20,
        };
        p.Work = FwWork.Plan(o);
        for (int i = 0; i < p.Files; i++) if (!p.IsHeaderFile(i)) p.UnitFile.Add(i);
        p.Units = p.UnitFile.Count;

        foreach (var d in Dirs)
        {
            string full = Path.Combine(root, d);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        foreach (var d in new[] { "include", "big", "dense", "src/traps", "misc" }) Directory.CreateDirectory(p.Fw(d));
        if (p.Work is not null) Directory.CreateDirectory(p.Fw(FwWork.Dir));
        for (int d = 0; d <= p.DirOf(p.Files - 1); d++) Directory.CreateDirectory(p.Fw($"src/m{d}"));
        Directory.CreateDirectory(Path.Combine(root, ExtDir, "include"));
        Directory.CreateDirectory(Path.Combine(root, ExtDir, "app"));
        Directory.CreateDirectory(Path.Combine(root, BuildDir));
        Directory.CreateDirectory(Path.Combine(root, TruthDir));

        var r = new FirmwareResult { Truth = null! };
        WritePaste(p);
        Parallel.For(0, p.Big, par, h => WriteBig(p, h));
        if (p.Work is { } wk) Parallel.For(0, wk.Headers, par, h => WriteWork(p, wk, h));
        for (int x = 0; x < p.Dx; x++) WriteDx(p, x);
        WriteDenseFixtures(p);
        Parallel.For(0, p.Files, par, i =>
        {
            if (p.IsHeaderFile(i)) WriteKeptHeader(p, i);
        });
        Parallel.For(0, p.Units, par, u => WriteUnit(p, u));
        for (int t = 0; t < Traps; t++) WriteTrap(p, t);
        WriteBigSrc(p);
        WriteTable(p);
        WriteExt(p);
        WriteMisc(p, r.Hostile);
        WriteDb(p);
        r.Cases = WriteCases(p);
        r.Units = p.Units;
        r.Families = p.Families;
        r.Traps = Traps;

        var truth = FwSpec.Compute(root, Path.Combine(root, TruthDir), checkAlphabet: true);
        if (truth.AlphabetErrors.Count > 0)
            throw new InvalidOperationException("firmware: names whose class disagrees with their spelling (a generator bug):\n  "
                + string.Join("\n  ", truth.AlphabetErrors));
        return new FirmwareResult { Truth = truth, Units = r.Units, Families = r.Families, Traps = r.Traps, Cases = r.Cases, Work = p.Work }
            .WithHostile(r.Hostile);
    }

    private static FirmwareResult WithHostile(this FirmwareResult r, List<FwHostile> h)
    {
        r.Hostile.AddRange(h);
        return r;
    }

    // ---------------- writer ----------------

    private sealed class FwWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly byte[] _buf = new byte[1 << 16];
        private int _n;
        private readonly bool _crlf;
        public long Bytes { get; private set; }
        public int NlBytes => _crlf ? 2 : 1;

        public FwWriter(string path, bool crlf = false)
        {
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
            _crlf = crlf;
        }

        public void Raw(string s)
        {
            if (_n + s.Length > _buf.Length) Flush();
            if (s.Length > _buf.Length) { var b = Encoding.ASCII.GetBytes(s); _fs.Write(b); Bytes += b.Length; return; }
            for (int i = 0; i < s.Length; i++) _buf[_n++] = (byte)s[i];
            Bytes += s.Length;
        }

        public void Bin(ReadOnlySpan<byte> b)
        {
            Flush();
            _fs.Write(b);
            Bytes += b.Length;
        }

        public void Nl() => Raw(_crlf ? "\r\n" : "\n");

        public void L(string s) { Raw(s); Nl(); }

        /// <summary>Writes text whose lines end in LF, converting them to this file's line ending.</summary>
        public void Text(string s)
        {
            int st = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\n') continue;
                Raw(s[st..i]);
                Nl();
                st = i + 1;
            }
            if (st < s.Length) Raw(s[st..]);
        }

        /// <summary>Pads the file to exactly <paramref name="target"/> bytes with one comment line of no identifiers;
        /// false if it is already too long for one.</summary>
        public bool PadTo(long target)
        {
            long r = target - Bytes - NlBytes - 4;
            if (r < 0) return false;
            Raw("/*" + new string('-', (int)r) + "*/");
            Nl();
            return true;
        }

        private void Flush() { _fs.Write(_buf, 0, _n); _n = 0; }

        public void Dispose() { Flush(); _fs.Dispose(); }
    }

    // A Z filler define: a name no rule reaches and a body of constants.
    private static void Filler(FwWriter w, string stem, long i, ref Rng rng)
    {
        var sb = new StringBuilder(128);
        sb.Append("#define ").Append(stem).Append('N').Append(i).Append("Z (");
        for (int k = 0; k < 6; k++)
        {
            if (k > 0) sb.Append(" ^ ");
            sb.Append("0x").Append(((uint)rng.NextULong()).ToString("X8")).Append('u');
        }
        sb.Append(')');
        w.L(sb.ToString());
    }

    private const int FillerMax = 120;

    // A Z paste macro; the five forms cover direct pastes, beside-## pieces, forwarders and an alias.
    private static string Paster(long i) => (i % 5) switch
    {
        0 => $"#define ZPA{i}Z(a, b) a##b",
        1 => $"#define ZPB{i}Z(a) ZPQ{i}Q_##a",
        2 => $"#define ZPC{i}Z(a, b) CAT(a, b)",
        3 => $"#define ZPD{i}Z CAT",
        _ => $"#define ZPE{i}Z(a, b) ZPA{i - 4}Z(b, a)",
    };

    // ---------------- fw/include ----------------

    private static void WritePaste(Plan p)
    {
        using var w = new FwWriter(p.Fw("include/fwpaste.h"));
        w.Text("""
            /* fwpaste.h: the paste macros the firmware tree is built on */
            #ifndef FW_PASTE_H
            #define FW_PASTE_H
            #define CAT_(a, b) a##b
            #define CAT(a, b) CAT_(a, b)
            #define CAT3_(a, b, c) a##b##c
            #define CAT3(a, b, c) CAT3_(a, b, c)
            #define PCAT(a, b) a##b
            #define KAPPLY(m, a, b) m(a, b)
            #define KHWP(blk, n) KHWB_##blk##_r##n##_VAL
            #define FW_ON 1

            """);
        foreach (int n in new[] { 64, 65 })
        {
            var sb = new StringBuilder($"#define KJOIN{n}(");
            for (int i = 0; i < n; i++) sb.Append(i > 0 ? ", " : "").Append('a').Append(i);
            sb.Append(") (");
            for (int i = 0; i < n; i++) sb.Append(i > 0 ? " + " : "").Append('a').Append(i).Append("##1");
            sb.Append(')');
            w.L(sb.ToString());
        }
        w.L("#endif");
    }

    // ---------------- family text ----------------

    private static int Val(long f) => (int)(f % 97) + 1;

    /// <summary>The defines family <paramref name="f"/> puts in a carved header: in a dense header
    /// (<paramref name="dense"/>) or in its unit's big header.</summary>
    private static void HostText(Plan p, long f, bool dense, StringBuilder sb)
    {
        bool hosted = p.DenseHosted(f);
        if (p.SplitChain(f))
        {
            ChainText(p, f, dense ? 1 : 0, sb);
            return;
        }
        if (hosted != dense) return;
        int v = Val(f);
        switch (p.Kind(f))
        {
            case 0: sb.Append($"#define KD{f}X_VAL {v}\n"); break;
            case 1: sb.Append($"#define ZA{f}X_7 {v}\n#define ZA{f}X_REGZ 1\n"); break;
            case 2: sb.Append($"#define KSB_S{f}XZ {v}\n#define ZSC_S{f}XZ 1\n"); break;
            case 3: sb.Append($"#define KML{f}X_7 {v}\n"); break;
            case 4: ChainText(p, f, -1, sb); break;
            case 5: sb.Append($"#define KAL{f}X CAT\n#define KAN{f}X_5 {v}\n"); break;
            case 6: sb.Append($"#define KHO{f}X_5 {v}\n"); break;
            case 7: sb.Append($"#define KCC{f}X_7 PCAT\n#define KU{f}X_2 {v}\n"); break;
            case 8: sb.Append($"#define KWB{f}X CAT(KWP{f}X_, 5)\n#define KWP{f}X_5 {v}\n"); break;
            case 9: sb.Append($"#define KBX{f}Q_5 {v}\n"); break;
            case 10: sb.Append($"#define KSFA{f}X KSFB{f}X\n#define KSFB{f}X KSFC{f}X_\n#define KSFC{f}X_5 {v}\n"); break;
            case 11: sb.Append($"#if 0\nint kb0_{f} = CAT(KB0{f}X_, 5);\n#endif\n#define KB0{f}X_5 {v}\n"); break;
            case 12: sb.Append($"/* see KBC{f}X_5 */\n#define KBC{f}X_5 {v}\n"); break;
            case 13: sb.Append($"#define KGT{f}X_35 {v}\n"); break;
            case 14:
                {
                    string a = $"#define KRD{f}X CAT(KRPA{f}X_, 5)\n", b = $"#define KRD{f}X CAT(KRPB{f}X_, 5)\n";
                    bool swap = f / Kinds % 2 == 1;
                    sb.Append("#ifdef FW_ALT\n").Append(swap ? b : a).Append("#else\n").Append(swap ? a : b).Append("#endif\n");
                    sb.Append($"#define KRPA{f}X_5 {v}\n#define KRPB{f}X_5 {v}\n");
                    break;
                }
            case 15:
                if (p.KeptHeaderBody(f)) sb.Append($"#define KHP{f}X_5 {v}\n");
                else sb.Append($"#define KWB{f}X CAT(KWP{f}X_, 5)\n#define KWP{f}X_5 {v}\n");
                break;
            case 16: sb.Append($"#define YC{f}Q_1Y {v}\n"); break;
            case 17: sb.Append($"#define YS{f}Q_1Y {v}\n"); break;
            case 18: sb.Append($"#define KNUM{f}N 2\n#define KNM{f}X_2 {v}\n"); break;
            case 19: sb.Append($"#define KCH{f}H() KCX{f}X\n#define KCX{f}X_1 {v}\n"); break;
            case 20: sb.Append($"#define KNH{f}H() KNJ{f}J()\n#define KNJ{f}J() KNF{f}X\n#define KNF{f}X_2 {v}\n"); break;
            case 21: sb.Append($"#define KUU{f}X_2u {v}\n"); break;
            case 22: sb.Append($"#define KEX{f}X KEA{f}X KEB{f}X KEE{f}X\n#define KEA{f}X 1 +\n#define KEE{f}X\n#define KEB{f}X_3 {v}\n"); break;
            case 23: sb.Append($"#define KCE{f}H(x) x\n#define KCE{f}X KCE{f}H(KCF{f}X)\n#define KCF{f}X_4 {v}\n"); break;
            case 24: sb.Append($"#define KDG{f}Q_5 {v}\n"); break;
            case 25: sb.Append($"#define KHF{f}F(a) KAPPLY(CAT, a, _6)\n#define ZHG{f}X_6 {v}\n#define ZHG{f}X_REGZ 1\n"); break;
            case 26: sb.Append($"#define KUP{f}M(a, b) a##_S{f}_##b\n#define ZUP{f}A_S{f}_B {v}\n#define KUQ{f}C_S{f}_DZ 1\n#define ZUP{f}A_S{f}_DZ 1\n"); break;
            case 27: sb.Append($"#define KWG{f}G(a) ZWH{f}_##a##_OFF\n#define KWG{f}H(p) KWG{f}G(p)\n#define ZWH{f}_Q_OFF {v}\n#define ZWH{f}_Q_OFZ 1\n"); break;
            case 28: sb.Append($"#define KXU{f}N 3\n#define KXF{f}F(a) CAT(KXV{f}_, a)\n#define KXV{f}_3 {v}\n#define KXV{f}_KXU{f}N 1\n"); break;
            case 29: sb.Append($"#define KEN{f}N\n#define KEO{f}X_ {v}\n"); break;
            case 30: sb.Append($"#define KEM{f}X_ {v}\n"); break;
            case 31: sb.Append($"#define ZHQ{f}X_5 {v}\n#define KHR{f}X_QZ 1\n#define ZHQ{f}X_QZ 1\n"); break;
        }
    }

    // A forwarder chain KFW{f}L0X -> ... -> CAT; top-first or bottom-first by family. part: -1 all, 0 even links and
    // the target, 1 odd links (a long chain split across its unit's big and dense header).
    private static void ChainText(Plan p, long f, int part, StringBuilder sb)
    {
        int n = p.ChainLen(f);
        bool reverse = f / Kinds % 2 == 1;
        for (int j = 0; j < n; j++)
        {
            int k = reverse ? n - 1 - j : j;
            if (part >= 0 && k % 2 != part) continue;
            if (k < n - 1) sb.Append($"#define KFW{f}L{k}X(a, b) KFW{f}L{k + 1}X(a, b)\n");
            else sb.Append($"#define KFW{f}L{k}X(a, b) CAT(a, b)\n");
        }
        if (part <= 0) sb.Append($"#define KFC{f}X_5 {Val(f)}\n");
    }

    /// <summary>The unit's use of family <paramref name="f"/>: file-scope text (local defines, comment and string
    /// calls) and a function body expression.</summary>
    private static void UseText(Plan p, long f, StringBuilder scope, StringBuilder fn)
    {
        string? e = p.Kind(f) switch
        {
            0 => $"KD{f}X_VAL",
            1 => $"CAT(ZA{f}X_, 7)",
            2 => $"CAT(KSB_, S{f}XZ)",
            3 => $"CAT(\n\t\tKML{f}X_,\n\t\t7)",
            4 => $"KFW{f}L0X(KFC{f}X_, 5)",
            5 => $"KAL{f}X(KAN{f}X_, 5)",
            6 => $"KAPPLY(CAT, KHO{f}X_, 5)",
            7 => $"CAT(KCC{f}X_, 7)(KU{f}X_, 2)",
            8 => $"KWB{f}X",
            9 => $"KBX{f}G(5)",
            10 => $"CAT(KSFA{f}X, 5)",
            13 => $"CAT3(KGT{f}X_,\n\t\t3,\n\t\t5)",
            14 => $"KRD{f}X",
            15 => p.KeptHeaderBody(f) ? $"KHW{f}H" : $"KWB{f}X",
            18 => $"CAT(KNM{f}X_, KNUM{f}N)",
            19 => $"CAT(KCH{f}H(), _1)",
            20 => $"CAT(KNH{f}H(), _2)",
            21 => $"CAT(KUU{f}X_, 2u)",
            22 => $"CAT(KEX{f}X, _3)",
            23 => $"CAT(KCE{f}X, _4)",
            24 => $"KDG{f}G(5)",
            25 => $"KHF{f}F(ZHG{f}X)",
            26 => $"KUP{f}M(ZUP{f}A, B) + KUP{f}M(KUQ{f}C, DZ)",
            28 => $"KXF{f}F(KXU{f}N)",
            29 => $"CAT(KEO{f}X_, KEN{f}N)",
            30 => $"CAT(KEM{f}X_, )",
            31 => $"KAPPLY(CAT, ZHQ{f}X_, 5) + KAPPLY(CAT, KHR{f}X_, QZ)",
            _ => null,
        };
        switch (p.Kind(f))
        {
            case 9: scope.Append($"#define KBX{f}G(n) KBX{f}Q_##n\n"); break;
            case 16: scope.Append($"/* CAT(YC{f}Q_, 1Y) */\n"); break;
            case 17: scope.Append($"const char *ys{f} = \"CAT(YS{f}Q_, 1Y)\";\n"); break;
            case 24: scope.Append($"%:define KDG{f}G(n) KDG{f}Q_%:%:n\n"); break;
            case 27: scope.Append($"#if 0\nvoid *kwg{f} = (void *)KWG{f}H;\n#endif\n"); break;
        }
        if (e is not null) fn.Append($"int ff{f}(void)\n{{\n\treturn {e};\n}}\n");
    }

    // ---------------- traps ----------------

    private static void TrapHostText(int t, StringBuilder sb)
    {
        sb.Append($"""
            #define KNA{t}X (CAT)
            #define ZNB{t}Q_1Z 1
            #define ZLG{t}Q_1Z 1
            #define ZLA{t}Q_1Z 1
            #define ZST{t}Q_1Z 1
            #define ZUSE{t}Z CAT(ZUT{t}Q_, 1)
            #define ZUT{t}Q_1Z 1
            #define KPM{t}Q ZUZ{t}Q
            #define ZUZ{t}Q_1Z 1
            #define KCY{t}X KCZ{t}X
            #define KCZ{t}X KCY{t}X
            #define ZCQ{t}Q_1Z 1
            /*
            #define KCMT{t}X 1
            */
            #define KCO{t}X 1 /* a define line that ends
               inside a comment */
            #define KMBP{t}X_KMBA{t}X 1
            #define KMBP{t}X_KMBB{t}X 1
            #define YE{t}Q_1Y 1

            """);
        foreach (var (stem, n) in new[] { ("KJ", 64), ("KK", 65) })
        {
            for (int i = 0; i < n; i++) sb.Append($"#define {stem}{t}A{i}X1 1\n");
        }
        for (int k = 0; k < 20; k++)
        {
            sb.Append(k == 0 ? "#if FW_SEL == 0\n" : k < 19 ? $"#elif FW_SEL == {k}\n" : "#else\n");
            sb.Append($"#define KP8{t}X KPB{t}N{k}X_\n");
        }
        sb.Append("#endif\n");
        for (int k = 0; k < 20; k++) sb.Append($"#define KPB{t}N{k}X_5 {k + 1}\n");
    }

    private static void TrapUseText(int t, StringBuilder fn)
    {
        foreach (var (stem, n) in new[] { ("KJ", 64), ("KK", 65) })
        {
            fn.Append($"int tj{n}_{t}(void)\n{{\n\treturn KJOIN{n}(");
            for (int i = 0; i < n; i++) fn.Append(i > 0 ? (i % 8 == 0 ? ",\n\t\t" : ", ") : "").Append($"{stem}{t}A{i}X");
            fn.Append(");\n}\n");
        }
        fn.Append($"int tp8_{t}(void)\n{{\n\treturn CAT(KP8{t}X, 5);\n}}\n");
    }

    private static void WriteTrap(Plan p, int t)
    {
        using var w = new FwWriter(p.Fw($"src/traps/trap_{t}.h"));
        w.Text($$"""
            /* firmware traps: kept text, never compiled */
            int tna{{t}} = KNA{{t}}X(ZNB{{t}}Q_, 1);
            void tlog{{t}}(void) { KLOGV("x", ZLG{{t}}Q_, 1); KLOGA("x", ZLA{{t}}Q_, 1); }
            const char *tst{{t}} = KSTR(ZST{{t}}Q_);
            #define KPT{{t}}G(n) KPM{{t}}Q##n
            int tcy{{t}} = CAT(KCY{{t}}X, 1);
            int tmb{{t}} = CAT(KMBP{{t}}X_,
            #ifdef FW_ALT
            	KMBA{{t}}X);
            #else
            	KMBB{{t}}X);
            #endif
            int tye{{t}} = CAT(YE{{t}}Q_, 1Y

            """);
    }

    // Names only regs_0.h defines: reserved names, the non-paster variadic macros, -D, hostile-file and ext seeds.
    private const string Regs0Extras = """
        #define __KRS0 1
        #define _KRS0 1
        #define NOMINMAX 1
        #define KLOGV(fmt, ...) kprintf(fmt, ##__VA_ARGS__)
        #define KLOGA(fmt, args...) kprintf(fmt, ##args)
        #define KSTR(x) #x
        #define ZBIN0Z 1
        #define KBIN0X 1
        #define KU16V0X 1
        #define KHID0X 1
        #define KBM0X 1
        #define KL1N0X 1
        #define KUNT0X 1
        #define KCRL0X 1
        #define YEXU0Y 1
        #define KOS0X_VAL 1
        #define KOSH0X_VAL 1
        #define KHWB_Z_rZ_VAL 1
        #define ZIL0Z 1
        #define KDE0X_4 1

        """;

    // ---------------- big headers ----------------

    private static void WriteBig(Plan p, int h)
    {
        // the blocks this header hosts: families of the units that include it, traps, extras
        var blocks = new List<string>();
        var sb = new StringBuilder();
        for (int u = h; u < p.Units; u += p.Big)
            for (long f = u; f < p.Families; f += p.Units)
            {
                sb.Clear();
                HostText(p, f, dense: false, sb);
                if (sb.Length > 0) blocks.Add(sb.ToString());
            }
        for (int t = 0; t < Traps; t++)
            if (p.BigOf(p.TrapUnit(t)) == h)
            {
                sb.Clear();
                TrapHostText(t, sb);
                blocks.Add(sb.ToString());
            }
        if (h == 0) blocks.Add(Regs0Extras);
        long p0 = p.Pasters * h / p.Big, p1 = p.Pasters * (h + 1) / p.Big;

        long target = h == 0 ? BoundaryBig : p.BigBytes;
        bool headless = h == 1, crlf = h == 2;
        var rng = Rng.For(p.O.Seed, Category.Firmware, h);
        using var w = new FwWriter(p.Fw($"big/regs_{h}.h"), crlf);
        w.L($"/* regs_{h}.h: generated register map */");
        if (!headless) { w.L($"#ifndef FW_REGS_{h}_H"); w.L($"#define FW_REGS_{h}_H"); }
        w.L($"#define KDV_VAL 9");

        long blockBytes = blocks.Sum(b => (long)b.Length) * (crlf ? 2 : 1);
        long pasterBytes = (p1 - p0) * 30;
        long budget = target - blockBytes - pasterBytes - 4096;
        int gaps = blocks.Count + 1;
        long perGap = Math.Max(0, budget * 7 / 10 / gaps / 100);   // filler lines per gap (~100 bytes each)
        long filler = 0, pi = p0;
        for (int b = 0; b < blocks.Count; b++)
        {
            long until = p0 + (p1 - p0) * (b + 1) / gaps;
            for (; pi < until; pi++) w.L(Paster(pi));
            long n = perGap == 0 ? 0 : rng.Next((int)Math.Min(int.MaxValue, perGap * 2 + 1));
            for (long k = 0; k < n; k++) Filler(w, $"ZF{h}", filler++, ref rng);
            w.Text(blocks[b]);
        }
        for (; pi < p1; pi++) w.L(Paster(pi));
        string end = headless ? "" : "#endif";
        long tail = (end.Length + w.NlBytes) + 300;
        while (w.Bytes + FillerMax + tail < target) Filler(w, $"ZF{h}", filler++, ref rng);
        if (end.Length > 0) w.L(end);
        if (!w.PadTo(target) && h == 0)
            throw new InvalidOperationException($"firmware: regs_0.h content passes {BoundaryBig:N0} bytes; lower --fw-families or --fw-pasters, or raise --fw-big-headers");
    }

    // A work header (docs/firmware-design.md "Work shape"); FwWork lays it out.
    private static void WriteWork(Plan p, FwWork wk, int h)
    {
        var rng = Rng.For(p.O.Seed, Category.Firmware, (1L << 40) + h);
        using var w = new FwWriter(p.Fw($"{FwWork.Dir}/{FwWork.HeaderFile(h)}"));
        wk.Write(h, w.L, i => Filler(w, $"ZWF{h}", i, ref rng), () => w.Bytes);
    }

    // ---------------- dense headers ----------------

    private static void WriteDx(Plan p, int x)
    {
        using var w = new FwWriter(p.Fw($"dense/dx_{x}.h"));
        w.L($"#ifndef FW_DX_{x}_H");
        w.L($"#define FW_DX_{x}_H");
        var sb = new StringBuilder();
        for (int u = x; u < p.Units; u += p.Dx)
            for (long f = u; f < p.Families; f += p.Units)
            {
                sb.Clear();
                HostText(p, f, dense: true, sb);
                w.Text(sb.ToString());
            }
        var rng = Rng.For(p.O.Seed, Category.Firmware, 1_000_000L + x);
        long i = 0;
        while (w.Bytes + FillerMax + 300 < DenseBytes) Filler(w, $"ZX{x}", i++, ref rng);
        w.L("#endif");
    }

    // The density fixtures: 0.70 (dense), exactly 0.60 (dense, edge), 0.5999 (not dense, edge), 0.50, and a file of
    // nothing but defines one byte under the dense floor. The counted prefix is followed by a // line running past
    // the 262,144-char window, so what Carver counts is exactly the lines written here.
    private static void WriteDenseFixtures(Plan p)
    {
        Fixture(p, "dense_70", 70, 100, carved: true);
        Fixture(p, "dense_60", 30, 50, carved: true);
        Fixture(p, "dense_5999", 5999, 10000, carved: false);
        Fixture(p, "dense_50", 50, 100, carved: false);

        using (var w = new FwWriter(p.Fw("dense/dense_small.h")))
        {
            long i = 0;
            var rng = Rng.For(p.O.Seed, Category.Firmware, 2_000_000L);
            while (w.Bytes + FillerMax + 300 < BoundarySmall) Filler(w, "WSM", i++, ref rng);
            w.PadTo(BoundarySmall);
        }

        // dense, and included straight from a unit outside the root: kept whole, so not carved
        using (var w = new FwWriter(p.Fw("dense/shared_0.h")))
        {
            w.L("#ifndef FW_SHARED_0_H");
            w.L("#define FW_SHARED_0_H");
            w.L("#define KSH0X_1 1");
            long i = 0;
            var rng = Rng.For(p.O.Seed, Category.Firmware, 2_000_001L);
            while (w.Bytes + FillerMax + 300 < DenseBytes) Filler(w, "KSH", i++, ref rng);
            w.L("#endif");
        }
    }

    private static void Fixture(Plan p, string name, int defines, int nonBlank, bool carved)
    {
        // nonBlank = the #ifndef line + `defines` defines (the guard among them) + extern lines
        string stem = carved ? $"ZDN{name[6..]}" : $"WDN{name[6..]}";
        using var w = new FwWriter(p.Fw($"dense/{name}.h"));
        string guard = $"FW_{name.ToUpperInvariant()}_H";
        w.L($"#ifndef {guard}");
        w.L($"#define {guard}");
        int externs = nonBlank - 1 - defines;
        for (int i = 0; i < defines - 1; i++)
        {
            w.L($"#define {stem}D{i}{(carved ? "Z" : "W")} 1");
            if (i < externs) w.L($"extern int e{i};");
        }
        for (int i = defines - 1; i < externs; i++) w.L($"extern int e{i};");
        if (w.Bytes >= FwLexer.DensePrefixChars) throw new InvalidOperationException($"firmware: {name} prefix too long");
        w.L("//" + new string('-', (int)(FwLexer.DensePrefixChars - w.Bytes) + 64));
        var rng = Rng.For(p.O.Seed, Category.Firmware, 3_000_000L + defines);
        long n = 0;
        while (w.Bytes + FillerMax + 300 < DenseBytes) Filler(w, stem + "F", n++, ref rng);
        w.L("#endif");
    }

    // ---------------- fw/src ----------------

    private static void WriteKeptHeader(Plan p, int i)
    {
        using var w = new FwWriter(p.Fw(p.HdrRel(i)));
        w.L($"#ifndef FW_K{i}_H");
        w.L($"#define FW_K{i}_H");
        int d = p.DirOf(i);
        for (int j = d * FilesPerDir; j < Math.Min(p.Files, (d + 1) * FilesPerDir); j++)
        {
            if (p.IsHeaderFile(j)) continue;
            int u = p.UnitFile.BinarySearch(j);
            for (long f = u; f < p.Families; f += p.Units)
                if (p.KeptHeaderBody(f)) w.L($"#define KHW{f}H CAT(KHP{f}X_, 5)");
        }
        w.L("#endif");
    }

    private static void WriteUnit(Plan p, int u)
    {
        int file = p.UnitFile[u];
        using var w = new FwWriter(p.Fw(p.UnitRel(u)));
        w.L($"/* {p.UnitRel(u)}: firmware unit */");
        w.L("#include \"fwpaste.h\"");
        w.L($"#include \"regs_{p.BigOf(u)}.h\"");
        if (p.Dx > 0) w.L($"#include \"dx_{p.DxOf(u)}.h\"");
        w.L("#include <fw_os.h>");
        int hdr = p.HeaderOfDir(p.DirOf(file));
        if (hdr >= 0) w.L($"#include \"k{hdr}.h\"");
        if (p.Work is { } wk) foreach (int h in wk.HeadersOfUnit(u, p.Units)) w.L($"#include \"{FwWork.HeaderFile(h)}\"");
        var scope = new StringBuilder();
        var fn = new StringBuilder();
        fn.Append($"int fu{u}(void)\n{{\n\treturn {u % 7};\n}}\n");
        for (long f = u; f < p.Families; f += p.Units)
        {
            UseText(p, f, scope, fn);
            if (scope.Length + fn.Length > 1 << 16) { w.Text(scope.ToString()); w.Text(fn.ToString()); scope.Clear(); fn.Clear(); }
        }
        for (int t = 0; t < Traps; t++) if (p.TrapUnit(t) == u) TrapUseText(t, fn);
        p.Work?.UnitText(u, p.Units, fn);
        if (u == 0) fn.Append("int fdv(void)\n{\n\treturn KDV_9 + KD_7 + KHWP(Z, Z) + CAT(KDE0X_, KDE_N);\n}\n");
        w.Text(scope.ToString());
        w.Text(fn.ToString());
    }

    // A .c past maxParseBytes: carved like a big header.
    private static void WriteBigSrc(Plan p)
    {
        using var w = new FwWriter(p.Fw("src/bigsrc_0.c"));
        w.L("/* bigsrc_0.c: a generated source past maxParseBytes */");
        w.L("#include \"fwpaste.h\"");
        w.L("#define KBS0X_5 5");
        w.L("int fw_bigsrc = CAT(KBS0X_, 5);");
        var rng = Rng.For(p.O.Seed, Category.Firmware, 4_000_000L);
        long i = 0;
        while (w.Bytes + FillerMax + 300 < BigSrcBytes) Filler(w, "ZBS", i++, ref rng);
        w.PadTo(BigSrcBytes);
    }

    // A .c of exactly maxParseBytes: parsed, a big initializer table, no defines.
    private static void WriteTable(Plan p)
    {
        using var w = new FwWriter(p.Fw("src/table_big.c"));
        w.L("/* table_big.c: exactly maxParseBytes, parsed, not carved */");
        w.L("unsigned fw_table_big[] = {");
        var rng = Rng.For(p.O.Seed, Category.Firmware, 5_000_000L);
        var sb = new StringBuilder(128);
        while (w.Bytes + 200 < BoundaryTable)
        {
            sb.Clear().Append('\t');
            for (int k = 0; k < 8; k++) sb.Append("0x").Append(((uint)rng.NextULong()).ToString("X8")).Append("u, ");
            w.L(sb.ToString().TrimEnd());
        }
        w.L("\t0u");
        w.L("};");
        if (!w.PadTo(BoundaryTable)) throw new InvalidOperationException("firmware: table_big.c overran its size");
    }

    // ---------------- ext, misc ----------------

    private static void WriteExt(Plan p)
    {
        string ext = Path.Combine(p.Root, ExtDir);
        File.WriteAllText(Path.Combine(ext, "include", "fw_os.h"),
            "#ifndef FW_OS_H\n#define FW_OS_H\n/* outside the firmware root, included by every unit */\n#include <fw_osh.h>\n#define FW_OS_LIMIT KOS0X_VAL\n#endif\n");
        // Hidden (the Windows attribute) and outside the root: a tool that skips hidden files misses its seed.
        string osh = Path.Combine(ext, "include", "fw_osh.h");
        File.WriteAllText(osh, "#ifndef FW_OSH_H\n#define FW_OSH_H\n/* hidden, outside the root, included by fw_os.h */\n#define FW_OSH_LIMIT KOSH0X_VAL\n#endif\n");
        File.SetAttributes(osh, File.GetAttributes(osh) | FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(ext, "include", "unused_os.h"),
            "/* outside the root in an include dir, included by nothing: a computed include could, so it may be kept */\n#define FW_OS_UNUSED YEXU0Y\n");
        File.WriteAllText(Path.Combine(ext, "app", "host.c"),
            "/* a unit outside the firmware root that includes a dense header: that header is kept whole */\n"
            + "#include <fw_os.h>\n#include \"shared_0.h\"\nint fw_host(void)\n{\n\treturn KSH0X_1;\n}\n");
    }

    private static void WriteMisc(Plan p, List<FwHostile> hostile)
    {
        void Put(string rel, byte[] bytes, string kind)
        {
            File.WriteAllBytes(p.Fw(rel), bytes);
            hostile.Add(new FwHostile($"{FwSpec.FwDir}/{rel}", kind));
        }
        byte[] A(string s) => Encoding.ASCII.GetBytes(s);

        Put("misc/empty.h", [], "empty");
        Put("misc/bom.h", [0xEF, 0xBB, 0xBF, .. A("/* utf-8 bom */\nint fw_bom = KBM0X;\n")], "utf8-bom");
        Put("misc/latin1.c", [.. A("/* caf"), 0xE9, .. A(" */\nint fw_l1 = KL1N0X;\n")], "latin1");
        Put("misc/unterminated.h", A("int fw_ut = KUNT0X;\n/* this comment never ends\n"), "unterminated-comment");
        Put("misc/crlf.h", A("/* crlf */\r\nint fw_cr = KCRL0X;\r\n"), "crlf");
        var blob = new byte[4096];
        A("blob ZBIN0Z").CopyTo(blob, 100);   // NUL bytes from offset 0: binary, never read
        Put("misc/blob.bin", blob, "nul-early");
        var late = new byte[9000];
        Array.Fill(late, (byte)'-');
        A(" KBIN0X ").CopyTo(late, 10);
        late[8500] = 0;                       // a NUL past the 8 KB probe: text
        Put("misc/late.bin", late, "nul-late");
        Put("misc/notes_u16.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("notes: uses KU16V0X\n")], "utf16");
        string hidden = p.Fw("misc/.fwhidden");
        Put("misc/.fwhidden", A("KHID0X\n"), "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        hostile.Add(new FwHostile($"{ExtDir}/include/fw_osh.h", "hidden (outside the root)"));
        hostile.Add(new FwHostile($"{FwSpec.FwDir}/big/regs_0.h", $"size {BoundaryBig} (one past maxParseBytes)"));
        if (p.Big > 1) hostile.Add(new FwHostile($"{FwSpec.FwDir}/big/regs_1.h", "no include guard"));
        if (p.Big > 2) hostile.Add(new FwHostile($"{FwSpec.FwDir}/big/regs_2.h", "crlf"));
        hostile.Add(new FwHostile($"{FwSpec.FwDir}/src/table_big.c", $"size {BoundaryTable} (exactly maxParseBytes)"));
        hostile.Add(new FwHostile($"{FwSpec.FwDir}/src/bigsrc_0.c", "a .c past maxParseBytes"));
        hostile.Add(new FwHostile($"{FwSpec.FwDir}/dense/dense_small.h", $"size {BoundarySmall} (one under the dense floor)"));
    }

    // ---------------- compile db, cases ----------------

    private static void WriteDb(Plan p)
    {
        bool rel = p.O.FwDbPaths == "relative";
        string P(string corpusRel) => rel ? corpusRel : Path.GetFullPath(Path.Combine(p.Root, corpusRel));
        string[] inc = p.Work is null ? ["fw/include", "fw/big", "fw/dense", "ext/include"]
            : ["fw/include", "fw/big", "fw/dense", $"fw/{FwWork.Dir}", "ext/include"];
        using var fs = File.Create(Path.Combine(p.Root, FwSpec.DbPath));
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        void Entry(string file)
        {
            w.WriteStartObject();
            w.WriteString("directory", rel ? "." : Path.GetFullPath(p.Root));
            w.WriteStartArray("arguments");
            w.WriteStringValue("cc");
            w.WriteStringValue("-std=c11");
            foreach (var i in inc) { w.WriteStringValue("-I"); w.WriteStringValue(P(i)); }
            w.WriteStringValue("-DKD_7");
            w.WriteStringValue("-DKDV_9=KDV_VAL");
            w.WriteStringValue("-DKDE_N=4");
            w.WriteStringValue("-c");
            w.WriteStringValue(P(file));
            w.WriteEndArray();
            w.WriteString("file", P(file));
            w.WriteEndObject();
        }
        for (int u = 0; u < p.Units; u++) Entry($"{FwSpec.FwDir}/{p.UnitRel(u)}");
        Entry($"{ExtDir}/app/host.c");
        w.WriteEndArray();
    }

    private static long WriteCases(Plan p)
    {
        var sb = new StringBuilder("name\tcase\tsite\n");
        long n = 0;
        void Row(string name, string kase, string site) { sb.Append(name).Append('\t').Append(kase).Append('\t').Append(site).Append('\n'); n++; }

        for (int t = 0; t < Traps; t++)
        {
            string trap = $"src/traps/trap_{t}.h", unit = p.UnitRel(p.TrapUnit(t));
            Row($"ZNB{t}Q_1Z", "non-alias-call", trap);
            Row($"KNA{t}X", "non-alias-call", trap);
            Row($"ZLG{t}Q_1Z", "non-paster-va-args", trap);
            Row($"ZLA{t}Q_1Z", "non-paster-named-variadic", trap);
            Row($"ZST{t}Q_1Z", "non-paster-stringify", trap);
            Row($"ZUT{t}Q_1Z", "unwanted-body", $"big/regs_{p.BigOf(p.TrapUnit(t))}.h");
            Row($"ZUZ{t}Q_1Z", "no-expansion-beside-paste", trap);
            Row($"KPM{t}Q", "no-expansion-beside-paste", trap);
            Row($"KCY{t}X", "cycle", trap);
            Row($"KCZ{t}X", "cycle", trap);
            Row($"ZCQ{t}Q_1Z", "cycle", trap);
            Row($"KCO{t}X", "comment-open-define", $"big/regs_{p.BigOf(p.TrapUnit(t))}.h");
            Row($"KMBP{t}X_KMBA{t}X", "branch-split-call", trap);
            Row($"KMBP{t}X_KMBB{t}X", "branch-split-call", trap);
            Row($"YE{t}Q_1Y", "eof-open-call", trap);
            Row($"KJ{t}A63X1", "join-64", unit);
            Row($"KK{t}A64X1", "join-65", unit);
            Row($"KPB{t}N19X_5", "bodies-20", unit);
        }
        foreach (var (name, kase, site) in new[]
        {
            ("__KRS0", "reserved", "big/regs_0.h"), ("_KRS0", "reserved", "big/regs_0.h"), ("NOMINMAX", "reserved", "big/regs_0.h"),
            ("KDV_VAL", "define-arg", FwSpec.DbPath), ("KBIN0X", "late-nul-text", "misc/late.bin"),
            ("ZBIN0Z", "binary-not-read", "misc/blob.bin"), ("KU16V0X", "utf16-text", "misc/notes_u16.txt"),
            ("KHID0X", "hidden-file", "misc/.fwhidden"), ("KBM0X", "utf8-bom", "misc/bom.h"), ("KL1N0X", "latin1", "misc/latin1.c"),
            ("KUNT0X", "unterminated-comment", "misc/unterminated.h"), ("KCRL0X", "crlf", "misc/crlf.h"),
            ("YEXU0Y", "outside-not-included", $"../{ExtDir}/include/unused_os.h"), ("KOS0X_VAL", "outside-included", $"../{ExtDir}/include/fw_os.h"),
            ("KOSH0X_VAL", "outside-included-hidden", $"../{ExtDir}/include/fw_osh.h"),
            ("KBS0X_5", "big-source", "src/bigsrc_0.c"),
            ("KHWB_Z_rZ_VAL", "interior-letter", p.UnitRel(0)), ("ZIL0Z", "interior-letter", "big/regs_0.h"),
            ("KDE0X_4", "define-arg-value", p.UnitRel(0)),
        })
            Row(name, kase, site);

        var seen = new int[Kinds];
        for (long f = 0; f < p.Families && seen.Any(c => c < CasesPerKind); f++)
        {
            int k = p.Kind(f);
            if (seen[k]++ >= CasesPerKind) continue;
            string site = k is 11 or 12 ? $"big/regs_{p.BigOf(p.UnitOf(f))}.h" : p.UnitRel(p.UnitOf(f));
            string kase = KindNames[k] + (p.DenseHosted(f) ? " (dense host)" : "") + (p.LongChain(f) ? " (long chain)" : "");
            Row(Target(p, f), kase, site);
            if (Decoy(p, f) is { } d) Row(d, kase + " decoy", site);
        }
        if (p.Work is { } wk) foreach (var (name, kase, site) in wk.Cases()) Row(name, kase, site);
        File.WriteAllText(Path.Combine(p.Root, CasesPath), sb.ToString());
        return n;
    }

    // The name a family exists to keep (or leave open).
    private static string Target(Plan p, long f) => p.Kind(f) switch
    {
        0 => $"KD{f}X_VAL",
        1 => $"ZA{f}X_7",
        2 => $"KSB_S{f}XZ",
        3 => $"KML{f}X_7",
        4 => $"KFC{f}X_5",
        5 => $"KAN{f}X_5",
        6 => $"KHO{f}X_5",
        7 => $"KU{f}X_2",
        8 => $"KWP{f}X_5",
        9 => $"KBX{f}Q_5",
        10 => $"KSFC{f}X_5",
        11 => $"KB0{f}X_5",
        12 => $"KBC{f}X_5",
        13 => $"KGT{f}X_35",
        14 => $"KRPA{f}X_5",
        15 => p.KeptHeaderBody(f) ? $"KHP{f}X_5" : $"KWP{f}X_5",
        16 => $"YC{f}Q_1Y",
        17 => $"YS{f}Q_1Y",
        18 => $"KNM{f}X_2",
        19 => $"KCX{f}X_1",
        20 => $"KNF{f}X_2",
        21 => $"KUU{f}X_2u",
        22 => $"KEB{f}X_3",
        23 => $"KCF{f}X_4",
        24 => $"KDG{f}Q_5",
        25 => $"ZHG{f}X_6",
        26 => $"ZUP{f}A_S{f}_B",
        27 => $"ZWH{f}_Q_OFF",
        28 => $"KXV{f}_3",
        29 => $"KEO{f}X_",
        30 => $"KEM{f}X_",
        _ => $"ZHQ{f}X_5",
    };

    // A name beside the target that has only one side of it, so it is dropped: a start piece with no end, or the reverse.
    private static string? Decoy(Plan p, long f) => p.Kind(f) switch
    {
        1 => $"ZA{f}X_REGZ",
        2 => $"ZSC_S{f}XZ",
        25 => $"ZHG{f}X_REGZ",
        26 => $"ZUP{f}A_S{f}_DZ",
        27 => $"ZWH{f}_Q_OFZ",
        31 => $"ZHQ{f}X_QZ",
        _ => null,
    };
}
