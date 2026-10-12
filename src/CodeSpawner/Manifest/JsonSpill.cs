using System.Text.Json;

namespace CodeSpawner.Manifest;

/// <summary>
/// Utf8JsonWriter over a Stream buffers the WHOLE document in memory until Flush, and that buffer caps at
/// ~2 GiB. The record loops of the big artifacts (manifest symbols, delta records, conflicts) call Spill once
/// per record so the buffer is written out every ~1 MiB instead. The bytes are unchanged.
/// </summary>
internal static class JsonSpill
{
    public static void Spill(this Utf8JsonWriter w)
    {
        if (w.BytesPending >= 1 << 20) w.Flush();
    }
}
