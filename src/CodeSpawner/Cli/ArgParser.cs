using System.Globalization;

namespace CodeSpawner.Cli;

/// <summary>
/// Tiny dependency-free arg parser: <c>--knob value</c> and <c>--flag[=true|false]</c>. Kept minimal
/// on purpose — no reflection (AOT-friendly) and no third-party dependency for a drop-on-a-machine exe.
/// </summary>
public static class ArgParser
{
    public static GenOptions ParseGen(string[] args)
    {
        var o = new GenOptions { Out = "" };
        var ci = CultureInfo.InvariantCulture;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgException($"unexpected argument '{a}' (expected --knob)");

            string key = a[2..];
            string? inlineVal = null;
            int eq = key.IndexOf('=');
            if (eq >= 0) { inlineVal = key[(eq + 1)..]; key = key[..eq]; }

            // Presets are handled by the caller before this point; ignore if it slips through.
            string Val()
            {
                if (inlineVal != null) return inlineVal;
                if (i + 1 >= args.Length) throw new ArgException($"--{key} needs a value");
                return args[++i];
            }

            int I() { o.Explicit.Add(Canon(key)); return Int(key, Val()); }
            double D() { o.Explicit.Add(Canon(key)); return Dbl(key, Val()); }
            bool B() { o.Explicit.Add(Canon(key)); return inlineVal == null || ParseBool(inlineVal); }

            switch (key.ToLowerInvariant())
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
        return o;
    }

    public static VerifyOptions ParseVerify(string[] args)
    {
        var o = new VerifyOptions { Corpus = "" };
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgException($"unexpected argument '{a}' (expected --corpus / --manifest)");
            string key = a[2..].ToLowerInvariant();
            string Val() { if (i + 1 >= args.Length) throw new ArgException($"--{key} needs a value"); return args[++i]; }
            switch (key)
            {
                case "corpus": o.Corpus = Val(); break;
                case "manifest": o.Manifest = Val(); break;
                default: throw new ArgException($"unknown option --{key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Corpus)) throw new ArgException("--corpus <dir> is required");
        return o;
    }

    public static ScanOptions ParseScan(string[] args)
    {
        var o = new ScanOptions { Tree = "" };
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Val() { if (i + 1 >= args.Length) throw new ArgException($"'{a}' needs a value"); return args[++i]; }

            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                // first positional is the tree to scan
                if (string.IsNullOrEmpty(o.Tree)) { o.Tree = a; continue; }
                throw new ArgException($"unexpected argument '{a}'");
            }
            string key = a[2..].ToLowerInvariant();
            switch (key)
            {
                case "tree": o.Tree = Val(); break;
                case "out": o.Out = Val(); break;
                case "structure-only": o.Posture = Profile.PrivacyPosture.StructureOnly; break;
                case "content-stats": o.Posture = Profile.PrivacyPosture.ContentStats; break;
                case "min-cluster": o.MinCluster = Int(key, Val()); break;
                case "sample": o.Sample = Int(key, Val()); break;
                default: throw new ArgException($"unknown option --{key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Tree)) throw new ArgException("scan <tree> is required");
        if (string.IsNullOrWhiteSpace(o.Out)) throw new ArgException("--out <profile.json> is required");
        if (o.MinCluster < 1) throw new ArgException("--min-cluster must be >= 1");
        return o;
    }

    public static Mutation.MutateOptions ParseMutate(string[] args)
    {
        var o = new Mutation.MutateOptions { Corpus = "" };
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgException($"unexpected argument '{a}'");
            string key = a[2..].ToLowerInvariant();
            string Val() { if (i + 1 >= args.Length) throw new ArgException($"--{key} needs a value"); return args[++i]; }
            switch (key)
            {
                case "corpus": o.Corpus = Val(); break;
                case "manifest": o.Manifest = Val(); break;
                case "seed": o.Seed = Int(key, Val()); break;
                case "edits": o.Edits = Int(key, Val()); break;
                case "step": o.Step = Int(key, Val()); break;
                case "through": o.Through = true; break;
                case "restream": o.Restream = true; break;
                case "target": o.Target = Val().ToLowerInvariant(); break;
                case "files-changed": o.FilesChanged = Int(key, Val()); break;
                case "edit-density": o.EditDensity = Dbl(key, Val()); break;
                case "giant-min-mb": o.GiantMinMb = Int(key, Val()); break;
                default: throw new ArgException($"unknown option --{key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Corpus)) throw new ArgException("--corpus <dir> is required");

        if (o.IsBulk)
        {
            if (o.Target is not ("source" or "headers" or "giant" or "all"))
                throw new ArgException($"--target must be one of source|headers|giant|all, got '{o.Target}'");
            if (o.Step is not null) throw new ArgException("--target (bulk mode) cannot be combined with --step");
            if (o.EditDensity <= 0 || o.EditDensity > 1) throw new ArgException("--edit-density must be in (0, 1]");
            if (o.FilesChanged is { } fc && fc < 1) throw new ArgException("--files-changed must be >= 1");
            if (o.GiantMinMb < 1) throw new ArgException("--giant-min-mb must be >= 1");
            return o;
        }

        if (o.FilesChanged is not null) throw new ArgException("--files-changed requires --target (bulk mode)");
        if (o.Step is not null && o.Through) throw new ArgException("--step and --through are mutually exclusive");
        if (o.Step is { } k && (k < 1 || k > o.Edits)) throw new ArgException($"--step must be in 1..{o.Edits}");
        return o;
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

    private static double Dbl(string key, string val) =>
        double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new ArgException($"--{key} expects a number, got '{val}'");

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
