# Firmware corpus (`gen --firmware`)

Phase 1 of the firmware plan agreed with CodeCarver (its `docs/TESTING.md`, "Coverage plan"). It is a synthetic
firmware tree, with paste-macro register headers past `maxParseBytes`, dense headers, units that reach those
headers' defines by every paste shape, and trap files. Beside it sits the truth a **sound** header carve must meet:
what it must keep, what it must drop, and exact counts. The truth follows the rules below, never any tool's current
code. A case a carver gets wrong is that carver's bug, not a corpus case to remove.

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
    misc/                    #   hostile files (see below)
  ext/include/, ext/app/     # outside the root: an OS header every unit includes (it includes a Hidden one),
                             #   one nothing includes, host.c
  fw-build/compile_commands.json   # one entry per unit, plus ext/app/host.c
  fw-truth/                  # main.<list>.txt, union.<list>.txt, cases.tsv
```

Every compile db entry is `cc -std=c11 -I fw/include -I fw/big -I fw/dense -I ext/include -DKD_7 -DKDV_9=KDV_VAL -c
<file>`. With `--fw-db-paths absolute` (the default) `directory` and every path are absolute. With `relative`,
`directory` is `"."` and the paths are relative to the corpus root, so the db resolves against the directory it
names, resolved from the corpus root. Every unit compiles cleanly with `-std=c11 -Wall -Werror`, with or without
`-DFW_ALT`, and with `-DFW_SEL=7` or `-DFW_SEL=19`. 1.4.0 checked this with clang; gcc was not run.

## Names carry their class

Every name a case is about is spelled by its class:

- `Z…Z` (starts and ends with `Z`): **must drop**;
- `Y…Y`: **open** (the truth does not decide it);
- anything else: **must keep**.

Gen computes the truth from the tree it wrote, using the rules below, and fails if any name's class disagrees with
its spelling. A slip in the generator can never ship as truth.

`fw-truth/cases.tsv` (`name\tcase\tsite`, LF) names each case's target and where it is exercised. The site is
relative to `fw/`, or `../ext/…` for a site outside the root. It lists:

- every trap;
- the hostile and reserved cases;
- three families of each of the 18 kinds: direct, cat-prefix, cat-suffix, multi-line-call, forwarder-chain, alias,
  higher-order, computed-call, wanted-body, beside-paste, object-expansion, if0-call, comment-seed, cat3-split,
  redefinition, kept-header-body, comment-call-open and string-call-open.

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

These are CodeCarver's header-carve rules (TESTING.md "Header-carve rules the truth is built from"), applied to
`fw/` and to whatever files the compile db's builds include from outside it.

The truth is about the header carve alone. **Every `fw/` file that is not a carved file is kept text, kept whole**,
whether or not anything includes it: the traps, `dense_60.h`/`dense_70.h` and the other fixtures, and every unit and
kept header. A carve that also drops unreached files does not match it. Run the carver with every file forced kept
(CodeCarver: `forceKeepFiles = ["*"]`), so only the carved files' define lines change.

1. **K files.** A file under `fw/` is big when it is longer than `maxParseBytes` (20,000,000 bytes; exactly
   20,000,000 is parsed). It is dense when it is 1,000,000 bytes up to `maxParseBytes` and, over its first 262,144
   chars (a truncated last line dropped), define lines are at least 0.60 of the substantive lines and there are at
   least 50 of them. Blank lines and lines starting with `//` or `/*` are not substantive. Big and dense files are
   the K files. A K file included whole from outside the root (here `dense/shared_0.h`, via `ext/app/host.c`) is
   kept whole. The rest are the **carved** files.
2. **Paste macros.** A name is a paste macro if any of its definitions pastes a parameter (`a##b`, not
   `, ##__VA_ARGS__`), or forwards to one. A function-like body forwards when it calls one; an object-like define
   forwards when its body's last token is one. This is computed to a fixpoint, so order does not matter.
