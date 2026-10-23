using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodeSpawner.Firmware;

/// <summary>One truth list: where it was written (null when only hashed), its sha256 and line count.</summary>
internal sealed record FwList(string? Path, string Sha256, long Count);

/// <summary>A dense header and whether a one-line change flips Carver's verdict.</summary>
internal sealed record FwDense(string Path, bool Edge);

/// <summary>Everything the firmware truth says (docs/firmware-design.md), as computed from the tree.</summary>
internal sealed class FwTruth
{
    public string TreeSha = "";
    public long BigHeadersKept, WholeIncludedFromOutside, DenseHeaders, BytesBefore, PasteMacros, ForwardingMacros, TotalDefineLines;
    public long DefinesDroppedMin, DefinesDroppedMax, BytesAfterMin, BytesAfterMax;
    public List<string> BigHeaders = new(), WholeFromOutside = new();
    /// <summary>The fw-relative files a carve rewrites (big and dense, less those included from outside). Not in the manifest.</summary>
    public List<string> Carved = new();
    public List<FwDense> Dense = new();
    public FwList MustKeepDefines = null!, MustDropDefines = null!, OpenDefines = null!, MustKeepFunctions = null!, MustKeepFiles = null!;
    /// <summary>Generator check: C names whose class disagrees with the alphabet (Z…Z drop, Y…Y open, else keep).</summary>
    public List<string> AlphabetErrors = new();
    public long MustKeepCount, MustDropCount, OpenCount;
}

/// <summary>
/// The firmware truth, computed from the written tree (docs/firmware-design.md): the generator runs it on what it
/// wrote and fails on any disagreement with the names it meant, and verify runs it again. Single-threaded; one
/// file in memory at a time; names are bytes in a <see cref="NameTable"/>.
/// </summary>
internal sealed class FwSpec
{
    public const long MaxParseBytes = 20_000_000;
    public const string FwDir = "fw", DbPath = "fw-build/compile_commands.json";

    private static readonly string[] Reserved =
    {
        "NDEBUG", "DEBUG", "WIN32_LEAN_AND_MEAN", "VC_EXTRALEAN", "NOMINMAX", "STRICT", "UNICODE", "NOGDI", "NOUSER",
        "NOCRYPT", "NOSERVICE", "NOMCX", "NOIME", "WINVER", "NTDDI_VERSION",
    };

    // flags beyond NameTable's
    private const ushort FwdC = 512;
    /// <summary>Seeded under max only: named by an outside header no build includes (a computed include may).</summary>
    private const ushort SeedMax = 1024;

    public readonly NameTable Names = new(1 << 16);
    private readonly string _root, _fwRoot;

    // files: fw files (sorted, fw-relative) then ext closure files
    private readonly List<string> _files = new();        // full paths
    private readonly List<string> _rel = new();          // fw-relative (fw files) or corpus-relative (ext)
    private int _fwCount;
    private bool[] _isK = Array.Empty<bool>(), _isC = Array.Empty<bool>(), _binary = Array.Empty<bool>();

    // C defs (struct of arrays)
    private readonly IntList _cdName = new(), _cdLenTerm = new();   // name; line length << 2 | terminator bytes
    private long[] _cdOff = new long[1024];               // offset in all carved bytes (see CFile)
    private int[] _cdNext = Array.Empty<int>();
    private readonly List<long> _cBase = new();           // where each carved file starts in all carved bytes
    private readonly List<int> _cBaseFile = new();
    private long _cBytes;
    private int[] _cHead = Array.Empty<int>();            // name -> first C def (-1)

    // all defs: edge pieces (rule 2), forward edges and direct pasters (rule 1), each with the runs it counts for
    // (bit 0 min, bit 1 max: a maybe-kept header's defines count under max only)
    private readonly IntList _xName = new(), _xOff = new(), _xMask = new(), _xItems = new();   // items: nS, S..., nE, E...
    private int[] _xHead = Array.Empty<int>(), _xNext = Array.Empty<int>();
    private readonly IntList _edgeFrom = new(), _edgeTo = new(), _edgeMask = new();
    private readonly List<(int Name, int Mask)> _directPasters = new();
    private readonly HashSet<int> _definedUnder = new();   // names starting `__` that something defines
    // the defines outside the carved files (kept fw files, ext, -D, maybe-kept): their text, for rule 2's sides
    private readonly List<(int Name, byte[] Text, int Mask)> _ncDefs = new();
    private readonly Dictionary<int, List<int>> _ncByName = new();
    private List<int> _maybe = new();
    private readonly IntList _fwDefFile = new(), _fwDefName = new();   // defs in kept (non-C) fw files

    // the build
    private readonly List<Unit> _units = new();
    private readonly HashSet<int> _closure = new();       // file indices (fw and ext) in any unit's closure
    private readonly List<int> _wholeFromOutside = new();

    private int[] _sortedFwd = Array.Empty<int>(), _sortedRev = Array.Empty<int>();
    private readonly IntList[] _pieceQueue = { new(), new() };   // id << 1 | (1: an end piece), or a wildcard
    private readonly IntList[] _wantQueue = { new(), new() };
    private static readonly ushort[] StartFlag = { NameTable.StartMin, NameTable.StartMax };
    private static readonly ushort[] EndFlag = { NameTable.EndMin, NameTable.EndMax };
    private static readonly ushort[] ExpSFlag = { NameTable.ExpSMin, NameTable.ExpSMax };
    private static readonly ushort[] ExpEFlag = { NameTable.ExpEMin, NameTable.ExpEMax };
    private static readonly ushort[] PasterFlag = { NameTable.Paster, NameTable.PasterMax };
    private static readonly ushort[] WantFlag = { NameTable.WantMin, NameTable.WantMax };
    private readonly Dictionary<int, SafeFileHandle> _handles = new();

    private sealed class Unit
    {
        public required string File;
        public required List<string> IncDirs;
        public required List<string> DefineArgs;
    }

    private FwSpec(string root)
    {
        _root = Path.GetFullPath(root);
        _fwRoot = Path.Combine(_root, FwDir);
    }

    /// <summary>Computes the truth of the corpus at <paramref name="root"/>. With <paramref name="listDir"/> the lists
    /// are written there (main.* and union.*); without, they are only hashed.</summary>
    public static FwTruth Compute(string root, string? listDir, bool checkAlphabet)
    {
        var s = new FwSpec(root);
        try { return s.Run(listDir, checkAlphabet); }
        finally { foreach (var h in s._handles.Values) h.Dispose(); }
    }

