using System.Buffers.Binary;
using System.Text;

namespace CodeSpawner.Firmware;

// Phase 2 (1.5.0, plain firmware only): the reset path, assembly, linker scripts and prebuilt objects, so the
// function truth (docs/firmware-design.md "Functions") has roots to reach from. Every name here is spelled by its
// class: Z…Z must-drop, Y…Y open, anything else must-keep.
internal static partial class FirmwareEmitter
{
    public const string Reset = "Reset_Handler";
    public const string DbWorld2Path = BuildDir + "/compile_commands.world2.json";
    public const string FunctionCasesPath = TruthDir + "/cases.functions.tsv";
    /// <summary>firmware.roots: where execution starts; the function truth reaches from here.</summary>
    public static readonly string[] Roots = [Reset];
    private static readonly string[] ArmCc = ["clang", "--target=arm-none-eabi", "-mcpu=cortex-m4"];

    /// <summary>The C files phase 2 adds to the main build (fw-relative); world.c is also in the world-2 db.</summary>
    private static readonly string[] Phase2Units = ["src/main.c", "src/handlers.c", "src/fw_attr.c", "src/world.c"];
    /// <summary>The assembly the main build names (fw-relative); asm/spare.s is named by none.</summary>
    private static readonly string[] Phase2Asm = ["asm/startup.S", "asm/fw_util.s", "asm/big_0.s"];

    private static int Modules(Plan p) => p.DirOf(p.Files - 1) + 1;

    // Unit u's own calls: its families' functions, its traps' functions and unit 0's extras, from fu<u>.
    private static void FuText(Plan p, int u, List<string> calls, StringBuilder fn)
    {
        fn.Append($"int fu{u}(void)\n{{\n\tint r = {u % 7};\n");
        foreach (var c in calls) fn.Append($"\tr += {c};\n");
        fn.Append("\treturn r;\n}\n");
        if (u < 3) fn.Append($"int Zfu{u}deadZ(void)\n{{\n\treturn {u + 1};\n}}\n");
        // the first unit of each module holds its init, which main calls
        int file = p.UnitFile[u];
        int d = p.DirOf(file);
        if (file != d * FilesPerDir) return;
        var mine = new List<int>();
        for (int j = d * FilesPerDir; j < Math.Min(p.Files, (d + 1) * FilesPerDir); j++)
            if (!p.IsHeaderFile(j)) mine.Add(p.UnitFile.BinarySearch(j));
        foreach (int x in mine) if (x != u) fn.Append($"int fu{x}(void);\n");
        fn.Append($"void m{d}_init(void)\n{{\n");
        foreach (int x in mine) fn.Append($"\tfu{x}();\n");
        fn.Append("}\n");
    }

