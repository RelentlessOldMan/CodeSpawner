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

    public readonly NameTable Names = new(1 << 16);
    private readonly string _root, _fwRoot;

    // files: fw files (sorted, fw-relative) then ext closure files
    private readonly List<string> _files = new();        // full paths
    private readonly List<string> _rel = new();          // fw-relative (fw files) or corpus-relative (ext)
    private int _fwCount;
    private bool[] _isK = Array.Empty<bool>(), _isC = Array.Empty<bool>(), _binary = Array.Empty<bool>();

    // C defs (struct of arrays)
    private readonly IntList _cdName = new(), _cdFile = new(), _cdLen = new(), _cdTerm = new(), _cdNext = new();
    private long[] _cdOff = new long[1024];
    private int[] _cHead = Array.Empty<int>();            // name -> first C def (-1)

    // all defs: object-like first/last ident (S5), forward edges, direct pasters
    private readonly IntList _objName = new(), _objFirst = new(), _objLast = new(), _objNext = new();
    private int[] _objHead = Array.Empty<int>();
    private readonly IntList _edgeFrom = new(), _edgeTo = new();
    private readonly IntList _fwDefFile = new(), _fwDefName = new();   // defs in kept (non-C) fw files

    // the build
    private readonly List<Unit> _units = new();
    private readonly HashSet<int> _closure = new();       // file indices (fw and ext) in any unit's closure
    private readonly List<int> _wholeFromOutside = new();

    private int[] _sortedFwd = Array.Empty<int>(), _sortedRev = Array.Empty<int>();
    private readonly IntList[] _pieceQueue = { new(), new() };
    private readonly IntList[] _wantQueue = { new(), new() };
    private static readonly ushort[] PieceFlag = { NameTable.PieceMin, NameTable.PieceMax };
    private static readonly ushort[] WantFlag = { NameTable.WantMin, NameTable.WantMax };
    private static readonly ushort[] S5Flag = { NameTable.S5Min, NameTable.S5Max };
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
        foreach (int f in _wholeFromOutside) _isC[f] = false;
        t.WholeFromOutside = _wholeFromOutside.Select(f => _rel[f]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        t.WholeIncludedFromOutside = _wholeFromOutside.Count;
        for (int f = 0; f < _fwCount; f++) if (_isC[f]) t.Carved.Add(_rel[f]);

        Pass1();
        PasterFixpoint();
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
            int i = s.IndexOf((byte)'#') + 1;
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

    // ---------------- pass 1: definitions ----------------

    private void Pass1()
    {
        var d = new DefineParse();
        var files = Enumerable.Range(0, _fwCount).Where(i => IsCExt(_files[i]) && !_binary[i]).Concat(ExtClosure());
        foreach (int f in files)
        {
            byte[] raw = File.ReadAllBytes(_files[f]);
            var text = FwLexer.TextBytes(raw, out int st);
            bool isC = f < _fwCount && _isC[f];
            var w = new LineWalker(text, st);
            while (w.Next())
            {
                if (w.Kind != LineKind.Define || !d.Parse(w.Spliced)) continue;
                int name = Names.GetOrAdd(w.Spliced.Slice(d.NameStart, d.NameLen));
                RecordDef(d, name);
                if (isC)
                {
                    Names.Set(name, NameTable.CName);
                    int k = _cdName.Count;
                    _cdName.Add(name);
                    _cdFile.Add(f);
                    if (k == _cdOff.Length) Array.Resize(ref _cdOff, k * 2);
                    _cdOff[k] = w.Start;      // offset in the file (no BOM shift: text == raw for C files)
                    _cdLen.Add(w.Length);
                    _cdTerm.Add(w.TermBytes);
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
        _cHead = Enumerable.Repeat(-1, Names.Count).ToArray();
        for (int k = _cdName.Count - 1; k >= 0; k--) { _cdNext.Add(0); }
        for (int k = _cdName.Count - 1; k >= 0; k--)
        {
            int n = _cdName[k];
            _cdNext.A[k] = _cHead[n];
            _cHead[n] = k;
        }
        _objHead = Enumerable.Repeat(-1, Names.Count).ToArray();
        for (int k = 0; k < _objName.Count; k++) _objNext.Add(-1);
        for (int k = _objName.Count - 1; k >= 0; k--)
        {
            int n = _objName[k];
            _objNext.A[k] = _objHead[n];
            _objHead[n] = k;
        }
    }

    private readonly List<int> _directPasters = new();

    private void RecordDef(DefineParse d, int name)
    {
        if (d.PastesParam()) _directPasters.Add(name);
        var body = d.Body;
        if (d.IsFunc)
        {
            for (int k = 0; k + 1 < body.Count; k++)
                if (body[k].Kind == Tok.Ident && body[k + 1].Kind == Tok.LParen && !d.IsParam(d.Text(k)))
                {
                    _edgeFrom.Add(name);
                    _edgeTo.Add(Names.GetOrAdd(d.Text(k)));
                }
        }
        else if (body.Count > 0)
        {
            if (body[^1].Kind == Tok.Ident)
            {
                _edgeFrom.Add(name);
                _edgeTo.Add(Names.GetOrAdd(d.Text(body.Count - 1)));
            }
            int first = body[0].Kind == Tok.Ident ? Names.GetOrAdd(d.Text(0)) : -1;
            int last = body[^1].Kind == Tok.Ident ? Names.GetOrAdd(d.Text(body.Count - 1)) : -1;
            if (first >= 0 || last >= 0)
            {
                _objName.Add(name);
                _objFirst.Add(first);
                _objLast.Add(last);
            }
        }
    }

    /// <summary>Rule 1 to a fixpoint: a name forwarding to a paste macro is one.</summary>
    private void PasterFixpoint()
    {
        int n = Names.Count;
        var start = new int[n + 1];
        for (int k = 0; k < _edgeTo.Count; k++) start[_edgeTo[k] + 1]++;
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var callers = new int[_edgeTo.Count];
        var fill = (int[])start.Clone();
        for (int k = 0; k < _edgeTo.Count; k++) callers[fill[_edgeTo[k]]++] = _edgeFrom[k];
        var work = new Stack<int>();
        foreach (int p in _directPasters) if (Names.Set(p, NameTable.Paster)) work.Push(p);
        while (work.Count > 0)
        {
            int x = work.Pop();
            for (int k = start[x]; k < start[x + 1]; k++)
                if (Names.Set(callers[k], NameTable.Paster)) work.Push(callers[k]);
        }
    }

    public bool IsPaster(int id) => Names.Has(id, NameTable.Paster);

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
        if (id >= 0 && Names.Has(id, NameTable.CName)) Names.Set(id, NameTable.Seed);
    }

    private void SeedText(ReadOnlySpan<byte> s) => FwLexer.RawScan(s, Seed);

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

    private void Pass2()
    {
        var min = new Tracker(this, 3, commitAtEof: false);
        var shadow = new Tracker(this, 2, commitAtEof: true);
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
            if (_isC[f]) CarvedStream(f, min, shadow);
            else kept.Add(f);
        }
        kept.AddRange(ExtClosure());
        foreach (int f in kept) KeptFile(f, min, shadow, d);
    }

    /// <summary>A kept file: all of it seeds; its non-define lines are one stream and each define body its own.</summary>
    private void KeptFile(int f, Tracker min, Tracker shadow, DefineParse d)
    {
        var text = FwLexer.TextBytes(File.ReadAllBytes(_files[f]), out int st);
        SeedText(text.AsSpan(st));
        var body = new Tracker[] { new(this, 3, false), new(this, 2, true) };
        min.Begin(null);
        shadow.Begin(null);
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind == LineKind.Define && d.Parse(w.Spliced))
            {
                var b = w.Spliced[d.BodyStart..];
                body[0].Begin(d);
                bool ib = false;
                FwLexer.Lex(b, ref ib, false, body[0]);
                body[0].End();
                body[1].Begin(d);
                FwLexer.Lex(b, ref ib, true, body[1]);
                body[1].End();
                continue;
            }
            StreamLine(ref w, min, shadow);
        }
        min.End();
        shadow.End();
    }

    /// <summary>A carved file: its non-define lines seed and are one stream; its defines were recorded in pass 1.</summary>
    private void CarvedStream(int f, Tracker min, Tracker shadow)
    {
        byte[] text = File.ReadAllBytes(_files[f]);
        int st = text.Length >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF ? 3 : 0;
        min.Begin(null);
        shadow.Begin(null);
        var w = new LineWalker(text, st);
        while (w.Next())
        {
            if (w.Kind == LineKind.Define && IsNamedDefine(w.Spliced)) continue;
            SeedText(w.Raw);
            StreamLine(ref w, min, shadow);
        }
        min.End();
        shadow.End();
    }

    private readonly DefineParse _probe = new();
    private bool IsNamedDefine(ReadOnlySpan<byte> s) => _probe.Parse(s);

    private static void StreamLine(ref LineWalker w, Tracker min, Tracker shadow)
    {
        switch (w.Kind)
        {
            case LineKind.Cond:
            case LineKind.Else:
            case LineKind.EndIf:
                min.Directive(w.Kind);
                shadow.Directive(w.Kind);
                return;
            case LineKind.OtherDirective:
            case LineKind.Define:
                return;
        }
        bool ib = w.StartsInComment;
        FwLexer.Lex(w.Spliced, ref ib, false, min);
        FwLexer.Lex(w.Spliced, ref ib, true, shadow);
    }

    // ---------------- pieces, S5, wanted ----------------

    private readonly Stack<(int Id, int Mask)> _s5 = new();

    /// <summary>A piece for the runs in <paramref name="mask"/> (bit 0 min, bit 1 max); <paramref name="s5"/> expands
    /// an argument through its object-like definitions.</summary>
    public void Piece(int id, int mask, bool s5)
    {
        AddPiece(id, mask);
        if (!s5) return;
        _s5.Push((id, mask));
        while (_s5.Count > 0)
        {
            var (x, m) = _s5.Pop();
            int fresh = 0;
            for (int r = 0; r < 2; r++)
                if ((m & (1 << r)) != 0 && Names.Set(x, S5Flag[r])) fresh |= 1 << r;
            if (fresh == 0 || x >= _objHead.Length) continue;
            for (int k = _objHead[x]; k >= 0; k = _objNext[k])
            {
                foreach (int t in new[] { _objFirst[k], _objLast[k] })
                {
                    if (t < 0) continue;
                    AddPiece(t, fresh);
                    _s5.Push((t, fresh));
                }
            }
        }
    }

    private void AddPiece(int id, int mask)
    {
        for (int r = 0; r < 2; r++)
            if ((mask & (1 << r)) != 0 && Names.Set(id, PieceFlag[r])) _pieceQueue[r].Add(id);
    }

    private void Want(int id, int run)
    {
        if (id >= 0 && Names.Has(id, NameTable.CName) && Names.Set(id, WantFlag[run])) _wantQueue[run].Add(id);
    }

    private void Fixpoint(int run)
    {
        for (int i = 0; i < Names.Count; i++) if (Names.Has(i, NameTable.Seed)) Want(i, run);
        var trackers = run == 0
            ? new[] { new Tracker(this, 1, false) }
            : new[] { new Tracker(this, 2, false), new Tracker(this, 2, true) };
        var d = new DefineParse();
        var pq = _pieceQueue[run];
        var wq = _wantQueue[run];
        int pi = 0, wi = 0;
        var buf = new byte[4096];
        while (pi < pq.Count || wi < wq.Count)
        {
            while (pi < pq.Count) Ranges(pq[pi++], run);
            while (wi < wq.Count)
            {
                int name = wq[wi++];
                for (int k = _cHead[name]; k >= 0; k = _cdNext[k])
                {
                    int len = _cdLen[k];
                    if (buf.Length < len) buf = new byte[Math.Max(len, buf.Length * 2)];
                    RandomAccess.Read(Handle(_cdFile[k]), buf.AsSpan(0, len), _cdOff[k]);
                    var w = new LineWalker(buf.AsSpan(0, len), 0);
                    if (!w.Next() || !d.Parse(w.Spliced)) continue;
                    var b = w.Spliced[d.BodyStart..];
                    FwLexer.RawScan(b, s =>
                    {
                        if (d.IsParam(s)) return;
                        Want(Names.Find(s), run);
                    });
                    for (int t = 0; t < trackers.Length; t++)
                    {
                        trackers[t].Begin(d);
                        bool ib = false;
                        FwLexer.Lex(b, ref ib, t == 1, trackers[t]);
                        trackers[t].End();
                    }
                }
                if (pi < pq.Count) break;
            }
        }
    }

    private SafeFileHandle Handle(int f)
    {
        if (!_handles.TryGetValue(f, out var h))
            _handles[f] = h = File.OpenHandle(_files[f], FileMode.Open, FileAccess.Read, FileShare.Read);
        return h;
    }

    /// <summary>Every C name starting or ending with the piece is wanted (a piece equal to a name counts).</summary>
    private void Ranges(int piece, int run)
    {
        var p = Names.Get(piece).ToArray();
        if (p.Length == 0) return;
        int lo = 0, hi = _sortedFwd.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Names.Get(_sortedFwd[mid]).SequenceCompareTo(p) < 0) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _sortedFwd.Length && Names.Get(_sortedFwd[i]).StartsWith(p); i++) Want(_sortedFwd[i], run);
        lo = 0; hi = _sortedRev.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (NameTable.CompareReversed(Names.Get(_sortedRev[mid]), p) < 0) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _sortedRev.Length && Names.Get(_sortedRev[i]).EndsWith(p); i++) Want(_sortedRev[i], run);
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
            if (!keep) t.BytesAfterMin += _cdLen[k];
            if (drop) t.BytesAfterMax += _cdLen[k] - _cdTerm[k];
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
        for (int k = 0; k < _cdName.Count; k++) if (Names.Has(_cdName[k], NameTable.WantMin)) files.Add(_rel[_cdFile[k]]);
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