    private FwTruth Run(string? listDir, bool checkAlphabet)
    {
        var t = new FwTruth();
        EnumerateFw();
        t.TreeSha = TreeSha();
        ReadDb();
        ClassifyFiles(t);
        Closure();
        _maybe = MaybeKept();
        foreach (int f in _wholeFromOutside) _isC[f] = false;
        t.WholeFromOutside = _wholeFromOutside.Select(f => _rel[f]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        t.WholeIncludedFromOutside = _wholeFromOutside.Count;
        for (int f = 0; f < _fwCount; f++) if (_isC[f]) t.Carved.Add(_rel[f]);

        Pass1();
        PasterFixpoint(0);
        PasterFixpoint(1);
        SortNames();
        Seeds();
        Pass2();
        for (int run = 0; run < 2; run++) Fixpoint(run);
        Results(t, listDir, checkAlphabet);
        return t;
    }

    // ---------------- files ----------------

    private static bool IsCExt(string p) =>
        p.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".h", StringComparison.OrdinalIgnoreCase);

    private void EnumerateFw()
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var all = Directory.EnumerateFiles(_fwRoot, "*", opts)
            .Select(p => (Full: p, Rel: Path.GetRelativePath(_fwRoot, p).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.Ordinal).ToList();
        foreach (var (full, rel) in all) { _files.Add(full); _rel.Add(rel); }
        _fwCount = _files.Count;
    }

    private string TreeSha()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        for (int i = 0; i < _fwCount; i++)
        {
            using var fs = new FileStream(_files[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            sha.AppendData(Encoding.UTF8.GetBytes(_rel[i] + "\n" + fs.Length + "\n"));
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private int FileIndex(string full)
    {
        // fw files by binary search on the relative path; ext files appended on first sight
        if (IsUnder(full, _fwRoot))
        {
            string rel = Path.GetRelativePath(_fwRoot, full).Replace('\\', '/');
            int lo = 0, hi = _fwCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = string.CompareOrdinal(_rel[mid], rel);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }
        for (int i = _fwCount; i < _files.Count; i++)
            if (string.Equals(_files[i], full, StringComparison.OrdinalIgnoreCase)) return i;
        _files.Add(full);
        _rel.Add(Path.GetRelativePath(_root, full).Replace('\\', '/'));
        return _files.Count - 1;
    }

    private static bool IsUnder(string full, string dir) =>
        full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void ReadDb()
    {
        string db = Path.Combine(_root, DbPath);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(db));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string dir = e.GetProperty("directory").GetString()!;
            if (!Path.IsPathRooted(dir)) dir = Path.Combine(_root, dir);
            dir = Path.GetFullPath(dir);
            var args = new List<string>();
            if (e.TryGetProperty("arguments", out var a)) foreach (var x in a.EnumerateArray()) args.Add(x.GetString()!);
            else if (e.TryGetProperty("command", out var c))
                args.AddRange(c.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var u = new Unit { File = Resolve(dir, e.GetProperty("file").GetString()!), IncDirs = new(), DefineArgs = new() };
            for (int i = 1; i < args.Count; i++)
            {
                string x = args[i];
                if (x == "-I" && i + 1 < args.Count) u.IncDirs.Add(Resolve(dir, args[++i]));
                else if (x.StartsWith("-I", StringComparison.Ordinal)) u.IncDirs.Add(Resolve(dir, x[2..]));
                else if (x == "-D" && i + 1 < args.Count) u.DefineArgs.Add(args[++i]);
                else if (x.StartsWith("-D", StringComparison.Ordinal)) u.DefineArgs.Add(x[2..]);
            }
            _units.Add(u);
        }
    }

    private static string Resolve(string dir, string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(dir, p));

    private void ClassifyFiles(FwTruth t)
    {
        _isK = new bool[_fwCount];
        _binary = new bool[_fwCount];
        for (int i = 0; i < _fwCount; i++)
        {
            _binary[i] = FwLexer.LooksBinary(_files[i]);
            if (!IsCExt(_files[i]) || _binary[i]) continue;
            long len = new FileInfo(_files[i]).Length;
            if (len > MaxParseBytes) { _isK[i] = true; t.BigHeaders.Add(_rel[i]); continue; }
            if (len < FwLexer.DenseMinBytes) continue;
            var (dense, edge) = FwLexer.Density(_files[i]);
            if (dense) _isK[i] = true;
            if (dense || edge) t.Dense.Add(new FwDense(_rel[i], edge));
        }
        // a 0.5999 decoy is listed with edge: true but is not dense
        t.DenseHeaders = t.Dense.Count(d => _isK[FileIndex(Path.Combine(_fwRoot, d.Path))]);
        t.BigHeadersKept = _isK.Count(k => k);
        _isC = (bool[])_isK.Clone();
    }

    // ---------------- includes ----------------

    private readonly Dictionary<int, List<(bool Quoted, string Name)>> _incCache = new();

    private List<(bool Quoted, string Name)> Includes(int f)
    {
        if (_incCache.TryGetValue(f, out var l)) return l;
        l = new();
        if (f < _fwCount && _binary[f]) { _incCache[f] = l; return l; }
        byte[] raw = File.ReadAllBytes(_files[f]);
        var text = FwLexer.TextBytes(raw, out int st);
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind != LineKind.OtherDirective) continue;
            var s = w.Spliced;
            int i = FwLexer.DirectiveStart(s);
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (!s[i..].StartsWith("include"u8)) continue;
            i += 7;
            if (i < s.Length && FwLexer.IsIdChar(s[i])) continue;   // include_next etc.
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            if (i >= s.Length) continue;
            byte close = s[i] == '"' ? (byte)'"' : s[i] == '<' ? (byte)'>' : (byte)0;
            if (close == 0) continue;   // computed: skipped
            int e = s[(i + 1)..].IndexOf(close);
            if (e < 0) continue;
            l.Add((close == '"', Encoding.UTF8.GetString(s.Slice(i + 1, e))));
        }
        _incCache[f] = l;
        return l;
    }

    private int ResolveInclude(int from, bool quoted, string name, Unit u)
    {
        IEnumerable<string> dirs = u.IncDirs;
        if (quoted) dirs = dirs.Prepend(Path.GetDirectoryName(_files[from])!);
        foreach (var d in dirs)
        {
            string p;
            try { p = Path.GetFullPath(Path.Combine(d, name)); } catch { continue; }
            if (!IsUnder(p, _root) || !File.Exists(p)) continue;
            return FileIndex(p);
        }
        return -1;
    }

