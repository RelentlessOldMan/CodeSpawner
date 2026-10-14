using System.Globalization;

namespace CodeSpawner.Cli;

/// <summary>
/// Tiny dependency-free arg parser: <c>--knob value</c> and <c>--flag[=true|false]</c>. Kept minimal
/// on purpose — no reflection (AOT-friendly) and no third-party dependency for a drop-on-a-machine exe.
/// </summary>
public static class ArgParser
{
    public static GenOptions ParseGen(string[] args) => ParseGen(args, 0);

    /// <param name="presetTokens">How many leading args a <c>--preset</c> expanded to; the user may override those.</param>
    public static GenOptions ParseGen(string[] args, int presetTokens)
    {
        var o = new GenOptions { Out = "" };
        var c = new Cursor(args, presetTokens);
        var given = new HashSet<string>(StringComparer.Ordinal);
        while (c.Next())
        {
            if (c.Key is null) throw new ArgException($"unexpected argument '{c.Raw}' (expected --knob)");
            string key = c.Key;
            given.Add(key);
            string Val() => c.Val();
            int I() { o.Explicit.Add(Canon(key)); return c.Int(); }
            double D() { o.Explicit.Add(Canon(key)); return c.Dbl(); }
            bool B() { o.Explicit.Add(Canon(key)); return c.Flag(); }

            switch (key)
            {
                case "out": o.Out = Val(); break;
                case "scale": o.Scale = D(); break;
                case "seed": o.Seed = I(); break;
                case "macro-density": o.MacroDensity = I(); break;
                case "giant-headers": o.GiantHeaders = I(); break;
                case "big-headers": o.BigHeaders = I(); break;
                case "med-headers": o.MedHeaders = I(); break;
                case "ordinary-headers": o.OrdinaryHeaders = I(); break;
                case "cfiles": o.CFiles = I(); break;
                case "giant-includers": o.GiantIncluders = I(); break;
                case "tiny-files": o.TinyFiles = I(); break;
                case "blob-files": o.BlobFiles = I(); break;
                case "max-header-mb": o.MaxHeaderMB = I(); break;
                case "compile-db": o.Explicit.Add("compile-db"); o.CompileDb = ParseCompileDb(Val()); break;
                case "build-output": o.BuildOutput = B(); break;
                case "dirs": o.Dirs = I(); break;
                case "depth": o.Depth = I(); break;
                case "linked-roots": o.LinkedRoots = I(); break;
                case "unresolved-includes": o.UnresolvedIncludes = I(); break;
                case "manifest": o.Manifest = B(); break;
                case "force": o.Force = B(); break;
                case "io-parallelism": o.IoParallelism = I(); break;
                // Batch 1 pathologies:
                case "dense-headers": o.DenseHeaders = I(); break;
                case "dense-under-mb": o.DenseUnderMb = I(); break;
                case "broad-token-files": o.BroadTokenFiles = I(); break;
                case "hot-token-share": o.HotTokenShare = D(); break;
                case "long-line-files": o.LongLineFiles = I(); break;
                case "max-line-bytes": o.MaxLineBytes = I(); break;
                case "no-newline": o.NoNewline = B(); break;
                case "encoding-mix": o.EncodingMix = I(); break;
                case "pathological-symbols": o.PathologicalSymbols = I(); break;
                case "dup-groups": o.DupGroups = I(); break;
                case "dup-copies": o.DupCopies = I(); break;
                case "shrink-seeds": o.ShrinkSeeds = I(); break;
                case "restream-seeds": o.RestreamSeeds = I(); break;
                // scan/shape-profile regeneration:
                case "from-profile": o.FromProfile = Val(); break;
                case "with-oracle": o.WithOracle = B(); break;
                case "oracle-scale": o.OracleScale = B(); break;
                case "oracle-chain": o.OracleChain = I(); break;
                case "oracle-fanout": o.OracleFanout = I(); break;
                case "oracle-depth": o.OracleDepth = I(); break;
                case "oracle-shared-leaves": o.OracleSharedLeaves = I(); break;
                case "oracle-reachable-frac": o.OracleReachableFrac = D(); break;
                case "oracle-indirect": o.OracleIndirect = I(); break;
                case "oracle-bytes": o.OracleBytes = B(); break;
                default: throw new ArgException($"unknown knob --{key}");
            }
        }

        if (string.IsNullOrWhiteSpace(o.Out)) throw new ArgException("--out <dir> is required");
        CheckGen(o, given);
        return o;
    }

