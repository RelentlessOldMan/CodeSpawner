using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// Clears and recreates a generator's <c>--out</c> directory — shared by <see cref="CorpusGenerator"/> and
/// <see cref="ProfileGenerator"/> so the deletion guards cannot drift apart. A non-empty directory we did not
/// create is refused unless <c>--force</c>; a directory that is never safe to wipe (a drive/share root, the
/// working directory or the user profile or one of their ancestors, a git checkout, a well-known user folder)
/// is refused EVEN WITH <c>--force</c>, because <c>--force</c> means "overwrite this corpus", not "delete
/// whatever path was typed".
/// </summary>
public static class OutputDir
{
    public const string MarkerName = ".codespawner";

    public static void Prepare(string outDir, bool force)
    {
        if (Directory.Exists(outDir))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(outDir).Any();
            if (!empty)
            {
                string full = Path.GetFullPath(outDir);
                if (UnsafeToDelete(full) is { } why)
                    throw new ArgException($"--out '{outDir}' resolves to {full}, {why}. Refusing to delete it " +
                                           "(--force does not override this); choose a dedicated --out directory.");
                bool ours = File.Exists(Path.Combine(outDir, MarkerName));
                if (!ours && !force)
                    throw new ArgException(
                        $"--out '{outDir}' already exists, is not empty, and was not created by CodeSpawner. " +
                        "Refusing to delete it. Pass --force to overwrite, or choose a different --out.");
                // The old sibling manifest described the corpus being deleted; drop it now so an interrupted
                // regen (or --manifest=false) never leaves a stale manifest beside a different tree. Only for a
                // marker-carrying corpus — a foreign directory's neighbour is not ours to remove.
                if (ours)
                {
                    string staleManifest = CorpusGenerator.ManifestPath(full);
                    if (File.Exists(staleManifest)) File.Delete(staleManifest);
                }
            }
            Directory.Delete(outDir, recursive: true);
        }
        else if (File.Exists(outDir))
        {
            throw new ArgException($"--out '{outDir}' is a file, not a directory.");
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, MarkerName), $"CodeSpawner {Program.Version}\n");
    }

    /// <summary>Why <paramref name="full"/> (an absolute path) must never be recursively deleted, or null if it
    /// may be. Internal so the guard is unit-testable without pointing a real delete at anything.</summary>
    internal static string? UnsafeToDelete(string full)
    {
        string dir = Trim(full);
        string? root = Path.GetPathRoot(dir);
        if (root is not null && SamePath(dir, Trim(root))) return "which is a drive or share root";

        if (IsSameOrAncestor(dir, Directory.GetCurrentDirectory())) return "which is the current directory or one of its parents";
        if (IsSameOrAncestor(dir, AppContext.BaseDirectory)) return "which contains the running codespawner executable";

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && IsSameOrAncestor(dir, home)) return "which is the user profile or one of its parents";

        foreach (var sf in new[]
                 {
                     Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                 })
        {
            string p = Environment.GetFolderPath(sf);
            if (p.Length > 0 && SamePath(dir, Trim(p))) return "which is a well-known system or user folder";
        }

        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            return "which is a git checkout";
        return null;
    }

    private static string Trim(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);

    /// <summary>True when <paramref name="candidate"/> is <paramref name="other"/> or one of its ancestors.</summary>
    private static bool IsSameOrAncestor(string candidate, string other)
    {
        string o = Trim(other);
        return SamePath(candidate, o) || o.StartsWith(candidate + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
