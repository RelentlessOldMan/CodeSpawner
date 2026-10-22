namespace CodeSpawner.Firmware;

/// <summary>
/// Rule 2's call stream (docs/firmware-design.md, "Pieces"): follows paste-macro calls through a token stream that
/// spans lines and conditional directives, and reports the pieces they pass. One tracker is one stream; a define
/// body is its own stream with the define's parameters excluded.
/// </summary>
internal sealed class Tracker : ITokenSink
{
    private sealed class Frame
    {
        public bool Paster, Marked;
        public int Depth;
        public readonly IntList Idents = new();

        public Frame Clone()
        {
            var f = new Frame { Paster = Paster, Marked = Marked, Depth = Depth };
            for (int i = 0; i < Idents.Count; i++) f.Idents.Add(Idents[i]);
            return f;
        }
    }

    private sealed class State
    {
        public readonly List<Frame> Frames = new();
        public bool LastClosedPaster;
    }

    private readonly FwSpec _a;
    private readonly int _mask;           // runs the pieces count for
    private readonly bool _commitAtEof;
    private DefineParse? _params;
    private List<Frame> _frames = new();
    private readonly List<(State AtIf, bool Any)> _conds = new();
    private int _pending = -1;
    private bool _pendingParam;
    private bool _lastClosedPaster;
    private int _prevIdent = -1;
    private bool _pasteNext;

    public Tracker(FwSpec a, int mask, bool commitAtEof)
    {
        _a = a;
        _mask = mask;
        _commitAtEof = commitAtEof;
    }

    /// <summary>Starts a new stream; <paramref name="defineParams"/> is the define whose body this is, if any.</summary>
    public void Begin(DefineParse? defineParams)
    {
        _params = defineParams;
        _frames.Clear();
        _conds.Clear();
        _pending = -1;
        _lastClosedPaster = false;
        _prevIdent = -1;
        _pasteNext = false;
    }

    private Frame? Top => _frames.Count > 0 ? _frames[^1] : null;

    public void Token(Tok kind, ReadOnlySpan<byte> text)
    {
        if (kind == Tok.Ident)
        {
            bool isParam = _params is not null && _params.IsParam(text);
            int id = isParam ? -1 : _a.Names.GetOrAdd(text);
            if (_pasteNext && !isParam) _a.Piece(id, _mask, s5: false);
            _pasteNext = false;
            FinalizePending();
            _pending = isParam ? -2 : id;   // -2: a parameter
            _pendingParam = isParam;
            _lastClosedPaster = false;
            _prevIdent = id;
            return;
        }
        if (kind == Tok.HashHash)
        {
            if (_prevIdent >= 0) _a.Piece(_prevIdent, _mask, s5: false);
            FinalizePending();
            _pasteNext = true;
            _prevIdent = -1;
            _lastClosedPaster = false;
            return;
        }
        _pasteNext = false;
        _prevIdent = -1;
        switch (kind)
        {
            case Tok.LParen:
                if (_pending != -1)
                {
                    bool paster = !_pendingParam && _a.IsPaster(_pending);
                    if (!_pendingParam) Top?.Idents.Add(_pending);
                    _pending = -1;
                    _frames.Add(new Frame { Paster = paster });
                }
                else if (_lastClosedPaster) _frames.Add(new Frame { Paster = true });
                else if (Top is { } t) t.Depth++;
                _lastClosedPaster = false;
                break;
            case Tok.RParen:
                FinalizePending();
                if (_frames.Count == 0) { _lastClosedPaster = false; break; }
                var top = _frames[^1];
                if (top.Depth > 0) { top.Depth--; _lastClosedPaster = false; break; }
                Close();
                _lastClosedPaster = top.Paster;
                break;
            default:
                FinalizePending();
                _lastClosedPaster = false;
                break;
        }
    }

    private void Close()
    {
        var f = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        if (f.Paster || f.Marked)
            for (int i = 0; i < f.Idents.Count; i++) _a.Piece(f.Idents[i], _mask, s5: true);
        if (Top is { } parent)
            for (int i = 0; i < f.Idents.Count; i++) parent.Idents.Add(f.Idents[i]);
    }

    private void FinalizePending()
    {
        if (_pending == -1) return;
        if (!_pendingParam && Top is { } t)
        {
            t.Idents.Add(_pending);
            if (_a.IsPaster(_pending)) t.Marked = true;
        }
        _pending = -1;
    }

    /// <summary>A conditional directive: <c>#if</c>-family opens, <c>#elif</c>/<c>#else</c> restarts from the
    /// state at the <c>#if</c>, <c>#endif</c> continues from the last branch.</summary>
    public void Directive(LineKind kind)
    {
        FinalizePending();
        _pasteNext = false;
        _prevIdent = -1;
        switch (kind)
        {
            case LineKind.Cond:
                _conds.Add((Snapshot(), true));
                break;
            case LineKind.Else:
                if (_conds.Count > 0) Restore(_conds[^1].AtIf);
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

    /// <summary>End of the stream: open calls are dropped, or (shadow) closed and committed top-down.</summary>
    public void End()
    {
        FinalizePending();
        if (_commitAtEof)
            while (_frames.Count > 0) Close();
        _frames.Clear();
        _conds.Clear();
        _params = null;
    }
}
