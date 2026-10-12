using System.Security.Cryptography;
using System.Text;

namespace CodeSpawner.Manifest;

/// <summary>
/// Streams a canonical digest's text into SHA-256 piece by piece instead of building one string first: the
/// digest inputs grow with the edit count, and a single .NET string caps at ~1G chars. The hashed bytes are
/// exactly the UTF-8 of the concatenated pieces (each piece is a whole string or an ASCII separator, so no
/// surrogate pair is ever split), i.e. the same sha the one-string form produced.
/// </summary>
internal sealed class DigestSink : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _buf = new byte[256];

    public DigestSink Append(string s)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (_buf.Length < max) _buf = new byte[Math.Max(max, _buf.Length * 2)];
        int n = Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, 0);
        _hash.AppendData(_buf, 0, n);
        return this;
    }

    public DigestSink Append(char separator)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = (byte)separator;                                // the digests' separators are all ASCII
        _hash.AppendData(b);
        return this;
    }

    /// <summary>Lowercase hex SHA-256 of everything appended.</summary>
    public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public void Dispose() => _hash.Dispose();
}
