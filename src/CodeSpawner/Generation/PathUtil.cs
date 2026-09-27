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
}
