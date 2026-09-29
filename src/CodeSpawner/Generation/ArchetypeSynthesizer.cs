using System.Buffers.Text;
using CodeSpawner.Profile;

namespace CodeSpawner.Generation;

/// <summary>
/// Synthesizes a single generic file for a content class, filled to a target byte size. Content is fake and
/// deterministic, and — the invariant that makes the round-trip meaningful — is shaped so a re-scan
/// re-classifies it to the SAME class (reproducing cost/shape, never real content). All identifiers, values,
/// and text are synthetic.
/// </summary>
public static class ArchetypeSynthesizer
{
    private const int FlushAt = 1 << 20; // flush the ASCII buffer past ~1 MB
    private const int Slop = 4096;

    private static readonly string[] Words =
    { "system", "module", "value", "handler", "buffer", "context", "record", "index", "region", "packet",
      "config", "session", "channel", "device", "frame", "vector", "signal", "matrix", "stream", "token" };

    public static void Write(string path, ContentClass cls, long targetBytes, ref Rng rng)
    {
        // template-metaprogramming degrades to inline-function-heavy until the C++ profile lands (design note).
        if (cls == ContentClass.TemplateMetaprogramming) cls = ContentClass.InlineFunctionHeavy;

        if (cls == ContentClass.Binary) { WriteBinary(path, targetBytes, ref rng); return; }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        byte[] buf = new byte[FlushAt + Slop];
        int len = 0;
        long flushed = 0;
        int n = 0;

        void Flush() { if (len > 0) { fs.Write(buf, 0, len); flushed += len; len = 0; } }
        void Maybe() { if (len >= FlushAt) Flush(); }

        while (flushed + len < targetBytes)
        {
            switch (cls)
            {
                case ContentClass.PreprocessorDense:
                    len = A(buf, len, "#define CS_R"); len = I(buf, len, n); len = A(buf, len, "_ADDR 0x");
                    len = Hex(buf, len, (uint)n * 4); len = A(buf, len, "\n#define CS_R"); len = I(buf, len, n);
                    len = A(buf, len, "_MSK 0x000000ff\n");
                    break;

                case ContentClass.InlineFunctionHeavy:
                    len = A(buf, len, "static int cs_fn"); len = I(buf, len, n);
                    len = A(buf, len, "(int x){int a=x;a+=x*3;a^=a>>2;return a;}\n");
                    break;

                case ContentClass.EnumStructTable:
                    len = A(buf, len, "enum CsE"); len = I(buf, len, n); len = A(buf, len, "{CS_A"); len = I(buf, len, n);
                    len = A(buf, len, ",CS_B"); len = I(buf, len, n); len = A(buf, len, "};\nstruct CsS"); len = I(buf, len, n);
                    len = A(buf, len, "{int a;int b;};\n");
                    break;

                case ContentClass.XMacro:
                    len = A(buf, len, "CS_ITEM("); len = I(buf, len, n); len = A(buf, len, ", 0x");
                    len = Hex(buf, len, (uint)n); len = A(buf, len, ", \"e\")\n");
                    break;

                case ContentClass.TabularData:
                    len = A(buf, len, "col_a_"); len = I(buf, len, n); len = A(buf, len, ",col_b_"); len = I(buf, len, n);
                    len = A(buf, len, ",col_c_"); len = I(buf, len, n); len = A(buf, len, ",col_d_"); len = I(buf, len, n);
                    len = A(buf, len, ",val_"); len = I(buf, len, n); len = A(buf, len, "\n");
                    break;

                case ContentClass.MinifiedLongLine:
                    // one very long line: no newline until the end.
                    len = A(buf, len, "a"); len = I(buf, len, n); len = A(buf, len, "=b"); len = I(buf, len, n); len = A(buf, len, ";");
                    break;

                case ContentClass.DataBlobHighEntropy:
                    len = A(buf, len, "0x"); len = Hex2(buf, len, (byte)rng.Next(256)); len = A(buf, len, ",");
                    if ((n & 15) == 15) len = A(buf, len, "\n");
                    break;

                case ContentClass.DataBlobRepetitive:
                    len = A(buf, len, "0x00,");
                    if ((n & 15) == 15) len = A(buf, len, "\n");
                    break;

                case ContentClass.Text:
                    len = A(buf, len, Words[rng.Next(Words.Length)]); len = A(buf, len, " ");
                    if ((n % 12) == 11) len = A(buf, len, "\n");
                    break;

                default: // GenericCode — one body per ~1.2 KB of decls/comments so density reads "generic".
                    len = A(buf, len, "/* unit "); len = I(buf, len, n); len = A(buf, len, " */\n#include \"h");
                    len = I(buf, len, n); len = A(buf, len, ".h\"\nint compute"); len = I(buf, len, n);
                    len = A(buf, len, "(int x){ return x + "); len = I(buf, len, n); len = A(buf, len, "; }\n");
                    for (int k = 0; k < 30; k++)
                    { len = A(buf, len, "static const int pad"); len = I(buf, len, n); len = A(buf, len, "_"); len = I(buf, len, k); len = A(buf, len, " = "); len = I(buf, len, k); len = A(buf, len, ";\n"); }
                    break;
            }
            n++;
            Maybe();
        }

        if (cls == ContentClass.MinifiedLongLine) { len = A(buf, len, "\n"); }
        Flush();
    }

    private static void WriteBinary(string path, long targetBytes, ref Rng rng)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        byte[] buf = new byte[FlushAt];
        long written = 0;
        while (written < targetBytes)
        {
            int chunk = (int)Math.Min(buf.Length, targetBytes - written);
            for (int i = 0; i < chunk; i += 8)
            {
                ulong r = rng.NextULong();
                for (int b = 0; b < 8 && i + b < chunk; b++) buf[i + b] = (byte)(r >> (b * 8));
            }
            fs.Write(buf, 0, chunk);
            written += chunk;
        }
    }

    /// <summary>Draw a plausible size from a p50/p90/max distribution (deterministic via rng).</summary>
    public static long DrawSize(ref Rng rng, SizeDistribution d)
    {
        long p50 = Math.Max(1, d.P50), p90 = Math.Max(p50, d.P90), max = Math.Max(p90, d.Max);
        int r = rng.Next(100);
        long lo, hi;
        if (r < 50) { lo = Math.Max(1, p50 / 2); hi = p50; }
        else if (r < 90) { lo = p50; hi = p90; }
        else { lo = p90; hi = max; }
        if (hi <= lo) return lo;
        return lo + (long)((ulong)rng.NextULong() % (ulong)(hi - lo));
    }

    private static int A(byte[] buf, int len, string s)
    {
        for (int k = 0; k < s.Length; k++) buf[len + k] = (byte)s[k];
        return len + s.Length;
    }

    private static int I(byte[] buf, int len, int v)
    {
        Utf8Formatter.TryFormat(v, buf.AsSpan(len), out int w);
        return len + w;
    }

    private static int Hex(byte[] buf, int len, uint v)
    {
        Utf8Formatter.TryFormat(v, buf.AsSpan(len), out int w, new System.Buffers.StandardFormat('x', 8));
        return len + w;
    }

    private static int Hex2(byte[] buf, int len, byte v)
    {
        Utf8Formatter.TryFormat(v, buf.AsSpan(len), out int w, new System.Buffers.StandardFormat('x', 2));
        return len + w;
    }
}
