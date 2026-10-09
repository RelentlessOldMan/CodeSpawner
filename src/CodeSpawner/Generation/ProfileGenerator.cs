using System.Diagnostics;
using System.Globalization;
using CodeSpawner.Cli;
using CodeSpawner.Manifest;
using CodeSpawner.Profile;

namespace CodeSpawner.Generation;

/// <summary>
/// The regenerate half of the round-trip: <c>gen --from-profile</c>. Reads a scan profile and synthesizes a
/// generic look-alike reproducing its size/type/dir/content-class/cost shape — all content fake. With
/// <c>--with-oracle</c> it also overlays the ground-truth spine + a manifest so verify/carve run at the real
/// tree's cost/shape. See docs/scan-design.md.
/// </summary>
public sealed class ProfileGenerator
{
    private const string MarkerName = ".codespawner";
    private readonly GenOptions _o;
    public ProfileGenerator(GenOptions o) => _o = o;

    public void Run()
    {
        var sw = Stopwatch.StartNew();
        var profile = ProfileReader.Load(_o.FromProfile!);

        PrepareOutputDir();
        string outFull = new DirectoryInfo(_o.Out).FullName;
        Console.WriteLine($"Regenerating from {_o.FromProfile} (posture={profile.Posture.Label()}, " +
                          $"{profile.Archetypes.Count} archetypes, seed {_o.Seed}) ...");

        var dirs = BuildDirs(outFull, profile);
        Console.WriteLine($"  dir tree: {dirs.Count} dirs");

        // Each archetype gets its own RngLaneWidth-wide band of the ProfileGen stream index, so no two
        // (archetype, file) pairs ever share a stream. The index is 64-bit (no cast), so the archetype lane
        // no longer truncates at int.MaxValue; the only remaining bound is that one archetype's file count
        // must fit inside a single lane — guarded below. No real scan profile comes anywhere near it.
        const long RngLaneWidth = 100_000_000L;
        foreach (var a in profile.Archetypes)
            if (a.Count > RngLaneWidth)
                throw new ArgException(
                    $"archetype '{a.Label}' has {a.Count:N0} files, exceeding the per-archetype RNG lane width " +
                    $"({RngLaneWidth:N0}) — its streams would overlap the next archetype's. No real scan profile " +
                    "approaches this; widen RngLaneWidth in ProfileGenerator if a synthetic one does.");

        int degraded = 0;
        for (int ai = 0; ai < profile.Archetypes.Count; ai++)
        {
            var a = profile.Archetypes[ai];
            if (a.Class == ContentClass.TemplateMetaprogramming) degraded++;
            string ext = a.Extension == "*" ? ".dat" : a.Extension;
            int archIndex = ai;
            long count = a.Count;

            Parallel.For(0L, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var rng = Rng.For(_o.Seed, Category.ProfileGen, archIndex * RngLaneWidth + i);
                string dir = dirs[rng.Next(dirs.Count)];
                string path = Path.Combine(dir, $"{a.Label}_{i}{ext}");
                long size = ArchetypeSynthesizer.DrawSize(ref rng, a.SizeDistribution);
                ArchetypeSynthesizer.Write(path, a.Class, size, ref rng);
            });
        }

        long files = profile.Archetypes.Sum(a => a.Count);
        Console.WriteLine($"  synthesized {files:N0} files across {profile.Archetypes.Count} archetypes" +
                          (degraded > 0 ? $" ({degraded} template archetype(s) degraded to inline-fn-heavy)" : ""));

        if (_o.WithOracle)
        {
            var overlay = new OracleOverlay(_o, profile, outFull, dirs);
            var model = overlay.Emit();
            string mpath = CorpusGenerator.ManifestPath(outFull);
            ManifestWriter.Write(model, mpath);
            Console.WriteLine($"  oracle overlay: {model.Symbols.Count} symbols -> {mpath}" +
                              (_o.OracleScale ? " (bodies scaled to measured .c size)" : " (compact spine)"));
        }

        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:N1}s.");
    }

    private List<string> BuildDirs(string outFull, ProfileModel profile)
    {
        // Rebuild a tree whose TOTAL dir count and per-depth distribution match the profile: attach each
        // depth-d directory under an EXISTING depth-(d-1) directory, so shared parents are reused rather than
        // recreated per leaf (the bug that inflated the dir count ~depth-fold). Round-robin over the parent
        // set at each level approximates the fan-out.
        var pool = new List<string> { outFull };
        var levels = new Dictionary<int, List<string>> { [0] = new() { outFull } };
        long g = 0;

        var byDepth = new SortedDictionary<int, long>();
        foreach (var (label, cnt) in profile.Dirs.DepthHistogram.Entries)
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && cnt > 0)
                byDepth[d] = byDepth.TryGetValue(d, out var e) ? e + cnt : cnt;

        if (byDepth.Count == 0)
        {
            // No depth model (e.g. an empty histogram): fall back to a flat spread under the root.
            long flat = Math.Max(1, profile.Dirs.Count);
            for (long k = 0; k < flat; k++)
            {
                string dir = Path.Combine(outFull, "d" + g.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir); pool.Add(dir); g++;
            }
            return pool;
        }

        foreach (var (depth, count) in byDepth)
        {
            if (depth <= 0) continue; // depth 0 is the root itself, already present
            // Parent set = nearest shallower populated level. Real trees are downward-closed so this is
            // depth-1; the search only matters for a pathological gap in the histogram.
            List<string>? parents = null;
            for (int p = depth - 1; p >= 0; p--)
                if (levels.TryGetValue(p, out parents) && parents.Count > 0) break;
            parents ??= levels[0];

            var here = levels.TryGetValue(depth, out var lst) ? lst : (levels[depth] = new List<string>());
            for (long k = 0; k < count; k++)
            {
                string parent = parents[(int)(k % parents.Count)];
                string dir = Path.Combine(parent, "d" + g.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                here.Add(dir); pool.Add(dir); g++;
            }
        }
        return pool;
    }

    private void PrepareOutputDir()
    {
        if (Directory.Exists(_o.Out))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(_o.Out).Any();
            bool ours = File.Exists(Path.Combine(_o.Out, MarkerName));
            if (!empty && !ours && !_o.Force)
                throw new ArgException(
                    $"--out '{_o.Out}' already exists, is not empty, and was not created by CodeSpawner. " +
                    "Pass --force to overwrite, or choose a different --out.");
            Directory.Delete(_o.Out, recursive: true);
        }
        else if (File.Exists(_o.Out))
            throw new ArgException($"--out '{_o.Out}' is a file, not a directory.");

        Directory.CreateDirectory(_o.Out);
        File.WriteAllText(Path.Combine(_o.Out, MarkerName), $"CodeSpawner {Program.Version}\n");
    }
}
