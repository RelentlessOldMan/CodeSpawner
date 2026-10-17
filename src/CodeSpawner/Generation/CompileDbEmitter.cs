using System.Text;
using System.Text.Json;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// Emits a <c>compile_commands.json</c> as an A/B control: <c>none</c> (the firmware norm, the
/// best-effort-clang trigger), <c>partial</c> (half the .c), or <c>full</c> (all). Written with
/// <see cref="Utf8JsonWriter"/> (AOT-safe, no reflection). The include web's units are always listed (after the .c
/// entries the mode picks), with the web's -I order: their closures are defined by it.
/// </summary>
public static class CompileDbEmitter
{
    public static void Write(GenOptions o, string outFull, IReadOnlyList<CFileInfo> files, IReadOnlyList<string>? webUnits = null)
    {
        int web = webUnits?.Count ?? 0;
        if ((o.CompileDb == CompileDbMode.None || files.Count == 0) && web == 0) return;

        int take = o.CompileDb switch { CompileDbMode.Full => files.Count, CompileDbMode.Partial => files.Count / 2, _ => 0 };
        string path = Path.Combine(outFull, "compile_commands.json");

        // Through AtomicFile for its LF-only line endings, so the bytes match on every OS.
        Manifest.AtomicFile.Write(path, fs => WriteEntries(fs, outFull, files, take, webUnits ?? []));
        Console.WriteLine($"  compile_commands.json: {take}/{files.Count} TUs ({o.CompileDb.ToString().ToLowerInvariant()})" +
                          (web > 0 ? $" + {web} include-web unit(s)" : ""));
    }

    private static void WriteEntries(Stream fs, string outFull, IReadOnlyList<CFileInfo> files, int take, IReadOnlyList<string> webUnits)
    {
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
        string incs = string.Join(" ", WebEmitter.IncludeDirs.Select(d => $"-I\"{Path.Combine(outFull, d)}\""));
        foreach (var file in webUnits)
        {
            w.WriteStartObject();
            w.WriteString("directory", outFull);
            w.WriteString("file", file);
            w.WriteString("command", $"clang {incs} -c \"{file}\"");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.Flush();
    }
}
