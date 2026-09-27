using System.Text;

namespace CodeSpawner.Generation;

/// <summary>Ordinary ~6.6 KB headers: an include, a struct typedef, and 30 function prototypes.</summary>
public static class OrdinaryHeaderEmitter
{
    public static void Write(string dir, int i)
    {
        var sb = new StringBuilder(4096);
        sb.Append("#ifndef HDR_").Append(i).Append("_H\n");
        sb.Append("#define HDR_").Append(i).Append("_H\n");
        sb.Append("#include <stddef.h>\n");
        sb.Append("typedef struct mod").Append(i).Append("_ctx { int id; unsigned flags; } mod")
          .Append(i).Append("_ctx_t;\n");
        for (int f = 0; f < 30; f++)
            sb.Append("int mod").Append(i).Append("_op").Append(f).Append("(mod")
              .Append(i).Append("_ctx_t* c, int arg);\n");
        sb.Append("#endif\n");

        File.WriteAllText(Path.Combine(dir, $"hdr_{i}.h"), sb.ToString(), Encodings.Utf8NoBom);
    }
}
