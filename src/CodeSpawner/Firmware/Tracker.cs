namespace CodeSpawner.Firmware;

/// <summary>
/// Rule 3's call stream (docs/firmware-design.md, "Uses"): follows <c>##</c> chains and calls through a token stream
/// that spans lines and conditional directives, and reports each paste use with its arguments, each literal chain,
/// and which names are called and named. One tracker is one stream for one run; a define body is its own stream,
/// where a call or chain holding one of the define's parameters is a template (built when the define is used).
/// Conditional branches are followed as paths: each branch continues from the state at its <c>#if</c>, and after
/// <c>#endif</c> every branch's end state goes on (the union of the branches).
/// </summary>
internal sealed class Tracker : ITokenSink
{
    // One token of the stream as a ## operand: a name id, a parameter, or anything else.
    private const int OpParam = -2, OpOther = -1;

    /// <summary>Paths past this many (a call open across many branches) are dropped, the first kept; under max a
    /// dropped one's open calls are used as if the stream ended there, so what they could build is open.</summary>
    public const int MaxPaths = 16;

    /// <summary>A path's tokens: a run shared with the paths it forked from, then its own. Forking is O(1), so a
    /// call open across many branches costs no copy per <c>#if</c>.</summary>
    private sealed class TokBuf
    {
        private sealed class Seg
        {
            public Seg? Up;
            public XTok[] Toks = Array.Empty<XTok>();
        }

        private Seg? _up;
        private int _base;                        // tokens in the shared run
        private List<XTok> _own = new();
        private ulong _hash;                      // of every token, in order

        public int Count => _base + _own.Count;

        public void Add(XTok t)
        {
            _own.Add(t);
            _hash = (_hash ^ (uint)(t.Id << 5 | t.K << 1 | (t.Exp ? 1 : 0))) * 0x100000001B3UL;
        }

        public void Clear()
        {
            _up = null;
            _base = 0;
            _hash = 0;
            _own = new List<XTok>();
        }

        public TokBuf Fork()
        {
            if (_own.Count > 0)
            {
                _up = new Seg { Up = _up, Toks = _own.ToArray() };
                _base += _own.Count;
                _own = new List<XTok>();
            }
            return new TokBuf { _up = _up, _base = _base, _hash = _hash };
        }

        public List<XTok> GetRange(int from, int n)
        {
            var r = new List<XTok>(n);
            if (n == 0) return r;
            var segs = new List<XTok[]>();
            for (var s = _up; s is not null; s = s.Up) segs.Add(s.Toks);
            int at = 0, end = from + n;
            for (int i = segs.Count - 1; i >= 0 && at < end; i--)
            {
                var t = segs[i];
                int lo = Math.Max(from - at, 0), hi = Math.Min(end - at, t.Length);
                for (int k = lo; k < hi; k++) r.Add(t[k]);
                at += t.Length;
            }
            for (int k = Math.Max(from - at, 0); k < _own.Count && at + k < end; k++) r.Add(_own[k]);
            return r;
        }

        public bool SameAs(TokBuf o)
        {
            if (Count != o.Count || _hash != o._hash) return false;
            if (ReferenceEquals(_up, o._up)) return Equal(_own, o._own);
            return Equal(GetRange(0, Count), o.GetRange(0, o.Count));
        }

        private static bool Equal(List<XTok> a, List<XTok> b)
        {
            for (int i = 0; i < a.Count; i++)
                if (a[i].K != b[i].K || a[i].Id != b[i].Id || a[i].Exp != b[i].Exp) return false;
            return true;
        }
    }

    private sealed class Frame
    {
        public int Callee = -1;                   // the name called (-1: computed, or a parameter)
        public int Open;                          // its ( in the buffer
        public bool Marked, Computed, Crossed, Template;
        public int Depth;                         // parentheses open inside the current argument
        public int[]? Heads;                      // computed: the names the use before it can end in
        public Frame Clone() => (Frame)MemberwiseClone();

        public bool Same(Frame o) => Callee == o.Callee && Open == o.Open && Marked == o.Marked && Computed == o.Computed &&
                                     Crossed == o.Crossed && Template == o.Template && Depth == o.Depth && SameHeads(Heads, o.Heads);
    }

