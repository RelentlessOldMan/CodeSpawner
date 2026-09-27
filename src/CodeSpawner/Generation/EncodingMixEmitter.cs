using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// The <c>encoding-mix</c> pathology: files across a spread of encodings and byte hazards — UTF-16LE/BE with
/// BOM, UTF-8 with BOM, invalid/mixed byte runs, and non-ASCII identifiers. Exercises trigram extraction on
/// multibyte input and BOM handling. First cut is non-symbol (a decode/extraction stressor), so no manifest
/// entries; non-ASCII idents as findable symbols is a later follow-on.
/// </summary>
public static class EncodingMixEmitter
{
    // A small C-ish body reused across the text variants so only the ENCODING differs between files.
    private const string Sample =
        "#include <stddef.h>\nint enc_probe(int x) {\n    int total = x;\n    total += (x << 2) ^ (x >> 1);\n    return total;\n}\n";

    // Non-ASCII identifiers (Latin-1, Greek, Cyrillic) — valid UTF-8, exercises multibyte trigram extraction.
    private const string NonAscii =
        "int café_valeur = 1;\nint Ωmega(int x) { return x + 1; }\nint переменная = 42;\nint 変数 = 7;\n";

    public static void Emit(GenOptions o, DirTree tree, int nFiles, PopulationStats stats)
    {
        long[] byteSlots = new long[nFiles];
        Parallel.For(0, nFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rng = Rng.For(o.Seed, Category.EncodingMix, i);
            string dir = tree.PickDir(ref rng);
            int variant = i % 5;
            byte[] bytes;
            string name;
            switch (variant)
            {
                case 0: // UTF-16LE + BOM
                    name = $"enc_{i}_utf16le.c";
                    bytes = Concat(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(Sample));
                    break;
                case 1: // UTF-16BE + BOM
                    name = $"enc_{i}_utf16be.c";
                    bytes = Concat(Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode.GetBytes(Sample));
                    break;
                case 2: // UTF-8 + BOM
                    name = $"enc_{i}_utf8bom.c";
                    bytes = Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes(Sample));
                    break;
                case 3: // invalid / mixed byte run (not valid UTF-8)
                    name = $"enc_{i}_invalid.dat";
                    bytes = new byte[4096];
                    for (int k = 0; k < bytes.Length; k++) bytes[k] = (byte)(0x80 + rng.Next(0, 128)); // high bytes only
                    break;
                default: // non-ASCII identifiers (valid UTF-8, no BOM)
                    name = $"enc_{i}_nonascii.c";
                    bytes = new UTF8Encoding(false).GetBytes(NonAscii);
                    break;
            }
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            byteSlots[i] = bytes.Length;
        });

        long total = 0;
        foreach (var b in byteSlots) total += b;
        stats.Add("encoding-mix", nFiles, total, 0);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