    private void Closure()
    {
        var whole = new HashSet<int>();
        foreach (var u in _units)
        {
            if (!File.Exists(u.File) || !IsUnder(u.File, _root)) continue;
            int start = FileIndex(u.File);
            if (start < 0) continue;
            var seen = new HashSet<int> { start };
            var q = new Queue<int>();
            q.Enqueue(start);
            while (q.Count > 0)
            {
                int f = q.Dequeue();
                _closure.Add(f);
                foreach (var (quoted, name) in Includes(f))
                {
                    int g = ResolveInclude(f, quoted, name, u);
                    if (g < 0) continue;
                    if (f >= _fwCount && g < _fwCount && _isK[g]) whole.Add(g);
                    if (seen.Add(g)) q.Enqueue(g);
                }
            }
        }
        _wholeFromOutside.AddRange(whole.OrderBy(x => x));
    }

    private IEnumerable<int> ExtClosure() => _closure.Where(f => f >= _fwCount).OrderBy(f => _rel[f], StringComparer.Ordinal);

    /// <summary>The headers in the builds' include dirs outside the root that no build includes (corpus-relative
    /// order). A computed include could name one, so a sound carver may keep its text: it seeds under max only.</summary>
    private List<int> MaybeKept()
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var found = new HashSet<int>();
        foreach (var dir in _units.SelectMany(u => u.IncDirs).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir) || !IsUnder(dir, _root) || IsUnder(dir, _fwRoot)) continue;
            foreach (var p in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (!IsCExt(p)) continue;
                int g = FileIndex(Path.GetFullPath(p));
                if (g >= _fwCount && !_closure.Contains(g)) found.Add(g);
            }
        }
        return found.OrderBy(g => _rel[g], StringComparer.Ordinal).ToList();
    }

    // ---------------- pass 1: definitions ----------------

    private readonly List<byte[]> _dashDefines = new();

    private void Pass1()
    {
        var d = new DefineParse();
        foreach (int f in Enumerable.Range(0, _fwCount).Where(i => IsCExt(_files[i]) && !_binary[i]).Concat(ExtClosure())) DefFile(f, d, 3);
        foreach (int f in _maybe) if (!FwLexer.LooksBinary(_files[f])) DefFile(f, d, 2);
        // -DN=v is `#define N v`, -DN is `#define N 1`
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in _units)
            foreach (var a in u.DefineArgs)
            {
                if (!seen.Add(a)) continue;
                int eq = a.IndexOf('=');
                var line = Encoding.UTF8.GetBytes(eq < 0 ? $"#define {a} 1" : $"#define {a[..eq]} {a[(eq + 1)..]}");
                if (!d.Parse(line)) continue;
                _dashDefines.Add(line);
                RecordDef(d, line, Names.GetOrAdd(line.AsSpan(d.NameStart, d.NameLen)), 3, keepText: true);
            }

        _cHead = Enumerable.Repeat(-1, Names.Count).ToArray();
        _cdNext = new int[_cdName.Count];
        for (int k = _cdName.Count - 1; k >= 0; k--)
        {
            int n = _cdName[k];
            _cdNext[k] = _cHead[n];
            _cHead[n] = k;
        }
        _xHead = Enumerable.Repeat(-1, Names.Count).ToArray();
        _xNext = new int[_xName.Count];
        for (int k = _xName.Count - 1; k >= 0; k--)
        {
            int n = _xName[k];
            _xNext[k] = _xHead[n];
            _xHead[n] = k;
        }
    }

    /// <summary>The defines of one file, counting for the runs in <paramref name="mask"/>.</summary>
    private void DefFile(int f, DefineParse d, int mask)
    {
        byte[] raw = File.ReadAllBytes(_files[f]);
        var text = FwLexer.TextBytes(raw, out int st);
        bool isC = f < _fwCount && _isC[f];
        long cbase = _cBytes;
        if (isC) { _cBase.Add(cbase); _cBaseFile.Add(f); _cBytes += raw.Length; }
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind != LineKind.Define || !d.Parse(w.Spliced)) continue;
            int name = Names.GetOrAdd(w.Spliced.Slice(d.NameStart, d.NameLen));
            RecordDef(d, w.Spliced, name, mask, keepText: !isC);
            if (isC)
            {
                Names.Set(name, NameTable.CName);
                int k = _cdName.Count;
                _cdName.Add(name);
                if (k == _cdOff.Length) Array.Resize(ref _cdOff, NameTable.NextCapacity(k));
                _cdOff[k] = cbase + w.Start;      // no BOM shift: text == raw for C files
                if (w.Length >= 1 << 29 || w.TermBytes > 3) throw new InvalidOperationException($"{_rel[f]}: a define line of {w.Length:N0} bytes");
                _cdLenTerm.Add(w.Length << 2 | w.TermBytes);
                if (d.IsFunc && !d.PastesParam() && d.Body.Any(b => b.Kind == Tok.LParen)) Names.Set(name, FwdC);
                if (d.EndsInComment) Names.Set(name, NameTable.Seed);   // a comment-open define is kept
            }
            else if (f < _fwCount)
            {
                _fwDefFile.Add(f);
                _fwDefName.Add(name);
            }
        }
    }

    private void RecordDef(DefineParse d, ReadOnlySpan<byte> line, int name, int mask, bool keepText)
    {
        if (d.PastesParam()) _directPasters.Add((name, mask));
        var body = d.Body;
        bool alias = !d.IsFunc && body.Count > 0 && body[^1].Kind == Tok.Ident;
        // A function-like body with a call forwards to every identifier in it: a paste macro it names without a call
        // (`APPLY2(CAT, a)`) is reached through the call.
        if (d.IsFunc && body.Any(t => t.Kind == Tok.LParen))
        {
            for (int k = 0; k < body.Count; k++)
                if (body[k].Kind == Tok.Ident && !d.IsParam(d.Text(k))) Edge(name, Names.GetOrAdd(d.Text(k)), mask);
        }
        else if (alias) Edge(name, Names.GetOrAdd(d.Text(body.Count - 1)), mask);
        var s = Names.Get(name);
        if (s.Length >= 2 && s[0] == '_' && s[1] == '_') _definedUnder.Add(name);
        EdgeList(d, name, mask);
        if (keepText && (d.IsFunc || alias))
        {
            if (!_ncByName.TryGetValue(name, out var l)) _ncByName[name] = l = new();
            l.Add(_ncDefs.Count);
            _ncDefs.Add((name, line.ToArray(), mask));
        }
    }

    private void Edge(int from, int to, int mask)
    {
        _edgeFrom.Add(from);
        _edgeTo.Add(to);
        _edgeMask.Add(mask);
    }

    private readonly IntList _edgeS = new(), _edgeE = new();

    /// <summary>Rule 2's edge pieces of one body: walking in from the end gives the last edges (S), from the start the
    /// first edges (E). An identifier is one (a parameter is not) and the walk goes on past it; a call gives its name
    /// and every identifier and number among its arguments, and the walk goes on; <c>##</c> is skipped; a number
    /// ends the walk (a piece if it is identifier characters); anything else ends it.</summary>
    private void EdgeList(DefineParse d, int name, int mask)
    {
        var b = d.Body;
        _edgeS.Count = _edgeE.Count = 0;
        for (int k = b.Count - 1; k >= 0;)
        {
            var kind = b[k].Kind;
            if (kind == Tok.HashHash) { k--; continue; }
            if (kind == Tok.Ident) { EdgeToken(d, k, _edgeS); k--; continue; }
            if (kind == Tok.Number) { EdgeToken(d, k, _edgeS); break; }
            if (kind != Tok.RParen) break;
            int open = k, depth = 0;
            for (; open >= 0; open--)
            {
                if (b[open].Kind == Tok.RParen) depth++;
                else if (b[open].Kind == Tok.LParen && --depth == 0) break;
            }
            if (open <= 0 || b[open - 1].Kind != Tok.Ident) break;
            for (int j = open - 1; j < k; j++) EdgeToken(d, j, _edgeS);
            k = open - 2;
        }
        for (int k = 0; k < b.Count;)
        {
            var kind = b[k].Kind;
            if (kind == Tok.HashHash) { k++; continue; }
            if (kind == Tok.Number) { EdgeToken(d, k, _edgeE); break; }
            if (kind != Tok.Ident) break;
            if (k + 1 >= b.Count || b[k + 1].Kind != Tok.LParen) { EdgeToken(d, k, _edgeE); k++; continue; }
            int close = k + 1, depth = 0;
            for (; close < b.Count; close++)
            {
                if (b[close].Kind == Tok.LParen) depth++;
                else if (b[close].Kind == Tok.RParen && --depth == 0) break;
            }
            for (int j = k; j < close && j < b.Count; j++) EdgeToken(d, j, _edgeE);
            k = close + 1;
        }
        if (_edgeS.Count == 0 && _edgeE.Count == 0) return;
        _xName.Add(name);
        _xMask.Add(mask);
        _xOff.Add(_xItems.Count);
        _xItems.Add(_edgeS.Count);
        for (int i = 0; i < _edgeS.Count; i++) _xItems.Add(_edgeS[i]);
        _xItems.Add(_edgeE.Count);
        for (int i = 0; i < _edgeE.Count; i++) _xItems.Add(_edgeE[i]);
    }

    private void EdgeToken(DefineParse d, int k, IntList to)
    {
        var kind = d.Body[k].Kind;
        var t = d.Text(k);
        if ((kind == Tok.Ident && !d.IsParam(t)) || (kind == Tok.Number && IsIdNumber(t))) to.Add(Names.GetOrAdd(t));
    }

    /// <summary>A pp-number made only of identifier characters (<c>1</c>, <c>0x1F</c>, <c>2u</c>; not <c>1.5</c>).</summary>
    public static bool IsIdNumber(ReadOnlySpan<byte> s)
    {
        foreach (byte c in s) if (!FwLexer.IsIdChar(c)) return false;
        return s.Length > 0;
    }

    /// <summary>Rule 1 to a fixpoint for one run: a name forwarding to a paste macro is one.</summary>
    private void PasterFixpoint(int run)
    {
        int n = Names.Count, bit = 1 << run;
        var start = new int[n + 1];
        for (int k = 0; k < _edgeTo.Count; k++) if ((_edgeMask[k] & bit) != 0) start[_edgeTo[k] + 1]++;
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var callers = new int[start[n]];
        var fill = (int[])start.Clone();
        for (int k = 0; k < _edgeTo.Count; k++) if ((_edgeMask[k] & bit) != 0) callers[fill[_edgeTo[k]]++] = _edgeFrom[k];
        var work = new Stack<int>();
        foreach (var (p, m) in _directPasters) if ((m & bit) != 0 && Names.Set(p, PasterFlag[run])) work.Push(p);
        while (work.Count > 0)
        {
            int x = work.Pop();
            for (int k = start[x]; k < start[x + 1]; k++)
                if (Names.Set(callers[k], PasterFlag[run])) work.Push(callers[k]);
        }
    }

    public bool IsPaster(int id, int run) => Names.Has(id, PasterFlag[run]);

    // ---------------- rule 2: a paste macro's argument sides ----------------

    /// <summary>A slot's sides beyond Start/End: the argument sits inside a chain, so only its expansion joins.</summary>
    public const int Interior = 4;

    /// <summary>A paste macro's sides per argument position; past the end, <see cref="Rest"/>.</summary>
    private sealed class Sig
    {
        public int[] Pos = Array.Empty<int>();
        public int Rest;
    }

    /// <summary>One definition of a paste macro: its parameters' own sides and the slots they are passed on to.</summary>
    private sealed class DefSig
    {
        public int Owner, AliasOf = -1, NParams;
        public bool Variadic;
        public int[] Const = Array.Empty<int>();
        public readonly List<(int Param, int Q, int K, bool FromK)> Refs = new();
    }

    private readonly Dictionary<int, Sig>?[] _sigs = new Dictionary<int, Sig>?[2];

    /// <summary>The sides (Start, End, <see cref="Interior"/>) argument <paramref name="arg"/> of a call to
    /// <paramref name="paster"/> takes under <paramref name="run"/>.</summary>
    public int SlotOf(int paster, int arg, int run)
    {
        var sigs = _sigs[run] ??= Sigs(run);
        if (!sigs.TryGetValue(paster, out var s)) return Tracker.Both;
        return arg < s.Pos.Length ? s.Pos[arg] : s.Rest;
    }

    private Dictionary<int, Sig> Sigs(int run)
    {
        var defs = new List<DefSig>();
        var d = new DefineParse();
        var buf = new byte[4096];
        int bit = 1 << run;
        for (int id = 0; id < Names.Count; id++)
        {
            if (!IsPaster(id, run)) continue;
            if (id < _cHead.Length)
                for (int k = _cHead[id]; k >= 0; k = _cdNext[k])
                {
                    int len = _cdLenTerm[k] >> 2;
                    int cf = CFile(k, out long off);
                    if (buf.Length < len) buf = new byte[Math.Max(len, buf.Length * 2)];
                    RandomAccess.Read(Handle(cf), buf.AsSpan(0, len), off);
                    var w = new LineWalker(buf.AsSpan(0, len), 0);
                    if (w.Next() && d.Parse(w.Spliced)) defs.Add(Analyze(d, id, run));
                }
            if (_ncByName.TryGetValue(id, out var l))
                foreach (int k in l)
                    if ((_ncDefs[k].Mask & bit) != 0 && d.Parse(_ncDefs[k].Text)) defs.Add(Analyze(d, id, run));
        }
        var sigs = new Dictionary<int, Sig>();
        foreach (var ds in defs) if (!sigs.ContainsKey(ds.Owner)) sigs[ds.Owner] = new Sig();
        var vals = new List<int>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var ds in defs)
            {
                var s = sigs[ds.Owner];
                if (ds.AliasOf >= 0)
                {
                    if (sigs.TryGetValue(ds.AliasOf, out var q)) changed |= Union(s, q.Pos.ToArray(), q.Rest);
                    else changed |= Union(s, Array.Empty<int>(), Tracker.Both);
                    continue;
                }
                if (ds.NParams == 0 && !ds.Variadic) continue;
                vals.Clear();
                for (int i = 0; i < ds.NParams; i++) vals.Add(ds.Const[i]);
                foreach (var (p, q, k, fromK) in ds.Refs) vals[p] |= SlotRange(sigs, q, k, fromK);
                int fixedN = ds.Variadic ? ds.NParams - 1 : ds.NParams;
                changed |= Union(s, vals.Take(fixedN).ToArray(), ds.Variadic ? vals[ds.NParams - 1] : Tracker.Both);
            }
        }
        return sigs;
    }

    private static int SlotRange(Dictionary<int, Sig> sigs, int q, int k, bool fromK)
    {
        if (!sigs.TryGetValue(q, out var s)) return Tracker.Both;
        if (!fromK) return k < s.Pos.Length ? s.Pos[k] : s.Rest;
        int v = s.Rest;
        for (int i = k; i < s.Pos.Length; i++) v |= s.Pos[i];
        return v;
    }

    private static bool Union(Sig s, int[] pos, int rest)
    {
        if (pos.Length > s.Pos.Length)
        {
            var np = new int[pos.Length];
            for (int i = 0; i < np.Length; i++) np[i] = i < s.Pos.Length ? s.Pos[i] : s.Rest;
            s.Pos = np;
        }
        bool changed = false;
        for (int i = 0; i < s.Pos.Length; i++)
        {
            int v = s.Pos[i] | (i < pos.Length ? pos[i] : rest);
            if (v != s.Pos[i]) { s.Pos[i] = v; changed = true; }
        }
        if ((s.Rest | rest) != s.Rest) { s.Rest |= rest; changed = true; }
        return changed;
    }

    private static int ParamIndex(DefineParse d, int k)
    {
        if (d.Body[k].Kind != Tok.Ident) return -1;
        var t = d.Text(k);
        for (int i = 0; i < d.Params.Count; i++) if (t.SequenceEqual(d.Params[i])) return i;
        return -1;
    }

    /// <summary>A parameter's sides from its <c>##</c> chains (one at neither end is interior, unless variadic: both),
    /// and the slots of the paste macros it is passed to whole; passed as part of a longer argument, or to a computed
    /// or parameter-named call, it gets both.</summary>
    private DefSig Analyze(DefineParse d, int owner, int run)
    {
        var ds = new DefSig { Owner = owner, NParams = d.Params.Count, Variadic = d.Variadic, Const = new int[d.Params.Count] };
        var b = d.Body;
        if (!d.IsFunc)
        {
            if (b.Count > 0 && b[^1].Kind == Tok.Ident)
            {
                int q = Names.Find(d.Text(b.Count - 1));
                if (q >= 0 && IsPaster(q, run)) ds.AliasOf = q;
            }
            return ds;
        }
        int va = d.Variadic ? d.Params.Count - 1 : -1;
        var bare = new List<int>();   // paste macros named without a call: a non-paster call's arguments may reach them
        for (int k = 0; k < b.Count; k++)
        {
            if (b[k].Kind != Tok.Ident || (k + 1 < b.Count && b[k + 1].Kind == Tok.LParen) || ParamIndex(d, k) >= 0) continue;
            int q = Names.Find(d.Text(k));
            if (q >= 0 && IsPaster(q, run) && !bare.Contains(q)) bare.Add(q);
        }
        var ops = new List<int>();
        for (int k = 0; k < b.Count;)
        {
            if (k + 2 >= b.Count || b[k + 1].Kind != Tok.HashHash) { k++; continue; }
            ops.Clear();
            int j = k;
            ops.Add(j);
            while (j + 2 < b.Count && b[j + 1].Kind == Tok.HashHash) { j += 2; ops.Add(j); }
            k = j + 1;
            if (ops.Count == 2 && b[ops[0]].Kind == Tok.Comma && va >= 0 && ParamIndex(d, ops[1]) == va) continue;   // , ## __VA_ARGS__
            int n = ops.Count, fnp = 0, lnp = n - 1;
            while (fnp < n && ParamIndex(d, ops[fnp]) >= 0) fnp++;
            while (lnp >= 0 && ParamIndex(d, ops[lnp]) >= 0) lnp--;
            for (int i = 0; i < n; i++)
            {
                int p = ParamIndex(d, ops[i]);
                if (p < 0) continue;
                int sides = (i <= fnp ? Tracker.S : 0) | (i >= lnp ? Tracker.E : 0);
                ds.Const[p] |= sides != 0 ? sides : p == va ? Tracker.Both : Interior;
            }
        }
        for (int k = 1; k < b.Count; k++)
        {
            if (b[k].Kind != Tok.LParen) continue;
            bool unknown = b[k - 1].Kind == Tok.RParen;   // computed
            int q = -1;
            if (!unknown)
            {
                if (b[k - 1].Kind != Tok.Ident) continue;
                if (ParamIndex(d, k - 1) >= 0) unknown = true;
                else
                {
                    q = Names.Find(d.Text(k - 1));
                    if (q < 0 || !IsPaster(q, run))
                    {
                        if (bare.Count == 0) continue;
                        q = -1;   // a higher-order call: its arguments take the union of the named paste macros' slots
                    }
                }
            }
            int depth = 0, argStart = k + 1, arg = 0;
            for (int j = k + 1; ; j++)
            {
                var kind = j < b.Count ? b[j].Kind : Tok.RParen;
                if (j < b.Count && kind == Tok.LParen) { depth++; continue; }
                if (j < b.Count && kind == Tok.RParen && depth > 0) { depth--; continue; }
                if (depth > 0 || (kind != Tok.Comma && kind != Tok.RParen)) continue;
                if (unknown || j - argStart != 1)
                {
                    for (int t = argStart; t < j; t++)
                    {
                        int p = ParamIndex(d, t);
                        if (p >= 0) ds.Const[p] |= Tracker.Both;
                    }
                }
                else
                {
                    int p = ParamIndex(d, argStart);
                    if (p >= 0 && q >= 0) ds.Refs.Add((p, q, arg, p == va));
                    else if (p >= 0) foreach (int r in bare) ds.Refs.Add((p, r, 0, true));
                }
                if (kind == Tok.RParen) break;
                argStart = j + 1;
                arg++;
            }
        }
        return ds;
    }

    private void SortNames()
    {
        var c = new List<int>();
        for (int i = 0; i < Names.Count; i++) if (Names.Has(i, NameTable.CName)) c.Add(i);
        _sortedFwd = c.ToArray();
        _sortedRev = c.ToArray();
        Array.Sort(_sortedFwd, Comparer<int>.Create((a, b) => Names.Compare(a, b, false)));
        Array.Sort(_sortedRev, Comparer<int>.Create((a, b) => Names.Compare(a, b, true)));
    }

    // ---------------- pass 2: seeds and pieces ----------------

    private void Seed(ReadOnlySpan<byte> s)
    {
        int id = Names.Find(s);
        if (id >= 0 && Names.Has(id, NameTable.CName)) Names.Set(id, _seedFlag);
    }

    private void SeedText(ReadOnlySpan<byte> s) => FwLexer.RawScan(s, Seed);
    private ushort _seedFlag = NameTable.Seed;

    private void Seeds()
    {
        foreach (var u in _units) foreach (var d in u.DefineArgs) SeedText(Encoding.UTF8.GetBytes(d));
        foreach (var r in Reserved) Seed(Encoding.ASCII.GetBytes(r));
        for (int i = 0; i < Names.Count; i++)
        {
            if (!Names.Has(i, NameTable.CName)) continue;
            var s = Names.Get(i);
            if (s.Length >= 2 && s[0] == '_' && (s[1] == '_' || (uint)(s[1] - 'A') < 26)) Names.Set(i, NameTable.Seed);
        }
    }

    /// <summary>One stream's trackers for the runs in a mask: min lexing per run, and under max the shadow lexing too
    /// (comments and strings are tokens; calls open at the end commit).</summary>
    private sealed class Streams : ITokenSink
    {
        private readonly Tracker? _min0, _min1, _shadow;

        public Streams(FwSpec a, int runs)
        {
            if ((runs & 1) != 0) _min0 = new Tracker(a, 0, commitAtEof: false);
            if ((runs & 2) == 0) return;
            _min1 = new Tracker(a, 1, commitAtEof: false);
            _shadow = new Tracker(a, 1, commitAtEof: true);
        }

        public void Token(Tok kind, ReadOnlySpan<byte> text)
        {
            _min0?.Token(kind, text);
            _min1?.Token(kind, text);
        }

        public void Begin(DefineParse? d) { _min0?.Begin(d); _min1?.Begin(d); _shadow?.Begin(d); }
        public void Directive(LineKind k) { _min0?.Directive(k); _min1?.Directive(k); _shadow?.Directive(k); }
        public void End() { _min0?.End(); _min1?.End(); _shadow?.End(); }

        public void Lex(ReadOnlySpan<byte> s, bool startsInComment)
        {
            bool ib = startsInComment;
            FwLexer.Lex(s, ref ib, false, this);
            if (_shadow is not null) FwLexer.Lex(s, ref ib, true, _shadow);
        }
    }

    private void Pass2()
    {
        var main = new Streams(this, 3);
        var body = new Streams(this, 3);
        var d = new DefineParse();
        var kept = new List<int>();
        for (int f = 0; f < _fwCount; f++)
        {
            if (_binary[f]) continue;
            if (!IsCExt(_files[f]))
            {
                var text = FwLexer.TextBytes(File.ReadAllBytes(_files[f]), out int st);
                SeedText(text.AsSpan(st));
                continue;
            }
            if (_isC[f]) CarvedStream(f, main);
            else kept.Add(f);
        }
        kept.AddRange(ExtClosure());
        foreach (int f in kept) KeptFile(f, main, body, d);
        foreach (var line in _dashDefines)
        {
            if (!d.Parse(line)) continue;
            body.Begin(d);
            body.Lex(line.AsSpan(d.BodyStart), false);
            body.End();
        }
        var maybeMain = new Streams(this, 2);
        var maybeBody = new Streams(this, 2);
        foreach (int f in _maybe) KeptFile(f, maybeMain, maybeBody, d, maybe: true);
    }

    /// <summary>A kept file: all of it seeds; its non-define lines are one stream and each define body its own. A
    /// <paramref name="maybe"/> file (see <see cref="MaybeKept"/>) seeds and gives pieces under max only.</summary>
    private void KeptFile(int f, Streams main, Streams body, DefineParse d, bool maybe = false)
    {
        if (maybe && FwLexer.LooksBinary(_files[f])) return;
        var text = FwLexer.TextBytes(File.ReadAllBytes(_files[f]), out int st);
        _seedFlag = maybe ? SeedMax : NameTable.Seed;
        SeedText(text.AsSpan(st));
        _seedFlag = NameTable.Seed;
        main.Begin(null);
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind == LineKind.Define && d.Parse(w.Spliced))
            {
                body.Begin(d);
                body.Lex(w.Spliced[d.BodyStart..], false);
                body.End();
                continue;
            }
            StreamLine(ref w, main);
        }
        main.End();
    }

    /// <summary>A carved file: its non-define lines seed and are one stream; its defines were recorded in pass 1.</summary>
    private void CarvedStream(int f, Streams main)
    {
        byte[] text = File.ReadAllBytes(_files[f]);
        int st = text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF ? 3 : 0;
        main.Begin(null);
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind == LineKind.Define && IsNamedDefine(w.Spliced)) continue;
            SeedText(w.Raw);
            StreamLine(ref w, main);
        }
        main.End();
    }

    private readonly DefineParse _probe = new();
    private bool IsNamedDefine(ReadOnlySpan<byte> s) => _probe.Parse(s);

    private static void StreamLine(ref LineWalker w, Streams main)
    {
        switch (w.Kind)
        {
            case LineKind.Cond:
            case LineKind.Else:
            case LineKind.EndIf:
                main.Directive(w.Kind);
                return;
            case LineKind.OtherDirective:
            case LineKind.Define:
                return;
        }
        main.Lex(w.Spliced, w.StartsInComment);
    }

    // ---------------- pieces, expansion, wanted ----------------

    private readonly Stack<(int Id, int Dir)> _expand = new();
    private readonly bool[] _digitEnd = new bool[2], _anyEnd = new bool[2];
    private readonly bool[][] _startLen = { new bool[64], new bool[64] }, _endLen = { new bool[64], new bool[64] };

    /// <summary>A piece with <paramref name="sides"/> (<see cref="Tracker.S"/>, <see cref="Tracker.E"/>) under
    /// <paramref name="run"/>; its definitions' edges join in <paramref name="dirs"/>: the last edges (Start) as start
    /// pieces, the first edges (End) as end pieces, transitively.</summary>
    public void Piece(int id, int run, int sides, int dirs)
    {
        AddPiece(id, run, sides);
        int bit = 1 << run;
        if ((dirs & Tracker.S) != 0) _expand.Push((id, Tracker.S));
        if ((dirs & Tracker.E) != 0) _expand.Push((id, Tracker.E));
        while (_expand.Count > 0)
        {
            var (x, dir) = _expand.Pop();
            if (!Names.Set(x, dir == Tracker.S ? ExpSFlag[run] : ExpEFlag[run]) || x >= _xHead.Length) continue;
            for (int k = _xHead[x]; k >= 0; k = _xNext[k])
            {
                if ((_xMask[k] & bit) == 0) continue;
                int o = _xOff[k], nS = _xItems[o];
                int from = dir == Tracker.S ? o + 1 : o + 2 + nS, n = dir == Tracker.S ? nS : _xItems[o + 1 + nS];
                for (int i = from; i < from + n; i++)
                {
                    AddPiece(_xItems[i], run, dir);
                    _expand.Push((_xItems[i], dir));
                }
            }
        }
    }

    private void AddPiece(int id, int run, int sides)
    {
        int len = Names.Get(id).Length;
        if ((sides & Tracker.S) != 0 && Names.Set(id, StartFlag[run]))
        {
            MarkLen(ref _startLen[run], len);
            _pieceQueue[run].Add(id << 1);
        }
        if ((sides & Tracker.E) != 0 && Names.Set(id, EndFlag[run]))
        {
            MarkLen(ref _endLen[run], len);
            _pieceQueue[run].Add(id << 1 | 1);
            Wildcard(id, run);
        }
    }

    private static void MarkLen(ref bool[] lens, int len)
    {
        if (len >= lens.Length) Array.Resize(ref lens, Math.Max(len + 1, lens.Length * 2));
        lens[len] = true;
    }

    /// <summary>An end piece that stands for many: pasting <c>__LINE__</c> or <c>__COUNTER__</c> ends a name in any
    /// digits, and an undefined <c>__x</c> (a compiler-predefined name) in anything.</summary>
    private void Wildcard(int id, int run)
    {
        var s = Names.Get(id);
        if (s.Length < 2 || s[0] != '_' || s[1] != '_') return;
        if (s.SequenceEqual("__LINE__"u8) || s.SequenceEqual("__COUNTER__"u8))
        {
            if (_digitEnd[run]) return;
            _digitEnd[run] = true;
        }
        else
        {
            if (_anyEnd[run] || _definedUnder.Contains(id)) return;
            _anyEnd[run] = true;
        }
        _pieceQueue[run].Add(-1);   // every name again
    }

    private void Want(int id, int run)
    {
        if (id >= 0 && Names.Has(id, NameTable.CName) && Names.Set(id, WantFlag[run])) _wantQueue[run].Add(id);
    }

    private void Fixpoint(int run)
    {
        ushort seeds = run == 0 ? NameTable.Seed : (ushort)(NameTable.Seed | SeedMax);
        for (int i = 0; i < Names.Count; i++) if (Names.Has(i, seeds)) Want(i, run);
        var streams = new Streams(this, 1 << run);
        var d = new DefineParse();
        var pq = _pieceQueue[run];
        var wq = _wantQueue[run];
        int pi = 0, wi = 0;
        var buf = new byte[4096];
        while (pi < pq.Count || wi < wq.Count)
        {
            while (pi < pq.Count) Directional(pq[pi++], run);
            while (wi < wq.Count)
            {
                int name = wq[wi++];
                for (int k = _cHead[name]; k >= 0; k = _cdNext[k])
                {
                    int len = _cdLenTerm[k] >> 2;
                    int cf = CFile(k, out long off);
                    if (buf.Length < len) buf = new byte[Math.Max(len, buf.Length * 2)];
                    RandomAccess.Read(Handle(cf), buf.AsSpan(0, len), off);
                    var w = new LineWalker(buf.AsSpan(0, len), 0);
                    if (!w.Next() || !d.Parse(w.Spliced)) continue;
                    var b = w.Spliced[d.BodyStart..];
                    FwLexer.RawScan(b, s =>
                    {
                        if (d.IsParam(s)) return;
                        Want(Names.Find(s), run);
                    });
                    streams.Begin(d);
                    streams.Lex(b, false);
                    streams.End();
                }
                if (pi < pq.Count) break;
            }
        }
    }

    /// <summary>The carved file holding C def <paramref name="k"/>, and the def's offset in it.</summary>
    private int CFile(int k, out long off)
    {
        long x = _cdOff[k];
        int lo = 0, hi = _cBase.Count;   // the last file starting at or before x (an empty file holds no def)
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (_cBase[mid] <= x) lo = mid; else hi = mid; }
        off = x - _cBase[lo];
        return _cBaseFile[lo];
    }

    private SafeFileHandle Handle(int f)
    {
        if (!_handles.TryGetValue(f, out var h))
            _handles[f] = h = File.OpenHandle(_files[f], FileMode.Open, FileAccess.Read, FileShare.Read);
        return h;
    }

    /// <summary>Rule 3 for a new piece: a C name is wanted when it starts with a start piece and ends with an end
    /// piece (one piece equal to the name counts when it is both). A new start piece checks the names it begins, a
    /// new end piece the names it ends; a wildcard (-1) checks every name.</summary>
    private void Directional(int item, int run)
    {
        if (item < 0)
        {
            foreach (int n in _sortedFwd)
                if (!Names.Has(n, WantFlag[run]) && StartOk(Names.Get(n), run) && EndOk(Names.Get(n), run)) Want(n, run);
            return;
        }
        var p = Names.Get(item >> 1).ToArray();
        if (p.Length == 0) return;
        if ((item & 1) == 0)
        {
            int lo = 0, hi = _sortedFwd.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Names.Get(_sortedFwd[mid]).SequenceCompareTo(p) < 0) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < _sortedFwd.Length; i++)
            {
                int n = _sortedFwd[i];
                var s = Names.Get(n);
                if (!s.StartsWith(p)) break;
                if (!Names.Has(n, WantFlag[run]) && EndOk(s, run)) Want(n, run);
            }
        }
        else
        {
            int lo = 0, hi = _sortedRev.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (NameTable.CompareReversed(Names.Get(_sortedRev[mid]), p) < 0) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < _sortedRev.Length; i++)
            {
                int n = _sortedRev[i];
                var s = Names.Get(n);
                if (!s.EndsWith(p)) break;
                if (!Names.Has(n, WantFlag[run]) && StartOk(s, run)) Want(n, run);
            }
        }
    }

    private bool StartOk(ReadOnlySpan<byte> n, int run)
    {
        var lens = _startLen[run];
        for (int j = 1; j <= n.Length && j < lens.Length; j++)
        {
            if (!lens[j]) continue;
            int id = Names.Find(n[..j]);
            if (id >= 0 && Names.Has(id, StartFlag[run])) return true;
        }
        return false;
    }

    private bool EndOk(ReadOnlySpan<byte> n, int run)
    {
        if (_anyEnd[run] || (_digitEnd[run] && n.Length > 0 && (uint)(n[^1] - '0') < 10)) return true;
        var lens = _endLen[run];
        for (int j = 1; j <= n.Length && j < lens.Length; j++)
        {
            if (!lens[j]) continue;
            int id = Names.Find(n[^j..]);
            if (id >= 0 && Names.Has(id, EndFlag[run])) return true;
        }
        return false;
    }

    // ---------------- results ----------------

    private void Results(FwTruth t, string? listDir, bool checkAlphabet)
    {
        // per-def counts
        for (int k = 0; k < _cdName.Count; k++)
        {
            int n = _cdName[k];
            bool keep = Names.Has(n, NameTable.WantMin), drop = !Names.Has(n, NameTable.WantMax);
            t.TotalDefineLines++;
            if (drop) t.DefinesDroppedMin++;
            if (!keep) t.DefinesDroppedMax++;
            int lt = _cdLenTerm[k];
            if (!keep) t.BytesAfterMin += lt >> 2;
            if (drop) t.BytesAfterMax += (lt >> 2) - (lt & 3);
        }
        for (int f = 0; f < _fwCount; f++) if (_isC[f]) t.BytesBefore += new FileInfo(_files[f]).Length;
        t.BytesAfterMin = t.BytesBefore - t.BytesAfterMin;
        t.BytesAfterMax = t.BytesBefore - t.BytesAfterMax;

        using var keepW = new ListWriter(listDir, "mustKeepDefines");
        using var dropW = new ListWriter(listDir, "mustDropDefines");
        using var openW = new ListWriter(listDir, "openDefines");
        foreach (int n in _sortedFwd)
        {
            if (Names.Has(n, NameTable.Paster)) t.PasteMacros++;
            if (Names.Has(n, FwdC)) t.ForwardingMacros++;
            var s = Names.Get(n);
            int cls = Names.Has(n, NameTable.WantMin) ? 0 : Names.Has(n, NameTable.WantMax) ? 2 : 1;
            (cls == 0 ? keepW : cls == 1 ? dropW : openW).Add(s);
            if (checkAlphabet)
            {
                int want = s.Length >= 2 && s[0] == 'Z' && s[^1] == 'Z' ? 1 : s.Length >= 2 && s[0] == 'Y' && s[^1] == 'Y' ? 2 : 0;
                if (want != cls && t.AlphabetErrors.Count < 50)
                    t.AlphabetErrors.Add($"{Encoding.ASCII.GetString(s)}: {ClassName(cls)}, alphabet says {ClassName(want)}");
            }
        }
        t.MustKeepCount = keepW.Count;
        t.MustDropCount = dropW.Count;
        t.OpenCount = openW.Count;
        t.MustKeepDefines = keepW.Finish();
        t.MustDropDefines = dropW.Finish();
        t.OpenDefines = openW.Finish();
        using (var fnW = new ListWriter(listDir, "mustKeepFunctions")) t.MustKeepFunctions = fnW.Finish();

        // files: defining a must-keep name, the fw units, and their closures
        var files = new SortedSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < _cdName.Count; k++) if (Names.Has(_cdName[k], NameTable.WantMin)) files.Add(_rel[CFile(k, out _)]);
        for (int k = 0; k < _fwDefName.Count; k++) if (Names.Has(_fwDefName[k], NameTable.WantMin)) files.Add(_rel[_fwDefFile[k]]);
        foreach (int f in _closure) if (f < _fwCount) files.Add(_rel[f]);
        using (var fw = new ListWriter(listDir, "mustKeepFiles"))
        {
            foreach (var p in files) fw.Add(Encoding.UTF8.GetBytes(p));
            t.MustKeepFiles = fw.Finish();
        }
    }

    private static string ClassName(int c) => c == 0 ? "keep" : c == 1 ? "drop" : "open";
}

