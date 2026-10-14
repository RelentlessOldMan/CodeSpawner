using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CodeSpawner.Cli;
using Microsoft.Win32.SafeHandles;

namespace CodeSpawner.Generation;

/// <summary>
/// Clears and recreates a generator's <c>--out</c> directory — shared by <see cref="CorpusGenerator"/> and
/// <see cref="ProfileGenerator"/> so the deletion guards cannot drift apart. A non-empty directory we did not
/// create is refused unless <c>--force</c>; a directory that is never safe to wipe (a drive/share root, the
/// working directory or the user profile or one of their ancestors, a git checkout, a well-known user folder)
/// is refused EVEN WITH <c>--force</c>, because <c>--force</c> means "overwrite this corpus", not "delete
/// whatever path was typed".
/// <para>Regenerating over our own corpus also clears what the old corpus left beside it (its manifest, mutate
/// deltas and mutation state, 3-way variant trees and <c>--linked-roots</c> siblings), so nothing of the deleted tree survives next to
/// the new one. A corpus directory's marker is deleted LAST, so an interrupted wipe still leaves a directory the
/// next run recognises as ours.</para>
/// </summary>
public static class OutputDir
{
    public const string MarkerName = ".codespawner";
    private const string LinkedRootsKey = "linkedRoots ";