    /// <summary>One path through the conditionals: its open calls, their tokens, and what the last token left.</summary>
    private sealed class Path
    {
        public List<Frame> Frames = new();
        public TokBuf Buf = new();                // the tokens since the outermost open call
        public int Pending = OpOther;             // an identifier not yet known to be called (a name id or OpParam)
        public bool HasPending;
        public bool LastClosedUse;
        public int[]? LastHeads;                  // the closed use's exact end names

        public Path Clone()
        {
            var p = new Path { Buf = Buf.Fork(), Pending = Pending, HasPending = HasPending, LastClosedUse = LastClosedUse, LastHeads = LastHeads };
            foreach (var f in Frames) p.Frames.Add(f.Clone());
            return p;
        }

        public bool Idle => Frames.Count == 0 && !HasPending && !LastClosedUse;

        public bool Same(Path o)
        {
            if (Frames.Count != o.Frames.Count || Buf.Count != o.Buf.Count || HasPending != o.HasPending ||
                (HasPending && Pending != o.Pending) || LastClosedUse != o.LastClosedUse ||
                (LastClosedUse && (!SameHeads(LastHeads, o.LastHeads)))) return false;
            for (int i = 0; i < Frames.Count; i++) if (!Frames[i].Same(o.Frames[i])) return false;
            return Buf.SameAs(o.Buf);
        }
    }

