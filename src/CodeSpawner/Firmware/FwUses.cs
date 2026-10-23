namespace CodeSpawner.Firmware;

/// <summary>A token of a call's argument or a define body, as rule 3 reads it (docs/firmware-design.md, "Uses").</summary>
internal struct XTok
{
    public const byte Ident = 0, Num = 1, LParen = 2, RParen = 3, Comma = 4, Other = 5, Param = 6, Wild = 7, Hash = 8;
    /// <summary>A name id (Ident, and Num when made of identifier characters), a parameter index (Param) or a
    /// wildcard kind (Wild).</summary>
    public int Id;
    public byte K;
    /// <summary>Stands for its expansion too: it came through a parameter used outside <c>##</c>.</summary>
    public bool Exp;

    public XTok(byte k, int id = -1, bool exp = false)
    {
        K = k;
        Id = id;
        Exp = exp;
    }
}

/// <summary>
/// Rule 3: a define kept only for a paste is a name one paste use builds. A use is a call of a paste macro (or a
/// call passing one), instantiated through forwarders, aliases and higher-order passes with the arguments it was
/// given; each <c>##</c> chain builds the cross product of its slots. Values are abstract: what can start (First)
/// and end (Last) an argument or an expansion, and whether it can be empty.
/// </summary>
internal sealed partial class FwSpec
{
    public const int ProductCap = 4096;
    private const long WorkMax = 20_000_000;
    private const byte WildDigits = 1, WildAny = 2;

    /// <summary>One name a slot or a chain can give: exact (<see cref="Pre"/> is the name), the empty string, or a
    /// pattern: <see cref="Pre"/>, a wildcard (one or more digits, or anything), <see cref="Suf"/>. Parts are name
    /// ids, -1 for none.</summary>
    private readonly record struct El(int Pre, byte Wild, int Suf)
    {
        public static readonly El Eps = new(-1, 0, -1);
        public bool IsEps => Pre < 0 && Wild == 0;
        public bool IsExact => Pre >= 0 && Wild == 0;
    }

    private sealed class Val
    {
        public readonly HashSet<El> First = new(), Last = new();
        public bool Empty;

        public void Union(Val o)
        {
            First.UnionWith(o.First);
            Last.UnionWith(o.Last);
            Empty |= o.Empty;
        }
    }

    /// <summary>One definition, its body as tokens (a parameter by index, <c>#param</c> as one Other).</summary>
    private sealed class Tpl
    {
        public bool IsFunc, Variadic;
        public int NParams, Mask;
        public XTok[] Body = Array.Empty<XTok>();
    }

    private sealed class Site
    {
        public int Close;
        public bool Computed;
        public readonly List<int> Callees = new();
        public List<XTok>[] Args = Array.Empty<List<XTok>>();
        public readonly Val V = new();
        public bool Inst;
    }

    // per-name bits beyond NameTable's flags
    private const byte BitCalled = 1, BitNamedMin = 2, BitNamedMax = 4, BitEmptyMin = 8, BitEmptyMax = 16;
    private byte[] _bits = new byte[1024];
    private bool _calledFinal;

    private bool HasBit(int id, byte bit) => id >= 0 && id < _bits.Length && (_bits[id] & bit) != 0;

    private bool SetBit(int id, byte bit)
    {
        if (id >= _bits.Length) Array.Resize(ref _bits, Math.Max(id + 1, NameTable.NextCapacity(_bits.Length)));
        if ((_bits[id] & bit) != 0) return false;
        _bits[id] |= bit;
        return true;
    }

    private bool Emptyable(int id) => HasBit(id, _run == 0 ? BitEmptyMin : BitEmptyMax);

    // ---------------- static facts from pass 1 ----------------

    /// <summary>Marks the names a define body calls: one followed by <c>(</c>, one inside a call's parentheses, and
    /// a body's last identifier (an alias).</summary>
    private void ScanCalls(DefineParse d)
    {
        var b = d.Body;
        int calls = 0;
        _parenIsCall.Count = 0;
        for (int k = 0; k < b.Count; k++)
        {
            switch (b[k].Kind)
            {
                case Tok.LParen:
                    bool call = k > 0 && b[k - 1].Kind == Tok.Ident;
                    _parenIsCall.Add(call ? 1 : 0);
                    if (call) calls++;
                    break;
                case Tok.RParen:
                    if (_parenIsCall.Count > 0 && _parenIsCall[--_parenIsCall.Count] == 1) calls--;
                    break;
                case Tok.Ident:
                    if (d.IsParam(d.Text(k))) break;
                    if (calls > 0 || (k + 1 < b.Count && b[k + 1].Kind == Tok.LParen) || k == b.Count - 1)
                        SetBit(Names.GetOrAdd(d.Text(k)), BitCalled);
                    break;
            }
        }
    }

    private readonly IntList _parenIsCall = new();
    private readonly IntList _emName = new(), _emMask = new(), _emOff = new(), _emItems = new();

    /// <summary>A body that can expand to nothing when its identifiers do: only <c>##</c>, parameters, identifiers
    /// and calls of identifiers. Its identifiers (callees for calls) are what it waits on.</summary>
    private void EmptyCandidate(DefineParse d, int name, int mask)
    {
        var b = d.Body;
        int start = _emItems.Count;
        for (int k = 0; k < b.Count; k++)
        {
            var kind = b[k].Kind;
            if (kind == Tok.HashHash) continue;
            if (kind != Tok.Ident) { _emItems.Count = start; return; }
            bool param = d.IsParam(d.Text(k));
            bool call = k + 1 < b.Count && b[k + 1].Kind == Tok.LParen;
            if (param && call) { _emItems.Count = start; return; }
            if (param) continue;
            _emItems.Add(Names.GetOrAdd(d.Text(k)));
            if (!call) continue;
            int depth = 0, j = k + 1;
            for (; j < b.Count; j++)
            {
                if (b[j].Kind == Tok.LParen) depth++;
                else if (b[j].Kind == Tok.RParen && --depth == 0) break;
            }
            k = j;
        }
        _emName.Add(name);
        _emMask.Add(mask);
        _emOff.Add(start);
    }

