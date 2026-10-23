namespace CodeSpawner.Firmware;

/// <summary>
/// Rule 3's call stream (docs/firmware-design.md, "Uses"): follows <c>##</c> chains and calls through a token stream
/// that spans lines and conditional directives, and reports each paste use with its arguments, each literal chain,
/// and which names are called and named. One tracker is one stream for one run; a define body is its own stream,
/// where a call or chain holding one of the define's parameters is a template (built when the define is used).
/// </summary>
internal sealed class Tracker : ITokenSink
{
    // One token of the stream as a ## operand: a name id, a parameter, or anything else.
    private const int OpParam = -2, OpOther = -1;

    private sealed class Frame
    {
        public int Callee = -1;                   // the name called (-1: computed, or a parameter)
        public int Open;                          // its ( in the buffer
        public bool Marked, Computed, Crossed, Template;
        public int Depth;                         // parentheses open inside the current argument
        public Frame Clone() => (Frame)MemberwiseClone();
    }

    private sealed class State
    {
        public readonly List<Frame> Frames = new();
        public int BufLen;
        public bool LastClosedUse;
    }

    private readonly FwSpec _a;
    private readonly int _run;
    private readonly bool _commitAtEof, _marks;
    private DefineParse? _params;
    private List<Frame> _frames = new();
    private readonly List<State> _conds = new();
    private readonly List<XTok> _buf = new();     // the tokens since the outermost open call
    private int _pending = OpOther;               // an identifier not yet known to be called (a name id or OpParam)
    private bool _hasPending, _lastClosedUse;
    private readonly IntList _chain = new();      // the ## chain being read: its operands
    private bool _afterHash;
    private int _last = OpOther;                  // the previous token as a ## operand
    private bool _hasLast;

    /// <param name="commitAtEof">Calls open at the end are used, their missing arguments wildcards (the shadow
    /// lexing, where comments and strings are tokens).</param>
    public Tracker(FwSpec a, int run, bool commitAtEof)
    {
        _a = a;
        _run = run;
        _commitAtEof = commitAtEof;
        _marks = !commitAtEof;   // comments and strings never call or name
    }

    /// <summary>Starts a new stream; <paramref name="defineParams"/> is the define whose body this is, if any.</summary>
    public void Begin(DefineParse? defineParams)
    {
        _params = defineParams;
        _frames.Clear();
        _conds.Clear();
        _buf.Clear();
        _hasPending = _lastClosedUse = _afterHash = _hasLast = false;
        _chain.Count = 0;
    }

    private Frame? Top => _frames.Count > 0 ? _frames[^1] : null;

    private int Operand(Tok kind, ReadOnlySpan<byte> text)
    {
        if (kind == Tok.Ident) return _params is not null && _params.IsParam(text) ? OpParam : _a.Names.GetOrAdd(text);
        if (kind == Tok.Number && FwSpec.IsIdNumber(text)) return _a.Names.GetOrAdd(text);
        return OpOther;
    }

    private void Buf(XTok t)
    {
        if (_frames.Count > 0) _buf.Add(t);
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
            if (!_afterHash)
            {
                if (_chain.Count == 0) _chain.Add(_hasLast ? _last : OpOther);
                _afterHash = true;
            }
            FinalizePending();
            Buf(new XTok(XTok.Other));
            _lastClosedUse = false;
            return;
        }
        int op = Operand(kind, text);
        if (_afterHash) { _chain.Add(op); _afterHash = false; }
        else FlushChain();
        _last = op;
        _hasLast = true;

