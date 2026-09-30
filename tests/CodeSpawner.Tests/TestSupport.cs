using System.Text;

namespace CodeSpawner.Tests;

/// <summary>A throwaway temp directory that deletes itself on Dispose — keeps file-touching tests isolated.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup; a leaked temp dir must never fail a test */ }
    }
}

internal static class Bytes
{
    /// <summary>ASCII bytes for a string — the classifier/meter operate on raw bytes.</summary>
    public static byte[] Of(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Repeat a line n times (each terminated with '\n').</summary>
    public static byte[] Lines(string line, int n)
    {
        var sb = new StringBuilder(line.Length * n + n);
        for (int i = 0; i < n; i++) sb.Append(line).Append('\n');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
