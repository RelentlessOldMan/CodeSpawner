using System.Text;

namespace CodeSpawner.Generation;

public static class Encodings
{
    /// <summary>UTF-8 without a byte-order mark — no stray BOM bytes at the head of generated sources.</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
