# Firmware corpus (`gen --firmware`)

A synthetic firmware tree for testing a header carver (for example CodeCarver), with two truths beside it. The
**define truth** is about the header carve: the tree has paste-macro register headers past `maxParseBytes`, dense
headers, units that reach those headers' defines by every paste shape, and trap files, and the truth says what a
**sound** header carve must keep, what it must drop, and exact counts. The **function truth** (plain preset) says
which C functions a firmware link needs, given asm, linker scripts and prebuilt objects. Both truths follow the
rules below, never any tool's current code. A case a carver gets wrong is that carver's bug, not a corpus case to
remove.

Off unless `--firmware` is given. `--preset firmware` is a bare tree plus `--firmware`. The firmware corpus is
C-only (`--lang cpp` is refused), needs the manifest (`--manifest false` is refused), and is refused with
`--from-profile`. Every `--fw-*` knob is refused without `--firmware`. `mutate` leaves the firmware dirs alone,
because the truth names their bytes.

## Layout

```
<out>/
  fw/                        # the firmware root: everything the truth is about
    big/regs_N.h             #   big headers (> maxParseBytes): paste macros, families' defines, never-reached filler
    dense/dx_N.h             #   dense headers (1 MB .. maxParseBytes, >= 60% define lines), hosts of some families
    dense/dense_{70,60,5999,50}.h, dense/dense_small.h, dense/shared_0.h   # density / size fixtures
    include/fw_paste.h       #   CAT, CAT3, PCAT, the forwarders' targets
    src/mD/uI.c, src/mD/kI.h #   units (one per compile db entry) and the kept headers beside them
    src/traps/trap_T.h       #   trap text: kept, included under #if 0, never compiled
    src/bigsrc_0.c           #   a .c past maxParseBytes (carved like a big header)
    src/table_big.c          #   exactly maxParseBytes: parsed, not big
    misc/                    #   hostile files: empty, BOMs, latin-1, CRLF, lone CR, NULs, an unterminated comment, a
                             #   comment around a directive's `#`, a backslash-space splice, hidden
    include/fw_q3.h          #   compiled hostile text (unit 0): a `(` in a comment and in a char literal, then 2,000 `#if`s
    work/wreg_N.h            #   the bulk carve, with --fw-bulk (see "Bulk shape")
    src/main.c, src/handlers.c, src/fw_attr.c, src/world.c   # function truth (plain preset): main, the handlers and
                             #   targets, the attribute/section functions, the two worlds (see "Function truth")
    asm/, ld/, lib/          #   function truth: startup.S, fw_util.s, an unbuilt spare.s, an oversize big_0.s, the
                             #   headers and .inc they include; fw.ld
                             #   (INCLUDEs mem.ld, big_0.ld), IAR/armlink/TI files; a prebuilt .o and .a
  ext/include/, ext/app/     # outside the root: an OS header every unit includes (it includes a Hidden one),
                             #   one nothing includes (maybe-kept), host.c
  fw-build/compile_commands.json   # one entry per unit, plus ext/app/host.c; the function truth adds main.c,
                             #   handlers.c, fw_attr.c, world.c and the asm a build names (startup.S, fw_util.s, big_0.s)
  fw-build/compile_commands.world2.json   # function truth: world.c at -DFW_WORLD=2 only (the union's other build)
  fw-truth/                  # main.<list>.txt, union.<list>.txt, cases.tsv, cases.functions.tsv (function truth)