    private static bool SameHeads(int[]? a, int[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private sealed class Cond
    {
        public List<Path> Start = new();          // the paths at the #if
        public readonly List<Path> Parked = new();   // the earlier branches' end paths
        public bool SawElse;
    }

    private readonly FwSpec _a;
    private readonly int _run;
    private readonly bool _commitAtEof, _marks, _named;
    private DefineParse? _params;
    private List<Path> _paths = new() { new Path() };
    private Path _p = null!;
    private readonly List<Cond> _conds = new();
    private readonly IntList _chain = new();      // the ## chain being read: its operands
    private bool _afterHash;
    private int _last = OpOther;                  // the previous token as a ## operand
    private bool _hasLast;
    private readonly IntList _tail = new();       // the names at the stream's end, in order (a body's alias walk)

    /// <param name="commitAtEof">Calls open at the end are used, their missing arguments wildcards (the shadow
    /// lexing, where comments and strings are tokens).</param>
    /// <param name="named">A name outside any call is named (false: an <c>#if</c> condition, which only calls).</param>
    public Tracker(FwSpec a, int run, bool commitAtEof, bool named = true)
    {
        _a = a;
        _run = run;
        _commitAtEof = commitAtEof;
        _marks = !commitAtEof;   // comments and strings never call or name
        _named = named;
    }

    /// <summary>Starts a new stream; <paramref name="defineParams"/> is the define whose body this is, if any.</summary>
    public void Begin(DefineParse? defineParams)
    {
        _params = defineParams;
        _paths = new List<Path> { new Path() };
        _conds.Clear();
        _afterHash = _hasLast = false;
        _chain.Count = 0;
        _tail.Count = 0;
    }

    private Frame? Top => _p.Frames.Count > 0 ? _p.Frames[^1] : null;

    private int Operand(Tok kind, ReadOnlySpan<byte> text)
    {
        if (kind == Tok.Ident) return _params is not null && _params.IsParam(text) ? OpParam : _a.Names.GetOrAdd(text);
        if (kind == Tok.Number && FwSpec.IsIdNumber(text)) return _a.Names.GetOrAdd(text);
        return OpOther;
    }

    private void Buf(XTok t)
    {
        if (_p.Frames.Count > 0) _p.Buf.Add(t);
    }

    private static XTok AsTok(Tok kind, int op) => kind switch
    {
        Tok.Ident => op == OpParam ? new XTok(XTok.Param) : new XTok(XTok.Ident, op),
        Tok.Number => op >= 0 ? new XTok(XTok.Num, op) : new XTok(XTok.Other),
        Tok.LParen => new XTok(XTok.LParen),
        Tok.RParen => new XTok(XTok.RParen),
        Tok.Comma => new XTok(XTok.Comma),
        _ => new XTok(XTok.Other),
    };

    public void Token(Tok kind, ReadOnlySpan<byte> text)
    {
        if (kind == Tok.HashHash)
        {
        _tail.Count = 0;
            if (!_afterHash)
            {
                if (_chain.Count == 0) _chain.Add(_hasLast ? _last : OpOther);
                _afterHash = true;
            }
            foreach (var p in _paths)
            {
                _p = p;
                FinalizePending();
                Buf(new XTok(XTok.Other));
                p.LastClosedUse = false;
            }
            return;
        }
        int op = Operand(kind, text);
        if (kind == Tok.Ident && op != OpParam) _tail.Add(op); else _tail.Count = 0;
        if (_afterHash) { _chain.Add(op); _afterHash = false; }
        else FlushChain();
        _last = op;
        _hasLast = true;
        foreach (var p in _paths)
        {
            _p = p;
            Step(kind, op);
        }
    }

    private void Step(Tok kind, int op)
    {
        var p = _p;
        if (kind == Tok.Ident)
        {
            // a name before a parameter may be called by the argument's text (`CAT x` with x = `(a, b)`)
            if (op == OpParam && p.HasPending && p.Pending >= 0 && p.Frames.Count == 0)
            {
                p.HasPending = false;
                if (_marks) _a.Called(p.Pending);
            }
            FinalizePending();
            Buf(AsTok(kind, op));
            p.Pending = op;
            p.HasPending = true;
            p.LastClosedUse = false;
            return;
        }
        switch (kind)
        {
            case Tok.LParen:
                if (p.HasPending)
                {
                    int callee = p.Pending;
                    p.HasPending = false;
                    if (callee >= 0 && _marks) _a.Called(callee);
                    p.Buf.Add(new XTok(XTok.LParen));
                    p.Frames.Add(new Frame { Callee = callee >= 0 ? callee : -1, Open = p.Buf.Count - 1, Template = callee == OpParam });
                }
                else if (p.LastClosedUse)
                {
                    p.Buf.Add(new XTok(XTok.LParen));
                    p.Frames.Add(new Frame { Computed = true, Open = p.Buf.Count - 1, Heads = p.LastHeads });
                }
                else if (Top is { } t) { t.Depth++; p.Buf.Add(new XTok(XTok.LParen)); }
                p.LastClosedUse = false;
                break;
            case Tok.RParen:
                FinalizePending();
                p.LastClosedUse = false;
                if (p.Frames.Count == 0) break;
                var top = p.Frames[^1];
                p.Buf.Add(new XTok(XTok.RParen));
                if (top.Depth > 0) { top.Depth--; break; }
                p.LastClosedUse = Close(p.Buf.Count - 1, eof: false);
                break;
            default:
                FinalizePending();
                Buf(AsTok(kind, op));
                p.LastClosedUse = false;
                break;
        }
    }

    // An identifier that was not called: inside a call it is passed (a paste macro passed marks the call and every
    // call around it), outside one it is named.
    private void FinalizePending()
    {
        if (!_p.HasPending) return;
        _p.HasPending = false;
        int id = _p.Pending;
        if (id < 0) return;
        if (_p.Frames.Count > 0)
        {
            if (_marks) _a.Called(id);
            if (_a.IsPaster(id, _run)) foreach (var f in _p.Frames) f.Marked = true;
        }
        else if (_marks && _named) _a.Named(id, _run);
    }

    // A chain with no parameter builds its literal name; one with a parameter is a template.
    private void FlushChain()
    {
        int n = _chain.Count;
        _chain.Count = 0;
        if (n < 2) return;
        var ops = _chain.A;
        for (int i = 0; i < n; i++) if (ops[i] < 0) return;
        _a.LiteralChain(ops, n, _run);
    }

    /// <summary>Closes the top call, its ) at <paramref name="close"/> (or the buffer's end): a paste use, a
    /// higher-order use, or a computed or crossed one with its own pool. True if it was a use.</summary>
    private bool Close(int close, bool eof)
    {
        var p = _p;
        var f = p.Frames[^1];
        p.Frames.RemoveAt(p.Frames.Count - 1);
        bool use = f.Computed || (f.Callee >= 0 && (f.Marked || _a.IsPaster(f.Callee, _run)));
        p.LastHeads = null;
        if (use && !f.Template)
        {
            var inner = p.Buf.GetRange(f.Open + 1, close - f.Open - 1);
            bool template = false;
            foreach (var t in inner) if (t.K == XTok.Param) { template = true; break; }
            if (!template) p.LastHeads = _a.Use(_run, f.Callee, Split(inner), pool: f.Computed || f.Crossed, eof, f.Heads);
        }
        if (p.Frames.Count == 0) p.Buf.Clear();
        return use;
    }

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

    /// <summary>A conditional directive. <c>#if</c>-family opens; <c>#elif</c>/<c>#else</c> parks the branch's
    /// paths and restarts from the state at the <c>#if</c>; <c>#endif</c> goes on with every branch's paths (and
    /// the <c>#if</c>'s own when there was no <c>#else</c>). A call open across one is used with its own pool too.</summary>
    public void Directive(LineKind kind)
    {
        FlushChain();
        _tail.Count = 0;
        _afterHash = _hasLast = false;
        foreach (var p in _paths)
        {
            foreach (var f in p.Frames) f.Crossed = true;
            // gcc and clang look for a macro's ( across a directive only while reading a call's arguments; max
            // looks across one anywhere (names a carver may keep: open)
            if (p.Frames.Count > 0 || _run == 1) continue;
            _p = p;
            FinalizePending();
            p.LastClosedUse = false;
        }
        switch (kind)
        {
            case LineKind.Cond:
                _conds.Add(new Cond { Start = Clones(_paths) });
                break;
            case LineKind.Elif:
            case LineKind.Else:
                if (_conds.Count == 0) break;
                var c = _conds[^1];
                if (c.SawElse) break;   // a branch after #else is never taken
                c.Parked.AddRange(_paths);
                _paths = Clones(c.Start);
                if (kind == LineKind.Else) c.SawElse = true;
                break;
            case LineKind.EndIf:
                if (_conds.Count == 0) break;
                var e = _conds[^1];
                _conds.RemoveAt(_conds.Count - 1);
                var all = new List<Path>(e.Parked);
                all.AddRange(_paths);
                if (!e.SawElse) all.AddRange(e.Start);
                _paths = Distinct(all);
                break;
        }
    }

    private static List<Path> Clones(List<Path> ps)
    {
        var l = new List<Path>(ps.Count);
        foreach (var p in ps) l.Add(p.Clone());
        return l;
    }

    private List<Path> Distinct(List<Path> all)
    {
        var l = new List<Path>();
        // idle paths differ in nothing that is still to come: one is kept, first
        foreach (var p in all) if (p.Idle) { l.Add(p); break; }
        foreach (var p in all)
        {
            if (p.Idle) continue;
            bool dup = false;
            foreach (var q in l) if (q.Same(p)) { dup = true; break; }
            if (dup) continue;
            if (l.Count < MaxPaths) { l.Add(p); continue; }
            if (_run != 1) continue;
            // past the cap: what the dropped path's open calls could build is open (max uses them, min never)
            _p = p;
            FinalizePending();
            while (p.Frames.Count > 0) Close(p.Buf.Count, eof: true);
        }
        return l;
    }

    /// <summary>End of the stream: open calls are dropped, or (commit) closed top-down. A define body's last
    /// identifier is called (an alias).</summary>
    public void End()
    {
        foreach (var p in _paths)
        {
            _p = p;
            if (p.HasPending && p.Frames.Count == 0 && _params is not null)
            {
                p.HasPending = false;
                if (p.Pending >= 0 && _marks) _a.Called(p.Pending);
                // and on past names that may expand to nothing (`CAT EMPTYM` calls CAT)
                for (int i = _tail.Count - 2; i >= 0 && _marks && p.Pending >= 0 && _a.MayBeEmpty(_tail[i + 1]); i--) _a.Called(_tail[i]);
            }
            FinalizePending();
        }
        FlushChain();
        if (_commitAtEof)
            foreach (var p in _paths)
            {
                _p = p;
                while (p.Frames.Count > 0) Close(p.Buf.Count, eof: true);
            }
        _paths = new List<Path> { new Path() };
        _conds.Clear();
        _params = null;
    }
}
