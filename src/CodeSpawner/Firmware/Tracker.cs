namespace CodeSpawner.Firmware;

/// <summary>
/// Rule 2's call stream (docs/firmware-design.md, "Pieces"): follows <c>##</c> chains and paste-macro calls through a
/// token stream that spans lines and conditional directives, and reports the pieces they give with their sides. One
/// tracker is one stream for one run; a define body is its own stream with the define's parameters excluded.
/// </summary>
internal sealed class Tracker : ITokenSink
{
    public const int S = 1, E = 2, Both = 3;   // sides: a piece starts (S) or ends (E) a pasted name

    // One token of the stream as a ## operand or an argument token: a piece id, a parameter, or anything else.
    private const int OpParam = -2, OpOther = -1;

    private sealed class Frame
    {
        public int Callee = -1;                   // the paste macro called (-1: none)
        public bool Paster, Marked, Computed, Crossed;
        public int Depth;                         // parentheses open inside the current argument
        public int Arg;                           // the current argument's index
        public readonly IntList ArgTokens = new();   // per argument: its token count
        public readonly IntList ArgFirst = new();    // per argument: its first token (a piece id, OpParam or OpOther)
        public readonly IntList Pieces = new();      // every piece id among the arguments ...
        public readonly IntList PieceArg = new();    // ... and the argument it is in

        public Frame()
        {
            ArgTokens.Add(0);
            ArgFirst.Add(OpOther);
        }

        public void Tok(int op)
        {
            if (ArgTokens.A[Arg]++ == 0) ArgFirst.A[Arg] = op;
            if (op >= 0) { Pieces.Add(op); PieceArg.Add(Arg); }
        }

        public void NextArg()
        {
            Arg++;
            ArgTokens.Add(0);
            ArgFirst.Add(OpOther);
        }

        public Frame Clone()
        {
            var f = new Frame { Callee = Callee, Paster = Paster, Marked = Marked, Computed = Computed, Crossed = Crossed, Depth = Depth, Arg = Arg };
            f.ArgTokens.Count = f.ArgFirst.Count = 0;
            for (int i = 0; i < ArgTokens.Count; i++) { f.ArgTokens.Add(ArgTokens[i]); f.ArgFirst.Add(ArgFirst[i]); }
            for (int i = 0; i < Pieces.Count; i++) { f.Pieces.Add(Pieces[i]); f.PieceArg.Add(PieceArg[i]); }
            return f;
        }
    }

    private sealed class State
    {
        public readonly List<Frame> Frames = new();
        public bool LastClosedPaster;
    }

    private readonly FwSpec _a;
    private readonly int _run;
    private readonly bool _commitAtEof;
    private DefineParse? _params;
    private List<Frame> _frames = new();
    private readonly List<State> _conds = new();
    private int _pending = OpOther;           // an identifier not yet known to be called (a piece id or OpParam)
    private bool _hasPending, _lastClosedPaster;
    private readonly IntList _chain = new();  // the ## chain being read: its operands
    private bool _afterHash;
    private int _last = OpOther;              // the previous token as a ## operand
    private bool _hasLast;

    public Tracker(FwSpec a, int run, bool commitAtEof)
    {
        _a = a;
        _run = run;
        _commitAtEof = commitAtEof;
    }

    /// <summary>Starts a new stream; <paramref name="defineParams"/> is the define whose body this is, if any.</summary>
    public void Begin(DefineParse? defineParams)
    {
        _params = defineParams;
        _frames.Clear();
        _conds.Clear();
        _hasPending = _lastClosedPaster = _afterHash = _hasLast = false;
        _chain.Count = 0;
    }

    private Frame? Top => _frames.Count > 0 ? _frames[^1] : null;

