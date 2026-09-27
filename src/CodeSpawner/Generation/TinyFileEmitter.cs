using System.Text;

namespace CodeSpawner.Generation;

/// <summary>Tiny ~1 KB CSV files — the file-COUNT / per-file-overhead / SMB stat-pressure axis.</summary>
public static class TinyFileEmitter
{
    public static void Write(string dir, int i, ref Rng rng)
    {
        var sb = new StringBuilder(1100);
        sb.Append("id,name,value,ts\n");
        for (int r = 0; r < 25; r++)
            sb.Append(r).Append(",item_").Append(r).Append(',').Append(rng.Next(0, int.MaxValue))
              .Append(",2026-09-24\n");

        File.WriteAllText(Path.Combine(dir, $"data_{i}.csv"), sb.ToString(), Encoding.ASCII);
    }
}