    private static void WritePhase2(Plan p)
    {
        foreach (var d in new[] { "asm", "ld", "lib" }) Directory.CreateDirectory(p.Fw(d));
        int mods = Modules(p);

        var h = new StringBuilder("""
            /* fw_init.h: the C side of the reset path (Reset_Handler, main, the module inits) */
            #ifndef FW_INIT_H
            #define FW_INIT_H

            """);
        for (int d = 0; d < mods; d++) h.Append($"void m{d}_init(void);\n");
        h.Append("""
            int kfz1(void);
            int kfz2(void);
            int kworld(void);
            int fw_asm_crc(int);
            int fw_objfn(void);
            #endif

            """);
        File.WriteAllText(p.Fw("include/fw_init.h"), h.ToString());

        var m = new StringBuilder("""
            /* main.c: Reset_Handler's bl main lands here */
            #include "fw_init.h"
            typedef int (*fwfp)(void);
            const fwfp fw_tbl[] = { kfz1, kfz2 };
            int main(void)
            {
            	int r = 0;

            """);
        for (int d = 0; d < mods; d++) m.Append($"\tm{d}_init();\n");
        m.Append("""
            	for (unsigned i = 0; i < sizeof fw_tbl / sizeof fw_tbl[0]; i++) r += fw_tbl[i]();
            	r += kworld();
            	r += fw_asm_crc(r);
            	r += fw_objfn();
            	return r;
            }

            """);
        File.WriteAllText(p.Fw("src/main.c"), m.ToString());

        File.WriteAllText(p.Fw("src/handlers.c"), """
            /* handlers.c: functions the vector table, the assembly, the linker scripts and the prebuilt objects reach */
            typedef int (*fwfp)(void);
            void Default_Handler(void)
            {
            	for (;;) { }
            }
            void TIM2_IRQHandler(void) { }
            void TIM3_IRQHandler(void) { }
            void USART1_IRQHandler(void) { }
            void UART_Handler(void) { }
            void KTIM5_IRQHandlerZ(void) { }
            void ZUART4_IRQHandler(void) { }
            int ZKTIM5_IRQHandlerZ(void) { return 1; }
            int ZUART4_IRQHandler_oldZ(void) { return 2; }
            int kSystemInit(void) { return 0; }
            int kcat(int a, int b) { return a + b; }
            int kfst(int a, int b) { return a - b; }
            int kfz1(void) { return 1; }
            int kfz2(void) { return 2; }
            int ZFwTblDeadZ(void) { return 3; }
            int ZFwTblUserZ(void)
            {
            	fwfp q = ZFwTblDeadZ;
            	return q();
            }
            static int ZFwLocalZ(void) { return 4; }
            int ZFwLocalUserZ(void) { return ZFwLocalZ() + 1; }
            int kfwasmcall(void) { return 5; }
            void kfwinit(void) { }
            int kfwprovide(void) { return 6; }
            int kfwextern(void) { return 7; }
            int kfwassign(void) { return 8; }
            int kfwobj(void) { return 9; }
            int kfwar(void) { return 10; }
            int kfwbigs(void) { return 11; }
            int YFwSpareY(void) { return 12; }
            int YFwCmtY(void) { return 13; }
            int YFwCmtSY(void) { return 14; }

            """);

        File.WriteAllText(p.Fw("src/fw_attr.c"), """
            /* fw_attr.c: functions an attribute keeps, or a linker script's section */
            __attribute__((used)) int kfwused(void) { return 1; }
            __attribute__((used, retain)) int kfwretain(void) { return 2; }
            __attribute__((constructor)) void kfwctor(void) { }
            __attribute__((section(".fwtab.0"))) int kfwtab0(void) { return 3; }
            __attribute__((section(".fwmem"))) int kfwmem(void) { return 4; }
            __attribute__((section(".fwbig"))) int kfwbigld(void) { return 5; }
            __attribute__((section(".fwtab_iar"))) int YFwIarY(void) { return 6; }
            __attribute__((section(".fwtab_arm"))) int YFwArmY(void) { return 7; }
            __attribute__((section(".fwnokeep"))) int ZFwNoKeepZ(void) { return 8; }

            """);

        File.WriteAllText(p.Fw("src/world.c"), """
            /* world.c: compiled once per world (FW_WORLD=1 in the main db, 2 in the world-2 db) */
            int kworld1(void) { return 1; }
            int kworld2(void) { return 2; }
            #if FW_WORLD == 1
            int kworld(void) { return kworld1(); }
            #else
            int kworld(void) { return kworld2(); }
            #endif

            """);

        File.WriteAllText(p.Fw("asm/fw_asm.h"), """
            /* fw_asm.h: a header of assembler lines startup.S includes */
            	.equ fw_stack_words, 256

            """);
        File.WriteAllText(p.Fw("asm/startup.S"), $$"""
            /* startup.S: the vector table and the reset path; this comment names YFwCmtSY */
            #include "fw_asm.h"
            #define H(x) x##_Handler
            	.syntax unified
            	.cpu cortex-m4
            	.thumb

            	.macro def_irq name
            	.weak \name
            	.thumb_set \name, Default_Handler
            	.endm

            	.macro def_vec name
            	.word \name\()_IRQHandler
            	.endm

            	.set fw_vec_count, 11

            	.section .isr_vector,"a",%progbits
            	.global g_fwVectors
            g_fwVectors:
            	.word _estack
            	.word {{Reset}}
            	.word NMI_Handler
            	.word TIM2_IRQHandler
            	.word TIM3_IRQHandler
            	.word H(UART)
            	def_vec USART1
            	.include "fw_vec.inc"
            	.word fw_hook
            	.word fw_alias
            	.word fw_stack_words

            	.text
            	.global {{Reset}}
            	.type {{Reset}}, %function
            {{Reset}}:
            	bl kSystemInit
            	bl main
            	b .

            	.weak NMI_Handler
            	.thumb_set NMI_Handler, Default_Handler
            	.weak TIM2_IRQHandler
            	.thumb_set TIM2_IRQHandler, Default_Handler
            	def_irq TIM3_IRQHandler

            """);
        File.WriteAllText(p.Fw("asm/fw_vec.inc"), """
            @ fw_vec.inc: vectors startup.S .includes
            	.word KTIM5_IRQHandlerZ
            	.word ZUART4_IRQHandler

            """);
        File.WriteAllText(p.Fw("asm/fw_util.s"), """
            @ fw_util.s: assembly the C side calls; this comment names YFwCmtY
            	.syntax unified
            	.cpu cortex-m4
            	.thumb
            	.text
            	.global fw_asm_crc
            	.type fw_asm_crc, %function
            fw_asm_crc:
            	push {r4, lr}
            	bl kfwasmcall
            	pop {r4, pc}

            	.section .init_array,"aw",%init_array
            	.word kfwinit

            """);
        File.WriteAllText(p.Fw("asm/spare.s"), """
            @ spare.s: assembly no build command names
            	.syntax unified
            	.cpu cortex-m4
            	.thumb
            	.text
            	.global fw_spare
            	.type fw_spare, %function
            fw_spare:
            	push {r4, lr}
            	bl YFwSpareY
            	pop {r4, pc}

            """);
        using (var w = new FwWriter(p.Fw("asm/big_0.s")))
        {
            w.Text("""
                @ big_0.s: assembly past maxParseBytes
                	.syntax unified
                	.cpu cortex-m4
                	.thumb
                	.text
                	.global fw_big_s
                	.type fw_big_s, %function
                fw_big_s:
                	push {r4, lr}
                	bl kfwbigs
                	pop {r4, pc}

                """);
            if (!w.PadTo(BoundaryBig)) throw new InvalidOperationException("firmware: big_0.s overran its size");
        }

        File.WriteAllText(p.Fw("ld/fw.ld"), $$"""
            /* fw.ld: the link script (ld -T fw.ld -L fw/ld) */
            ENTRY({{Reset}})
            INCLUDE mem.ld
            INCLUDE big_0.ld
            EXTERN(kfwextern fw_arfn)
            PROVIDE(fw_hook = kfwprovide);
            fw_alias = kfwassign;
            _estack = ORIGIN(RAM) + LENGTH(RAM);
            SECTIONS
            {
            	.isr_vector : { KEEP(*(.isr_vector)) } > FLASH
            	.text : { *(.text*) } > FLASH
            	.fwtab : { __fwtab_start = .; KEEP(*(.fwtab.*)) __fwtab_end = .; } > FLASH
            	.init_array : { KEEP(*(.init_array*)) } > FLASH
            	.data : { *(.data*) } > RAM AT > FLASH
            	.bss : { *(.bss*) *(COMMON) } > RAM
            }

            """);
        File.WriteAllText(p.Fw("ld/mem.ld"), """
            /* mem.ld: the memory map, INCLUDEd by fw.ld */
            MEMORY
            {
            	FLASH (rx) : ORIGIN = 0x08000000, LENGTH = 64M
            	RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 128K
            }
            SECTIONS
            {
            	.fwmem : { KEEP(*(.fwmem)) } > FLASH
            }

            """);
        using (var w = new FwWriter(p.Fw("ld/big_0.ld")))
        {
            w.Text("""
                /* big_0.ld: a linker script past maxParseBytes, INCLUDEd by fw.ld */
                SECTIONS
                {
                	.fwbig : { KEEP(*(.fwbig)) } > FLASH
                }

                """);
            if (!w.PadTo(BoundaryBig)) throw new InvalidOperationException("firmware: big_0.ld overran its size");
        }
        File.WriteAllText(p.Fw("ld/fw.icf"), """
            /* fw.icf: an IAR ILINK configuration (copied, not read) */
            define memory mem with size = 4G;
            define region ROM_region = mem:[from 0x08000000 to 0x0807FFFF];
            define region RAM_region = mem:[from 0x20000000 to 0x2001FFFF];
            keep { section .fwtab_iar };
            place in ROM_region { readonly };
            place in RAM_region { readwrite };

            """);
        File.WriteAllText(p.Fw("ld/fw.sct"), """
            ; fw.sct: an armlink scatter file (copied, not read)
            LR_IROM1 0x08000000 0x00080000 {
              ER_IROM1 0x08000000 0x00080000 {
                *.o (RESET, +First)
                *(.fwtab_arm)
                .ANY (+RO)
              }
              RW_IRAM1 0x20000000 0x00020000 {
                .ANY (+RW +ZI)
              }
            }

            """);
        File.WriteAllText(p.Fw("ld/fw_ti.cmd"), """
            /* fw_ti.cmd: a TI linker command file (copied, not read) */
            -stack 0x400
            MEMORY
            {
                FLASH (RX) : origin = 0x00000000, length = 0x00040000
                SRAM (RWX) : origin = 0x20000000, length = 0x00008000
            }
            SECTIONS
            {
                .text : > FLASH
                .bss : > SRAM
            }

            """);

        File.WriteAllBytes(p.Fw("lib/fwcrc.o"), ArmObject("fw_objfn", "kfwobj"));
        File.WriteAllBytes(p.Fw("lib/libfwx.a"), Archive("fwx.o", ArmObject("fw_arfn", "kfwar"), "fw_arfn"));
    }