    // Knobs gen --from-profile reads; every other knob would be ignored, so it is refused rather than recorded.
    private static readonly HashSet<string> ProfileKnobs = new(StringComparer.Ordinal) { "out", "seed", "force", "from-profile", "with-oracle" };

    private static void CheckGen(GenOptions o, HashSet<string> given)
    {
        if (o.FromProfile is not null)
        {
            var ignored = given.Where(k => !ProfileKnobs.Contains(k) && !k.StartsWith("oracle-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            if (ignored.Count > 0)
                throw new ArgException($"--from-profile takes its shape from the profile and ignores {string.Join(", ", ignored.Select(k => "--" + k))}; drop them (a --preset counts too)");
            var oracle = given.FirstOrDefault(k => k.StartsWith("oracle-", StringComparison.Ordinal));
            if (oracle is not null && !o.WithOracle) throw new ArgException($"--{oracle} requires --with-oracle");
        }
        else
        {
            // Plain gen writes its own func_i spine; the oracle overlay only runs on a profile regeneration. A default
            // value is let through: _meta.gen records these knobs, and replaying it passes them back.
            var d = new GenOptions { Out = "" };
            string? oracle = o.WithOracle != d.WithOracle ? "with-oracle"
                : o.OracleScale != d.OracleScale ? "oracle-scale"
                : o.OracleBytes != d.OracleBytes ? "oracle-bytes"
                : o.OracleChain != d.OracleChain ? "oracle-chain"
                : o.OracleFanout != d.OracleFanout ? "oracle-fanout"
                : o.OracleDepth != d.OracleDepth ? "oracle-depth"
                : o.OracleSharedLeaves != d.OracleSharedLeaves ? "oracle-shared-leaves"
                : o.OracleReachableFrac != d.OracleReachableFrac ? "oracle-reachable-frac"
                : o.OracleIndirect != d.OracleIndirect ? "oracle-indirect" : null;
            if (oracle is not null) throw new ArgException($"--{oracle} applies to gen --from-profile only (plain gen ignores it)");
        }

        // Ranges. Out-of-range values used to be clamped, ignored, or (--scale) overflow into a disabled population.
        if (!(o.Scale > 0 && o.Scale <= 1000)) throw new ArgException($"--scale must be in (0, 1000], got {o.Scale.ToString(CultureInfo.InvariantCulture)}");
        foreach (var (k, v) in new (string, int)[]
        {
            ("giant-headers", o.GiantHeaders), ("big-headers", o.BigHeaders), ("med-headers", o.MedHeaders),
            ("ordinary-headers", o.OrdinaryHeaders), ("cfiles", o.CFiles), ("giant-includers", o.GiantIncluders),
            ("tiny-files", o.TinyFiles), ("blob-files", o.BlobFiles), ("unresolved-includes", o.UnresolvedIncludes),
            ("macro-density", o.MacroDensity), ("dense-headers", o.DenseHeaders), ("broad-token-files", o.BroadTokenFiles),
            ("long-line-files", o.LongLineFiles), ("encoding-mix", o.EncodingMix), ("pathological-symbols", o.PathologicalSymbols),
            ("dup-groups", o.DupGroups), ("shrink-seeds", o.ShrinkSeeds), ("restream-seeds", o.RestreamSeeds),
            ("oracle-fanout", o.OracleFanout), ("oracle-depth", o.OracleDepth), ("oracle-shared-leaves", o.OracleSharedLeaves),
            ("oracle-indirect", o.OracleIndirect),
        })
            if (v < 0) throw new ArgException($"--{k} must be >= 0, got {v}");
        foreach (var (k, v, min) in new (string, int, int)[]
        {
            ("max-header-mb", o.MaxHeaderMB, 1), ("dense-under-mb", o.DenseUnderMb, 1), ("max-line-bytes", o.MaxLineBytes, 1),
            ("dirs", o.Dirs, 1), ("linked-roots", o.LinkedRoots, 1), ("dup-copies", o.DupCopies, 2),
            ("io-parallelism", o.IoParallelism, 1), ("oracle-chain", o.OracleChain, 2),
        })
            if (v < min) throw new ArgException($"--{k} must be >= {min}, got {v}");
        if (o.Depth is < 1 or > 12) throw new ArgException($"--depth must be in 1..12, got {o.Depth}");
        if (o.HotTokenShare is < 0 or > 1) throw new ArgException("--hot-token-share must be in [0, 1]");
        // 0 = all reachable; a tiny fraction would size the dead block as live/frac nodes.
        if (o.OracleReachableFrac != 0 && o.OracleReachableFrac is < 0.01 or > 1)
            throw new ArgException("--oracle-reachable-frac must be 0 (all reachable) or in [0.01, 1]");
    }

    public static VerifyOptions ParseVerify(string[] args)
    {
        var o = new VerifyOptions { Corpus = "" };
        var c = new Cursor(args);
        while (c.Next())
        {
            if (c.Key is null) throw new ArgException($"unexpected argument '{c.Raw}' (expected --corpus / --manifest)");
            switch (c.Key)
            {
                case "corpus": o.Corpus = c.Val(); break;
                case "manifest": o.Manifest = c.Val(); break;
                default: throw new ArgException($"unknown option --{c.Key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Corpus)) throw new ArgException("--corpus <dir> is required");
        return o;
    }

    public static ScanOptions ParseScan(string[] args)
    {
        var o = new ScanOptions { Tree = "" };
        var c = new Cursor(args);
        bool posture = false;
        void Posture(Profile.PrivacyPosture p)
        {
            if (posture && o.Posture != p) throw new ArgException("--structure-only and --content-stats are exclusive; pick one");
            posture = true; o.Posture = p;
        }
        while (c.Next())
        {
            if (c.Key is null)
            {
                // first positional is the tree to scan
                if (string.IsNullOrEmpty(o.Tree)) { o.Tree = c.Raw; continue; }
                throw new ArgException($"unexpected argument '{c.Raw}'");
            }
            switch (c.Key)
            {
                case "tree": o.Tree = c.Val(); break;
                case "out": o.Out = c.Val(); break;
                // posture switches, not booleans: a word after them is the positional tree, never a value
                case "structure-only": c.NoValue(); Posture(Profile.PrivacyPosture.StructureOnly); break;
                case "content-stats": c.NoValue(); Posture(Profile.PrivacyPosture.ContentStats); break;
                case "min-cluster": o.MinCluster = c.Int(); break;
                case "sample": o.Sample = c.Int(); break;
                default: throw new ArgException($"unknown option --{c.Key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Tree)) throw new ArgException("scan <tree> is required");
        if (string.IsNullOrWhiteSpace(o.Out)) throw new ArgException("--out <profile.json> is required");
        if (o.MinCluster < 1) throw new ArgException("--min-cluster must be >= 1");
        // scan never writes into the tree it reads (the profile would land in it, or overwrite one of its files).
        string tree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.Tree)) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(o.Out).StartsWith(tree, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgException($"--out '{o.Out}' is inside the tree being scanned; write the profile somewhere else");
        return o;
    }

    public static Mutation.MutateOptions ParseMutate(string[] args)
    {
        var o = new Mutation.MutateOptions { Corpus = "" };
        var c = new Cursor(args);
        while (c.Next())
        {
            if (c.Key is null) throw new ArgException($"unexpected argument '{c.Raw}'");
            switch (c.Key)
            {
                case "corpus": o.Corpus = c.Val(); break;
                case "manifest": o.Manifest = c.Val(); break;
                case "seed": o.Seed = c.Int(); break;
                case "edits": o.Edits = c.Int(); break;
                case "step": o.Step = c.Int(); break;
                case "through": o.Through = c.Flag(); break;
                case "restream": o.Restream = c.Flag(); break;
                case "target": o.Target = c.Val().ToLowerInvariant(); break;
                case "files-changed": o.FilesChanged = c.Int(); break;
                case "edit-density": o.EditDensity = c.Dbl(); break;
                case "giant-min-mb": o.GiantMinMb = c.Int(); break;
                case "edit-kind": o.EditKind = c.Val().ToLowerInvariant(); break;
                case "decoy-fraction": o.DecoyFraction = c.Dbl(); break;
                case "three-way": o.ThreeWay = c.Flag(); break;
                case "overlap-fraction": o.OverlapFraction = c.Dbl(); break;
                case "conflict-edges": o.ConflictEdges = c.Flag(); break;
                case "giant-edit": o.GiantEdit = c.Val().ToLowerInvariant(); break;
                case "shard-size": o.ShardSize = c.Int(); break;
                default: throw new ArgException($"unknown option --{c.Key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Corpus)) throw new ArgException("--corpus <dir> is required");

        if (o.IsBulk)
        {
            if (o.Target is not ("source" or "headers" or "giant" or "all"))
                throw new ArgException($"--target must be one of source|headers|giant|all, got '{o.Target}'");
            if (o.Step is not null) throw new ArgException("--target (bulk mode) cannot be combined with --step");
            if (o.EditKind is not (null or "content" or "line-insert" or "line-delete" or "eol" or "whitespace" or "encoding" or "binary" or "metadata" or "mixed" or "rename"))
                throw new ArgException($"--edit-kind must be one of content|line-insert|line-delete|eol|whitespace|encoding|binary|metadata|mixed|rename, got '{o.EditKind}'");
            if (o.DecoyFraction is < 0 or > 1) throw new ArgException("--decoy-fraction must be in [0, 1]");
            if (o.DecoyFraction > 0 && o.Kind != "rename") throw new ArgException("--decoy-fraction only applies to --edit-kind rename");
            if (o.OverlapFraction is < 0 or > 1) throw new ArgException("--overlap-fraction must be in [0, 1]");
            if (o.ThreeWay && o.EditKind is not null) throw new ArgException("--three-way cannot be combined with --edit-kind (it uses content edits)");
            if (o.ConflictEdges && !o.ThreeWay) throw new ArgException("--conflict-edges requires --three-way");
            if (o.GiantEdit is not (null or "strided" or "single")) throw new ArgException($"--giant-edit must be strided|single, got '{o.GiantEdit}'");
            if (o.EditDensity <= 0 || o.EditDensity > 1) throw new ArgException("--edit-density must be in (0, 1]");
            if (o.FilesChanged is { } fc && fc < 1) throw new ArgException("--files-changed must be >= 1");
            if (o.GiantMinMb < 1) throw new ArgException("--giant-min-mb must be >= 1");
            if (o.ShardSize < 0) throw new ArgException("--shard-size must be >= 0 (0 = monolithic delta)");
            if (o.ShardSize > 0 && o.ThreeWay) throw new ArgException("--shard-size applies to the 2-way delta; it cannot be combined with --three-way");
            return o;
        }

        if (o.EditKind is not null) throw new ArgException("--edit-kind requires --target (bulk mode)");
        if (o.ThreeWay) throw new ArgException("--three-way requires --target (bulk mode)");
        if (o.ConflictEdges) throw new ArgException("--conflict-edges requires --three-way (bulk mode)");
        if (o.GiantEdit is not null) throw new ArgException("--giant-edit requires --target (bulk mode)");
        if (o.ShardSize > 0) throw new ArgException("--shard-size requires --target (bulk mode)");
        if (o.DecoyFraction > 0) throw new ArgException("--decoy-fraction requires --target (bulk mode)");
        if (o.FilesChanged is not null) throw new ArgException("--files-changed requires --target (bulk mode)");
        if (o.Edits < 1) throw new ArgException("--edits must be >= 1");
        if (o.Step is not null && o.Through) throw new ArgException("--step and --through are mutually exclusive");
        if (o.Step is { } k && (k < 1 || k > o.Edits)) throw new ArgException($"--step must be in 1..{o.Edits}");
        return o;
    }

    /// <summary>
    /// Walks the args the same way for every command: <c>--key value</c>, <c>--key=value</c>, and boolean flags as
    /// <c>--flag</c>, <c>--flag=false</c> or <c>--flag false</c> (the space form only consumes a boolean word, so a
    /// following positional or knob is never eaten). Keys are lower-cased; a non-<c>--</c> arg has a null Key.
    /// </summary>
    private sealed class Cursor(string[] args, int userFrom = 0)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int _i = -1;
        private string? _inline;
        public string Raw { get; private set; } = "";
        public string? Key { get; private set; }

        public bool Next()
        {
            if (++_i >= args.Length) return false;
            Raw = args[_i];
            _inline = null;
            if (!Raw.StartsWith("--", StringComparison.Ordinal)) { Key = null; return true; }
            string k = Raw[2..];
            int eq = k.IndexOf('=');
            if (eq >= 0) { _inline = k[(eq + 1)..]; k = k[..eq]; }
            Key = k.ToLowerInvariant();
            // A repeated option used to be last-wins without a word. A preset's own tokens (the first userFrom
            // args) are exempt: the user overriding them is the point.
            if (_i >= userFrom && !_seen.Add(Key)) throw new ArgException($"--{Key} is given twice");
            return true;
        }

        public string Val()
        {
            if (_inline != null) return _inline;
            if (_i + 1 >= args.Length) throw new ArgException($"--{Key} needs a value");
            return args[++_i];
        }

        public int Int() => ArgParser.Int(Key!, Val());
        public double Dbl() => ArgParser.Dbl(Key!, Val());

        public bool Flag()
        {
            if (_inline != null) return ParseBool(_inline);
            if (_i + 1 < args.Length && IsBoolWord(args[_i + 1])) return ParseBool(args[++_i]);
            return true;
        }

        public void NoValue()
        {
            if (_inline != null) throw new ArgException($"--{Key} takes no value");
        }

        private static bool IsBoolWord(string s) =>
            s.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off";
    }

    // Map CLI knob spelling to the GenOptions.Eff knob name (PascalCase used in Eff calls).
    private static string Canon(string key) => key.ToLowerInvariant() switch
    {
        "giant-headers" => "GiantHeaders",
        "big-headers" => "BigHeaders",
        "med-headers" => "MedHeaders",
        "ordinary-headers" => "OrdinaryHeaders",
        "cfiles" => "CFiles",
        "tiny-files" => "TinyFiles",
        "blob-files" => "BlobFiles",
        "macro-density" => "MacroDensity",
        "dirs" => "Dirs",
        "dense-headers" => "DenseHeaders",
        "broad-token-files" => "BroadTokenFiles",
        "long-line-files" => "LongLineFiles",
        "encoding-mix" => "EncodingMix",
        "pathological-symbols" => "PathologicalSymbols",
        "dup-groups" => "DupGroups",
        _ => key,
    };

    // Numeric knob values route through these so a malformed value fails as a clean ArgException (→ exit 2,
    // "error: ...") like every other arg error, instead of an unhandled FormatException stack dump.
    private static int Int(string key, string val) =>
        int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new ArgException($"--{key} expects an integer, got '{val}'");

    // Finite only: "NaN" / "Infinity" parse as doubles but slip past every range check (NaN compares false
    // both ways) and only blow up later in the JSON writer — after mutate has already edited files in place.
    private static double Dbl(string key, string val) =>
        double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : throw new ArgException($"--{key} expects a finite number, got '{val}'");

    private static bool ParseBool(string s) => s.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new ArgException($"expected a boolean, got '{s}'"),
    };

    private static CompileDbMode ParseCompileDb(string s) => s.ToLowerInvariant() switch
    {
        "none" => CompileDbMode.None,
        "partial" => CompileDbMode.Partial,
        "full" => CompileDbMode.Full,
        _ => throw new ArgException($"--compile-db must be none|partial|full, got '{s}'"),
    };
}

public sealed class ArgException(string message) : Exception(message);
