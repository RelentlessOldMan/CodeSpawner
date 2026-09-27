using System.Buffers;
using System.Buffers.Text;

namespace CodeSpawner.Generation;

/// <summary>
/// High-byte, zero-symbol data blob: a dense numeric array, no symbols. The tree-sitter parse-cost /
/// skip-heuristic axis. Byte-formatted directly for speed (each blob is ~1 MB of hex).
/// </summary>
public static class BlobEmitter
{
    private const int Lines = 40_000; // 4 bytes/line -> ~160k bytes of data, ~1 MB of text

    public static void Write(string dir, int i, ref Rng rng)
    {
        string path = Path.Combine(dir, $"blob_{i}.c");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.SequentialScan);

        byte[] buf = new byte[1 << 20];
        int len = 0;

        len = Ascii(buf, len, "static const unsigned char blob_");
        len = Int(buf, len, i);
        len = Ascii(buf, len, "[] = {\n");

        for (int l = 0; l < Lines; l++)
        {
            for (int b = 0; b < 4; b++)
            {
                len = Ascii(buf, len, "0x");
                Utf8Formatter.TryFormat((byte)rng.Next(0, 255), buf.AsSpan(len), out int w, new StandardFormat('x', 2));
                len += w;
                buf[len++] = (byte)',';
            }
            buf[len++] = (byte)'\n';
            if (len >= buf.Length - 64) { fs.Write(buf, 0, len); len = 0; }
        }
        len = Ascii(buf, len, "};\n");
        fs.Write(buf, 0, len);
    }

    private static int Ascii(byte[] buf, int len, string s)
    {
        for (int k = 0; k < s.Length; k++) buf[len++] = (byte)s[k];
        return len;
    }

    private static int Int(byte[] buf, int len, int v)
    {
        Utf8Formatter.TryFormat(v, buf.AsSpan(len), out int w);
        return len + w;
    }
}
