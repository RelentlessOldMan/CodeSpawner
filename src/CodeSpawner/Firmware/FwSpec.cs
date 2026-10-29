using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodeSpawner.Firmware;

/// <summary>One truth list: where it was written (null when only hashed), its sha256 and line count.</summary>
internal sealed record FwList(string? Path, string Sha256, long Count);

/// <summary>A dense header and whether a one-line change flips Carver's verdict.</summary>
internal sealed record FwDense(string Path, bool Edge);

/// <summary>The corpus's compile db cannot be read as one, or does not describe this corpus.</summary>
internal sealed class FwDbException(string message) : Exception(message);

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
    /// <summary>Phase 2 only (null in phase 1, where the union lists are copies of main's and only an empty mustKeepFunctions is written).</summary>
    public FwList? MustDropFunctions, OpenFunctions, UnionMustKeepFunctions, UnionMustDropFunctions, UnionOpenFunctions;
    /// <summary>Generator check: C names whose class disagrees with the alphabet (Z…Z, Y…Z and Z…Y drop, Y…Y open, else keep).</summary>
    public List<string> AlphabetErrors = new();
    public long MustKeepCount, MustDropCount, OpenCount;
}

/// <summary>
/// The firmware truth, computed from the written tree (docs/firmware-design.md): the generator runs it on what it
/// wrote and fails on any disagreement with the names it meant, and verify runs it again. Single-threaded; one
/// file in memory at a time; names are bytes in a <see cref="NameTable"/>.
/// </summary>
internal sealed partial class FwSpec
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
    /// <summary>firmware.roots: non-null turns on the function truth (phase 2).</summary>
    private IReadOnlyList<string>? _roots;

    // files: fw files (sorted, fw-relative) then ext closure files
    private readonly List<string> _files = new();        // full paths
    private readonly List<string> _rel = new();          // fw-relative (fw files) or corpus-relative (ext)
    private int _fwCount;
    private bool[] _isK = Array.Empty<bool>(), _isC = Array.Empty<bool>(), _binary = Array.Empty<bool>();

    // C defs (struct of arrays)
    private readonly IntList _cdName = new(), _cdLen = new(), _cdTerm = new();   // name; line length; its terminator bytes (every physical line)
    private long[] _cdOff = new long[1024];               // offset in all carved bytes (see CFile)
    private int[] _cdNext = Array.Empty<int>();
    private readonly List<long> _cBase = new();           // where each carved file starts in all carved bytes
    private readonly List<int> _cBaseFile = new();
    private long _cBytes;
    private int[] _cHead = Array.Empty<int>();            // name -> first C def (-1)

    // all defs: forward edges and direct pasters (rule 1), each with the runs it counts for
    // (bit 0 min, bit 1 max: a maybe-kept header's defines count under max only)
    private readonly IntList _edgeFrom = new(), _edgeTo = new(), _edgeMask = new();
    private readonly List<(int Name, int Mask)> _directPasters = new();
    private readonly HashSet<int> _definedUnder = new();   // names starting `__` that something defines
    // the defines outside the carved files (kept fw files, ext, -D, maybe-kept): their text, for rule 3's uses
    private readonly List<(int Name, byte[] Text, int Mask)> _ncDefs = new();
    private readonly Dictionary<int, List<int>> _ncByName = new();
    private List<int> _maybe = new();
    private readonly IntList _fwDefFile = new(), _fwDefName = new();   // defs in kept (non-C) fw files

    // the build
    private readonly List<Unit> _units = new();
    private readonly HashSet<int> _closure = new();       // file indices (fw and ext) in any unit's closure
    private readonly List<int> _wholeFromOutside = new();

    private int[] _sortedFwd = Array.Empty<int>(), _sortedRev = Array.Empty<int>();
    private readonly IntList[] _wantQueue = { new(), new() };
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
    public static FwTruth Compute(string root, string? listDir, bool checkAlphabet, IReadOnlyList<string>? roots = null)
    {
        var s = new FwSpec(root) { _roots = roots };
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
        ComputeEmpty(0);
        ComputeEmpty(1);
        AliasEdges();
        PasterFixpoint(0);
        PasterFixpoint(1);
        SortNames();
        Seeds();
        Pass2();
        _calledFinal = true;   // every call site is seen: a paste macro only named gets its wildcard use
        WildUses(0);
        WildUses(1);
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
        byte[] bytes = File.ReadAllBytes(db);
        // a db that is not an array of { directory, file, arguments | command } is a bad corpus, not a crash
        try { ReadDb(bytes, DbPath); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException)
        { throw new FwDbException($"{DbPath} is not a compile db this generator writes ({ex.Message})"); }
    }

    private void ReadDb(byte[] bytes, string name)
    {
        using var doc = JsonDocument.Parse(bytes);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string dir = e.GetProperty("directory").GetString()!;
            if (!Path.IsPathRooted(dir)) dir = Path.Combine(_root, dir);
            dir = Path.GetFullPath(dir);
            // --fw-db-paths absolute names the corpus where it was generated: once moved, every path misses
            if (!string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(_root), StringComparison.OrdinalIgnoreCase))
                throw new FwDbException($"{name} names directory {dir}, not this corpus ({_root}): an absolute-path db " +
                                        "only fits the corpus where it was generated; regenerate it there, or with --fw-db-paths relative");
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
                if (w.Length >= 1 << 30) throw new InvalidOperationException($"{_rel[f]}: a define line of {w.Length:N0} bytes");
                _cdLen.Add(w.Length);
                _cdTerm.Add(w.TermBytes);
                if (d.IsFunc && !d.PastesParam() && d.Body.Any(b => b.Kind == Tok.LParen)) Names.Set(name, FwdC);
                if (d.EndsInComment || w.CommentJoin >= d.BodyStart) Names.Set(name, NameTable.Seed);   // a comment-open define is kept
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
        // a body ending in a name (object-like, or function-like with no call: `PICK(x) CAT`, `PICK2(x) CAT x` with a
        // parameter that may be empty) is an alias of it
        int tail = body.Count;
        while (tail > 0 && body[tail - 1].Kind == Tok.Ident && d.IsFunc && d.IsParam(d.Text(tail - 1))) tail--;
        bool alias = tail > 0 && body[tail - 1].Kind == Tok.Ident;
        // A function-like body with a call forwards to every identifier in it: a paste macro it names without a call
        // (`APPLY2(CAT, a)`) is reached through the call.
        if (d.IsFunc && body.Any(t => t.Kind == Tok.LParen))
        {
            for (int k = 0; k < body.Count; k++)
                if (body[k].Kind == Tok.Ident && !d.IsParam(d.Text(k))) Edge(name, Names.GetOrAdd(d.Text(k)), mask);
        }
        else if (alias)
        {
            // the alias edge goes past the names at the end that may expand to nothing (AliasEdges, once emptiness is known)
            _alName.Add(name);
            _alMask.Add(mask);
            _alOff.Add(_alItems.Count);
            for (int k = tail - 1; k >= 0 && body[k].Kind == Tok.Ident; k--)
                if (!d.IsFunc || !d.IsParam(d.Text(k))) _alItems.Add(Names.GetOrAdd(d.Text(k)));   // a parameter may be empty: walked past
        }
        var s = Names.Get(name);
        if (s.Length >= 2 && s[0] == '_' && s[1] == '_') _definedUnder.Add(name);
        ScanCalls(d);
        EmptyCandidate(d, name, mask);
        if (keepText)
        {
            if (!_ncByName.TryGetValue(name, out var l)) _ncByName[name] = l = new();
            l.Add(_ncDefs.Count);
            _ncDefs.Add((name, line.ToArray(), mask));
        }
    }

    private readonly IntList _alName = new(), _alMask = new(), _alOff = new(), _alItems = new();   // alias bodies' trailing names, last first

    /// <summary>An alias is an edge to its body's last name that need not expand to nothing (rule 2's "may be empty",
    /// one definition for both runs: `#define F CAT EMPTYM` forwards to CAT).</summary>
    private void AliasEdges()
    {
        for (int i = 0; i < _alName.Count; i++)
        {
            int end = i + 1 < _alName.Count ? _alOff[i + 1] : _alItems.Count;
            for (int j = _alOff[i]; j < end; j++)
            {
                Edge(_alName[i], _alItems[j], _alMask[i]);
                if (!MayBeEmpty(_alItems[j])) break;
                if (j + 1 < end) Called(_alItems[j + 1]);   // only names that may be empty follow it: `CAT EMPTYM` calls CAT
            }
        }
    }

    private void Edge(int from, int to, int mask)
    {
        _edgeFrom.Add(from);
        _edgeTo.Add(to);
        _edgeMask.Add(mask);
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

    private void SortNames()
    {
        int n = 0;
        for (int i = 0; i < Names.Count; i++) if (Names.Has(i, NameTable.CName)) n++;
        _sortedFwd = new int[n];
        n = 0;
        for (int i = 0; i < Names.Count; i++) if (Names.Has(i, NameTable.CName)) _sortedFwd[n++] = i;
        _sortedRev = (int[])_sortedFwd.Clone();
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
        private readonly CondSink? _cond;

        public Streams(FwSpec a, int runs)
        {
            _cond = new CondSink(new Streams((runs & 1) != 0 ? new Tracker(a, 0, commitAtEof: false, named: false) : null,
                                             (runs & 2) != 0 ? new Tracker(a, 1, commitAtEof: false, named: false) : null));
            if ((runs & 1) != 0) _min0 = new Tracker(a, 0, commitAtEof: false);
            if ((runs & 2) == 0) return;
            _min1 = new Tracker(a, 1, commitAtEof: false);
            _shadow = new Tracker(a, 1, commitAtEof: true);
        }

        // an #if condition's stream: min lexing, nothing named
        private Streams(Tracker? min0, Tracker? min1)
        {
            _min0 = min0;
            _min1 = min1;
        }

        public void Token(Tok kind, ReadOnlySpan<byte> text)
        {
            _min0?.Token(kind, text);
            _min1?.Token(kind, text);
        }

        public void Begin(DefineParse? d, bool carved = false) { _min0?.Begin(d, carved); _min1?.Begin(d, carved); _shadow?.Begin(d, carved); }
        public void Directive(LineKind k) { _min0?.Directive(k); _min1?.Directive(k); _shadow?.Directive(k); }
        public void End() { _min0?.End(); _min1?.End(); _shadow?.End(); }

        public void Lex(ReadOnlySpan<byte> s, bool startsInComment)
        {
            bool ib = startsInComment;
            FwLexer.Lex(s, ref ib, false, this);
            if (_shadow is not null) FwLexer.Lex(s, ref ib, true, _shadow);
        }

        /// <summary>An <c>#if</c>/<c>#elif</c> line: its condition is a stream of its own, where a paste macro
        /// called builds (<c>defined X</c> is no call).</summary>
        public void Condition(ReadOnlySpan<byte> line)
        {
            if (_cond is null) return;
            int i = FwLexer.DirectiveStart(line);
            int st = i;
            while (i < line.Length && FwLexer.IsIdChar(line[i])) i++;
            var w = line[st..i];
            if (!w.SequenceEqual("if"u8) && !w.SequenceEqual("elif"u8)) return;
            var s = _cond.S;
            s.Begin(null);
            _cond.Reset();
            bool ib = false;
            FwLexer.Lex(line[i..], ref ib, false, _cond);
            s.End();
        }
    }

    /// <summary>Passes a condition's tokens on, without <c>defined X</c> and <c>defined ( X )</c>.</summary>
    private sealed class CondSink : ITokenSink
    {
        public readonly Streams S;
        private int _state;   // 1: after defined, 2: after defined (, 3: after defined ( X

        public CondSink(Streams s) => S = s;

        public void Reset() => _state = 0;

        public void Token(Tok kind, ReadOnlySpan<byte> text)
        {
            switch (_state)
            {
                case 1 when kind == Tok.LParen: _state = 2; return;
                case 1 when kind == Tok.Ident: _state = 0; return;
                case 2 when kind == Tok.Ident: _state = 3; return;
                case 3 when kind == Tok.RParen: _state = 0; return;
            }
            _state = 0;
            if (kind == Tok.Ident && text.SequenceEqual("defined"u8)) { _state = 1; return; }
            S.Token(kind, text);
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
                main.Directive(LineKind.Define);
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
            if (w.Kind == LineKind.Define && IsNamedDefine(w.Spliced)) { main.Directive(LineKind.Define); continue; }
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
            case LineKind.Elif:
                main.Directive(w.Kind);
                main.Condition(w.Spliced);
                return;
            case LineKind.Else:
            case LineKind.EndIf:
                main.Directive(w.Kind);
                return;
            case LineKind.OtherDirective:
            case LineKind.Define:
                main.Directive(w.Kind);   // a macro named before one is not called across it at top level
                return;
        }
        main.Lex(w.Spliced, w.StartsInComment);
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
        var wq = _wantQueue[run];
        int wi = 0;
        var buf = new byte[4096];
        while (wi < wq.Count)
        {
            int name = wq[wi++];
            for (int k = _cHead[name]; k >= 0; k = _cdNext[k])
            {
                int len = _cdLen[k];
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
                streams.Begin(d, carved: true);
                streams.Lex(b, false);
                streams.End();
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
            int len = _cdLen[k];
            if (!keep) t.BytesAfterMin += len;
            if (drop) t.BytesAfterMax += len - _cdTerm[k];
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
                int want = s.Length < 2 ? 0 : (s[0] == 'Z' || s[0] == 'Y') && s[^1] == 'Z' || s[0] == 'Z' && s[^1] == 'Y' ? 1 : s[0] == 'Y' && s[^1] == 'Y' ? 2 : 0;
                if (want != cls && t.AlphabetErrors.Count < 50)
                    t.AlphabetErrors.Add($"{Encoding.ASCII.GetString(s)}: {ClassName(cls)}, alphabet says {ClassName(want)}");
            }
        }
        // every case row names a graded define (a row for a name the tree never defines, or defines only outside the
        // carved files, would test nothing)
        if (checkAlphabet && listDir is not null && File.Exists(Path.Combine(listDir, "cases.tsv")))
            foreach (var line in File.ReadLines(Path.Combine(listDir, "cases.tsv")).Skip(1))
            {
                var name = line.Split('\t')[0];
                int id = Names.Find(Encoding.ASCII.GetBytes(name));
                if ((id < 0 || !Names.Has(id, NameTable.CName)) && t.AlphabetErrors.Count < 50)
                    t.AlphabetErrors.Add($"{name}: a case row, but no carved file defines it");
            }
        t.MustKeepCount = keepW.Count;
        t.MustDropCount = dropW.Count;
        t.OpenCount = openW.Count;
        t.MustKeepDefines = keepW.Finish();
        t.MustDropDefines = dropW.Finish();
        t.OpenDefines = openW.Finish();

        // files: defining a must-keep name, the fw units, and their closures. A file no build compiles and nothing
        // includes is never one (Carver ruling, 1.5.3): its own lines seed its names (a header guard, bigsrc_0.c's CAT
        // use), but an unforced carve rightly drops the file. Those names stay must-keep defines only because defines
        // are graded in the forced carve.
        var files = new SortedSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < _cdName.Count; k++)
        {
            if (!Names.Has(_cdName[k], NameTable.WantMin)) continue;
            int f = CFile(k, out _);
            if (_closure.Contains(f)) files.Add(_rel[f]);
        }
        for (int k = 0; k < _fwDefName.Count; k++) if (Names.Has(_fwDefName[k], NameTable.WantMin)) files.Add(_rel[_fwDefFile[k]]);
        foreach (int f in _closure) if (f < _fwCount) files.Add(_rel[f]);
        if (_roots is null) { using var fnW = new ListWriter(listDir, "mustKeepFunctions"); t.MustKeepFunctions = fnW.Finish(); }
        else Functions(t, listDir, checkAlphabet, files);
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
    /// <summary>A list longer than this is written gzipped. Settable only so a test can reach the gz path.</summary>
    internal static long GzipPast = 100L << 20;
    private bool _finished;
    private readonly string? _dir, _name, _side;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly FileStream? _out;
    private readonly string? _tmp;
    private long _bytes;
    public long Count { get; private set; }
    private static readonly byte[] Lf = { (byte)'\n' };

    /// <param name="side">"main" or "union" to write only that side's list (phase 2's function lists differ); null writes main and copies it to union.</param>
    public ListWriter(string? dir, string name, string? side = null)
    {
        _dir = dir;
        _name = name;
        _side = side;
        if (dir is null) return;
        _tmp = Path.Combine(dir, $"{side ?? "main"}.{name}.txt.tmp");
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
        string main = Path.Combine(_dir!, $"{_side ?? "main"}.{_name}.txt"), union = Path.Combine(_dir!, $"union.{_name}.txt");
        string rel;
        if (_bytes > GzipPast)
        {
            // compressed once; the union (one build in phase 1) is a copy
            using (var src = File.OpenRead(_tmp!))
            using (var dst = File.Create(main + ".gz"))
            using (var gz = new System.IO.Compression.GZipStream(dst, System.IO.Compression.CompressionLevel.Fastest))
                src.CopyTo(gz);
            if (_side is null) File.Copy(main + ".gz", union + ".gz", true);
            File.Delete(_tmp!);
            rel = $"{_side ?? "main"}.{_name}.txt.gz";
        }
        else
        {
            if (_side is null) File.Copy(_tmp!, union, true);
            File.Move(_tmp!, main, true);
            rel = $"{_side ?? "main"}.{_name}.txt";
        }
        _finished = true;
        return new FwList(rel, sha, Count);
    }

    public void Dispose()
    {
        _out?.Dispose();
        // a failed gen leaves no multi-GB temp behind
        if (!_finished && _tmp is not null) try { File.Delete(_tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _sha.Dispose();
    }
}