/// <summary>
/// Writes one truth list as <c>main.NAME.txt</c> (LF, sorted by the caller) and copies it to <c>union.NAME.txt</c>;
/// past 100 MB both are gzipped. The sha256 is over the uncompressed text either way. Without a directory it
/// only hashes.
/// </summary>
internal sealed class ListWriter : IDisposable
{
    public const long GzipPast = 100L << 20;
    private readonly string? _dir, _name;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly FileStream? _out;
    private readonly string? _tmp;
    private long _bytes;
    public long Count { get; private set; }
    private static readonly byte[] Lf = { (byte)'\n' };

    public ListWriter(string? dir, string name)
    {
        _dir = dir;
        _name = name;
        if (dir is null) return;
        _tmp = Path.Combine(dir, $"main.{name}.txt.tmp");
        _out = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
    }

    public void Add(ReadOnlySpan<byte> line)
    {
        _sha.AppendData(line);
        _sha.AppendData(Lf);
        _out?.Write(line);
        _out?.Write(Lf);
        _bytes += line.Length + 1;
        Count++;
    }

    public FwList Finish()
    {
        string sha = Convert.ToHexString(_sha.GetHashAndReset()).ToLowerInvariant();
        if (_out is null) return new FwList(null, sha, Count);
        _out.Dispose();
        string main = Path.Combine(_dir!, $"main.{_name}.txt"), union = Path.Combine(_dir!, $"union.{_name}.txt");
        string rel;
        if (_bytes > GzipPast)
        {
            foreach (var target in new[] { main + ".gz", union + ".gz" })
            {
                using var src = File.OpenRead(_tmp!);
                using var dst = File.Create(target);
                using var gz = new System.IO.Compression.GZipStream(dst, System.IO.Compression.CompressionLevel.Fastest);
                src.CopyTo(gz);
            }
            File.Delete(_tmp!);
            rel = $"main.{_name}.txt.gz";
        }
        else
        {
            File.Copy(_tmp!, union, true);
            File.Move(_tmp!, main, true);
            rel = $"main.{_name}.txt";
        }
        return new FwList(rel, sha, Count);
    }

    public void Dispose()
    {
        _out?.Dispose();
        _sha.Dispose();
    }
}