    /// <summary>A minimal ELF32 ARM relocatable: .text holds Thumb <c>push {r7, lr}; bl need; pop {r7, pc}</c> as
    /// global function <paramref name="def"/>, with an R_ARM_THM_CALL against <paramref name="need"/> (global,
    /// undefined). Written by hand so the bytes never depend on a toolchain.</summary>
    internal static byte[] ArmObject(string def, string need)
    {
        byte[] text = [0x80, 0xB5, 0xFF, 0xF7, 0xFE, 0xFF, 0x80, 0xBD];
        byte[] strtab = Encoding.ASCII.GetBytes($"\0$t\0{def}\0{need}\0");
        byte[] shstr = Encoding.ASCII.GetBytes("\0.text\0.rel.text\0.symtab\0.strtab\0.shstrtab\0");
        int nmT = 1, nmRel = 7, nmSym = 17, nmStr = 25, nmShs = 33;
        int defName = 4, needName = 4 + def.Length + 1;
        // symbols: null, $t (local, mapping), def (global func, thumb), need (global, undefined)
        var sym = new byte[16 * 4];
        void Sym(int i, int name, uint value, uint size, byte info, ushort shndx)
        {
            var s = sym.AsSpan(i * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)name);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], value);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], size);
            s[12] = info;
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], shndx);
        }
        Sym(1, 1, 0, 0, 0x00, 1);
        Sym(2, defName, 1, (uint)text.Length, 0x12, 1);
        Sym(3, needName, 0, 0, 0x10, 0);
        var rel = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(rel, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(rel.AsSpan(4), (3u << 8) | 10u);   // R_ARM_THM_CALL

        var body = new List<byte>();
        int Put(byte[] b) { while (body.Count % 4 != 0) body.Add(0); int at = 52 + body.Count; body.AddRange(b); return at; }
        int oText = Put(text), oRel = Put(rel), oSym = Put(sym), oStr = Put(strtab), oShs = Put(shstr);
        while (body.Count % 4 != 0) body.Add(0);
        int shoff = 52 + body.Count;
        var sh = new byte[40 * 6];
        void Sh(int i, int name, uint type, uint flags, int off, int size, uint link, uint info, uint align, uint entsize)
        {
            var s = sh.AsSpan(i * 40);
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)name);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)off);
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], link);
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], info);
            BinaryPrimitives.WriteUInt32LittleEndian(s[32..], align);
            BinaryPrimitives.WriteUInt32LittleEndian(s[36..], entsize);
        }
        Sh(1, nmT, 1, 6, oText, text.Length, 0, 0, 4, 0);
        Sh(2, nmRel, 9, 0x40, oRel, rel.Length, 3, 1, 4, 8);
        Sh(3, nmSym, 2, 0, oSym, sym.Length, 4, 2, 4, 16);
        Sh(4, nmStr, 3, 0, oStr, strtab.Length, 0, 0, 1, 0);
        Sh(5, nmShs, 3, 0, oShs, shstr.Length, 0, 0, 1, 0);

        var eh = new byte[52];
        eh[0] = 0x7F; eh[1] = (byte)'E'; eh[2] = (byte)'L'; eh[3] = (byte)'F'; eh[4] = 1; eh[5] = 1; eh[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(16), 1);       // ET_REL
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(18), 40);      // EM_ARM
        BinaryPrimitives.WriteUInt32LittleEndian(eh.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(eh.AsSpan(32), (uint)shoff);
        BinaryPrimitives.WriteUInt32LittleEndian(eh.AsSpan(36), 0x05000000);   // EABI version 5
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(40), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(46), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(48), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(eh.AsSpan(50), 5);
        return [.. eh, .. body, .. sh];
    }

    /// <summary>A System V ar archive of one member, with the <c>/</c> symbol index a linker needs.</summary>
    internal static byte[] Archive(string member, byte[] obj, string symbol)
    {
        static byte[] Hdr(string name, int size) =>
            Encoding.ASCII.GetBytes($"{name,-16}{"0",-12}{"0",-6}{"0",-6}{"644",-8}{size,-10}`\n");
        var index = new List<byte>();
        var cnt = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(cnt, 1);
        index.AddRange(cnt);
        int indexSize = 4 + 4 + symbol.Length + 1;
        int padded = indexSize + (indexSize & 1);
        int memberAt = 8 + 60 + padded;
        var off = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(off, (uint)memberAt);
        index.AddRange(off);
        index.AddRange(Encoding.ASCII.GetBytes(symbol + "\0"));
        var a = new List<byte>(Encoding.ASCII.GetBytes("!<arch>\n"));
        a.AddRange(Hdr("/", indexSize));
        a.AddRange(index);
        if ((indexSize & 1) != 0) a.Add((byte)'\n');
        a.AddRange(Hdr(member + "/", obj.Length));
        a.AddRange(obj);
        if ((obj.Length & 1) != 0) a.Add((byte)'\n');
        return [.. a];
    }

    private static void AsmEntry(System.Text.Json.Utf8JsonWriter w, bool rel, Func<string, string> P, string file)
    {
        w.WriteStartObject();
        w.WriteString("directory", rel ? "." : P("."));
        w.WriteStartArray("arguments");
        foreach (var a in ArmCc) w.WriteStringValue(a);
        w.WriteStringValue("-I");
        w.WriteStringValue(P($"{FwSpec.FwDir}/asm"));
        w.WriteStringValue("-c");
        w.WriteStringValue(P($"{FwSpec.FwDir}/{file}"));
        w.WriteEndArray();
        w.WriteString("file", P($"{FwSpec.FwDir}/{file}"));
        w.WriteEndObject();
    }

    /// <summary>The function cases (fw-truth/cases.functions.tsv): each graded function and why it is in its class.</summary>
    private static void WriteFunctionCases(Plan p)
    {
        var sb = new StringBuilder("name\tcase\tsite\n");
        foreach (var (name, kase, site) in Phase2Cases()) sb.Append(name).Append('\t').Append(kase).Append('\t').Append(site).Append('\n');
        File.WriteAllText(Path.Combine(p.Root, FunctionCasesPath), sb.ToString());
    }

    private static IEnumerable<(string Name, string Case, string Site)> Phase2Cases() =>
    [
        ("fu0", "reached: main, m0_init, fu", "src/" + "m0/u0.c"),
        ("Default_Handler", "weak default (.thumb_set)", "src/handlers.c"),
        ("TIM2_IRQHandler", "strong over a weak alias", "src/handlers.c"),
        ("TIM3_IRQHandler", "asm .macro, plain argument", "src/handlers.c"),
        ("USART1_IRQHandler", "asm .macro, \\name\\() paste", "src/handlers.c"),
        ("UART_Handler", ".S cpp paste", "src/handlers.c"),
        ("KTIM5_IRQHandlerZ", "asm .include", "src/handlers.c"),
        ("ZKTIM5_IRQHandlerZ", "dead, shares a suffix with an asm name", "src/handlers.c"),
        ("ZUART4_IRQHandler", "asm .include", "src/handlers.c"),
        ("ZUART4_IRQHandler_oldZ", "dead, shares a prefix with an asm name", "src/handlers.c"),
        ("kfz1", "function-pointer table", "src/handlers.c"),
        ("ZFwTblDeadZ", "address taken only in a dead function", "src/handlers.c"),
        ("ZFwLocalZ", "static, called only from a dead function", "src/handlers.c"),
        ("Zfu0deadZ", "dead", "src/" + "m0/u0.c"),
        ("kfwasmcall", "asm bl", "src/handlers.c"),
        ("kfwinit", "asm .init_array", "src/handlers.c"),
        ("YFwCmtY", "named only in an asm comment", "src/handlers.c"),
        ("YFwSpareY", "named only by asm no build names", "src/handlers.c"),
        ("kfwbigs", "named only by asm past maxParseBytes", "src/handlers.c"),
        ("kfwprovide", "ld PROVIDE", "src/handlers.c"),
        ("kfwextern", "ld EXTERN", "src/handlers.c"),
        ("kfwassign", "ld assignment", "src/handlers.c"),
        ("kfwobj", "needed by a prebuilt .o", "src/handlers.c"),
        ("kfwar", "needed by a prebuilt .a", "src/handlers.c"),
        ("kfwused", "attribute used", "src/fw_attr.c"),
        ("kfwretain", "attribute retain", "src/fw_attr.c"),
        ("kfwctor", "attribute constructor", "src/fw_attr.c"),
        ("kfwtab0", "section under ld KEEP", "src/fw_attr.c"),
        ("kfwmem", "section under KEEP in an INCLUDEd ld", "src/fw_attr.c"),
        ("kfwbigld", "section under KEEP in ld past maxParseBytes", "src/fw_attr.c"),
        ("YFwIarY", "section kept only by an IAR .icf", "src/fw_attr.c"),
        ("YFwArmY", "section placed only by an armlink .sct", "src/fw_attr.c"),
        ("ZFwNoKeepZ", "section no script keeps", "src/fw_attr.c"),
        ("kworld", "defined in both FW_WORLD branches", "src/world.c"),
        ("kworld2", "kept only in world 2 (union)", "src/world.c"),
    ];
}
