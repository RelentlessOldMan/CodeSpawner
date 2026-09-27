using System.Text;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

public sealed class UnresolvedEmitResult
{
    public required string VendorGatedPath { get; init; }
    public required List<string> UnreachableRefs { get; init; } // absolute "path:line"
}

/// <summary>
/// The honest NEGATIVE case: N TUs each <c>#include</c> a vendor header that exists nowhere in the tree,
/// holding a reference to <c>vendor_gated()</c> inside <c>#ifdef VENDOR_OK</c> — and VENDOR_OK is defined
/// ONLY by that missing header, so the reference is preprocessed out and is genuinely unreachable without
/// the header. An indexer must NOT resolve it; a soundness-first carver may keep it (closed-world precision).
/// </summary>
public static class UnresolvedIncludeEmitter
{
    public static UnresolvedEmitResult? Emit(GenOptions o, DirTree tree, int nC)
    {
        int nUnres = o.UnresolvedIncludes > 0 ? Math.Min(o.UnresolvedIncludes, Math.Max(1, nC)) : 0;
        if (nUnres == 0) return null;

        var vgRng = Rng.For(o.Seed, Category.Unresolved, -1);
        string vgPath = Path.Combine(tree.PickDir(ref vgRng), "vendor_gated.c");
        File.WriteAllText(vgPath, "int vendor_gated(int x) { return x + 2; }", Encodings.Utf8NoBom);

        var unreach = new List<string>(nUnres);
        for (int k = 0; k < nUnres; k++)
        {
            var rng = Rng.For(o.Seed, Category.Unresolved, k);
            string up = Path.Combine(tree.PickDir(ref rng), $"unres_{k}.c");

            var ul = new List<string>
            {
                $"#include \"VENDOR_missing_{k}.h\"",   // never created anywhere -> unresolvable
                "int vendor_gated(int x);",
                "#ifdef VENDOR_OK",                      // VENDOR_OK is defined only by the missing header
                $"int use_vendor_{k}(int x) {{",
                $"    return vendor_gated(x + {k});",     // UNREACHABLE ref (macro undefined -> block dropped)
            };
            unreach.Add($"{up}:{ul.Count}");
            ul.Add("}");
            ul.Add("#endif");
            File.WriteAllText(up, string.Join('\n', ul), Encodings.Utf8NoBom);
        }

        return new UnresolvedEmitResult { VendorGatedPath = vgPath, UnreachableRefs = unreach };
    }
}