```

Every compile db entry is `cc -std=c11 -I fw/include -I fw/big -I fw/dense -I ext/include -DKD_7 -DKDV_9=KDV_VAL
-DKDE_N=4 -c <file>`. With `--fw-bulk`, `-I fw/work` comes after `-I fw/dense`. With `--fw-db-paths absolute` (the
default) `directory` and every path are absolute. With `relative`, `directory` is `"."` and the paths are relative to
the corpus root, so the db resolves against the directory it names, resolved from the corpus root.

In the plain preset the db's compiler is `clang --target=arm-none-eabi -mcpu=cortex-m4` instead of `cc`.
`world.c` alone adds `-DFW_WORLD=1` (`=2` in the world-2 db). The asm files a build names (`startup.S`, `fw_util.s`,
`big_0.s`) have entries of their own: `clang --target=arm-none-eabi -mcpu=cortex-m4 -I fw/asm -c <file>`.

Every unit compiles cleanly with `-std=c11 -Wall -Werror`, with or without `-DFW_ALT`, and with `-DFW_SEL=7` or
`-DFW_SEL=19` (checked with clang and gcc). The kinds' shapes were checked with gcc and clang -E. Every arm entry of
both dbs compiles with clang `-Wall -Werror -ffunction-sections -fdata-sections` and links with ld.lld
`--gc-sections -T fw.ld -L fw/ld`, with `lib/fwcrc.o` and `lib/libfwx.a` (checked at scales 0.01 and 0.1). In the
linked images every must-keep function is defined, and no must-drop or open function is.

## Names carry their class

Every name a case is about is spelled by its class:

- `Z…Z`, `Y…Z` and `Z…Y` (starts and ends with `Z` or `Y`, at least one of them `Z`): **must drop**;
- `Y…Y`: **open** (the truth does not decide it);
- anything else: **must keep**.

Gen computes the truth from the tree it wrote, using the rules below, and fails if any name's class disagrees with
its spelling. It also fails if a `cases.tsv` row names a define no carved file has (a row that would test
nothing). A slip in the generator can never ship as truth.

`fw-truth/cases.tsv` (`name\tcase\tsite`, LF) names each case's target and where it is exercised. The site is
relative to `fw/`, or `../…` for a site outside the root (`../ext/…`; the `-DKDV_9=KDV_VAL` case is
`../fw-build/compile_commands.json`). A site need not be in `mustKeepFiles`: the `big-source` row's site
`src/bigsrc_0.c` is a file no build compiles and nothing includes, so it is unlisted (open), while its `KBS0X_5`
stays a must-keep define, graded in the forced pass. It lists:

- every trap;
- the hostile and reserved cases, and `self-comma`: `KPORT(KSRC0X)` with `KSRC0X = KSRC0X, 1`, an
  object-like name whose comma body names itself;
- three families of each of the 44 kinds: direct, cat-prefix, cat-suffix, multi-line-call, forwarder-chain, alias,
  higher-order, computed-call, wanted-body, beside-paste, object-expansion, if0-call, comment-seed, cat3-split,
  redefinition, kept-header-body, comment-call-open, string-call-open, numeric-object, call-arg, nested-call-edge,
  number-suffix, empty-edge, call-edge, digraph, higher-order-forwarder,
  use-pairs, uncalled-wildcard, expanded-arg, empty-expansion, empty-arg, higher-order-pairs,
  comma-expansion, split-arg, body-alias, open-body-call, branch-paren-in-args, branch-paren-top (open: a
  macro's `(` after a directive at top level), if-condition, nested-callee, computed-head, comment-hash,
  empty-tail-alias and param-tail-alias;
- a **decoy** beside each cat-prefix, cat-suffix, higher-order-forwarder, use-pairs, uncalled-wildcard,
  higher-order-pairs, split-arg, empty-tail-alias and param-tail-alias target: a must-drop name that has only part of what the target's uses build (one use's start
  with another use's end, or a near miss of a wildcard);
- `interior-letter` (a one-letter argument between two pastes, as in `KHWP(Z, Z)`) and `define-arg-value` (a `-D`
  value used as a piece).

A kind's case is marked `(dense host)` when its defines sit in a dense header, and `(long chain)` for a
`--fw-chain-max` forwarder chain.

The traps (8, in every corpus) cover:

- a call to a non-alias, and to non-paste variadic or stringify macros;
- an unwanted define body;
- no expansion beside `##`;
- forwarder cycles;
- a define that opens a block comment;
- a call split across `#if` branches;
- a paste call left open at EOF;
- 64- and 65-piece joins;
- one object define with 20 bodies.

## The truth rules

The rules are stated here in full. They follow the header-carve rules of the carver named at the top, applied to
`fw/` and to whatever files the compile db's builds include from outside it.

The truth is about the header carve alone. **Every `fw/` file that is not a carved file is kept text, kept whole**,
whether or not anything includes it: the traps, `dense_60.h`/`dense_70.h` and the other fixtures, and every unit and
kept header. A carve that also drops unreached files does not match it. Run the carver with every file forced kept,
so only the carved files' define lines change.

1. **K files.** A file under `fw/` is big when it is longer than `maxParseBytes` (20,000,000 bytes; exactly
   20,000,000 is parsed). It is dense when it is 1,000,000 bytes up to `maxParseBytes` and, over its first 262,144
   chars (a truncated last line dropped), define lines are at least 0.60 of the substantive lines and there are at
   least 50 of them. Blank lines and lines starting with `//` or `/*` are not substantive. Big and dense files are
   the K files. A K file included whole from outside the root (here `dense/shared_0.h`, via `ext/app/host.c`) is
   kept whole. The rest are the **carved** files.
2. **Paste macros.** A name is a paste macro if any of its definitions pastes a parameter (`a##b`, not
   `, ##__VA_ARGS__`), or forwards to one. A function-like body forwards when it holds a call and names
   one, called or not (`APPLY2(CAT, a)`); any define forwards when its body ends in one (`#define PICK(x) CAT`). The
   walk in from the body's end goes past names that may expand to nothing and past parameters (an argument may be empty):
   `#define F CAT EMPTYM` and `#define PICK2(x) CAT x` forward to `CAT`, and a name that need not be empty stops
   it. This is computed to a fixpoint, so order does not matter.
3. **Uses** (per-use pairing). Each paste-macro call in kept text is a **use**, and each use builds its own
   names; pieces of different uses never pair. A use is followed through forwarders and higher-order calls into
   every `##` chain it reaches, and each chain builds the cross product of its slots:
   - a literal token gives itself; an operand that is not an identifier or identifier-like pp-number (`2`, `2u`,
     `0x1F`) means the chain builds nothing;
   - an argument gives its spelling, plus its expansion where the macro expands it (nested paste results
     included). A left slot takes the argument's last token, a right slot its first, an interior slot both. An interior argument of
     several tokens splits the chain: `HX(b) = X_##b##_Y` with `HX(KA KB)` builds `X_KA` and `KB_Y`,
     never `X_KA_Y`;
   - an argument whose top-level commas come from an object-like name (or an alias chain to one), as in
     `PORT(PIN_LED)` with `PIN_LED = KGPIOA, 5`, is re-split at those commas;
   - an argument that can come to one token or to several (a name that may be empty in it, through a forwarder)
     is built both ways, over every mix. A function-like call in an argument that may give a
     top-level comma is pooled under max only, so what it builds is open;
   - an empty argument, or one whose expansion can be empty, gives nothing (the slot is skipped);
   - `__LINE__` and `__COUNTER__` are a run of digits (one or more), and an undefined `__name` is any text (zero
     or more chars), expanded or not. A chain with more than one such wildcard keeps only its outer text and one
     wildcard;
   - a call's missing arguments at end of file (max only) are any text;
   - a body that leaves a call open (`OPENF(x) = CAT(x,`) takes the rest of the call from the text after it (max
     only, so its names are open). Its last argument runs on as any text, and its missing ones are any text.

   A chain with no parameter builds its literal name. A paste macro that is named in code but never called gets
   one use whose arguments are all any text. "Called" is over the whole tree, dropped text included; comments and
   strings (max) never call or name. **The body-naming rule:** a paste macro that is named, and not called, only in
   the body of a define in a carved file counts as named under max only. Whether it is called depends on whether
   that define is kept, so its any-text use is open, not must-keep. A name is called when `(` follows it, when it ends a define body, when only
   names that may be empty or parameters follow it there (`CAT EMPTYM`, `CAT x`), and when the
   argument for the parameter after it opens with `(` (`PICK2((KPK_, 2))` gives `CAT (KPK_, 2)`).

   A use also gets its own **pool** (any of its start pieces with any of its end pieces, never overlapping) when its
   callee is computed or the call is open across `#if`/`#elif`/`#else`/`#endif`, and falls back to the pool alone
   for a chain whose product passes 4,096 names (a wildcard counts 1; its names are open). Those calls are also built
   exactly: a computed callee (`CAT(GET, CAT)(0)(x, y)`, `APPLY(ID(CAT), x, y)`) through the names it can expand to,
   and a call across a conditional through each branch's tokens.
   A computed call keeps its pool: `PICK2(NOPE)(YX_, 3Y)` builds `YX_3Y` from its
   own pool even though `NOPE` takes the arguments; a name none of its start pieces begins stays dropped. A pool is
   max only: a name only a pool builds, and no exact build, is **open**, since the
   truth cannot tell whether the call is made, and a carve that keeps it (sound) or drops it (proved) both pass.
   Exact builds stay must-keep. A use's exact end names (for a callee computed from it) are only the names it can
   end with: every token after them can be empty.
   - `%:` and `%:%:` are `#` and `##`. The builds' `-D` values are define bodies.

   An argument list runs to its matching `)` across lines and directives. At `#elif`/`#else` the tracker goes back
   to its state at the `#if`, so each branch closes the call on its own, and after `#endif` it goes on from every
   branch (and from the `#if` itself when there is no `#else`). A macro's `(` after a directive is a call only inside
   another call's arguments, as in gcc and clang. At top level max still calls it, so its names are open.
   `#elifdef` and `#elifndef` are branches like `#elif`. Past 16 paths through the branches the first 16 are
   followed; under max the others' open calls are used as if the text ended there, so what only they build is
   open.

   An `#if`/`#elif` condition is kept text too. Its paste calls are uses (`#if XCAT(CFG_, 1)` keeps
   `CFG_1`), and the names after `defined` are not. A closed `/* */` comment before or after the `#` is a space, also
   one that runs over several lines. A define whose body has a comment running on to the next line is wanted
   (seeded), like one ending in an open comment.
4. **Wanted** starts as every identifier of kept text outside the carved files and every non-define line of the
   carved files. It also includes:
   - the builds' `-D` names;
   - every header outside the root that a build includes;
   - the K files kept whole;
   - the reserved names.

   It grows to a fixpoint through the bodies of wanted defines, and every use in a wanted body is followed. A
   carved-file define is also wanted when a use builds its name (exactly, by a wildcard, or from its pool, under max only).

   A header in a build's include dir outside the root that no build includes is **maybe-kept** text: a computed
   `#include MACRO` could name it, so a sound carver may keep it, and one that proves it unused may drop it. It
   seeds and gives pieces under max only, so the defines only it reaches are open.
5. **Classes.** The tracker runs twice:
   - **min** skips comments and strings, and drops a call left open at end of file;
   - **max** reads comments and strings, and commits a call left open at end of file.

   must-keep = wanted under min; must-drop = not wanted under max; open = the rest. A define inside a block comment
   is no define, and is in no list.

Text that is not read at all: a file with a NUL byte in its first 8 KB (binary). A NUL past 8 KB leaves the file
text. A file with a UTF-16 or UTF-32 BOM is decoded as such (a NUL then does not make it binary). Hidden files are read.

## Truth in the manifest

The manifest gains a top-level `firmware` object (manifestVersion stays 1; additive). It is described field by
field in [manifest-schema.md](manifest-schema.md#firmware--the-firmware-corpus-optional). The lists themselves are
files under `fw-truth/`:

- **Lists.** Names are sorted by ordinal byte order, one per line, LF. A list over 100 MB is gzipped (`.txt.gz`);
  its sha256 is always over the uncompressed text.
- **Builds.** The define lists have one build, `main`, and `union` is the same lists. A carver that keeps the union
  of its builds checks `union`. The function lists differ by build: see "Function truth".
- **`mustKeepFunctions`.** Empty (count 0) in `--preset firmware-bulk`. In the plain preset it is the function
  truth, with `mustDropFunctions` and `openFunctions` beside it.
- **`mustKeepFiles`.** It lists the `fw/` files that define a must-keep name, plus every `fw/` file in a unit's
  include closure. A file no build compiles and nothing includes is never listed, even if it defines a must-keep name.
  An unlisted file is open: a carve may keep or drop it. So
  `dense_60.h`, `dense_70.h` (whose only must-keep names are their own guards) and `src/bigsrc_0.c` (whose `KBS0X_5`
  its own `CAT` line seeds) are not listed. Those self-seeded names stay must-keep defines only because defines are
  graded in the forced pass. Paths are relative to `fw/`.

**Counts:**

- **Exact counts:**
  - `bigHeadersKept` counts every K file, big and dense;
  - `denseHeaders`;
  - `wholeIncludedFromOutside`;
  - `pasteMacros`: names defined in a carved file that are paste macros by any definition (direct, forwarding, or
    an object-like alias ending in one), kept or dropped;
  - `forwardingMacros`: names defined in a carved file as a function-like macro that pastes none of its own
    parameters and whose body holds a `(`, whatever it calls (so `#define ADD(a) ((a)+1)` counts);
  - `totalDefineLines`: define lines in carved files;
  - `mustKeep`, `mustDrop` and `open`;
  - `bytesBefore`: the carved files' bytes.
- **Bounds.** The over-keep is the carve's choice, so the truth bounds it:
  - **`definesDroppedMin`** is the must-drop define lines, and **`definesDroppedMax`** every define line that is not
    must-keep.
  - **`bytesAfterMax`** assumes only the must-drop lines are dropped, each blanked (its line terminator kept).
  - **`bytesAfterMin`** assumes every define line that is not must-keep is removed, terminator included.

  So both blanking and removing fall inside the bounds.
- **`treeSha256`** is a sha256 over every `fw/` file, in ordinal order of its `fw/`-relative path: the path, `\n`,
  the byte length, `\n`, then the bytes. Check it before carving, to catch a stale or damaged copy. It does not
  cover `ext/` or the compile db; verify checks those by recomputing the truth from them.

## Function truth

The plain preset (not `--fw-bulk`, which has no function truth) also carries a function truth: which C functions a
firmware link needs, given asm, linker scripts and prebuilt objects.

- **Roots.** `firmware.roots` is `["Reset_Handler"]`. `asm/startup.S` holds the vector table and `Reset_Handler`, which
  calls `kSystemInit` and `main`; `main` calls each module's `m<k>_init()` (in the module's first unit, prototypes in
  `include/fw_init.h`), which calls its units' `fu<N>()`, which call the unit's `ff*`s. A function-pointer table in
  `main.c` keeps its entries.
- **The assumed link.** The corpus has no link command. The truth assumes the documented one: C built
  `-ffunction-sections -fdata-sections`, then `ld.lld --gc-sections -T fw/ld/fw.ld -L fw/ld` over every arm db entry's
  object, `lib/fwcrc.o` and `lib/libfwx.a`, with no `-u`/`--undefined` and no `-e`/`--entry`.
- **Nodes are input sections:** each C function and global; each section of a linked asm file (after `.section`,
  `.pushsection`, `.text`, `.data`); each section of a prebuilt object (an archive member only when it defines a name
  the link still needs). **An input section is kept whole once any symbol in it is reached.**
- **Classes.** A function is **must-keep** if it is reached from the min roots, **must-drop** if it is not reached
  from the max roots, and **open** otherwise. Reach starts from exactly `firmware.roots` and the roots below. Reached
  means called or address-taken from a reached section, or from a reached global's initializer. Statics, and a local
  that shadows a global, resolve in their own file, and a second definition keeps its own statics. Where the truth
  cannot prove what the real toolchain decides, the name is open, never drop.
- **Spelling.** `Z…Z`, `Y…Z` and `Z…Y` are drop; `Y…Y` is open; any other name is keep. Gen and verify check it
  on the union.
- **Min roots** are `roots` plus:
  - the names a linker script says (`ENTRY`, `EXTERN`, `PROVIDE`, assignments), in `fw.ld` and what it `INCLUDE`s, and
    every `.ld`/`.lds`/`.ldscript` under `fw/`;
  - the sections a linker script `KEEP`s (`KEEP` as a word; `SORT`, `EXCLUDE_FILE` read);
  - the undefined globals of `lib/fwcrc.o`, which links whole (ELF32/64, either byte order; `ar` with SysV, GNU, BSD
    and thin names);
  - functions marked `retain` (`used, retain` on a prototype counts), and `constructor`s and `destructor`s whose
    `.init_array`/`.fini_array` input sections a script `KEEP`s.
- **What a reached section references** (strong, followed under min): C calls and address-taking, after the C
  preprocessor expands macros (function-like ones too, with `#`, `##` and `__VA_ARGS__`); `alias("t")` and
  `#pragma weak X = Y`; in linked asm, every branch and data operand, after `.include`/`#include` (quoted and `<…>`)
  and after `.macro` (`:req`, `:vararg`, keyword arguments, `\()`), `.irp`, `.irpc` and `.rept` are expanded;
  `.set`/`.equ`/`.thumb_set` aliases; in C inline asm (and top-level `__asm__`), branch and data operands.
- **Max only** (so open unless something strong reaches it): a reference to a `.weak` name the object never defines;
  names in an asm comment (`@`, `//`, `/* */`, `#`, `;` in `.asm`) or `#if 0`; the rest of an inline asm string; every
  name of asm no build names (`spare.s`); `used` functions; sections only an IAR `.icf`, an armlink `.sct` or a TI
  `.cmd` keeps, or a `sym = .;` bracket holds without `KEEP`; constructors and destructors without a `KEEP`; function
  names that appear in a header too big to scan (big and dense headers).
- **Two worlds.** `src/world.c` defines `kworld1` and `kworld2`, and `kworld` twice: under `#if FW_WORLD == 1` it
  calls `kworld1`, under `#else` `kworld2`. The main db compiles it at `-DFW_WORLD=1`; `firmware.compileDbUnion`
  (`fw-build/compile_commands.world2.json`) holds only world.c at `-DFW_WORLD=2`. So `kworld2` is must-drop in
  `builds.main` and must-keep in `builds.union`. The union is the union of the two builds, not one image.
- **The C scanner** joins splices, skips comments, strings and chars, and evaluates `#if`/`#ifdef`/`#ifndef` on the
  unit's `-D`s where it can (`N`, `X`, `X == N`, `defined`); anything else is live. A function-like macro whose `(` comes
  after a directive line is not invoked (as `clang -E` does). Its scope is the `fw/` files in a C unit's include
  closure, minus big and dense headers.
- **Files.** The asm, linker, IAR/armlink/TI files, the `.inc`, `.o` and `.a`, and the files they include are
  `mustKeepFiles`.
- **Cases.** `fw-truth/cases.functions.tsv` (`name`, `case`, `site`) lists each designed name. Expected FAILs for a
  carver that does not do these yet: `USART1_IRQHandler` (a `\name\()` paste in a `.macro`), `UART_Handler` (a
  cpp `##` paste in a `.S`), `kfwbigs` (only referenced from an asm file past `maxParseBytes`) and `kfwbigld` (only
  kept by a `.ld` past `maxParseBytes`).

The tree compiles, assembles and links: every db entry builds with its own flags, and `ld.lld --gc-sections -T
fw/ld/fw.ld -L fw/ld` links the objects with `lib/fwcrc.o` and `lib/libfwx.a` (checked with clang and ld.lld, and
against the truth lists).

## Bulk shape (`--fw-bulk`, `--preset firmware-bulk`)

`--fw-bulk` adds a carve shaped like a production-sized firmware tree, for measuring how a carver's passes grow. It
sits beside the correctness part, in `fw/work/wreg_N.h` (each one over `maxParseBytes`; on `-I fw/work`). The output
dir and the manifest key keep the older name `work`. `--preset firmware-bulk` is the bulk carve at `--scale` plus a
small, fixed correctness part (1 big header, 296 files, 192 families, 20,000 pasters, no dense headers), so nearly
all of its size and time is the bulk carve.

- **A tree of paste uses.** Each must-keep define is a `##` use: `WRP<n>(a, b)` pastes a start piece and an end
  piece. Units call level 0. Each level-k define's body holds 8 level-k+1 uses at the first step (`--fw-bulk-growth`
  9: the pieces seeded grow ×9 from pass 1 to pass 2), then the levels halve, down to `--fw-bulk-depth` levels. A
  carve that seeds level by level needs `levels + 1` passes (the last one finds nothing new).