    /// <param name="linkedRoots">The <c>--linked-roots</c> count the new corpus will use (1 = none). It is recorded
    /// in the marker, so a later regen knows which <c>&lt;out&gt;_rootN</c> siblings are ours and manifest paths into
    /// siblings can be capped (<see cref="PathUtil.ResolveInCorpus"/>).</param>
    public static void Prepare(string outDir, bool force, int linkedRoots = 1)
    {
        string full = Path.GetFullPath(outDir);
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        int newRoots = Math.Max(1, linkedRoots);
        if (File.Exists(outDir)) throw new ArgException($"--out '{outDir}' is a file, not a directory.");

        // Every refusal comes before the first delete.
        bool exists = Directory.Exists(outDir);
        bool ours = exists && File.Exists(Path.Combine(outDir, MarkerName));
        if (exists && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            if (UnsafeToDelete(full) is { } why)
                throw new ArgException($"--out '{outDir}' resolves to {full}, {why}. Refusing to delete it " +
                                       "(--force does not override this); choose a dedicated --out directory.");
            if (!ours && !force)
                throw new ArgException(
                    $"--out '{outDir}' already exists, is not empty, and was not created by CodeSpawner. " +
                    "Refusing to delete it. Pass --force to overwrite, or choose a different --out.");
        }
        // Our corpus from 1.1.5 or earlier records no count in its marker; its manifest's ../<leaf>_rootN/ paths say
        // which siblings it wrote. Without a manifest none count as recorded, so a non-empty one needs --force.
        int? oldRoots = ours ? ReadLinkedRoots(full) ?? LegacyLinkedRoots(trimmed) : null;
        var staleRoots = LinkedRootsToClear(trimmed, oldRoots, force, newRoots);

        if (ours)
        {
            // The old sibling manifest, mutate artifacts and 3-way variants described the corpus being deleted; drop
            // them first so an interrupted regen (or --manifest=false) never leaves them beside a different tree.
            // Only for a marker-carrying corpus: a foreign directory's neighbours are not ours to remove.
            string staleManifest = CorpusGenerator.ManifestPath(full);
            if (File.Exists(staleManifest)) File.Delete(staleManifest);
            Mutation.Mutator.RemoveDeltaArtifacts(trimmed);
            Mutation.Mutator.RemoveStepDeltas(trimmed);
            string state = Mutation.Mutator.MutationStatePath(trimmed);
            if (File.Exists(state)) File.Delete(state);
            foreach (var variant in new[] { trimmed + "_v1", trimmed + "_v2" })
                if (!IsLink(variant)                           // a link made by hand: what it points at is not ours
                    && (File.Exists(Path.Combine(variant, MarkerName)) || File.Exists(Path.Combine(variant, Mutation.Mutator.JournalName)))
                    && UnsafeToDelete(variant) is null)
                    DeleteMarkerLast(variant);
        }
        foreach (var sib in staleRoots) Directory.Delete(sib, recursive: true);
        if (ours) DeleteMarkerLast(outDir);
        else if (exists) Directory.Delete(outDir, recursive: true);

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, MarkerName), $"CodeSpawner {Program.Version}\n{LinkedRootsKey}{newRoots}\n");
    }

    /// <summary>The <c>--linked-roots</c> count recorded in <paramref name="corpus"/>'s marker, or null when there is
    /// no marker or it predates the record (1.1.5 and earlier).</summary>
    public static int? ReadLinkedRoots(string corpus)
    {
        string marker = Path.Combine(corpus, MarkerName);
        if (!File.Exists(marker)) return null;
        foreach (var line in File.ReadLines(marker))
            if (line.StartsWith(LinkedRootsKey, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(LinkedRootsKey.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n >= 1)
                return n;
        return null;
    }

    /// <summary>The <c>--linked-roots</c> count a 1.1.5-or-earlier corpus used, read from the <c>../&lt;leaf&gt;_rootN/</c>
    /// paths in its manifest (1 when it has none), or null when the manifest is gone.</summary>
    private static int? LegacyLinkedRoots(string trimmed)
    {
        string manifest = CorpusGenerator.ManifestPath(trimmed);
        if (!File.Exists(manifest)) return null;
        var re = new Regex(@"\.\./" + Regex.Escape(Path.GetFileName(trimmed)) + @"_root([0-9]+)/", RegexOptions.CultureInvariant);
        int max = 0;
        using var r = new StreamReader(manifest);
        while (r.ReadLine() is { } line)
            for (var m = re.Match(line); m.Success; m = m.NextMatch())
                if (int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > max) max = n;
        return max + 1;
    }

    private static bool IsLink(string dir) =>
        Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// The <c>&lt;out&gt;_rootN</c> siblings to clear before a generation; throws (before anything is deleted) on one
    /// it may not clear. The new corpus writes into roots 1..<paramref name="newRoots"/>-1, so leftovers there would
    /// mix into it, and an old corpus's extra roots would outlive it. A sibling is cleared when it is empty, when the
    /// old marker records it (<paramref name="oldRoots"/>), or (with <c>--force</c>) when the new corpus would write
    /// into it. A non-empty, unrecorded sibling in the new range is refused without --force; one outside the new
    /// range is left alone. The
    /// <see cref="UnsafeToDelete"/> guards apply to every sibling, --force or not.
    /// </summary>
    private static List<string> LinkedRootsToClear(string trimmed, int? oldRoots, bool force, int newRoots)
    {
        var clear = new List<string>();
        if (Path.GetDirectoryName(trimmed) is not { } parent || !Directory.Exists(parent)) return clear;
        string prefix = Path.GetFileName(trimmed) + "_root";
        foreach (var sib in Directory.EnumerateDirectories(parent, prefix + "*").ToList())
        {
            string name = Path.GetFileName(sib);
            if (!int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                || n < 1 || name.Length - prefix.Length != n.ToString(CultureInfo.InvariantCulture).Length) continue;
            bool empty = !Directory.EnumerateFileSystemEntries(sib).Any();
            bool recorded = oldRoots is { } k && n < k;
            if (!empty && !recorded)
            {
                if (n >= newRoots) continue;                   // not ours and not in the way: leave it, even with --force
                if (!force) throw new ArgException(
                    $"linked root '{sib}' already exists, is not empty, and is not recorded as part of the corpus at " +
                    "--out, so the new corpus would mix into it. Pass --force to overwrite it, or move it aside.");
            }
            if (UnsafeToDelete(Path.GetFullPath(sib)) is { } why)
                throw new ArgException($"linked root '{sib}' resolves to a directory {why}. Refusing to delete it " +
                                       "(--force does not override this); choose a dedicated --out directory.");
            clear.Add(sib);
        }
        return clear;
    }

    /// <summary>Delete a corpus directory's contents, then its marker, then the directory itself. A plain recursive
    /// delete removes entries in no set order and could take the marker first; this way an interrupted wipe still
    /// leaves the marker behind.</summary>
    private static void DeleteMarkerLast(string dir)
    {
        foreach (var e in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            if (string.Equals(Path.GetFileName(e), MarkerName, StringComparison.Ordinal)) continue;
            if (Directory.Exists(e)) Directory.Delete(e, recursive: true);   // a junction: removes the link only
            else File.Delete(e);
        }
        File.Delete(Path.Combine(dir, MarkerName));
        Directory.Delete(dir);
    }

    /// <summary>Why <paramref name="full"/> (an absolute path) must never be recursively deleted, or null if it
    /// may be. Internal so the guard is unit-testable without pointing a real delete at anything.</summary>
    internal static string? UnsafeToDelete(string full)
    {
        string dir = Trim(full);
        string? root = Path.GetPathRoot(dir);
        if (root is not null && SamePath(dir, Trim(root))) return "which is a drive or share root";
        // The checks below compare paths, which an alias of the same directory (\\?\C:\..., \\localhost\C$\...,
        // a subst drive, an 8.3 short name) would get past; on Windows they also compare the directory itself.
        var id = DirIdentity.Of(dir);

        if (IsSameOrAncestor(dir, Directory.GetCurrentDirectory(), id)) return "which is the current directory or one of its parents";
        if (IsSameOrAncestor(dir, AppContext.BaseDirectory, id)) return "which contains the running codespawner executable";

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && IsSameOrAncestor(dir, home, id)) return "which is the user profile or one of its parents";

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
            if (p.Length > 0 && (SamePath(dir, Trim(p)) || (id is not null && DirIdentity.Of(Trim(p)) == id)))
                return "which is a well-known system or user folder";
        }

        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            return "which is a git checkout";
        return null;
    }

    private static string Trim(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);

    /// <summary>True when <paramref name="candidate"/> is <paramref name="other"/> or one of its ancestors, by path or
    /// (when <paramref name="id"/> is known) by directory identity, so the drive root an alias reaches counts too.</summary>
    private static bool IsSameOrAncestor(string candidate, string other, DirIdentity? id)
    {
        string o = Trim(other);
        if (SamePath(candidate, o) || o.StartsWith(candidate + Path.DirectorySeparatorChar, PathComparison)) return true;
        if (id is null) return false;
        for (string? d = o; d is not null; d = Path.GetDirectoryName(d))
            if (DirIdentity.Of(d) == id) return true;
        return false;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>A directory's identity on Windows (volume serial + file index), the same through any path that reaches
/// it. Null off Windows or when the directory can't be opened; callers then rely on comparing paths.</summary>
internal readonly record struct DirIdentity(uint Volume, ulong Index)
{
    public static DirIdentity? Of(string dir)
    {
        if (!OperatingSystem.IsWindows() || dir.Length == 0) return null;
        // No access rights, share everything, open existing, backup semantics (required to open a directory).
        using SafeFileHandle h = CreateFileW(dir, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        return new DirIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                                     uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);
}