    private int Operand(Tok kind, ReadOnlySpan<byte> text)
    {
        if (kind == Tok.Ident) return _params is not null && _params.IsParam(text) ? OpParam : _a.Names.GetOrAdd(text);
        if (kind == Tok.Number && FwSpec.IsIdNumber(text)) return _a.Names.GetOrAdd(text);
        return OpOther;
    }

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
            Top?.Tok(OpOther);
            _lastClosedPaster = false;
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
            _pending = op;
            _hasPending = true;
            _lastClosedPaster = false;
            return;
        }
        switch (kind)
        {
            case Tok.LParen:
                if (_hasPending)
                {
                    int callee = _pending;
                    _hasPending = false;
                    Top?.Tok(callee);
                    Top?.Tok(OpOther);
                    bool paster = callee >= 0 && _a.IsPaster(callee, _run);
                    _frames.Add(new Frame { Callee = paster ? callee : -1, Paster = paster });
                }
                else if (_lastClosedPaster)
                {
                    Top?.Tok(OpOther);
                    _frames.Add(new Frame { Computed = true });
                }
                else if (Top is { } t) { t.Depth++; t.Tok(OpOther); }
                _lastClosedPaster = false;
                break;
            case Tok.RParen:
                FinalizePending();
                if (_frames.Count == 0) { _lastClosedPaster = false; break; }
                var top = _frames[^1];
                if (top.Depth > 0) { top.Depth--; top.Tok(OpOther); _lastClosedPaster = false; break; }
                Close(eof: false);
                _lastClosedPaster = top.Paster || top.Computed;
                break;
            case Tok.Comma:
                FinalizePending();
                if (Top is { } c) { if (c.Depth == 0) c.NextArg(); else c.Tok(OpOther); }
                _lastClosedPaster = false;
                break;
            default:
                FinalizePending();
                Top?.Tok(op);
                _lastClosedPaster = false;
                break;
        }
    }

    // An identifier that was not called: an argument token, and a paste macro named without a call marks the call.
    private void FinalizePending()
    {
        if (!_hasPending) return;
        _hasPending = false;
        if (Top is not { } t) return;
        t.Tok(_pending);
        if (_pending >= 0 && _a.IsPaster(_pending, _run)) t.Marked = true;
    }

    // A chain's operands: one starts a name when every operand before it is a parameter, ends one when every one after
    // it is. A literal operand is a piece with those sides (never expanded); a parameter is not a piece.
    private void FlushChain()
    {
        int n = _chain.Count;
        _chain.Count = 0;
        if (n < 2) return;
        var ops = _chain.A;
        int firstNonParam = 0;
        while (firstNonParam < n && ops[firstNonParam] == OpParam) firstNonParam++;
        int lastNonParam = n - 1;
        while (lastNonParam >= 0 && ops[lastNonParam] == OpParam) lastNonParam--;
        for (int i = 0; i < n; i++)
        {
            if (ops[i] < 0) continue;
            int sides = (i <= firstNonParam ? S : 0) | (i >= lastNonParam ? E : 0);
            if (sides != 0) _a.Piece(ops[i], _run, sides, 0);
        }
    }

    private void Close(bool eof)
    {
        var f = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        bool all = f.Marked || f.Computed || (f.Paster && (f.Crossed || eof));
        if (f.Paster || all)
        {
            for (int i = 0; i < f.Pieces.Count; i++)
            {
                int arg = f.PieceArg[i];
                if (!all && f.ArgTokens[arg] == 1)
                {
                    int slot = _a.SlotOf(f.Callee, arg, _run);
                    int sides = slot & Both, dirs = sides | ((slot & FwSpec.Interior) != 0 ? Both : 0);
                    _a.Piece(f.Pieces[i], _run, sides, dirs);
                }
                else _a.Piece(f.Pieces[i], _run, Both, Both);
            }
        }
        if (Top is { } parent)
            for (int i = 0; i < f.Pieces.Count; i++) parent.Tok(f.Pieces[i]);
    }

    /// <summary>A conditional directive: <c>#if</c>-family opens, <c>#elif</c>/<c>#else</c> restarts from the
    /// state at the <c>#if</c>, <c>#endif</c> continues from the last branch. A call open across one gives both
    /// sides to all its arguments.</summary>
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
        var s = new State { LastClosedPaster = _lastClosedPaster };
        foreach (var f in _frames) s.Frames.Add(f.Clone());
        return s;
    }

    private void Restore(State s)
    {
        _frames = new List<Frame>(s.Frames.Count);
        foreach (var f in s.Frames) _frames.Add(f.Clone());
        _lastClosedPaster = s.LastClosedPaster;
    }

    /// <summary>End of the stream: open calls are dropped, or (commit) closed top-down with both sides.</summary>
    public void End()
    {
        FinalizePending();
        FlushChain();
        if (_commitAtEof)
            while (_frames.Count > 0) Close(eof: true);
        _frames.Clear();
        _conds.Clear();
        _params = null;
    }
}
