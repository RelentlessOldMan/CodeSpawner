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
                var rng = Rng.For(_o.Seed, Category.ProfileGen, (int)(archIndex * 100_000_000L + i));
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
        var dirs = new List<string>();
        long g = 0;
        var entries = profile.Dirs.DepthHistogram.Entries;
        if (entries.Count == 0)
        {
            int target = (int)Math.Max(1, profile.Dirs.Count);
            for (int k = 0; k < target; k++) dirs.Add(MakeDir(outFull, 1, g++));
        }
        else
        {
            foreach (var (label, cnt) in entries)
            {
                if (!int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth)) depth = 1;
                for (long k = 0; k < cnt; k++) dirs.Add(MakeDir(outFull, depth, g++));
            }
        }
        if (dirs.Count == 0) dirs.Add(outFull);
        return dirs;
    }

    // A directory at the given depth; a unique group segment keeps leaves distinct so the count is honored.
    private static string MakeDir(string outFull, int depth, long g)
    {
        if (depth <= 0) return outFull;
        var segs = new List<string> { "g" + g.ToString(CultureInfo.InvariantCulture) };
        for (int s = 1; s < depth; s++) segs.Add("s" + s.ToString(CultureInfo.InvariantCulture));
        string dir = Path.Combine(outFull, Path.Combine(segs.ToArray()));
        Directory.CreateDirectory(dir);
        return dir;
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
