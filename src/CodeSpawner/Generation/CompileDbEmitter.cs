using System.Text;
using System.Text.Json;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// Emits a <c>compile_commands.json</c> as an A/B control: <c>none</c> (the firmware norm, the
/// best-effort-clang trigger), <c>partial</c> (half the .c), or <c>full</c> (all). Written with
/// <see cref="Utf8JsonWriter"/> (AOT-safe, no reflection).
/// </summary>
public static class CompileDbEmitter
{
    public static void Write(GenOptions o, string outFull, IReadOnlyList<CFileInfo> files)
    {
        if (o.CompileDb == CompileDbMode.None || files.Count == 0) return;

        int take = o.CompileDb == CompileDbMode.Full ? files.Count : files.Count / 2;
        string path = Path.Combine(outFull, "compile_commands.json");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });

        w.WriteStartArray();
        for (int k = 0; k < take; k++)
        {
            string file = files[k].Path;
            string dir = Path.GetDirectoryName(file)!;
            w.WriteStartObject();
            w.WriteString("directory", dir);
            w.WriteString("file", file);
            w.WriteString("command", $"clang -I\"{dir}\" -I\"{outFull}\" -c \"{file}\"");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.Flush();

        Console.WriteLine($"  compile_commands.json: {take}/{files.Count} TUs ({o.CompileDb.ToString().ToLowerInvariant()})");
    }
}
