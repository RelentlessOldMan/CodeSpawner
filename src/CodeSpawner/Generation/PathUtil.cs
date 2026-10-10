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

    /// <summary>
    /// Resolve a manifest-supplied corpus-relative path to an absolute one, or null if it lands outside the corpus.
    /// Inside means under <paramref name="corpusRoot"/> itself or under one of its <c>--linked-roots</c> siblings
    /// (<c>&lt;corpus&gt;_rootN</c>, which a manifest legitimately reaches as <c>../&lt;corpus&gt;_rootN/...</c>).
    /// Anything else — a <c>..</c> escape, an absolute or drive-relative path — is refused, so a corrupt or hostile
    /// manifest can never steer a read, edit or delete outside the corpus.
    /// </summary>
    public static string? ResolveInCorpus(string corpusRoot, string rel)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot));
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(root + Path.DirectorySeparatorChar, cmp)) return full;

        // A --linked-roots sibling: <parent>/<leaf>_root<digits>/... (see DirTree.Build).
        if (Path.GetDirectoryName(root) is not { } parent) return null;
        string prefix = Path.Combine(parent, Path.GetFileName(root) + "_root");
        if (!full.StartsWith(prefix, cmp)) return null;
        int i = prefix.Length;
        while (i < full.Length && char.IsAsciiDigit(full[i])) i++;
        return i > prefix.Length && i < full.Length && full[i] == Path.DirectorySeparatorChar ? full : null;
    }
}