        if (kind == Tok.Ident)
        {
            FinalizePending();
            Buf(AsTok(kind, op));
            _pending = op;
            _hasPending = true;
            _lastClosedUse = false;
            return;
        }
        switch (kind)
        {
            case Tok.LParen:
                if (_hasPending)
                {
                    int callee = _pending;
                    _hasPending = false;
                    if (callee >= 0 && _marks) _a.Called(callee);
                    _buf.Add(new XTok(XTok.LParen));
                    _frames.Add(new Frame { Callee = callee >= 0 ? callee : -1, Open = _buf.Count - 1, Template = callee == OpParam });
                }
                else if (_lastClosedUse)
                {
                    _buf.Add(new XTok(XTok.LParen));
                    _frames.Add(new Frame { Computed = true, Open = _buf.Count - 1 });
                }
                else if (Top is { } t) { t.Depth++; _buf.Add(new XTok(XTok.LParen)); }
                _lastClosedUse = false;
                break;
            case Tok.RParen:
                FinalizePending();
                _lastClosedUse = false;
                if (_frames.Count == 0) break;
                var top = _frames[^1];
                _buf.Add(new XTok(XTok.RParen));
                if (top.Depth > 0) { top.Depth--; break; }
                _lastClosedUse = Close(_buf.Count - 1, eof: false);
                break;
            default:
                FinalizePending();
                Buf(AsTok(kind, op));
                _lastClosedUse = false;
                break;
        }
    }

    // An identifier that was not called: inside a call it is passed (a paste macro passed marks the call), outside
    // one it is named.
    private void FinalizePending()
    {
        if (!_hasPending) return;
        _hasPending = false;
        if (_pending < 0) return;
        if (Top is { } t)
        {
            if (_marks) _a.Called(_pending);
            if (_a.IsPaster(_pending, _run)) t.Marked = true;
        }
        else if (_marks) _a.Named(_pending, _run);
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
        var f = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        bool use = f.Computed || (f.Callee >= 0 && (f.Marked || _a.IsPaster(f.Callee, _run)));
        if (use && !f.Template)
        {
            var inner = _buf.GetRange(f.Open + 1, close - f.Open - 1);
            bool template = false;
            foreach (var t in inner) if (t.K == XTok.Param) { template = true; break; }
            if (!template) _a.Use(_run, f.Callee, Split(inner), pool: f.Computed || f.Crossed, eof);
        }
        if (_frames.Count == 0) _buf.RemoveRange(Floor, _buf.Count - Floor);
        return use;
    }

    private int Floor => _conds.Count > 0 ? Math.Min(_conds[^1].BufLen, _buf.Count) : 0;

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

    /// <summary>A conditional directive: <c>#if</c>-family opens, <c>#elif</c>/<c>#else</c> restarts from the
    /// state at the <c>#if</c>, <c>#endif</c> continues from the last branch. A call open across one is used with
    /// its own pool.</summary>
    public void Directive(LineKind kind)
    {
        FinalizePending();
        FlushChain();
        _afterHash = _hasLast = false;
        foreach (var f in _frames) f.Crossed = true;
        switch (kind)
        {
            case LineKind.Cond:
                _conds.Add(Snapshot());
                break;
            case LineKind.Else:
                if (_conds.Count > 0) Restore(_conds[^1]);
                break;
            case LineKind.EndIf:
                if (_conds.Count > 0) _conds.RemoveAt(_conds.Count - 1);
                break;
        }
    }

    private State Snapshot()
    {
        var s = new State { LastClosedUse = _lastClosedUse, BufLen = _buf.Count };
        foreach (var f in _frames) s.Frames.Add(f.Clone());
        return s;
    }

    private void Restore(State s)
    {
        _frames = new List<Frame>(s.Frames.Count);
        foreach (var f in s.Frames) _frames.Add(f.Clone());
        if (_buf.Count > s.BufLen) _buf.RemoveRange(s.BufLen, _buf.Count - s.BufLen);
        _lastClosedUse = s.LastClosedUse;
    }

    /// <summary>End of the stream: open calls are dropped, or (commit) closed top-down. An object-like body's last
    /// identifier is called (an alias).</summary>
    public void End()
    {
        if (_hasPending && _frames.Count == 0 && _params is { IsFunc: false })
        {
            _hasPending = false;
            if (_pending >= 0 && _marks) _a.Called(_pending);
        }
        FinalizePending();
        FlushChain();
        if (_commitAtEof)
            while (_frames.Count > 0) Close(_buf.Count, eof: true);
        _frames.Clear();
        _conds.Clear();
        _buf.Clear();
        _params = null;
    }
}