3. **Pieces** come from kept text: every identifier beside a `##`, and every identifier among a paste-macro call's
   arguments. An argument list runs to its matching `)` across lines and directives. At `#elif`/`#else` the call's
   state is restored to what it was at the `#if`, so each branch closes the call on its own. Pieces also come from
   higher-order calls, from a computed call's trailing arguments, and from the boundary tokens of an argument's
   object-like bodies, expanded transitively.
4. **Wanted** starts as every identifier of kept text outside the carved files and every non-define line of the
   carved files. It also includes:
   - the builds' `-D` names;
   - every header outside the root that a build includes;
   - the K files kept whole;
   - the reserved names.

   It grows to a fixpoint through the bodies of wanted defines. A carved-file define is also wanted when its name
   starts or ends with a piece.
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
- **Builds.** Phase 1 has one build, `main`, and `union` is the same lists. Carver keeps the union of its builds,
  so it checks `union`.
- **`mustKeepFunctions`.** It is empty (count 0) until the call graph lands in phase 2.
- **`mustKeepFiles`.** It lists the `fw/` files that define a must-keep name, plus every `fw/` file in a unit's
  include closure. Paths are relative to `fw/`.

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
  the byte length, `\n`, then the bytes. Check it before carving, to catch a stale or damaged copy.

## Verify

`verify` recomputes the truth from the tree and FAILs on any difference: each count, each list's sha and count, the
lists on disk, `treeSha256`, the big, dense and whole-from-outside lists, and that the cases file and the hostile
paths are files of the corpus.

`verify --reference-carve <dir>` also writes the carve the truth describes. It copies `fw/` and `ext/` (hidden
files included), and in every carved file removes each define line whose name is not must-keep. A define line that
leaves a block comment open becomes `/*`. It also writes the compile db with the corpus root replaced by `<dir>`.
Compiling every unit against it with the db's flags checks that must-keep is enough to build.

## Knobs

All counts scale with `--scale` (an explicit knob is used as given). `_meta.gen` records the effective values.

| Knob | Default (1x) | |
|---|---|---|
| `--firmware` | off | Writes the firmware corpus and its truth. |
| `--fw-big-headers` | 400 | Big headers `big/regs_N.h` (at least 1, at most 100,000). |
| `--fw-big-mb` | 24 | Size of each big header in MB (20–2000). `regs_0.h` is always exactly `maxParseBytes + 1`. |
| `--fw-files` | 38,000 | Files under `fw/src` (at least 4 are written): every fourth is a kept header, the rest units. |
| `--fw-families` | 1,000,000 | Must-keep families, cycling through the 18 kinds. |
| `--fw-pasters` | 6,000,000 | Paste macros in the big headers, most never called. |
| `--fw-dense-headers` | 200 | Dense headers `dense/dx_N.h`, besides the fixtures. |
| `--fw-chain-max` | 40 | The long forwarder chains' length (2–10,000; not scaled). |
| `--fw-db-paths` | `absolute` | `absolute` or `relative`: how the compile db names paths (not scaled). |

## Sizes

Measured on the 1.4.0 release exe (`--preset firmware`), on an otherwise idle machine. Verify recomputes the truth
on a single thread, and gen computes it once too.

| Scale | Size | Units | mustKeep / mustDrop / open | Gen | Verify | Verify peak memory |
|---|---|---|---|---|---|---|
| 0.01 | 0.16 GB | 285 | 17,019 / 1,197,207 / 1,118 | 7.9 s | 7.3 s | ~340 MB |
| 0.1 | 1.06 GB | 2,850 | 158,273 / 10,392,962 / 11,118 | 77 s | 70 s | ~2.5 GB |
| 1 | ~10 GB (estimated) | 28,500 | about 10x the 0.1 counts | | | ~25 GB (estimated) |

Memory grows with the number of distinct names (mostly must-drop paste macros and filler). At 1x it needs a
machine with that much memory free, or a leaner name table first. 1x has not been generated.