- **Calibrated to a production-sized tree.** The proportions come from aggregate counts of a large real firmware
  tree. At 1x the bulk carve is 122,300,000 defines in about 590 headers of about 31 MB, 0.018% of them kept; the
  correctness part adds 222 units. Most of the bulk defines are the wide population below, and the use tree is a
  small part.
- **Three use modes**, cycling 10:7:3 over each level: direct (`WRP<n>`), forwarded (`WFW<n>`, a forwarder to a
  paster) and higher-order (`WAP<k>(WRP<n>, a, b)`). A macro can't expand inside its own expansion, so each level has
  its own pasters, forwarders and applier `WAP<k>`. `--fw-bulk-pasters` and `--fw-bulk-forwarders` set the pools,
  split across the levels.
- **Cross-call decoys** (`--fw-bulk-decoy-pct`) are the start piece of one use joined to the end piece of another.
  Global start and end pools keep them; per-use pairing (rule 3) drops them. Spelled `Z…Z`.
- **Half decoys** (`--fw-bulk-half-pct`) have one real piece and a dead end: must-drop under either rule.
- **Filler** (`ZWF…Z`) makes up the rest and pads each header past `maxParseBytes`.

The tree's shares are percents of the bulk defines (fractions allowed): `--fw-bulk-keep-pct` 0.018, `-decoy-pct` 1,
`-half-pct` 1.

