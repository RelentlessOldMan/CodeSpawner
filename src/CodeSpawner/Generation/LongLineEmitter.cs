using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// The <c>long-lines</c> pathology: files whose content is a single pathologically long line (minified JS /
/// single-line JSON / generated blob). Exercises line-aligned block building and guards against &gt;2 GB
/// string materialization. Emits alternating newline-terminated and NO-newline variants (or all-no-newline
/// when <c>--no-newline</c> is set). Zero-symbol, so no manifest entries.
/// </summary>
public static class LongLineEmitter
{
    // A benign minified-ish repeating unit (no unique identifiers — a lexer/block stressor, not symbols).
    private const string Unit = "a.b(c);d=e+f;g[h]=i;";
    // Pre-tile the unit into a ~64 KB block (a whole multiple of the unit, so tiled blocks stay aligned and
    // the file is a clean unit repetition) — a multi-MB / multi-GB line becomes a handful of big writes.
    private static readonly byte[] Block = BuildBlock(64 * 1024 / Unit.Length * Unit.Length);

    private static byte[] BuildBlock(int size)
    {
        var b = new byte[size];
        for (int i = 0; i < size; i++) b[i] = (byte)Unit[i % Unit.Length];
        return b;
    }

    public static void Emit(GenOptions o, DirTree tree, int nFiles, PopulationStats stats)
    {
        long[] byteSlots = new long[nFiles];
        Parallel.For(0, nFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rng = Rng.For(o.Seed, Category.LongLine, i);
            bool noNewline = o.NoNewline || (i % 2 == 1); // alternate unless forced all-no-newline
            string path = Path.Combine(tree.PickDir(ref rng), $"longline_{i}.min.js");

            long target = Math.Max(1, (long)o.MaxLineBytes);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.SequentialScan);

            long written = 0;
            while (written < target)
            {
                int n = (int)Math.Min(Block.Length, target - written);
                fs.Write(Block, 0, n);
                written += n;
            }
            if (!noNewline) { fs.WriteByte((byte)'\n'); written++; }
            byteSlots[i] = written;
        });

        long total = 0;
        foreach (var b in byteSlots) total += b;
        stats.Add("long-lines", nFiles, total, 0);
    }
}
