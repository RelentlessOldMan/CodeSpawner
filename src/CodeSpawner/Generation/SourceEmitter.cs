using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>Ground-truth record for one emitted <c>src_i.c</c>.</summary>
public sealed record CFileInfo(int Index, string Path, int DefLine, int CallLine);

public sealed class SourceEmitResult
{
    public required List<CFileInfo> Files { get; init; }
    public required string HotPath { get; init; }
    public required List<string> HotRefs { get; init; } // absolute "path:line"
    public required int GiantIncluders { get; init; }
}

/// <summary>
/// Emits the <c>.c</c> files carrying the cross-file cross-directory call graph: <c>func_i</c> defined in
/// <c>src_i.c</c> calls <c>func_{i-1}</c> in another directory. The first N files are CO-LOCATED with a
/// giant header and <c>#include</c> it (co-location guarantees the include resolves), and each also calls
/// a shared hot symbol — the aggregate-memory stressor. Def/ref line numbers are recorded exactly for the
/// ground-truth manifest.
/// </summary>
public static class SourceEmitter
{
    public static SourceEmitResult Emit(GenOptions o, DirTree tree, IReadOnlyList<string> giantPaths,
        int nC, int nSmallH)
    {
        int nGiantInc = giantPaths.Count > 0 ? Math.Min(o.GiantIncluders, nC) : 0;

        // hot_shared: defined once in a cheap file (no giant), called from every giant-including .c.
        var hotRng = Rng.For(o.Seed, Category.Source, -1);
        string hotPath = Path.Combine(tree.PickDir(ref hotRng), "hot_shared.c");
        File.WriteAllText(hotPath, "int hot_shared(int x) { return x + 1; }", Encodings.Utf8NoBom);
        var hotRefs = new List<string>();

        // Index-stable slots so emission can run in parallel yet the manifest stays order-deterministic.
        var slots = new CFileInfo[nC];
        var hotSlots = new string?[nC]; // hot_shared ref site for giant-includers (first nGiantInc)

        Parallel.For(0, nC, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rng = Rng.For(o.Seed, Category.Source, i);
            bool isInc = i < nGiantInc;
            string giant = isInc ? giantPaths[i % giantPaths.Count] : "";
            string dir = isInc ? Path.GetDirectoryName(giant)! : tree.PickDir(ref rng);
            string p = Path.Combine(dir, $"src_{i}.c");

            var lines = new List<string>(650) { "#include <stddef.h>" };
            for (int inc = 0; inc < 6; inc++)
                if (nSmallH > 0) lines.Add($"#include \"hdr_{rng.Next(0, nSmallH)}.h\"");
            if (isInc) lines.Add($"#include \"{Path.GetFileName(giant)}\""); // co-located -> resolves
            lines.Add($"int func_{i}(int x);");
            if (isInc) lines.Add("int hot_shared(int x);");
            if (i > 0) lines.Add($"int func_{i - 1}(int x);");

            lines.Add($"int func_{i}(int x) {{");
            int defLine = lines.Count;              // 1-based line of the definition opener
            lines.Add("    int acc = x;");
            int callLine = 0;
            if (i > 0)
            {
                lines.Add($"    acc += func_{i - 1}(x - 1);"); // the cross-file reference to func_{i-1}
                callLine = lines.Count;
            }
            if (isInc)
            {
                lines.Add("    acc += hot_shared(x);");
                hotSlots[i] = $"{p}:{lines.Count}";
            }
            for (int l = 0; l < 600; l++)
                lines.Add("    acc = (acc * 1664525 + 1013904223) ^ (acc >> 3);");
            lines.Add("    return acc;");
            lines.Add("}");

            string body = string.Join('\n', lines);
            File.WriteAllText(p, body, Encodings.Utf8NoBom);
            slots[i] = new CFileInfo(i, p, defLine, callLine);

            if (o.BuildOutput)
            {
                string stem = p[..^2]; // strip ".c"
                File.WriteAllBytes(stem + ".o", new byte[64]);
                File.WriteAllText(stem + ".lst", $"   1 0000 func_{i}:\n   2 0004   push\n", Encoding.ASCII);
                File.WriteAllText(stem + ".bak", body, Encodings.Utf8NoBom);
            }
        });

        var files = new List<CFileInfo>(slots);
        foreach (var h in hotSlots) if (h is not null) hotRefs.Add(h);

        return new SourceEmitResult
        {
            Files = files, HotPath = hotPath, HotRefs = hotRefs, GiantIncluders = nGiantInc,
        };
    }
}