**The wide population** is most of a production-sized tree: register defines whose names only a wildcard paste
matches, so a carver has to match patterns, not look names up. The pieces are 2 chars from `a–z` (no `q`) and `0–9`.

- **Patterns** (`--fw-bulk-patterns`, 2,300). Each is a kept paste macro `KWP<p>(x)` whose body pastes a fixed prefix
  and suffix around its parameter, and a kept root `KWT<p>` = `(KWP<p> + 0x…u)`, named (not called) in a unit's `#if
  0` block. The body-naming rule does not apply (a unit is not a carved file), so `KWP<p>` gets an any-text use: every
  name with its prefix and suffix is wanted under max. 70% are two-sided (`Y<cc>`…`<cc>Y`), 15% prefix-only
  (`Yq<cc>`…`_Y`), 15% suffix-only (`Y_`…`<cc>qY`).
- **Matched** names (`openByPattern`): register defines a pattern matches. Open: wanted under max (the any-text use
  is in a `#if 0` block, which min skips), not under min. They take what the other kinds leave of the budget.
- **Named only** (`--fw-bulk-named-pct`, 11%): `Y_<mid>_Y`, named only in a matched define's body. Open.
- **Near misses** (`--fw-bulk-near-pct`, 6%): a pattern's prefix with a wrong end (`…Z`), a wrong start with its
  suffix (`Z…`), or the full name with a char after it. Must drop.
