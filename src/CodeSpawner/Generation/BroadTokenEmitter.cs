using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

public sealed class BroadTokenEmitResult
{
    public required string DefPath { get; init; }          // where broad_hot is defined
    public required List<string> Refs { get; init; }        // absolute "path:line" of every hot-token call
    public required long TotalBytes { get; init; }
    public required int FileCount { get; init; }
}

/// <summary>
/// The <c>broad-token</c> pathology: a hot shared symbol (<c>broad_hot</c>) seeded into a fraction of a
/// 2-8 MB carrier band, a few times per file at deterministic byte offsets (including near-EOF). Exercises
/// result capping/ranking and a network block-selective ("sidecar") read path — 2 MB is the sidecar cutoff,
/// so 2-8 MB carriers with a near-EOF hit force block-selective reads instead of whole-file reads. Every
/// call site is recorded, so the manifest carries the exact <c>find_references</c> expected-set.
/// </summary>
public static class BroadTokenEmitter
{
    private const string Filler = "    acc = (acc * 1664525 + 1013904223) ^ (acc >> 3);\n";
    private const string TokenLine = "    acc += broad_hot(acc);\n";
    // Deterministic offsets (fractions of target size) where the token is injected; 0.99 is the near-EOF hit.
    private static readonly double[] Offsets = [0.10, 0.40, 0.70, 0.95, 0.99];

    public static BroadTokenEmitResult Emit(GenOptions o, DirTree tree, int nFiles, PopulationStats stats)
    {
        // Define the hot symbol once (cheap file, no giant), like hot_shared.
        var defRng = Rng.For(o.Seed, Category.BroadToken, -1);
        string defPath = Path.Combine(tree.PickDir(ref defRng), "broad_hot.c");
        File.WriteAllText(defPath, "int broad_hot(int x) { return x + 3; }", Encodings.Utf8NoBom);

        double share = Math.Clamp(o.HotTokenShare, 0.0, 1.0);
        int withToken = (int)Math.Round(nFiles * share);

        var refSlots = new List<string>?[nFiles];
        long[] byteSlots = new long[nFiles];

        Parallel.For(0, nFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rng = Rng.For(o.Seed, Category.BroadToken, i);
            long target = (long)rng.Next(2, 9) * 1024 * 1024; // 2-8 MB carrier
            bool hasToken = i < withToken;
            string path = Path.Combine(tree.PickDir(ref rng), $"carrier_{i}.c");
            var refs = hasToken ? new List<string>(Offsets.Length) : null;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.SequentialScan);
            byte[] buf = new byte[1 << 20];
            int len = 0;
            long written = 0;
            int line = 0;

            void Emit(string s)
            {
                if (len + s.Length > buf.Length) { fs.Write(buf, 0, len); written += len; len = 0; }
                for (int k = 0; k < s.Length; k++) buf[len++] = (byte)s[k];
                line++;
            }

            Emit("#include <stddef.h>\n");
            Emit("int broad_hot(int x);\n");           // prototype so the ref resolves
            Emit($"int carrier_{i}(int x) {{\n");      // once per file — interpolation cost is negligible
            Emit("    int acc = x;\n");

            int oi = 0;
            while (written + len < target)
            {
                long here = written + len;
                if (hasToken && oi < Offsets.Length && here >= (long)(Offsets[oi] * target))
                {
                    Emit(TokenLine);
                    refs!.Add($"{path}:{line}");
                    oi++;
                }
                else Emit(Filler);
            }
            Emit("    return acc;\n");
            Emit("}\n");
            if (len > 0) { fs.Write(buf, 0, len); written += len; }

            byteSlots[i] = written;
            refSlots[i] = refs;
        });

        var allRefs = new List<string>();
        long total = 0;
        foreach (var r in refSlots) if (r is not null) allRefs.AddRange(r);
        foreach (var b in byteSlots) total += b;
        total += new FileInfo(defPath).Length;

        stats.Add("broad-token", nFiles + 1, total, /*idents*/ 1 + allRefs.Count);
        return new BroadTokenEmitResult { DefPath = defPath, Refs = allRefs, TotalBytes = total, FileCount = nFiles + 1 };
    }
}
