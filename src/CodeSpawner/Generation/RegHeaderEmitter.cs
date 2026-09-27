using System.Buffers;
using System.Buffers.Text;

namespace CodeSpawner.Generation;

/// <summary>
/// Emits a hardware-register-map header: many object-like <c>#define</c>s (3 per register), capped at a
/// byte ceiling. Density is INDEPENDENT of size — a high define count in a small file still stresses the
/// preprocessor macro table (the real memory-bug axis) without the bytes.
///
/// Throughput core: we format ASCII straight into a reused byte buffer and flush ~8 MB at a time to a raw
/// <see cref="FileStream"/> — no per-line WriteLine, no StringBuilder→string→encode round-trip, and no
/// per-register <c>Stream.Length</c> syscall (byte count is tracked in-process). A 110 MB / ~1M-define
/// header writes in a couple of seconds; a &gt;1 GB header streams at disk speed with flat memory.
/// </summary>
public static class RegHeaderEmitter
{
    private const int FlushThreshold = 8 * 1024 * 1024;   // flush once the buffer passes this
    // One register emits at most ~220 bytes; 512 B headroom past the flush mark is comfortably safe.
    private const int MaxRegisterBytes = 512;
    private const int BufferCapacity = FlushThreshold + MaxRegisterBytes;

    /// <summary>
    /// Write a register header to <paramref name="path"/>. Stops at whichever comes first:
    /// <paramref name="defines"/> emitted or <paramref name="maxBytes"/> reached. <paramref name="fam"/>
    /// is the block family number woven into symbol names.
    /// </summary>
    public static void Write(string path, long defines, long maxBytes, int fam)
    {
        string guard = "REGMAP_" + Path.GetFileNameWithoutExtension(path).ToUpperInvariant() + "_H";

        using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, FileOptions.SequentialScan);

        byte[] buf = new byte[BufferCapacity];
        int len = 0;
        long flushed = 0; // bytes already written to the stream (tracked, not queried)

        len = Ascii(buf, len, "#ifndef ");
        len = Ascii(buf, len, guard);
        len = Ascii(buf, len, "\n#define ");
        len = Ascii(buf, len, guard);
        len = Ascii(buf, len, "\n/* generated hardware register map - block ");
        len = Int(buf, len, fam);
        len = Ascii(buf, len, " (synthetic) */\n");

        long emitted = 0;
        int reg = 0;
        while (emitted < defines && flushed + len < maxBytes)
        {
            // #define HWIO_BLK{fam}_REG{reg}_ADDR (BASE_BLOCK{fam} + 0x{addr:x8})
            len = Ascii(buf, len, "#define HWIO_BLK");
            len = Int(buf, len, fam);
            len = Ascii(buf, len, "_REG");
            len = Int(buf, len, reg);
            len = Ascii(buf, len, "_ADDR (BASE_BLOCK");
            len = Int(buf, len, fam);
            len = Ascii(buf, len, " + 0x");
            len = Hex8(buf, len, (uint)reg * 4u);
            len = Ascii(buf, len, ")\n#define HWIO_BLK");
            len = Int(buf, len, fam);
            len = Ascii(buf, len, "_REG");
            len = Int(buf, len, reg);
            len = Ascii(buf, len, "_RMSK 0x000000ff\n#define HWIO_BLK");
            len = Int(buf, len, fam);
            len = Ascii(buf, len, "_REG");
            len = Int(buf, len, reg);
            len = Ascii(buf, len, "_IN in_dword(HWIO_BLK");
            len = Int(buf, len, fam);
            len = Ascii(buf, len, "_REG");
            len = Int(buf, len, reg);
            len = Ascii(buf, len, "_ADDR)\n");

            reg++;
            emitted += 3;

            if (len >= FlushThreshold)
            {
                fs.Write(buf, 0, len);
                flushed += len;
                len = 0;
            }
        }

        len = Ascii(buf, len, "#endif\n");
        if (len > 0) fs.Write(buf, 0, len);
    }

    // Direct ASCII copy of a constant fragment — avoids the per-call overhead of Encoding.ASCII.GetBytes
    // on a path that runs millions of times per giant header.
    private static int Ascii(byte[] buf, int len, string s)
    {
        for (int k = 0; k < s.Length; k++) buf[len + k] = (byte)s[k];
        return len + s.Length;
    }

    private static int Int(byte[] buf, int len, int value)
    {
        bool ok = Utf8Formatter.TryFormat(value, buf.AsSpan(len), out int written);
        // Buffer is sized so this never fails; guard makes the invariant explicit rather than silent.
        if (!ok) throw new InvalidOperationException("register header buffer overflow formatting int");
        return len + written;
    }

    private static int Hex8(byte[] buf, int len, uint value)
    {
        bool ok = Utf8Formatter.TryFormat(value, buf.AsSpan(len), out int written, new StandardFormat('x', 8));
        if (!ok) throw new InvalidOperationException("register header buffer overflow formatting hex");
        return len + written;
    }
}