- **Body macros** (`--fw-bulk-body-pasters` 2,600,000, `-body-forwarders` 2,900,000): a tenth are named in matched
  bodies (`Y_WP<j>_Y(p, r) p##r`, `Y_WF<f>_Y`: open), the rest nowhere (`ZWQP<j>Z`, `ZWQF<f>Z`: must drop).
  `--fw-bulk-body-uses` (1,800,000) matched bodies call a used paster on 2 of `--fw-bulk-pieces` (500,000) pieces.
  The body-naming rule makes each body macro's names open, not kept: whether it is called depends on whether the
  matched define is kept.
- **Edges** (2, 6 kept defines each, in `wreg_0.h`): a paste macro reached through a forwarder (`KWEF<e>(KWER<e>)`
  builds `KWEA<e>KWER<e>_Q`) and a function-like call with a space before its `(` (`KWEQ<e> (KWES<e>)`). Unit 0
  calls both in `fwe<e>()`.
- **Outside the db**: `fw/src/uncovered/unc_<i>.c` (`--fw-bulk-uncovered`, 4,340) and `fw/asm/start_<i>.s` /
  `vec_<i>.asm` (`--fw-bulk-asm`, 21, at least 2). No build compiles them, they name nothing a carve keeps, and they
  hold no define (so they are in no list). Kept text, kept whole.
