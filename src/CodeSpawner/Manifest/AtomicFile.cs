namespace CodeSpawner.Manifest;

/// <summary>
/// Write-then-rename for the JSON artifacts consumers trust (manifest, deltas, shards, conflict file, profile):
/// the bytes go to a sibling <c>.tmp</c> and are moved over the real path only after the writer finished, so a
/// crash, Ctrl+C, full disk or serializer exception never leaves a truncated-but-present artifact at the real
/// name. On failure the temp file is removed and whatever was at the real path before is left untouched.
/// <para>Every artifact is written with LF line endings on every OS: .NET 8's indented Utf8JsonWriter emits
/// Environment.NewLine, which would make the bytes (and every sha over them) differ between Windows and Linux.
/// Utf8JsonWriter escapes control characters inside strings, so a raw CR byte can only be indentation and is
/// dropped.</para>
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> body, int bufferSize = 4096)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                using (var lf = new LfOnlyStream(fs))
                    body(lf);
                // On disk before the rename: after a power cut the rename can survive while unflushed bytes don't,
                // which would leave a complete-looking name over a zero-filled or short file.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort; the original error is what matters */ }
            throw;
        }
    }

    /// <summary>Write-only pass-through that drops CR (0x0D) bytes, turning CRLF into LF.</summary>
    private sealed class LfOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int cr = buffer.IndexOf((byte)'\r');
                if (cr < 0) { inner.Write(buffer); return; }
                inner.Write(buffer[..cr]);
                buffer = buffer[(cr + 1)..];
            }
        }
    }
}
