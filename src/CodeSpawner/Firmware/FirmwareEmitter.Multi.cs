using System.Text;
using System.Text.Json;

namespace CodeSpawner.Firmware;

// The multi-target tree (--fw-targets, docs/firmware-design.md "Multi-target"): one source tree, two or three builds.
// Each target has its own startup file, board file, link script, compile db and build log, and builds every shared
// unit with its own -D. A per-target name ends in __ and one letter per target (K keep, D drop, O open).
internal static partial class FirmwareEmitter
{
    private static string[] TargetNames(Plan p) => FwMulti.Names[..p.Targets];
    private static string[] TargetDefs(int i) => [$"-DFW_TARGET={i + 1}", $"-DFW_FEAT_{(char)('A' + i)}"];
    private static string KeepIn(Plan p, int t) => p.Sfx(i => i == t ? 'K' : 'D');

    /// <summary>The C files of target <paramref name="t"/>'s build beyond the units (fw-relative).</summary>
    private static string[] MultiUnits(string t) => ["src/main.c", "src/handlers.c", "src/fw_attr.c", "src/multi.c", $"targets/{t}/board.c"];
    private static string[] MultiAsm(string t) => [$"targets/{t}/startup.S", "asm/fw_util.s", "asm/big_0.s"];

    // regs_0.h's names the multi-target files use
    private static string MultiHosts(Plan p)
    {
        var sb = new StringBuilder("#define ZMIF0_1Z 1\n#define ZMNW_1Z 1\n");
        sb.Append($"#define KMFB_1__{KeepIn(p, 1)} 1\n#define YMCF_1Y 1\n");
        for (int j = 0; j < 3; j++) sb.Append($"#define KMT_{j + 1}__{KeepIn(p, j)} 1\n");
        for (int j = 0; j < p.Targets; j++) sb.Append($"#define KBRD_{j + 1}__{KeepIn(p, j)} 1\n");
        return sb.ToString();
    }

    private static void WriteMultiTree(Plan p)
    {
        var names = TargetNames(p);
        File.WriteAllText(p.Fw("include/fw_mcfg.h"), """
            /* fw_mcfg.h: a configuration name the tree defines (a build could define it otherwise) */
            #ifndef FW_MCFG_H
            #define FW_MCFG_H
            #define FW_MCFG 1
            #endif

            """);
        File.WriteAllText(p.Fw("src/multi.c"), $"""
            /* multi.c: #if blocks each target decides by its own -D (FW_TARGET, FW_FEAT_*) */
            #include "fwpaste.h"
            #include "regs_0.h"
            #include "fw_mcfg.h"
            #if 0
            int zm0 = CAT(ZMIF0_, 1Z);
            #endif
            #ifdef FW_NOWHERE
            int zm1 = CAT(ZMNW_, 1Z);
            #endif
            #ifdef FW_FEAT_B
            int km2 = CAT(KMFB_, 1__{KeepIn(p, 1)});
            #endif
            #if FW_MCFG
            int ym3 = CAT(YMCF_, 1Y);
            #endif
            #ifndef FW_NOWHERE
            #if FW_TARGET == 1
            int km4 = CAT(KMT_, 1__{KeepIn(p, 0)});
            #elif FW_TARGET == 2
            int km5 = CAT(KMT_, 2__{KeepIn(p, 1)});
            #else
            int km6 = CAT(KMT_, 3__{KeepIn(p, 2)});
            #endif
            #endif

            """ + """
            /* the variadic and stringify macros the plain tree's traps use */
            int kprintf(const char *fmt, ...);
            void ZmlogZ(void) { KLOGV("x", 1); KLOGA("x", 1); }
            const char *kmst = KSTR(1);

            """);
        var bh = new StringBuilder("/* board.h: picks the target's board header by FW_TARGET (the build's -D) */\n#ifndef BOARD_H\n#define BOARD_H\n");
        for (int i = 0; i < names.Length; i++)
            bh.Append(i == 0 ? "#if" : "#elif").Append($" FW_TARGET == {i + 1}\n#include \"board_{names[i]}.h\"\n");
        File.WriteAllText(p.Fw("include/board.h"), bh.Append("#endif\n#endif\n").ToString());
        for (int i = 0; i < names.Length; i++)
        {
            string t = names[i], g = $"BOARD_{t.ToUpperInvariant()}_H";
            File.WriteAllText(p.Fw($"include/board_{t}.h"),
                $"/* board_{t}.h: {t}'s board */\n#ifndef {g}\n#define {g}\n#define FW_BRD CAT(KBRD_, {i + 1}__{KeepIn(p, i)})\n#endif\n");
            File.WriteAllText(p.Fw($"targets/{t}/board.c"),
                $"/* board.c: {t}'s board code, in {t}'s build only */\n#include \"fwpaste.h\"\n#include \"regs_0.h\"\n#include \"board.h\"\nint kboard(void) {{ return FW_BRD; }}\n");
        }
    }