- **A computed include**: unit 0 includes `FW_HDR`, given by the db as `-DFW_HDR="fw_cfg.h"`; unit 1 includes
  `fw_cfg.h` directly. Both use `KCFG0X`, which only that header defines.

Value lines are about 145 bytes: a register expression and a `/* rst 0x…, rw, bit n */` comment.

At the edges the counts give way to the shape, and the manifest records what was written:

- Level 0 has at least one use a header, so at tiny sizes more than `--fw-bulk-keep-pct` is kept.
- The tree ends at the first level smaller than the header count, so `levels` can be fewer than `--fw-bulk-depth`.
- Cross decoys are capped at the A pieces × B pieces there are. Half decoys need both sides; without them there are
  none. Filler takes up the rest, so `--fw-bulk-defines 0` still writes one padded header.
- A level with no forwarders folds its forwarded uses into direct ones. A level with no higher-order use (a level of
  under 18 uses) gets no applier, so `appliers` can be fewer than `levels`.
- A forwarder chain goes back to a paster every `--fw-chain-max` links.
- `tree.byLevel[k].piecesSoFar` counts the uses (one start and one end piece each) seeded by the end of level `k`.

`firmware.work` in the manifest gives the construction counts, which verify recounts from the lists. The top level
is the whole bulk carve and the use tree's counts are under `tree`. (Manifests from before 1.4.11 have the use tree's
counts at the top level, and no `tree`.) At `--scale 0.01`:

