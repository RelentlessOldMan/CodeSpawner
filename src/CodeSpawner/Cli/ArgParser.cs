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

            int I() { o.Explicit.Add(Canon(key)); return int.Parse(Val(), ci); }
            double D() { o.Explicit.Add(Canon(key)); return double.Parse(Val(), ci); }
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
                case "seed": o.Seed = int.Parse(Val(), CultureInfo.InvariantCulture); break;
                case "edits": o.Edits = int.Parse(Val(), CultureInfo.InvariantCulture); break;
                case "step": o.Step = int.Parse(Val(), CultureInfo.InvariantCulture); break;
                case "through": o.Through = true; break;
                case "restream": o.Restream = true; break;
                default: throw new ArgException($"unknown option --{key}");
            }
        }
        if (string.IsNullOrWhiteSpace(o.Corpus)) throw new ArgException("--corpus <dir> is required");
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
