using System.Text.Json;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;
using CodeSpawner.Mutation;
using Xunit;

namespace CodeSpawner.Tests;

/// <summary>
/// The symbol-overlay mutate path (the legacy round-robin oracle): BuildPlan + ApplyEdit (remove / line-shift /
/// add / grow / shrink / restream) + WriteDelta. Guards the delta shape a symbol-diff consumer composes onto
/// base truth — fileOps, the changed/tombstoned symbol overlay, removedSites, and the --step / --through modes.
/// </summary>
public class MutatorTests
{
    // A corpus of nFuncs one-func source files (src_a.c, src_b.c, …), func_i defined at line 2. Every symbol
    // carries a ref into EVERY file (line 4) so removing any one file always drops ref-sites from the survivors
    // (⇒ removedSites is exercised whichever file the shuffle picks).
    private static (string corpus, string delta) BuildSymbolCorpus(TempDir tmp, int nFuncs, Action<string>? extra = null)
    {
        string corpus = Path.Combine(tmp.Path, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, ".codespawner"), "CodeSpawner test\n");

        var files = Enumerable.Range(1, nFuncs).Select(i => $"src_{(char)('a' + i - 1)}.c").ToList();
        var m = new ManifestModel { Seed = 1, CorpusRoot = corpus, GeneratorVersion = "test" };
        for (int i = 1; i <= nFuncs; i++)
        {
            File.WriteAllText(Path.Combine(corpus, files[i - 1]),
                $"#include <stddef.h>\nint func_{i}(int x) {{ return x + {i}; }}\nint pad_{i}(void) {{ return {i}; }}\n// tail\n");
            var e = new SymbolEntry { Def = $"{files[i - 1]}:2" };
            foreach (var f in files) e.Refs.Add($"{f}:4");     // a ref into every file
            m.Symbols[$"func_{i}"] = e;
        }
        extra?.Invoke(corpus);
        ManifestWriter.Write(m, Path.Combine(tmp.Path, "corpus-manifest.json"));
        return (corpus, Path.Combine(tmp.Path, "corpus-delta.json"));
    }

    private static JsonDocument Doc(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static List<string> Arr(JsonElement fileOps, string name) =>
        fileOps.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToList();
    private static string StepDelta(string corpus, int k) => $"{corpus}-delta-{k}.json";

    [Fact]
    public void Default_EmitsPerStepDeltas_Remove_LineShift_Add()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 6);

        // types cycle [Remove, LineShift, Add] ⇒ 3 edits = one of each.
        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 3, Seed = 5 });
        Assert.Equal(0, rc);

        for (int k = 1; k <= 3; k++) Assert.True(File.Exists(StepDelta(corpus, k)), $"delta-{k} missing");

        // delta-1 = Remove: a source file removed + at least one TOMBSTONE + survivors lose ref-sites.
        using (var d1 = Doc(StepDelta(corpus, 1)))
        {
            var fileOps = d1.RootElement.GetProperty("fileOps");
            var removed = Arr(fileOps, "removed");
            Assert.Contains(removed, p => p.EndsWith(".c"));
            Assert.False(File.Exists(Path.Combine(corpus, removed.First(p => p.EndsWith(".c")).Replace('/', Path.DirectorySeparatorChar))));
            var symbols = d1.RootElement.GetProperty("symbols");
            Assert.Contains(symbols.EnumerateObject(), s => s.Value.ValueKind == JsonValueKind.String && s.Value.GetString() == "TOMBSTONE");
            Assert.Contains(symbols.EnumerateObject(), s => s.Value.ValueKind == JsonValueKind.Object && s.Value.TryGetProperty("removedSites", out _));
            Assert.Matches("^[0-9a-f]{64}$", d1.RootElement.GetProperty("_meta").GetProperty("prevTruthSha").GetString()!);
            Assert.Equal(1, d1.RootElement.GetProperty("_meta").GetProperty("step").GetInt32());
        }

        // delta-2 = LineShift: a file modified.
        using (var d2 = Doc(StepDelta(corpus, 2)))
            Assert.NotEmpty(Arr(d2.RootElement.GetProperty("fileOps"), "modified"));

        // delta-3 = Add: a new mut_add_2.c, present on disk + in the symbol overlay.
        using (var d3 = Doc(StepDelta(corpus, 3)))
        {
            Assert.Contains(Arr(d3.RootElement.GetProperty("fileOps"), "added"), p => p.EndsWith("mut_add_2.c"));
            Assert.True(d3.RootElement.GetProperty("symbols").TryGetProperty("mut_add_2", out _));
        }
        Assert.True(File.Exists(Path.Combine(corpus, "mut_add_2.c")));
    }

    [Fact]
    public void Default_PrevTruthSha_Composes_StepToStep()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 6);
        Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 3, Seed = 2 });
        // Each step composes onto the previous truth: delta-2's prevTruthSha = truth after edit 1 ≠ base truth.
        using var d1 = Doc(StepDelta(corpus, 1));
        using var d2 = Doc(StepDelta(corpus, 2));
        Assert.NotEqual(
            d1.RootElement.GetProperty("_meta").GetProperty("prevTruthSha").GetString(),
            d2.RootElement.GetProperty("_meta").GetProperty("prevTruthSha").GetString());
    }

    [Fact]
    public void Through_EmitsOneCumulativeDelta_NoStepFiles()
    {
        using var tmp = new TempDir();
        var (corpus, delta) = BuildSymbolCorpus(tmp, nFuncs: 6);

        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 3, Seed = 1, Through = true });
        Assert.Equal(0, rc);

        Assert.True(File.Exists(delta));                        // corpus-delta.json
        Assert.False(File.Exists(StepDelta(corpus, 1)));        // no per-step files
        using var d = Doc(delta);
        Assert.False(d.RootElement.GetProperty("_meta").TryGetProperty("step", out _));   // cumulative ⇒ no step
        var fileOps = d.RootElement.GetProperty("fileOps");
        Assert.Contains(Arr(fileOps, "added"), p => p.EndsWith("mut_add_2.c"));            // all 3 edits aggregated
        Assert.NotEmpty(Arr(fileOps, "removed"));
        Assert.NotEmpty(Arr(fileOps, "modified"));
    }

    [Fact]
    public void Step_AppliesOnlyThatEdit_ReplaysEarlierTableOnly()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 6);
        var before = Directory.GetFiles(corpus, "src_*.c").Select(Path.GetFileName).OrderBy(x => x).ToList();

        // --step 3 is the Add edit: edits 1-2 (Remove/LineShift) replay table-only (NO disk), edit 3 hits disk.
        int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 3, Seed = 5, Step = 3 });
        Assert.Equal(0, rc);

        Assert.True(File.Exists(StepDelta(corpus, 3)));
        Assert.True(File.Exists(Path.Combine(corpus, "mut_add_2.c")));            // edit 3 applied to disk
        var after = Directory.GetFiles(corpus, "src_*.c").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(before, after);                                             // Remove (edit 1) did NOT touch disk
    }

    [Fact]
    public void Remove_DeletesSiblingSet_IncludingPhantomSidecars()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 4);
        Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 1, Seed = 9 });   // edit 1 = Remove
        using var d = Doc(StepDelta(corpus, 1));
        var removed = Arr(d.RootElement.GetProperty("fileOps"), "removed");
        string stem = Path.GetFileNameWithoutExtension(removed.First(p => p.EndsWith(".c")));
        // the sibling set (.c + .o/.lst/.bak) is always recorded, even for sidecars that never existed on disk
        foreach (var ext in new[] { ".c", ".o", ".lst", ".bak" })
            Assert.Contains(removed, p => p.EndsWith($"{stem}{ext}"));
    }

    [Fact]
    public void NotEnoughSourceFiles_Throws()
    {
        using var tmp = new TempDir();
        var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 1);
        // types cycle Remove, LineShift, Add, Remove, LineShift, …; 4 edits needs 3 source-file edits but only 1 func.
        Assert.Throws<ArgException>(() => Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 4, Seed = 1 }));
    }

    [Fact]
    public void Default_IsDeterministic_SameSeed()
    {
        string Fingerprint()
        {
            using var tmp = new TempDir();
            var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 6);
            Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 3, Seed = 7 });
            // Compare only root-independent content (prevTruthSha + fileOps + symbols overlay); baseManifestSha
            // can embed the absolute corpus root, which legitimately differs between temp dirs.
            var sb = new System.Text.StringBuilder();
            for (int k = 1; k <= 3; k++)
            {
                using var d = Doc(StepDelta(corpus, k));
                sb.Append(d.RootElement.GetProperty("_meta").GetProperty("prevTruthSha").GetString());
                sb.Append(d.RootElement.GetProperty("fileOps").GetRawText());
                sb.Append(d.RootElement.GetProperty("symbols").GetRawText());
                sb.Append('|');
            }
            return sb.ToString();
        }
        Assert.Equal(Fingerprint(), Fingerprint());
    }

    [Fact]
    public void SizeEdits_Grow_Shrink_Restream_ChangeFileSizes()
    {
        long grow0 = Mutator.GrowTargetBytes, shrink0 = Mutator.ShrinkTargetBytes, restream0 = Mutator.RestreamTargetBytes;
        try
        {
            Mutator.GrowTargetBytes = 8 * 1024;        // tiny targets so the test writes KB, not 110 MB
            Mutator.ShrinkTargetBytes = 4 * 1024;
            Mutator.RestreamTargetBytes = 4 * 1024;

            using var tmp = new TempDir();
            var (corpus, _) = BuildSymbolCorpus(tmp, nFuncs: 6, extra: c =>
            {
                File.WriteAllText(Path.Combine(c, "hdr_0.h"), "#pragma once\n");           // Grow pool (hdr_*.h)
                File.WriteAllText(Path.Combine(c, "mut_shrink_0.h"), new string('x', 200)); // Shrink pool
                File.WriteAllText(Path.Combine(c, "mut_restream_0.h"), new string('y', 200));// Restream pool
            });

            // With the pools present, types = [Remove, LineShift, Add, Grow, Shrink, Restream]; 6 edits = one each.
            int rc = Mutator.Run(new MutateOptions { Corpus = corpus, Edits = 6, Seed = 3, Restream = true });
            Assert.Equal(0, rc);

            Assert.True(new FileInfo(Path.Combine(corpus, "hdr_0.h")).Length >= Mutator.GrowTargetBytes);
            Assert.True(new FileInfo(Path.Combine(corpus, "mut_shrink_0.h")).Length >= Mutator.ShrinkTargetBytes);
            Assert.True(new FileInfo(Path.Combine(corpus, "mut_restream_0.h")).Length >= Mutator.RestreamTargetBytes);

            // each size edit is recorded as a modified file across the per-step deltas
            var modified = new HashSet<string>();
            for (int k = 1; k <= 6; k++)
                if (File.Exists(StepDelta(corpus, k)))
                    using (var d = Doc(StepDelta(corpus, k)))
                        foreach (var p in Arr(d.RootElement.GetProperty("fileOps"), "modified")) modified.Add(p);
            Assert.Contains(modified, p => p.EndsWith("hdr_0.h"));
            Assert.Contains(modified, p => p.EndsWith("mut_shrink_0.h"));
            Assert.Contains(modified, p => p.EndsWith("mut_restream_0.h"));
        }
        finally
        {
            Mutator.GrowTargetBytes = grow0; Mutator.ShrinkTargetBytes = shrink0; Mutator.RestreamTargetBytes = restream0;
        }
    }

    [Fact]
    public void Run_MissingCorpus_ReturnsError()
        => Assert.Equal(2, Mutator.Run(new MutateOptions { Corpus = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()) }));
}
