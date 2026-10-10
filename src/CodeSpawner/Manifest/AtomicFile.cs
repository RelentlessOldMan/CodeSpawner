namespace CodeSpawner.Manifest;

/// <summary>
/// Write-then-rename for the JSON artifacts consumers trust (manifest, deltas, shards, conflict file, profile):
/// the bytes go to a sibling <c>.tmp</c> and are moved over the real path only after the writer finished, so a
/// crash, Ctrl+C, full disk or serializer exception never leaves a truncated-but-present artifact at the real
/// name. On failure the temp file is removed and whatever was at the real path before is left untouched.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> body, int bufferSize = 4096)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
                body(fs);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort; the original error is what matters */ }
            throw;
        }
    }
}
