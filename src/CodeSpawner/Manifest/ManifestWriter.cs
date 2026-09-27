using System.Text.Json;

namespace CodeSpawner.Manifest;

/// <summary>
/// Serializes a <see cref="ManifestModel"/> to the v1 schema with a streaming
/// <see cref="Utf8JsonWriter"/> — AOT-safe (no reflection) and cheap even for tens of thousands of
/// symbols. Shape: <c>{ "_meta": {...}, "symbols": { name: { def, refs, edges, unreachableRefs? } } }</c>.
/// </summary>
public static class ManifestWriter
{
    public static void Write(ManifestModel m, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();

        w.WriteStartObject("_meta");
        w.WriteNumber("manifestVersion", m.ManifestVersion);
        w.WriteString("generatorVersion", m.GeneratorVersion);
        w.WriteNumber("seed", m.Seed);
        w.WriteString("corpusRoot", m.CorpusRoot);
        w.WriteEndObject();

        w.WriteStartObject("symbols");
        foreach (var (name, s) in m.Symbols)
        {
            w.WriteStartObject(name);
            w.WriteString("def", s.Def);

            w.WriteStartArray("refs");
            foreach (var r in s.Refs) w.WriteStringValue(r);
            w.WriteEndArray();

            w.WriteStartArray("edges");
            foreach (var e in s.Edges) w.WriteStringValue(e);
            w.WriteEndArray();

            if (s.UnreachableRefs is { Count: > 0 })
            {
                w.WriteStartArray("unreachableRefs");
                foreach (var u in s.UnreachableRefs) w.WriteStringValue(u);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();

        w.WriteEndObject();
        w.Flush();
    }
}