    /// <summary>Which names can expand to nothing under <paramref name="run"/>, to a fixpoint.</summary>
    private void ComputeEmpty(int run)
    {
        byte bit = run == 0 ? BitEmptyMin : BitEmptyMax;
        int n = _emName.Count, rb = 1 << run;
        var waiting = new int[n];
        var byDep = new Dictionary<int, List<int>>();
        var ready = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if ((_emMask[i] & rb) == 0) { waiting[i] = -1; continue; }
            int end = i + 1 < n ? _emOff[i + 1] : _emItems.Count;
            waiting[i] = end - _emOff[i];
            for (int j = _emOff[i]; j < end; j++)
            {
                if (!byDep.TryGetValue(_emItems[j], out var l)) byDep[_emItems[j]] = l = new();
                l.Add(i);
            }
            if (waiting[i] == 0) ready.Push(i);
        }
        while (ready.Count > 0)
        {
            int name = _emName[ready.Pop()];
            if (!SetBit(name, bit) || !byDep.TryGetValue(name, out var l)) continue;
            foreach (int c in l)
                if (waiting[c] > 0 && --waiting[c] == 0) ready.Push(c);
        }
    }

    // ---------------- templates ----------------

    private readonly Dictionary<int, List<Tpl>> _tpls = new();
    private static readonly List<Tpl> NoTpl = new();
    private readonly DefineParse _tplParse = new();
    private byte[] _tplBuf = new byte[4096];

    private List<Tpl> Templates(int id)
    {
        if (_tpls.TryGetValue(id, out var l)) return l;
        l = new();
        var d = _tplParse;
        if (id < _cHead.Length)
            for (int k = _cHead[id]; k >= 0; k = _cdNext[k])
            {
                int len = _cdLenTerm[k] >> 2;
                int cf = CFile(k, out long off);
                if (_tplBuf.Length < len) _tplBuf = new byte[Math.Max(len, _tplBuf.Length * 2)];
                RandomAccess.Read(Handle(cf), _tplBuf.AsSpan(0, len), off);
                var w = new LineWalker(_tplBuf.AsSpan(0, len), 0);
                if (w.Next() && d.Parse(w.Spliced)) l.Add(MakeTpl(d, 3));
            }
        if (_ncByName.TryGetValue(id, out var nl))
            foreach (int k in nl)
                if (d.Parse(_ncDefs[k].Text)) l.Add(MakeTpl(d, _ncDefs[k].Mask));
        // most names have no definition: they share one empty list (read only) instead of one each
        _tpls[id] = l = l.Count == 0 ? NoTpl : l;
        return l;
    }

    private Tpl MakeTpl(DefineParse d, int mask)
    {
        var t = new Tpl { IsFunc = d.IsFunc, Variadic = d.Variadic, NParams = d.Params.Count, Mask = mask };
        var body = new List<XTok>(d.Body.Count);
        for (int k = 0; k < d.Body.Count; k++)
        {
            var kind = d.Body[k].Kind;
            var s = d.Text(k);
            switch (kind)
            {
                case Tok.Ident:
                    int p = -1;
                    for (int i = 0; i < d.Params.Count; i++) if (s.SequenceEqual(d.Params[i])) { p = i; break; }
                    if (p < 0) { body.Add(new XTok(XTok.Ident, Names.GetOrAdd(s))); break; }
                    if (body.Count > 0 && body[^1].K == XTok.Other && body[^1].Id == StringifyMark) { body[^1] = new XTok(XTok.Other); break; }
                    body.Add(new XTok(XTok.Param, p));
                    break;
                case Tok.Number:
                    body.Add(IsIdNumber(s) ? new XTok(XTok.Num, Names.GetOrAdd(s)) : new XTok(XTok.Other));
                    break;
                case Tok.LParen: body.Add(new XTok(XTok.LParen)); break;
                case Tok.RParen: body.Add(new XTok(XTok.RParen)); break;
                case Tok.Comma: body.Add(new XTok(XTok.Comma)); break;
                case Tok.HashHash: body.Add(new XTok(XTok.Hash)); break;
                default:
                    bool hash = d.IsFunc && (s.SequenceEqual("#"u8) || s.SequenceEqual("%:"u8));
                    body.Add(new XTok(XTok.Other, hash ? StringifyMark : -1));
                    break;
            }
        }
        t.Body = body.ToArray();
        return t;
    }

    private const int StringifyMark = -7;

    // ---------------- uses ----------------

    private int _run;
    private long _work;
    private HashSet<int> _active = new();      // the macros being instantiated: not instantiated again inside
    private readonly HashSet<int> _poolS = new(), _poolE = new();
    private bool _poolOn, _poolAnyS, _poolAnyE, _poolDigitsE;
    private int _noPool;                       // > 0 while expanding a name: its body's uses are its own stream's
    private List<XTok>[]? _eofArgs;            // a call left open at end of file: its missing arguments are wildcards
    private readonly HashSet<El>[] _matched = { new(), new() };

    /// <summary>One use found in a stream: instantiated, and with <paramref name="pool"/> (a computed callee, a
    /// call open across a directive) the call's own pool too. A computed call instantiates each of
    /// <paramref name="heads"/>, the names the use before it can end in. Returns the names this one can end in.</summary>
    public int[]? Use(int run, int callee, List<XTok>[] args, bool pool, bool eof, int[]? heads = null)
    {
        _run = run;
        _work = 0;
        ResetPool();
        if (pool) PoolArgs(args);
        var v = new Val();
        _eofArgs = eof ? args : null;
        try
        {
            if (callee >= 0) v.Union(Instantiate(callee, args));
            if (heads is not null)
                foreach (int h in heads)
                    if (Instantiable(h, args)) v.Union(Instantiate(h, args));
        }
        finally { _eofArgs = null; }
        FinishPool();
        return Heads(v.Last);
    }

    private const int MaxHeads = 64;

    /// <summary>The exact names among a value's ends (null if none).</summary>
    private static int[]? Heads(HashSet<El> last)
    {
        List<int>? l = null;
        foreach (var e in last)
        {
            if (!e.IsExact) continue;
            (l ??= new()).Add(e.Pre);
            if (l.Count == MaxHeads) break;
        }
        if (l is null) return null;
        l.Sort();
        return l.ToArray();
    }

    /// <summary>A literal <c>##</c> chain of a stream (no parameters): the name it builds.</summary>
    public void LiteralChain(int[] ops, int n, int run)
    {
        _run = run;
        ResetPool();
        var slots = new List<El>[n];
        for (int i = 0; i < n; i++) slots[i] = IdEls(ops[i]);
        Product(slots);
        FinishPool();
    }

    public void Called(int id) => SetBit(id, BitCalled);

    /// <summary>A name in code, outside any call and not called: a paste macro never called anywhere gets one use
    /// with every argument a wildcard.</summary>
    public void Named(int id, int run)
    {
        if (!SetBit(id, run == 0 ? BitNamedMin : BitNamedMax)) return;
        if (_calledFinal) WildUse(id, run);
    }

    private void WildUses(int run)
    {
        byte bit = run == 0 ? BitNamedMin : BitNamedMax;
        for (int id = 0; id < Names.Count; id++) if (HasBit(id, bit)) WildUse(id, run);
    }

    private void WildUse(int id, int run)
    {
        if (!IsPaster(id, run) || HasBit(id, BitCalled)) return;
        int np = 1;
        foreach (var t in Templates(id)) if ((t.Mask & (1 << run)) != 0 && t.IsFunc) np = Math.Max(np, t.NParams);
        var args = new List<XTok>[np];
        for (int i = 0; i < np; i++) args[i] = new List<XTok> { new(XTok.Wild, WildAny) };
        Use(run, id, args, pool: false, eof: false);
    }

    private bool Instantiable(int q, List<XTok>[] args)
    {
        if (IsPaster(q, _run)) return true;
        foreach (var a in args)
            foreach (var t in a)
                if (t.K == XTok.Ident && IsPaster(t.Id, _run)) return true;
        return false;
    }

    private Val Instantiate(int callee, List<XTok>[] args)
    {
        var v = new Val();
        if (!_active.Add(callee)) return v;
        try
        {
            foreach (var t in Templates(callee))
            {
                if ((t.Mask & (1 << _run)) == 0) continue;
                if (t.IsFunc) { v.Union(Interp(t, Bind(t, args))); continue; }
                // an object-like define called: its body, and its last identifier called with the arguments
                v.Union(Interp(t, null));
                if (t.Body.Length > 0 && t.Body[^1].K == XTok.Ident && Instantiable(t.Body[^1].Id, args))
                    v.Union(Instantiate(t.Body[^1].Id, args));
            }
        }
        finally { _active.Remove(callee); }
        return v;
    }

    private List<XTok>[] Bind(Tpl t, List<XTok>[] args)
    {
        bool pad = ReferenceEquals(args, _eofArgs);
        var b = new List<XTok>[t.NParams];
        int fixedN = t.Variadic ? t.NParams - 1 : t.NParams;
        for (int i = 0; i < fixedN; i++) b[i] = i < args.Length ? args[i] : Missing(pad);
        if (t.Variadic)
        {
            if (args.Length <= fixedN) b[fixedN] = Missing(pad);
            else
            {
                var va = new List<XTok>();
                for (int i = fixedN; i < args.Length; i++)
                {
                    if (i > fixedN) va.Add(new XTok(XTok.Comma));
                    va.AddRange(args[i]);
                }
                b[fixedN] = va;
            }
        }
        return b;
    }

    private static List<XTok> Missing(bool pad) => pad ? new List<XTok> { new(XTok.Wild, WildAny) } : new List<XTok>();

    /// <summary>Instantiates one body: builds each chain's names, instantiates each call in it, and returns the
    /// body's edges. With <paramref name="a"/> null (a name expanded, not called) parameters are unknown: chains and
    /// calls that hold one build nothing, and the walk passes them.</summary>
    private Val Interp(Tpl t, List<XTok>[]? a)
    {
        if (++_work > WorkMax) throw new InvalidOperationException("firmware truth: one paste use expands past the work limit");
        var b = t.Body;
        int n = b.Length;
        var v = new Val();
        if (n == 0)
        {
            v.First.Add(El.Eps);
            v.Last.Add(El.Eps);
            v.Empty = true;
            return v;
        }

        // ## chains
        var chainEnd = new int[n];
        var chainStart = new int[n];
        Array.Fill(chainEnd, -1);
        Array.Fill(chainStart, -1);
        var chainFirst = new HashSet<El>?[n];   // what a chain gives at its left end, and at its right
        var chainLast = new HashSet<El>?[n];
        var chainEps = new bool[n];
        var ops = new List<int>();
        for (int k = 0; k + 2 < n;)
        {
            if (b[k + 1].K != XTok.Hash || b[k].K == XTok.Hash) { k++; continue; }
            ops.Clear();
            int j = k;
            ops.Add(j);
            while (j + 2 < n && b[j + 1].K == XTok.Hash) { j += 2; ops.Add(j); }
            if (ops.Count == 2 && b[k].K == XTok.Comma && b[j].K == XTok.Param && t.Variadic && b[j].Id == t.NParams - 1) { k = j + 1; continue; }
            chainEnd[k] = j;
            chainStart[j] = k;
            (chainFirst[k], chainLast[k]) = Chain(b, ops, a, out chainEps[k]);
            k = j + 1;
        }

        // calls, nested ones included
        var open = new int[n];
        Array.Fill(open, -1);
        var stack = new Stack<int>();
        for (int k = 0; k < n; k++)
        {
            if (b[k].K == XTok.LParen) stack.Push(k);
            else if (b[k].K == XTok.RParen && stack.Count > 0) open[k] = stack.Pop();
        }
        var close = new int[n];
        Array.Fill(close, -1);
        for (int k = 0; k < n; k++) if (open[k] >= 0) close[open[k]] = k;
        var sites = new Site?[n];
        var savedEof = _eofArgs;
        for (int k = 1; k < n; k++)
        {
            if (b[k].K != XTok.LParen) continue;
            // a call the body leaves open takes the tokens after the use: under max its last argument goes on with
            // anything, its missing ones are anything (`OPENF(x) CAT(x,`)
            bool openCall = close[k] < 0;
            if (openCall && (_run == 0 || a is null)) continue;
            int p = k - 1;
            bool chainCallee = chainStart[p] >= 0;
            bool computed = !chainCallee && b[p].K == XTok.RParen && open[p] >= 0 && sites[open[p]] is { Inst: true };
            if (!chainCallee && !computed && b[p].K != XTok.Ident && b[p].K != XTok.Param) continue;
            bool unknown = false;
            var inner = Subst(b, k + 1, openCall ? n : close[k], a, ref unknown);
            if (openCall) inner.Add(new XTok(XTok.Wild, WildAny));
            var site = new Site { Close = openCall ? n - 1 : close[k], Computed = computed, Args = Split(Resplit(inner, 0) ?? inner) };
            sites[k] = site;
            if (openCall) _eofArgs = site.Args;
            try
            {
                if (computed)
                {
                    // the call before it gave a name: that name called, and the call's own pool
                    if (unknown) continue;
                    PoolArgs(site.Args);
                    site.Inst = true;
                    var heads = Heads(sites[open[p]]!.V.Last);
                    if (heads is not null) foreach (int h in heads) Call(site, h);
                    continue;
                }
                if (chainCallee)
                {
                    if (unknown) continue;
                    foreach (var e in chainLast[chainStart[p]]!)
                    {
                        if (e.IsExact) Call(site, e.Pre);
                        else if (!e.IsEps) { PoolArgs(site.Args); site.Inst = true; }
                    }
                    continue;
                }
                if (b[p].K == XTok.Ident)
                {
                    if (unknown) site.Callees.Add(b[p].Id);
                    else Call(site, b[p].Id);
                    continue;
                }
                if (a is null || unknown) continue;
                var pv = a[b[p].Id];   // a parameter called: the macro it was given
                if (pv.Count == 1 && pv[0].K == XTok.Ident) Call(site, pv[0].Id);
                else if (pv.Count > 0)
                {
                    // a call given (`APPLY(ID(CAT), ...)`): the names it can end in, and the call's own pool
                    PoolArgs(site.Args);
                    site.Inst = true;
                    foreach (var e in EdgeSet(pv, last: true, forceExp: true)) if (e.IsExact) Call(site, e.Pre);
                }
            }
            finally { _eofArgs = savedEof; }
        }

        // the body's edges, walking in from each end
        bool all = true;
        int x = 0;
        while (x < n)
        {
            if (chainEnd[x] >= 0)
            {
                int e = chainEnd[x];
                if (e + 1 < n && sites[e + 1] is { } cs) { all &= SiteEdges(v.First, cs, false); x = cs.Close + 1; continue; }
                var set = chainFirst[x]!;
                if (set.Count == 0) { all = false; break; }
                foreach (var el in set) { v.First.Add(el); if (el.IsExact) AddExp(v.First, el.Pre, false); }
                all &= chainEps[x];
                x = e + 1;
                continue;
            }
            var tk = b[x];
            if ((tk.K == XTok.Ident || tk.K == XTok.Param) && x + 1 < n && sites[x + 1] is { } s)
            {
                if (s.Callees.Count == 0 && !s.Inst) { all = false; break; }
                all &= SiteEdges(v.First, s, false);
                x = s.Close + 1;
                continue;
            }
            if (!Walk(v.First, tk, a, false, ref all)) break;
            x++;
        }
        if (x >= n && all)
        {
            v.First.Add(El.Eps);
            v.Empty = true;
        }
        all = true;
        x = n - 1;
        while (x >= 0)
        {
            if (chainStart[x] >= 0)
            {
                int st = chainStart[x];
                var set = chainLast[st]!;
                if (set.Count == 0) { all = false; break; }
                foreach (var el in set) { v.Last.Add(el); if (el.IsExact) AddExp(v.Last, el.Pre, true); }
                all &= chainEps[st];
                x = st - 1;
                continue;
            }
            var tk = b[x];
            if (tk.K == XTok.RParen)
            {
                int o = open[x];
                if (o < 0 || sites[o] is not { Computed: false } s || (s.Callees.Count == 0 && !s.Inst)) { all = false; break; }
                all &= SiteEdges(v.Last, s, true);
                int p = o - 1;
                x = chainStart[p] >= 0 ? chainStart[p] - 1 : p - 1;
                continue;
            }
            if (!Walk(v.Last, tk, a, true, ref all)) break;
            x--;
        }
        if (x < 0 && all) v.Last.Add(El.Eps);
        return v;
    }

    private void Call(Site site, int q)
    {
        site.Callees.Add(q);
        if (!Instantiable(q, site.Args)) return;
        site.V.Union(Instantiate(q, site.Args));
        site.Inst = true;
    }

    /// <summary>One token of a body's edge walk; false stops the walk.</summary>
    private bool Walk(HashSet<El> to, XTok tk, List<XTok>[]? a, bool last, ref bool all)
    {
        switch (tk.K)
        {
            case XTok.Ident:
                AddId(to, tk.Id);
                AddExp(to, tk.Id, last);
                all &= Emptyable(tk.Id);
                return true;
            case XTok.Param:
                if (a is null) return true;
                var s = EdgeSet(a[tk.Id], last, forceExp: true);
                to.UnionWith(s);
                all &= s.Contains(El.Eps);
                return true;
            case XTok.Hash:
                return true;
            case XTok.Num:
                AddId(to, tk.Id);
                all = false;
                return false;
            default:
                all = false;
                return false;
        }
    }

    /// <summary>A call at a body's edge: its callee, every identifier and number among its arguments (each with
    /// its expansion), and what the instantiated call gives. True if it can be empty.</summary>
    private bool SiteEdges(HashSet<El> to, Site s, bool last)
    {
        bool empty = s.V.Empty;
        foreach (int q in s.Callees)
        {
            AddId(to, q);
            AddExp(to, q, last);
            empty |= Emptyable(q);
        }
        foreach (var arg in s.Args)
            foreach (var t in arg)
            {
                if (t.K == XTok.Ident) { AddId(to, t.Id); AddExp(to, t.Id, last); }
                else if (t.K == XTok.Num) AddId(to, t.Id);
            }
        to.UnionWith(last ? s.V.Last : s.V.First);
        return empty;
    }

    /// <summary>A chain's names: a literal operand gives itself, a parameter its argument (the last token for a
    /// left slot, the first for a right one, both for an interior one); over <see cref="ProductCap"/> the use's
    /// pool takes the first and last slots. Unknown parameters: the literal operands, nothing built.</summary>
    private (HashSet<El> First, HashSet<El> Last) Chain(XTok[] b, List<int> ops, List<XTok>[]? a, out bool eps)
    {
        eps = false;
        int n = ops.Count;
        // an argument of several tokens, none expanded, splits the chain: `X_##b##_Y` with `A B` is X_A and B_Y
        var segs = new List<List<List<El>>> { new() };
        HashSet<El>? front = null, back = null;   // a split end argument's own far token
        bool unbound = false;
        for (int i = 0; i < n; i++)
        {
            var tk = b[ops[i]];
            if (tk.K == XTok.Ident || tk.K == XTok.Num) segs[^1].Add(IdEls(tk.Id));
            else if (tk.K == XTok.Param)
            {
                if (a is null) { unbound = true; segs[^1].Add(new List<El>()); continue; }
                var arg = a[tk.Id];
                if (SplitsChain(arg))
                {
                    var head = EdgeSet(arg, last: false, forceExp: false);
                    var tail = EdgeSet(arg, last: true, forceExp: false);
                    if (i > 0) segs[^1].Add(head.ToList()); else front = head;
                    segs.Add(new());
                    if (i < n - 1) segs[^1].Add(tail.ToList()); else back = tail;
                    continue;
                }
                var s = new HashSet<El>();
                if (i < n - 1) s.UnionWith(EdgeSet(arg, last: true, forceExp: false));
                if (i > 0) s.UnionWith(EdgeSet(arg, last: false, forceExp: false));
                segs[^1].Add(s.ToList());
            }
            else return (new HashSet<El>(), new HashSet<El>());   // a non-name operand builds nothing
        }
        if (unbound)
        {
            var lit = new HashSet<El>();
            foreach (var s in segs[0]) lit.UnionWith(s);
            return (lit, lit);
        }
        HashSet<El>? first = null, last = null;
        for (int g = 0; g < segs.Count; g++)
        {
            var slots = segs[g];
            if (slots.Count == 0) continue;
            var made = Product(slots.ToArray());
            if (made is null)
            {
                made = new HashSet<El>(slots[0]);
                made.UnionWith(slots[^1]);
            }
            else if (segs.Count == 1) eps = made.Contains(El.Eps);
            if (g == 0) first = made;
            if (g == segs.Count - 1) last = made;
        }
        return (front ?? first ?? new HashSet<El>(), back ?? last ?? new HashSet<El>());
    }

    private static bool SplitsChain(List<XTok> arg)
    {
        if (arg.Count < 2) return false;
        foreach (var t in arg) if (t.Exp || t.K == XTok.Wild) return false;
        return true;
    }

    /// <summary>A call's argument tokens with each expanded name whose one object-like body (through aliases) has a
    /// top-level comma replaced by that body, so the call splits at its commas (`PORT(PIN_LED)` with
    /// <c>PIN_LED</c> = <c>A, 5</c>); null when there is none.</summary>
    private List<XTok>? Resplit(List<XTok> inner, int depth)
    {
        List<XTok>? r = null;
        for (int i = 0; i < inner.Count; i++)
        {
            var t = inner[i];
            var body = t.K == XTok.Ident && t.Exp ? CommaBody(t.Id, depth) : null;
            if (body is null) { r?.Add(t); continue; }
            r ??= inner.GetRange(0, i);
            r.AddRange(body);
        }
        return r;
    }

    private const int MaxResplitDepth = 16;

    private List<XTok>? CommaBody(int id, int depth)
    {
        if (depth >= MaxResplitDepth) return null;
        Tpl? one = null;
        foreach (var t in Templates(id))
        {
            if ((t.Mask & (1 << _run)) == 0) continue;
            if (one is not null) return null;   // several definitions: no one splitting
            one = t;
        }
        if (one is null || one.IsFunc) return null;
        var b = one.Body;
        if (b.Length == 1 && b[0].K == XTok.Ident) return CommaBody(b[0].Id, depth + 1);
        int d = 0;
        bool comma = false;
        foreach (var x in b)
        {
            if (x.K == XTok.Hash) return null;
            if (x.K == XTok.LParen) d++;
            else if (x.K == XTok.RParen) d--;
            else if (x.K == XTok.Comma && d == 0) comma = true;
        }
        if (!comma) return null;
        var l = new List<XTok>(b.Length);
        foreach (var x in b) l.Add(new XTok(x.K, x.Id, exp: true));
        return Resplit(l, depth + 1) ?? l;
    }

    /// <summary>The cross product of <paramref name="slots"/>, each built; null (and the slots pooled) past the cap.</summary>
    private HashSet<El>? Product(List<El>[] slots)
    {
        long total = 1;
        foreach (var s in slots)
        {
            total *= s.Count;
            if (total > ProductCap) break;
        }
        if (total > ProductCap)
        {
            PoolStart(slots[0]);
            PoolEnd(slots[^1]);
            return null;
        }
        var made = new HashSet<El>();
        if (total == 0) return made;
        var idx = new int[slots.Length];
        var pre = new List<byte>();
        var suf = new List<byte>();
        while (true)
        {
            pre.Clear();
            suf.Clear();
            byte wild = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var e = slots[i][idx[i]];
                if (e.IsEps) continue;
                if (e.Wild == 0) { AppendName(wild == 0 ? pre : suf, e.Pre); continue; }
                if (wild == 0) { AppendName(pre, e.Pre); wild = e.Wild; }
                else wild = WildAny;   // more than one wildcard: the outer prefix*suffix
                suf.Clear();
                AppendName(suf, e.Suf);
            }
            var el = wild == 0
                ? (pre.Count == 0 ? El.Eps : new El(Names.GetOrAdd(pre.ToArray()), 0, -1))
                : new El(pre.Count == 0 ? -1 : Names.GetOrAdd(pre.ToArray()), wild, suf.Count == 0 ? -1 : Names.GetOrAdd(suf.ToArray()));
            if (made.Add(el)) Build(el);
            int k = slots.Length - 1;
            while (k >= 0 && ++idx[k] == slots[k].Count) idx[k--] = 0;
            if (k < 0) break;
        }
        return made;
    }

    private void AppendName(List<byte> to, int id)
    {
        if (id < 0) return;
        foreach (byte c in Names.Get(id)) to.Add(c);
    }

    /// <summary>What an argument gives at one end: the token there, and for a token that stands for its expansion
    /// that expansion's edge too, the walk going on past identifiers and calls; the empty string when every token
    /// can expand to nothing.</summary>
    private HashSet<El> EdgeSet(List<XTok> toks, bool last, bool forceExp)
    {
        var s = new HashSet<El>();
        int n = toks.Count;
        if (n == 0) { s.Add(El.Eps); return s; }
        var saved = _active;
        _active = new HashSet<int>();   // an argument is expanded on its own, before it is substituted
        try
        {
            bool all = true;
            int k = last ? n - 1 : 0;
            while (last ? k >= 0 : k < n)
            {
                var x = toks[k];
                int callee = -1, o = -1, c = -1;
                if (!last && x.K == XTok.Ident && k + 1 < n && toks[k + 1].K == XTok.LParen)
                {
                    c = MatchClose(toks, k + 1);
                    if (c >= 0) { callee = k; o = k + 1; }
                }
                else if (last && x.K == XTok.RParen)
                {
                    o = MatchOpen(toks, k);
                    if (o > 0 && toks[o - 1].K == XTok.Ident) { callee = o - 1; c = k; }
                }
                if (callee >= 0)
                {
                    var ct = toks[callee];
                    if (!(forceExp || ct.Exp))
                    {
                        if (!last) AddId(s, ct.Id);
                        all = false;
                        break;
                    }
                    AddId(s, ct.Id);
                    AddExp(s, ct.Id, last);
                    var inner = new List<XTok>(c - o - 1);
                    for (int i = o + 1; i < c; i++) { var t = toks[i]; if (forceExp) t.Exp = true; inner.Add(t); }
                    var args = Split(Resplit(inner, 0) ?? inner);
                    foreach (var t in inner)
                    {
                        if (t.K == XTok.Ident) { AddId(s, t.Id); AddExp(s, t.Id, last); }
                        else if (t.K == XTok.Num) AddId(s, t.Id);
                    }
                    bool empty = Emptyable(ct.Id);
                    if (Instantiable(ct.Id, args))
                    {
                        var cv = Instantiate(ct.Id, args);
                        s.UnionWith(last ? cv.Last : cv.First);
                        empty |= cv.Empty;
                    }
                    all &= empty;
                    k = last ? callee - 1 : c + 1;
                    continue;
                }
                if (x.K == XTok.Ident)
                {
                    AddId(s, x.Id);
                    if (!(forceExp || x.Exp)) { all = false; break; }
                    AddExp(s, x.Id, last);
                    all &= Emptyable(x.Id);
                    k += last ? -1 : 1;
                    continue;
                }
                if (x.K == XTok.Num) AddId(s, x.Id);
                else if (x.K == XTok.Wild) s.Add(new El(-1, (byte)x.Id, -1));
                all = false;
                break;
            }
            if (all && (last ? k < 0 : k >= n)) s.Add(El.Eps);
        }
        finally { _active = saved; }
        return s;
    }

    private static int MatchClose(List<XTok> t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Count; i++)
        {
            if (t[i].K == XTok.LParen) depth++;
            else if (t[i].K == XTok.RParen && --depth == 0) return i;
        }
        return -1;
    }

    private static int MatchOpen(List<XTok> t, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (t[i].K == XTok.RParen) depth++;
            else if (t[i].K == XTok.LParen && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>A body range with its parameters replaced by their arguments: one used outside <c>##</c> stands for
    /// its expansion too. With no arguments a parameter is dropped and <paramref name="unknown"/> set.</summary>
    private static List<XTok> Subst(XTok[] b, int from, int to, List<XTok>[]? a, ref bool unknown)
    {
        var r = new List<XTok>();
        for (int k = from; k < to; k++)
        {
            var x = b[k];
            if (x.K == XTok.Param)
            {
                if (a is null) { unknown = true; continue; }
                bool raw = (k > 0 && b[k - 1].K == XTok.Hash) || (k + 1 < b.Length && b[k + 1].K == XTok.Hash);
                foreach (var t in a[x.Id]) r.Add(new XTok(t.K, t.Id, raw ? t.Exp : true));
            }
            else if (x.K == XTok.Hash) r.Add(new XTok(XTok.Other));
            else r.Add(new XTok(x.K, x.Id));
        }
        return r;
    }

    /// <summary>A call's arguments: split at its top-level commas.</summary>
    private static List<XTok>[] Split(List<XTok> inner)
    {
        var args = new List<List<XTok>> { new() };
        int depth = 0;
        foreach (var t in inner)
        {
            if (t.K == XTok.LParen) depth++;
            else if (t.K == XTok.RParen) depth--;
            else if (t.K == XTok.Comma && depth == 0) { args.Add(new()); continue; }
            args[^1].Add(t);
        }
        return args.ToArray();
    }

    private List<El> IdEls(int id)
    {
        var l = new List<El> { new(id, 0, -1) };
        var w = WildOf(id);
        if (w != 0) l.Add(new El(-1, w, -1));
        return l;
    }

    private void AddId(HashSet<El> to, int id)
    {
        to.Add(new El(id, 0, -1));
        var w = WildOf(id);
        if (w != 0) to.Add(new El(-1, w, -1));
    }

    /// <summary><c>__LINE__</c> and <c>__COUNTER__</c> stand for digits too, an undefined <c>__x</c> for anything.</summary>
    private byte WildOf(int id)
    {
        var s = Names.Get(id);
        if (s.Length < 2 || s[0] != '_' || s[1] != '_') return 0;
        if (s.SequenceEqual("__LINE__"u8) || s.SequenceEqual("__COUNTER__"u8)) return WildDigits;
        return _definedUnder.Contains(id) ? (byte)0 : WildAny;
    }

    private void AddExp(HashSet<El> to, int id, bool last)
    {
        var e = Expand(id);
        to.UnionWith(last ? e.Last : e.First);
    }

    private readonly Dictionary<int, Val>[] _expMemo = { new(), new() };
    private static readonly Val NoVal = new();
    private readonly Dictionary<int, int> _expAt = new();   // names being expanded: their depth
    private int _expLow = int.MaxValue;

    /// <summary>A name's expansion edges (not the name itself): every body of it, with parameters unknown,
    /// transitively. Kept once complete (nothing below it on the expansion stack was cut short).</summary>
    private Val Expand(int id)
    {
        if (_expMemo[_run].TryGetValue(id, out var m)) return m;
        if (_expAt.TryGetValue(id, out int at)) { _expLow = Math.Min(_expLow, at); return new Val(); }
        int depth = _expAt.Count;
        _expAt[id] = depth;
        int savedLow = _expLow;
        _expLow = int.MaxValue;
        var savedActive = _active;
        _active = new HashSet<int>();
        _noPool++;
        var v = new Val();
        try
        {
            foreach (var t in Templates(id)) if ((t.Mask & (1 << _run)) != 0) v.Union(Interp(t, null));
        }
        finally
        {
            _noPool--;
            _active = savedActive;
            _expAt.Remove(id);
        }
        // an empty expansion (most names) is kept as the one shared, never-written NoVal
        if (v.First.Count == 0 && v.Last.Count == 0 && !v.Empty) v = NoVal;
        if (_expLow >= depth) _expMemo[_run][id] = v;
        _expLow = Math.Min(savedLow, _expLow);
        return v;
    }

    // ---------------- building ----------------

    private void Build(El e)
    {
        if (e.IsEps || _noPool > 0) return;   // an expansion's own builds are its body stream's
        if (e.Wild == 0) { Want(e.Pre, _run); return; }
        if (!_matched[_run].Add(e)) return;
        var p = e.Pre >= 0 ? Names.Get(e.Pre).ToArray() : Array.Empty<byte>();
        var q = e.Suf >= 0 ? Names.Get(e.Suf).ToArray() : Array.Empty<byte>();
        int min = p.Length + q.Length + (e.Wild == WildDigits ? 1 : 0);
        foreach (int n in Candidates(p, q))
        {
            var s = Names.Get(n);
            if (s.Length < min || !s.StartsWith(p) || !s.EndsWith(q)) continue;
            if (e.Wild == WildDigits)
            {
                bool digits = true;
                foreach (byte c in s[p.Length..^q.Length]) if ((uint)(c - '0') >= 10) { digits = false; break; }
                if (!digits) continue;
            }
            Want(n, _run);
        }
    }

    /// <summary>The C names starting with <paramref name="p"/>, or if it is empty ending with <paramref name="q"/>.</summary>
    private IEnumerable<int> Candidates(byte[] p, byte[] q)
    {
        if (p.Length > 0)
        {
            for (int i = LowerFwd(p); i < _sortedFwd.Length && Names.Get(_sortedFwd[i]).StartsWith(p); i++) yield return _sortedFwd[i];
        }
        else if (q.Length > 0)
        {
            for (int i = LowerRev(q); i < _sortedRev.Length && Names.Get(_sortedRev[i]).EndsWith(q); i++) yield return _sortedRev[i];
        }
        else foreach (int n in _sortedFwd) yield return n;
    }

    private int LowerFwd(ReadOnlySpan<byte> p)
    {
        int lo = 0, hi = _sortedFwd.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Names.Get(_sortedFwd[mid]).SequenceCompareTo(p) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private int LowerRev(ReadOnlySpan<byte> q)
    {
        int lo = 0, hi = _sortedRev.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (NameTable.CompareReversed(Names.Get(_sortedRev[mid]), q) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---------------- a use's own pool ----------------

    private void ResetPool()
    {
        _poolS.Clear();
        _poolE.Clear();
        _poolOn = _poolAnyS = _poolAnyE = _poolDigitsE = false;
    }

    private void PoolStart(IEnumerable<El> els)
    {
        if (_noPool > 0) return;
        _poolOn = true;
        foreach (var e in els)
        {
            if (e.IsEps) continue;
            if (e.Pre >= 0) _poolS.Add(e.Pre);
            else _poolAnyS = true;
        }
    }

    private void PoolEnd(IEnumerable<El> els)
    {
        if (_noPool > 0) return;
        _poolOn = true;
        foreach (var e in els)
        {
            if (e.IsEps) continue;
            if (e.Wild == 0) _poolE.Add(e.Pre);
            else if (e.Suf >= 0) _poolE.Add(e.Suf);
            else if (e.Wild == WildDigits) _poolDigitsE = true;
            else _poolAnyE = true;
        }
    }

    /// <summary>A computed call or one open across a directive: every identifier and number among its arguments
    /// starts and ends names, with its expansion's last and first edges.</summary>
    private void PoolArgs(List<XTok>[] args)
    {
        if (_noPool > 0) return;
        var s = new HashSet<El>();
        var e = new HashSet<El>();
        foreach (var arg in args)
            foreach (var t in arg)
            {
                if (t.K == XTok.Wild) { s.Add(new El(-1, (byte)t.Id, -1)); e.Add(new El(-1, (byte)t.Id, -1)); continue; }
                if (t.K != XTok.Ident && t.K != XTok.Num) continue;
                s.Add(new El(t.Id, 0, -1));
                AddId(e, t.Id);
                if (t.K != XTok.Ident) continue;
                AddExp(s, t.Id, true);
                AddExp(e, t.Id, false);
            }
        s.RemoveWhere(x => x.Wild != 0 && x.Pre < 0 && x.Suf < 0 && x.Wild == WildDigits);   // digits end names only
        PoolStart(s);
        PoolEnd(e);
    }

    /// <summary>The pool's names: one starting with a start piece and ending with an end piece, never overlapping.</summary>
    private void FinishPool()
    {
        if (!_poolOn) return;
        _poolOn = false;
        bool anyEnd = _poolAnyE || _poolDigitsE || _poolE.Count > 0;
        if ((!_poolAnyS && _poolS.Count == 0) || !anyEnd) return;
        var eLens = new HashSet<int>();
        foreach (int e in _poolE) eLens.Add(Names.Get(e).Length);
        if (_poolAnyS)
        {
            foreach (int n in _sortedFwd) if (EndsPool(Names.Get(n), 0, eLens)) Want(n, _run);
            return;
        }
        foreach (int st in _poolS)
        {
            var p = Names.Get(st).ToArray();
            for (int i = LowerFwd(p); i < _sortedFwd.Length; i++)
            {
                int n = _sortedFwd[i];
                var s = Names.Get(n);
                if (!s.StartsWith(p)) break;
                if (EndsPool(s, p.Length, eLens)) Want(n, _run);
            }
        }
    }

    private bool EndsPool(ReadOnlySpan<byte> s, int startLen, HashSet<int> eLens)
    {
        int room = s.Length - startLen;
        if (_poolAnyE) return true;
        if (_poolDigitsE && room >= 1 && (uint)(s[^1] - '0') < 10) return true;
        foreach (int l in eLens)
        {
            if (l > room) continue;
            int id = Names.Find(s[^l..]);
            if (id >= 0 && _poolE.Contains(id)) return true;
        }
        return false;
    }
}
