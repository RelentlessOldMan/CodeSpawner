using CodeSpawner.Cli;

namespace CodeSpawner.Generation;

/// <summary>
/// The block/sub/mod directory skeleton, optionally split across sibling "linked root" trees
/// (federation). Built once up front (cheap, single-threaded); emitters then place files into it.
/// Directory selection is done through a caller-supplied per-file <see cref="Rng"/> so placement is
/// deterministic without any shared mutable state.
/// </summary>
public sealed class DirTree
{
    public IReadOnlyList<string> Roots { get; }
    public IReadOnlyList<string> Dirs { get; }

    private DirTree(List<string> roots, List<string> dirs)
    {
        Roots = roots;
        Dirs = dirs;
    }

    public string PickDir(ref Rng rng) => Dirs[rng.Next(Dirs.Count)];

    public static DirTree Build(GenOptions o, string outFull)
    {
        var roots = new List<string> { outFull };
        string parent = Path.GetDirectoryName(outFull.TrimEnd(Path.DirectorySeparatorChar))
                        ?? Directory.GetCurrentDirectory();
        string leaf = Path.GetFileName(outFull.TrimEnd(Path.DirectorySeparatorChar));
        for (int r = 1; r < Math.Max(1, o.LinkedRoots); r++)
        {
            string sib = Path.Combine(parent, $"{leaf}_root{r}");
            Directory.CreateDirectory(sib);
            roots.Add(sib);
        }

        int depthCap = Math.Min(o.Depth, 12);
        int totalDirs = o.Eff("Dirs", o.Dirs);
        // Each (block,sub,mod) triple yields 9 leaf dirs, so divide the target accordingly.
        int perRoot = Math.Max(1, (int)Math.Round((double)totalDirs / roots.Count / 9));

        var dirs = new List<string>(roots.Count * perRoot * 9);
        foreach (string rt in roots)
        {
            for (int b = 0; b < perRoot; b++)
                for (int s = 0; s < 3; s++)
                    for (int m = 0; m < 3; m++)
                    {
                        var seg = new List<string> { $"block{b}", $"sub{s}", $"mod{m}" };
                        while (seg.Count < depthCap) seg.Add($"lvl{seg.Count}");
                        string d = Path.Combine(rt, Path.Combine([.. seg]));
                        Directory.CreateDirectory(d);
                        dirs.Add(d);
                    }
        }
        return new DirTree(roots, dirs);
    }
}