    /// <summary>Each target's compile db and build log, and the top-level db (every target's entries).</summary>
    private static void WriteMultiBuild(Plan p)
    {
        bool rel = p.O.FwDbPaths == "relative";
        string P(string corpusRel) => rel ? corpusRel : Path.GetFullPath(Path.Combine(p.Root, corpusRel));
        string[] inc = ["fw/include", "fw/big", "fw/dense", "ext/include"];
        var names = TargetNames(p);
        var jo = new JsonWriterOptions { Indented = true };
        using var fsAll = File.Create(Path.Combine(p.Root, FwSpec.DbPath));
        using var all = new Utf8JsonWriter(fsAll, jo);
        all.WriteStartArray();
        for (int i = 0; i < names.Length; i++)
        {
            string t = names[i];
            var defs = TargetDefs(i);
            Directory.CreateDirectory(Path.Combine(p.Root, BuildDir, t));
            using var fs = File.Create(Path.Combine(p.Root, FwMulti.DbPath(t)));
            using var w = new Utf8JsonWriter(fs, jo);
            w.WriteStartArray();
            var log = new StringBuilder();
            var objs = new List<string>();
            string Obj(string file) => $"{BuildDir}/{t}/obj/{Path.ChangeExtension(file, ".o")}";
            void C(string file)
            {
                var args = new List<string>(ArmCc) { "-std=c11" };
                foreach (var d in inc) { args.Add("-I"); args.Add(d); }
                args.AddRange(["-DKD_7", "-DKDV_9=KDV_VAL", "-DKDE_N=4", .. defs]);
                foreach (var x in new[] { w, all })
                {
                    x.WriteStartObject();
                    x.WriteString("directory", rel ? "." : Path.GetFullPath(p.Root));
                    x.WriteStartArray("arguments");
                    foreach (var a in args) x.WriteStringValue(a.StartsWith("fw/") || a.StartsWith("ext/") ? P(a) : a);
                    x.WriteStringValue("-c");
                    x.WriteStringValue(P(file));
                    x.WriteEndArray();
                    x.WriteString("file", P(file));
                    x.WriteEndObject();
                }
                Log(args, file);
            }
            void Log(List<string> args, string file)
            {
                // the log's -D values are quoted as a shell would need; its paths are relative to the corpus root
                log.Append(string.Join(' ', args.Select(a => a.Contains('"') ? $"'{a}'" : a)))
                   .Append(" -ffunction-sections -fdata-sections -c ").Append(file).Append(" -o ").Append(Obj(file)).Append('\n');
                objs.Add(Obj(file));
            }
            for (int u = 0; u < p.Units; u++) C($"{FwSpec.FwDir}/{p.UnitRel(u)}");
            foreach (var c in MultiUnits(t)) C($"{FwSpec.FwDir}/{c}");
            foreach (var s in MultiAsm(t))
            {
                AsmEntry(w, rel, P, s, defs);
                AsmEntry(all, rel, P, s, defs);
                Log([.. ArmCc, "-I", "fw/asm", .. defs], $"{FwSpec.FwDir}/{s}");
            }
            C($"{ExtDir}/app/host.c");
            w.WriteEndArray();
            // the board object goes into an archive the link takes (kboard, which main calls, pulls it in)
            string board = Obj($"{FwSpec.FwDir}/targets/{t}/board.c"), ar = $"{BuildDir}/{t}/libboard_{t}.a";
            objs.Remove(board);
            log.Append($"ar rcs {ar} {board}\n");
            var lib = new List<string> { ar, "fw/lib/fwcrc.o", "fw/lib/libfwx.a" };
            if (t == "tb") lib.Add("fw/lib/tb_obj.o");
            log.Append($"ld.lld --gc-sections -T fw/ld/{t}.ld -L fw/ld ").Append(string.Join(' ', objs.Concat(lib)))
               .Append($" -o {BuildDir}/{t}/{t}.elf\n");
            File.WriteAllText(Path.Combine(p.Root, FwMulti.LogPath(t)), log.ToString());
        }
        all.WriteEndArray();
    }

    private static IEnumerable<(string Name, string Case, string Site)> MultiCases(Plan p)
    {
        yield return ("ZMIF0_1Z", "multi-if0", "src/multi.c");
        yield return ("ZMNW_1Z", "multi-undefined-name", "src/multi.c");
        yield return ($"KMFB_1__{KeepIn(p, 1)}", "multi-feature-define", "src/multi.c");
        yield return ("YMCF_1Y", "multi-tree-define", "src/multi.c");
        for (int j = 0; j < 3; j++) yield return ($"KMT_{j + 1}__{KeepIn(p, j)}", "multi-target-chain", "src/multi.c");
        for (int j = 0; j < p.Targets; j++) yield return ($"KBRD_{j + 1}__{KeepIn(p, j)}", "multi-board-header", $"include/board_{FwMulti.Names[j]}.h");
    }

    private static IEnumerable<(string Name, string Case, string Site)> MultiFunctionCases(Plan p)
    {
        foreach (var r in Phase2Cases())
            if (r.Name is not ("kworld" or "kworld2" or "kfwdup" or "kfwshd" or "kfst")) yield return r;
        yield return ("kcat", "called from main (also a macro's name)", "src/handlers.c");
        yield return ("kfst", "called from main (also a macro's name)", "src/handlers.c");
        foreach (var t in TargetNames(p))
            yield return ($"targets/{t}/board.c:kboard", "each target's own board.c", $"targets/{t}/board.c");
        yield return ("src/handlers.c:kfwdup", "static in two units, one reached", "src/handlers.c");
        yield return ("src/fw_attr.c:kfwdup", "static in two units, the unreached one", "src/fw_attr.c");
        yield return ("src/fw_attr.c:kfwshd", "a static shadows a global of the same name", "src/fw_attr.c");
        yield return ("src/handlers.c:kfwshd", "the shadowed global", "src/handlers.c");
        yield return ($"kfwtbneed__{KeepIn(p, 1)}", "needed by a prebuilt object only tb links", "src/handlers.c");
        if (p.Targets == 3) yield return ("kfwtckeep__DDK", "section only tc's script keeps", "src/fw_attr.c");
    }
}