```json
"work": { "dir": "fw/work", "headers": 5, "defines": 1223104, "exactUsed": 376, "openByPattern": 935352,
          "openByBody": 140030, "nearMissDropped": 73380, "patterns": 23, "patternsTwoSided": 17,
          "patternsPrefixOnly": 3, "patternsSuffixOnly": 3, "namedOnly": 134530, "pasteMacros": 26000,
          "pasteMacrosUsed": 2600, "forwarders": 29000, "forwardersUsed": 2900, "uses": 18000, "pieces": 5000,
          "piecesStart": 2500, "piecesEnd": 2500, "edges": 12, "uncoveredFiles": 43, "asmFiles": 2,
          "tree": { "defines": 24680, "exactKept": 214, "crossDecoys": 11349, "halfDecoys": 12230, "filler": 887,
                    "keptUnderGlobalPools": 11563, "pasters": 47, "forwarders": 54, "appliers": 3,
                    "uses": { "direct": 126, "forwarded": 64, "higherOrder": 24 }, "levels": 6, "expectedPasses": 7,
                    "byLevel": [{ "level": 0, "uses": 13, "startPieces": 13, "endPieces": 13, "piecesSoFar": 13,
                                  "crossDecoysLiveSoFar": 40 }, …] } }
```

- **`defines`**: every define the bulk carve writes (`fw/work/` plus the pattern roots and pastes in the units).
- **`exactUsed`**: the must-keep bulk names: the tree's `exactKept`, `pasters`, `forwarders` and `appliers`, plus
  `2 × patterns` (`KWP`, `KWT`) and the 12 edge defines. The tree's `exactKept` alone is the kept register defines.
- **`openByPattern`**: the matched names. **`openByBody`** = `namedOnly + pasteMacrosUsed + forwardersUsed`: the open
  names only a matched body reaches. **`nearMissDropped`**: the near misses.
- **`pasteMacros`** / **`forwarders`**: the body macros, `…Used` of them named in a body; the rest are must-drop.
- **`tree.exactKept`** is the must-keep tree defines (one per use). `pasters`, `forwarders` and `appliers` are the
  must-keep macros. `filler` counts what was written, padding included.
- **`tree.keptUnderGlobalPools`** = `exactKept + crossDecoys`: what a carve that pools pieces tree-wide keeps.
- **`tree.byLevel`**: per level, the uses and the pieces they seed. `piecesSoFar` is the pieces seeded once that level is
  reached. `crossDecoysLiveSoFar` is the cross decoys whose two pieces are both seeded by then (what global pools
  keep after that pass).
- The correctness part also seeds pieces, so at small scales pass 1→2 growth is a little under ×9.

Measured on 1.5.3 (`--preset firmware-bulk`):

| Scale | Size | Bulk headers | Bulk defines | mustKeep / mustDrop / open | Gen | Verify | Peak memory |
|---|---|---|---|---|---|---|---|
| 0.05 | 1.0 GB | 27 | 6,115,549 | 3,629 / 1,162,804 / 5,376,995 | 126 s | 125 s | ~1.4 GB |
| 0.1 | 2.0 GB | 55 | 12,231,103 | 5,546 / 1,899,500 / 10,753,964 | 296 s | 249 s | ~2.4 GB |

## Verify

`verify` recomputes the truth from the tree with the same engine gen used, and FAILs on any difference. That makes
it a check that the corpus is the one gen wrote and is undamaged, not an independent oracle: a rule the engine gets
wrong, gen and verify both get wrong. It checks:

- the spelling rule (`Z…Z`, `Y…Y`, the rest) against every name's class, as gen does;
- each count; `units`, `traps` and `cases` are recounted from the tree, and `families` must be a count;
- each list's sha and count, and the lists on disk;
- `treeSha256`;
- the big, dense (each `edge` a JSON bool) and whole-from-outside lists, element by element;
- every `cases.tsv` row: three fields, and a site that is a file of the corpus;
- the `hostile` entries: each a file of the corpus, with a kind;
- `work` (the bulk carve), when `_meta.gen.fwWork` is true or `fw/work/` exists: its counts add up, and its
  per-level uses and live cross decoys are recounted from the lists.
- with `roots`: `compileDbUnion`, that `functionCases` is a file, and the main and union function lists.

A compile db it cannot read (malformed, or with absolute paths from where the corpus was before it moved) is a
`FAIL firmware compileDb`, not a crash. Regenerate a moved corpus, or generate it with `--fw-db-paths relative`.

