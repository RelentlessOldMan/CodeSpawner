using System.Security.Cryptography;
using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>A duplicate-content group with absolute paths (converted to repo-relative by the caller).</summary>
public sealed record DupGroupResult(string Name, string Sha256, List<string> Paths, List<string> NearVariants);

/// <summary>
/// The <c>dup-content</c> pathology: N groups of BYTE-IDENTICAL files scattered across directories (vendored
/// copies, generated variants), plus one near-identical control per group (1 line different). Stresses
/// content-hash dedup and segment-merge posting collapse: the identical set MUST collapse to one posting
/// set; the near-variant must NOT. The manifest records each group's content hash so a consumer can assert
/// the collapse against ground truth.
/// </summary>
public static class DupContentEmitter
{
    public static List<DupGroupResult> Emit(GenOptions o, DirTree tree, int nGroups, int copies, PopulationStats stats)
    {
        copies = Math.Max(2, copies); // a "group" needs at least two identical members to be a dedup target
        var slots = new DupGroupResult[nGroups];
        long[] byteSlots = new long[nGroups];

        Parallel.For(0, nGroups, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, g =>
        {
            var rng = Rng.For(o.Seed, Category.DupContent, g);
            int c1 = rng.Next(1, 1_000_000);
            int c2 = rng.Next(1, 1_000_000);

            // The shared, byte-identical content (same function names across copies — that's the duplication).
            var sb = new System.Text.StringBuilder(2048);
            sb.Append("// generated duplicate-content group ").Append(g).Append('\n');
            sb.Append("#include <stddef.h>\n");
            sb.Append("static int dup").Append(g).Append("_seed(int x) { return x * ").Append(c1).Append(" + 1; }\n");
            for (int f = 0; f < 24; f++)
                sb.Append("static int dup").Append(g).Append("_op").Append(f)
                  .Append("(int x) { return dup").Append(g).Append("_seed(x) ^ (").Append(c2).Append(" + ").Append(f).Append("); }\n");
            string content = sb.ToString();
            byte[] bytes = Encodings.Utf8NoBom.GetBytes(content);
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

            var paths = new List<string>(copies);
            long groupBytes = 0;
            for (int c = 0; c < copies; c++)
            {
                string p = Path.Combine(tree.PickDir(ref rng), $"dup{g}_c{c}.c");
                File.WriteAllBytes(p, bytes);
                paths.Add(p);
                groupBytes += bytes.Length;
            }

            // One near-identical control: same content with a single byte changed (must NOT collapse).
            byte[] near = (byte[])bytes.Clone();
            near[^2] ^= 0x01; // flip a bit near the end -> different hash, still valid-ish text
            string np = Path.Combine(tree.PickDir(ref rng), $"dup{g}_near.c");
            File.WriteAllBytes(np, near);
            groupBytes += near.Length;

            slots[g] = new DupGroupResult($"dup{g}", sha, paths, new List<string> { np });
            byteSlots[g] = groupBytes;
        });

        long total = 0;
        foreach (var b in byteSlots) total += b;
        stats.Add("dup-content", (long)nGroups * (copies + 1), total, 0);
        return [.. slots];
    }
}
