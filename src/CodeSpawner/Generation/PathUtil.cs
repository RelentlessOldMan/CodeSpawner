namespace CodeSpawner.Generation;

public static class PathUtil
{
    /// <summary>
    /// Repo-relative path (forward slashes) of <paramref name="absPath"/> rooted at
    /// <paramref name="corpusRoot"/>. Forward slashes keep the manifest portable across OSes and
    /// stable whether the corpus is read locally or over an SMB share.
    /// </summary>
    public static string Rel(string corpusRoot, string absPath)
    {
        string rel = Path.GetRelativePath(corpusRoot, absPath);
        return rel.Replace('\\', '/');
    }

    /// <summary>Ordinal order over the '/' form of OS paths, so a sorted list comes out the same on Windows and
    /// Linux. Plain ordinal differs: '\' sorts after digits and uppercase, '/' before them, so Windows put
    /// <c>block10\</c> ahead of <c>block1\</c>. A no-op on Linux, where the separator already is '/'.</summary>
    public static readonly IComparer<string> SlashOrdinal = Comparer<string>.Create((a, b) =>
        string.CompareOrdinal(a.Replace(Path.DirectorySeparatorChar, '/'), b.Replace(Path.DirectorySeparatorChar, '/')));

    /// <summary>One-off <see cref="Containment.Resolve"/>; a caller resolving many paths keeps one
    /// <see cref="Containment"/> so the marker is read once and each directory is checked once.</summary>
    public static string? ResolveInCorpus(string corpusRoot, string rel) => new Containment(corpusRoot).Resolve(rel);
}

/// <summary>
/// Resolves manifest-supplied corpus-relative paths to absolute ones, or null for anything outside the corpus.
/// Inside means under the corpus root itself, or under one of its <c>--linked-roots</c> siblings
/// (<c>&lt;corpus&gt;_rootN</c>, which a manifest legitimately reaches as <c>../&lt;corpus&gt;_rootN/...</c>). When the
/// corpus marker records the linked-root count, N must be below it; a pre-1.1.6 marker records none and any N is
/// accepted. Refused: a <c>..</c> escape, an absolute or drive-relative path, and any path through a junction or
/// symlink below the root (the root itself may be one). So a corrupt or hostile manifest, or a link planted in the
/// tree, can never steer a read, edit or delete outside the corpus.
/// </summary>
public sealed class Containment
{
    private readonly string _root;
    private readonly int? _linkedRoots;
    private readonly HashSet<string> _plain = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Containment(string corpusRoot)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot));
        _linkedRoots = OutputDir.ReadLinkedRoots(_root);
    }

    public string? Resolve(string rel)
    {
        // A NUL or control character is no file name; GetFullPath would throw on it (an unhandled crash, not a refusal).
        if (rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        string full = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
        char sep = Path.DirectorySeparatorChar;
        if (full.StartsWith(_root + sep, Cmp)) return NoLinksBelow(_root, full) ? full : null;

        // A --linked-roots sibling: <parent>/<leaf>_root<digits>/... (see DirTree.Build).
        if (Path.GetDirectoryName(_root) is not { } parent) return null;
        string prefix = Path.Combine(parent, Path.GetFileName(_root) + "_root");
        if (!full.StartsWith(prefix, Cmp)) return null;
        int i = prefix.Length;
        while (i < full.Length && char.IsAsciiDigit(full[i])) i++;
        if (i == prefix.Length || i >= full.Length || full[i] != sep) return null;
        if (_linkedRoots is { } k && (!int.TryParse(full.AsSpan(prefix.Length, i - prefix.Length), out int n) || n < 1 || n >= k))
            return null;
        string sibling = full[..i];
        return !IsLink(sibling) && NoLinksBelow(sibling, full) ? full : null;
    }

    /// <summary>True when no existing component of <paramref name="full"/> below <paramref name="top"/> (the file
    /// included) is a reparse point. Components that don't exist yet (a file mutate is about to add) pass.</summary>
    private bool NoLinksBelow(string top, string full)
    {
        int at = top.Length;
        while (at < full.Length)
        {
            int next = full.IndexOf(Path.DirectorySeparatorChar, at + 1);
            string p = next < 0 ? full : full[..next];
            if (!_plain.Contains(p))
            {
                FileAttributes a;
                try { a = File.GetAttributes(p); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
                if ((a & FileAttributes.ReparsePoint) != 0) return false;
                _plain.Add(p);
            }
            if (next < 0) break;
            at = next;
        }
        return true;
    }

    private bool IsLink(string dir)
    {
        if (_plain.Contains(dir)) return false;
        try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        _plain.Add(dir);
        return false;
    }
}