`verify --reference-carve <dir>` also writes the carve the truth describes. It copies `fw/` and `ext/` (hidden
files included, links skipped), and in every carved file removes each define line whose name is not must-keep. A
define line that leaves a block comment open becomes `/*`. It also writes the compile db with the corpus root
replaced by `<dir>`, at path boundaries only. Compiling every unit against it with the db's flags checks that
must-keep is enough to build.

`<dir>` is emptied first, so verify refuses (exit 2, before reading anything) a `<dir>` that is the corpus, inside it
or holds it, a file, a place gen refuses to clear (a drive root, the home folder, …), or a non-empty dir it did not
make. It marks the dirs it makes with `.codespawner-carve`, so a carve dir can be carved again. A carve that fails
halfway is deleted.

## Knobs

All counts scale with `--scale` (an explicit knob is used as given). `_meta.gen` records the effective values.

| Knob | Default (1x) | |
|---|---|---|
| `--firmware` | off | Writes the firmware corpus and its truth. |
| `--fw-big-headers` | 400 | Big headers `big/regs_N.h` (at least 1, at most 100,000). |
| `--fw-big-mb` | 24 | Size of each big header in MB (20–2000). `regs_0.h` is always exactly `maxParseBytes + 1`. |
| `--fw-files` | 38,000 | Files under `fw/src` (at least 4 are written): every fourth is a kept header, the rest units. |
| `--fw-families` | 1,000,000 | Must-keep families, cycling through the 44 kinds. |
| `--fw-pasters` | 6,000,000 | Paste macros in the big headers, most never called. |
| `--fw-dense-headers` | 200 | Dense headers `dense/dx_N.h`, besides the fixtures. |
| `--fw-chain-max` | 40 | The long forwarder chains' length (2–10,000; not scaled). |
| `--fw-db-paths` | `absolute` | `absolute` or `relative`: how the compile db names paths (not scaled). |
| `--fw-bulk` | off | The bulk carve (see "Bulk shape"). Every `--fw-bulk-*` knob is refused without it. |
| `--fw-bulk-defines` | 122,300,000 | Bulk defines: the use tree, the wide population and filler. |
| `--fw-bulk-pasters` | 5,000 | Paste macros, split across the levels. |
| `--fw-bulk-forwarders` | 15,000 | Forwarders, split across the levels. |
| `--fw-bulk-depth` | 6 | Levels of the use tree (1–64; not scaled). |
| `--fw-bulk-growth` | 9 | Pieces grow ×growth from pass 1 to pass 2 (2–1000; not scaled). |
| `--fw-bulk-keep-pct` | 0.018 | Percent of the bulk defines that are kept uses (over 0, at most 100, fractions allowed; not scaled). |
| `--fw-bulk-decoy-pct` | 1 | Percent that are cross-call decoys (not scaled; keep + decoy + half + near + named ≤ 100). |
| `--fw-bulk-half-pct` | 1 | Percent that are half decoys (not scaled). |
| `--fw-bulk-patterns` | 2,300 | Wide patterns: a kept `KWP` paste macro and `KWT` root each. |
| `--fw-bulk-near-pct` | 6 | Percent that are near misses of a pattern, must drop (not scaled). |
| `--fw-bulk-named-pct` | 11 | Percent named only by a matched body, open (not scaled). |
| `--fw-bulk-body-pasters` | 2,600,000 | Paste macros for matched bodies; a tenth are used (open), the rest must drop. |
| `--fw-bulk-body-forwarders` | 2,900,000 | Forwarders for matched bodies; a tenth are used. |
| `--fw-bulk-body-uses` | 1,800,000 | Paste uses in matched bodies. |
| `--fw-bulk-pieces` | 500,000 | Distinct pieces those uses paste. |
| `--fw-bulk-uncovered` | 4,340 | `.c` files in the tree but in no compile command. |
| `--fw-bulk-asm` | 21 | `.s` and `.asm` files (at least 2). |

## Sizes

Measured on 1.5.3 (`--preset firmware`). Verify recomputes the truth on a single thread, and gen computes it once
too. The counts are the define truth's.

| Scale | Size | Units | mustKeep / mustDrop / open | Gen | Verify | Verify peak memory |
|---|---|---|---|---|---|---|
| 0.01 | 0.20 GB | 285 | 22,943 / 1,198,161 / 1,598 | 12 s | 11 s | ~425 MB |
| 0.1 | 1.2 GB | 2,850 | 217,803 / 10,401,543 / 15,915 | 92 s | 88 s | ~1.4 GB |
| 0.3 | 3.15 GB | 8,550 | 650,842 / 30,759,073 / 47,735 | 284 s | 268 s | ~4.0 GB |
| 1 | 10.31 GB | 28,500 | 2,166,437 / 101,517,938 / 159,098 | 1,943 s | 1,082 s | ~11.7 GB |

At 1x the corpus is 38,683 files, and gen peaks at about 12.1 GB. Memory grows with the number of distinct names
(mostly must-drop paste macros and filler), so 1x needs a machine with that much memory free.
