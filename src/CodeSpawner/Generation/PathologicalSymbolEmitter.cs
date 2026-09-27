using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>One ground-truth pathological symbol: name, site, and whether it's an honest-miss.</summary>
public sealed record PathoSym(string Name, string Path, int Line, bool ExpectedMiss);

/// <summary>
/// The <c>pathological-symbols</c> C-subset: shapes that stress a symbol extractor and, crucially, some
/// symbols a lexical / preprocessor-blind indexer is EXPECTED to miss.
///   - token-paste macros (<c>CS_MK_HANDLER(k)</c> → <c>patho{i}_handler_{k}</c>): the name never appears
///     literally in the source, so a PP-blind indexer can't resolve it → <see cref="PathoSym.ExpectedMiss"/>.
///   - extreme-length identifiers and deeply-nested scopes: findable (lexically present), pure extractor cost.
/// </summary>
public static class PathologicalSymbolEmitter
{
    private const int HandlersPerFile = 8;   // token-paste (expected-miss) symbols per file
    private const int LongIdentLen = 400;    // characters in the extreme identifier
    private const int NestDepth = 64;        // nested scopes for the deep symbol
    public const string GenMacro = "CS_MK_HANDLER";

    public static List<PathoSym> Emit(GenOptions o, DirTree tree, int nFiles, PopulationStats stats)
    {
        var slots = new List<PathoSym>[nFiles];
        long[] byteSlots = new long[nFiles];

        Parallel.For(0, nFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rng = Rng.For(o.Seed, Category.PathoSymbol, i);
            string path = Path.Combine(tree.PickDir(ref rng), $"patho_{i}.c");
            var syms = new List<PathoSym>(HandlersPerFile + 2);
            var lines = new List<string>();

            // Token-paste generator: the ## fuses the name at preprocessing time, so patho{i}_handler_{k}
            // never appears literally below.
            lines.Add($"#define {GenMacro}(id) int patho{i}_handler_##id(int x) {{ return x + (id); }}");
            for (int k = 0; k < HandlersPerFile; k++)
            {
                lines.Add($"{GenMacro}({k})");
                syms.Add(new PathoSym($"patho{i}_handler_{k}", path, lines.Count, ExpectedMiss: true));
            }

            // Extreme-length identifier — findable, just expensive.
            string longName = $"patho{i}_" + new string('z', LongIdentLen);
            lines.Add($"int {longName}(int a) {{ return a; }}");
            syms.Add(new PathoSym(longName, path, lines.Count, ExpectedMiss: false));

            // Deep scope nesting — findable; stresses the parser's nesting handling.
            lines.Add($"int patho{i}_deep(int x) {{");
            int deepLine = lines.Count;
            lines.Add(string.Concat(Enumerable.Repeat("if(x){", NestDepth)) + "x++;" + new string('}', NestDepth));
            lines.Add("    return x;");
            lines.Add("}");
            syms.Add(new PathoSym($"patho{i}_deep", path, deepLine, ExpectedMiss: false));

            string body = string.Join('\n', lines);
            File.WriteAllText(path, body, Encodings.Utf8NoBom);
            slots[i] = syms;
            byteSlots[i] = System.Text.Encoding.UTF8.GetByteCount(body);
        });

        var all = new List<PathoSym>();
        long total = 0;
        foreach (var s in slots) all.AddRange(s);
        foreach (var b in byteSlots) total += b;
        stats.Add("pathological-symbols", nFiles, total, all.Count);
        return all;
    }
}
