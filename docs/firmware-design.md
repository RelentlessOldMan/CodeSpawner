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
    misc/                    #   hostile files: empty, BOMs, latin-1, CRLF, lone CR, NULs, an unterminated comment, a
                             #   comment around a directive's `#`, a backslash-space splice, hidden
    include/fw_q3.h          #   compiled hostile text (unit 0): a `(` in a comment and in a char literal, then 2,000 `#if`s
    work/wreg_N.h            #   the work carve, with --fw-work (see "Work shape")
  ext/include/, ext/app/     # outside the root: an OS header every unit includes (it includes a Hidden one),
                             #   one nothing includes (maybe-kept), host.c
  fw-build/compile_commands.json   # one entry per unit, plus ext/app/host.c
  fw-truth/                  # main.<list>.txt, union.<list>.txt, cases.tsv
```

Every compile db entry is `cc -std=c11 -I fw/include -I fw/big -I fw/dense -I ext/include -DKD_7 -DKDV_9=KDV_VAL
-DKDE_N=4 -c <file>`. With `--fw-work`, `-I fw/work` comes after `-I fw/dense`. With `--fw-db-paths absolute` (the default)
`directory` and every path are absolute. With `relative`, `directory` is `"."` and the paths are relative to the
corpus root, so the db resolves against the directory it names, resolved from the corpus root. Every unit compiles
cleanly with `-std=c11 -Wall -Werror`, with or without `-DFW_ALT`, and with `-DFW_SEL=7` or `-DFW_SEL=19`. 1.4.0
checked this with clang; 1.4.8 and 1.4.9 checked it again, with and without `-DFW_ALT`, and 1.4.9 with gcc too. The new kinds' shapes were checked with
gcc and clang -E.

## Names carry their class

Every name a case is about is spelled by its class:

- `Z…Z` (starts and ends with `Z`): **must drop**;
- `Y…Y`: **open** (the truth does not decide it);
- anything else: **must keep**.

Gen computes the truth from the tree it wrote, using the rules below, and fails if any name's class disagrees with
its spelling. Since 1.4.9 it also fails if a `cases.tsv` row names a define no carved file has (a row that would test
nothing). A slip in the generator can never ship as truth.

`fw-truth/cases.tsv` (`name\tcase\tsite`, LF) names each case's target and where it is exercised. The site is
relative to `fw/`, or `../…` for a site outside the root (`../ext/…`; the `-DKDV_9=KDV_VAL` case is
`../fw-build/compile_commands.json`, since 1.4.7). It lists:

- every trap;
- the hostile and reserved cases, and (since 1.4.9) `self-comma`: `KPORT(KSRC0X)` with `KSRC0X = KSRC0X, 1`, an
  object-like name whose comma body names itself;
- three families of each of the 44 kinds: direct, cat-prefix, cat-suffix, multi-line-call, forwarder-chain, alias,
  higher-order, computed-call, wanted-body, beside-paste, object-expansion, if0-call, comment-seed, cat3-split,
  redefinition, kept-header-body, comment-call-open, string-call-open, numeric-object, call-arg, nested-call-edge,
  number-suffix, empty-edge, call-edge, digraph, higher-order-forwarder,
  use-pairs, uncalled-wildcard, expanded-arg, empty-expansion, empty-arg, higher-order-pairs, and (since 1.4.8)
  comma-expansion, split-arg, body-alias, open-body-call, branch-paren-in-args, branch-paren-top (open: a
  macro's `(` after a directive at top level), if-condition, nested-callee, computed-head and comment-hash, and (since 1.4.9) empty-tail-alias and param-tail-alias;
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
   `, ##__VA_ARGS__`), or forwards to one. A function-like body forwards when it holds a call and names
   one, called or not (`APPLY2(CAT, a)`); any define forwards when its body ends in one (`#define PICK(x) CAT`, since 1.4.8). Since 1.4.9 the walk in
   from the body's end goes past names that may expand to nothing and past parameters (an argument may be empty):
   `#define F CAT EMPTYM` and `#define PICK2(x) CAT x` forward to `CAT`, and a name that need not be empty stops
   it. This is computed to a fixpoint, so order does not matter.
3. **Uses** (rule 3', since 1.4.5). Each paste-macro call in kept text is a **use**, and each use builds its own
   names; pieces of different uses never pair. A use is followed through forwarders and higher-order calls into
   every `##` chain it reaches, and each chain builds the cross product of its slots:
   - a literal token gives itself; an operand that is not an identifier or identifier-like pp-number (`2`, `2u`,
     `0x1F`) means the chain builds nothing;
   - an argument gives its spelling, plus its expansion where the macro expands it (nested paste results
     included). A left slot takes the argument's last token, a right slot its first, an interior slot both. An interior argument of
     several tokens splits the chain (since 1.4.8): `HX(b) = X_##b##_Y` with `HX(KA KB)` builds `X_KA` and `KB_Y`,
     never `X_KA_Y`;
   - an argument whose top-level commas come from an object-like name (or an alias chain to one), as in
     `PORT(PIN_LED)` with `PIN_LED = KGPIOA, 5`, is re-split at those commas (since 1.4.8);
   - an argument that can come to one token or to several (a name that may be empty in it, through a forwarder)
     is built both ways, over every mix (since 1.4.9). A function-like call in an argument that may give a
     top-level comma is pooled under max only, so what it builds is open (since 1.4.9);
   - an empty argument, or one whose expansion can be empty, gives nothing (the slot is skipped);
   - `__LINE__` and `__COUNTER__` are a run of digits (one or more), and an undefined `__name` is any text (zero
     or more chars), expanded or not. A chain with more than one such wildcard keeps only its outer text and one
     wildcard;
   - a call's missing arguments at end of file (max only) are any text;
   - a body that leaves a call open (`OPENF(x) = CAT(x,`) takes the rest of the call from the text after it (max
     only, so its names are open). Its last argument runs on as any text, and its missing ones are any text.

   A chain with no parameter builds its literal name. A paste macro that is named in code but never called gets
   one use whose arguments are all any text. "Called" is over the whole tree, dropped text included; comments and
   strings (max) never call or name. A name is called when `(` follows it, when it ends a define body, when only
   names that may be empty or parameters follow it there (since 1.4.9: `CAT EMPTYM`, `CAT x`), and when the
   argument for the parameter after it opens with `(` (`PICK2((KPK_, 2))` gives `CAT (KPK_, 2)`).

   A use also gets its own **pool** (any of its start pieces with any of its end pieces, never overlapping) when its
   callee is computed or the call is open across `#if`/`#elif`/`#else`/`#endif`, and falls back to the pool alone
   for a chain whose product passes 4,096 names (a wildcard counts 1; its names are open). Since 1.4.8 those calls are also built
   exactly: a computed callee (`CAT(GET, CAT)(0)(x, y)`, `APPLY(ID(CAT), x, y)`) through the names it can expand to,
   and a call across a conditional through each branch's tokens.
   A computed call keeps its pool (CodeCarver's ruling for 1.4.9): `PICK2(NOPE)(YX_, 3Y)` builds `YX_3Y` from its
   own pool even though `NOPE` takes the arguments; a name none of its start pieces begins stays dropped. A pool is
   max only (CodeCarver's ruling for 1.4.10): a name only a pool builds, and no exact build, is **open**, since the
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
   open (since 1.4.9).

   An `#if`/`#elif` condition is kept text too (since 1.4.8). Its paste calls are uses (`#if XCAT(CFG_, 1)` keeps
   `CFG_1`), and the names after `defined` are not. A closed `/* */` comment before or after the `#` is a space, also one that runs over several lines (since
   1.4.9). A define whose body has a comment running on to the next line is wanted (seeded), like one ending in an
   open comment.
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
  the byte length, `\n`, then the bytes. Check it before carving, to catch a stale or damaged copy. It does not
  cover `ext/` or the compile db; verify checks those by recomputing the truth from them.

## Work shape (`--fw-work`, `--preset firmware-work`)

`--fw-work` adds a carve shaped like a work tree, for measuring how a carver's passes grow. It sits beside the
correctness part, in `fw/work/wreg_N.h` (each one over `maxParseBytes`; on `-I fw/work`). `--preset firmware-work` is
the work carve at `--scale` plus a small, fixed correctness part (1 big header, 400 files, 192 families, 20,000
pasters, no dense headers), so nearly all of its size and time is the work carve.

- **A tree of paste uses.** Each must-keep define is a `##` use: `WRP<n>(a, b)` pastes a start piece and an end
  piece. Units call level 0. Each level-k define's body holds 8 level-k+1 uses at the first step (`--fw-work-growth`
  9: the pieces seeded grow ×9 from pass 1 to pass 2), then the levels halve, down to `--fw-work-depth` levels. A
  carve that seeds level by level needs `levels + 1` passes (the last one finds nothing new).
- **Three use modes**, cycling 10:7:3 over each level: direct (`WRP<n>`), forwarded (`WFW<n>`, a forwarder to a
  paster) and higher-order (`WAP<k>(WRP<n>, a, b)`). A macro can't expand inside its own expansion, so each level has
  its own pasters, forwarders and applier `WAP<k>`. `--fw-work-pasters` and `--fw-work-forwarders` set the pools,
  split across the levels.
- **Cross-call decoys** (`--fw-work-decoy-pct`, default 16% of the work defines) are the start piece of one use
  joined to the end piece of another. Global start and end pools keep them; rule 3' (per-use pairing) drops them.
  Spelled `Z…Z`.
- **Half decoys** (`--fw-work-half-pct`, 20%) have one real piece and a dead end: must-drop under either rule.
- **Filler** (`ZWF…Z`) makes up the rest and pads each header past `maxParseBytes`.

With the defaults, 4% of the work defines are kept, and 20% would be kept under global pools.

At the edges the counts give way to the shape, and the manifest records what was written:

- Level 0 has at least one use a header, so at tiny sizes more than `--fw-work-keep-pct` is kept.
- The tree ends at the first level smaller than the header count, so `levels` can be fewer than `--fw-work-depth`.
- Cross decoys are capped at the A pieces × B pieces there are. Half decoys need both sides; without them there are
  none. Filler takes up the rest, so `--fw-work-defines 0` still writes one padded header.
- A level with no forwarders folds its forwarded uses into direct ones. A level with no higher-order use (a level of
  under 18 uses) gets no applier, so `appliers` can be fewer than `levels`.
- A forwarder chain goes back to a paster every `--fw-chain-max` links.
- `byLevel[k].piecesSoFar` counts the uses (one start and one end piece each) seeded by the end of level `k`.

`firmware.work` in the manifest gives the construction counts, which verify recounts from the lists:

```json
"work": { "dir": "fw/work", "headers": 3, "defines": 945000, "exactKept": 37785, "crossDecoys": 151200,
          "halfDecoys": 189000, "filler": 567015, "keptUnderGlobalPools": 188985, "pasters": 250, "forwarders": 750,
          "appliers": 6, "uses": { "direct": 18905, "forwarded": 13216, "higherOrder": 5664 }, "levels": 6, "expectedPasses": 7,
          "byLevel": [{ "level": 0, "uses": 2290, "startPieces": 2290, "endPieces": 2290, "piecesSoFar": 2290,
                        "crossDecoysLiveSoFar": 8980 }, …] }
```

- **`exactKept`** is the must-keep work defines (one per use). `pasters`, `forwarders` and `appliers` are the must-keep
  macros. `filler` counts what was written, padding included.
- **`keptUnderGlobalPools`** = `exactKept + crossDecoys`: what a carve that pools pieces tree-wide keeps.
- **`byLevel`**: per level, the uses and the pieces they seed. `piecesSoFar` is the pieces seeded once that level is
  reached. `crossDecoysLiveSoFar` is the cross decoys whose two pieces are both seeded by then (what global pools
  keep after that pass).
- The correctness part also seeds pieces, so at small scales pass 1→2 growth is a little under ×9.

Measured on the 1.4.6 release exe (`--preset firmware-work`), on an otherwise idle machine:

| Scale | Size | Work headers | Work defines | Exact kept / under global pools | Gen | Verify | Peak memory |
|---|---|---|---|---|---|---|---|
| 0.05 | 0.17 GB | 3 | 945,000 | 37,785 / 188,985 | 12 s | 11 s | ~300 MB |
| 0.075 | 0.22 GB | 4 | 1,417,500 | 56,694 / 283,494 | 16 s | 14 s | ~420 MB |
| 0.1 | 0.27 GB | 6 | 1,890,000 | 75,586 / 377,986 | 20 s | 18 s | ~460 MB |

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
- `work`, when `_meta.gen.fwWork` is true or `fw/work/` exists: its counts add up, and its per-level uses and live
  cross decoys are recounted from the lists.

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
| `--fw-families` | 1,000,000 | Must-keep families, cycling through the 42 kinds. |
| `--fw-pasters` | 6,000,000 | Paste macros in the big headers, most never called. |
| `--fw-dense-headers` | 200 | Dense headers `dense/dx_N.h`, besides the fixtures. |
| `--fw-chain-max` | 40 | The long forwarder chains' length (2–10,000; not scaled). |
| `--fw-db-paths` | `absolute` | `absolute` or `relative`: how the compile db names paths (not scaled). |
| `--fw-work` | off | The work carve (see "Work shape"). Every `--fw-work-*` knob is refused without it. |
| `--fw-work-defines` | 18,900,000 | Work defines: kept uses, decoys and filler. |
| `--fw-work-pasters` | 5,000 | Paste macros, split across the levels. |
| `--fw-work-forwarders` | 15,000 | Forwarders, split across the levels. |
| `--fw-work-depth` | 6 | Levels of the use tree (1–64; not scaled). |
| `--fw-work-growth` | 9 | Pieces grow ×growth from pass 1 to pass 2 (2–1000; not scaled). |
| `--fw-work-keep-pct` | 4 | Percent of the work defines that are kept uses (1–100; not scaled). |
| `--fw-work-decoy-pct` | 16 | Percent that are cross-call decoys (not scaled; keep + decoy + half ≤ 100). |
| `--fw-work-half-pct` | 20 | Percent that are half decoys (not scaled). |

## Sizes

Measured on the 1.4.5 release exe (`--preset firmware`), on an otherwise idle machine. Verify recomputes the truth
on a single thread, and gen computes it once too.

| Scale | Size | Units | mustKeep / mustDrop / open | Gen | Verify | Verify peak memory |
|---|---|---|---|---|---|---|
| 0.01 | 0.16 GB | 285 | 20,131 / 1,198,300 / 633 | 11 s | 11 s | ~260 MB |
| 0.1 | 1.06 GB | 2,850 | 189,604 / 10,403,278 / 6,259 | 111 s | 103 s | ~1.1 GB |
| 1 | ~10 GB (estimated) | 28,500 | about 10x the 0.1 counts | | ~17 min (estimated) | ~11 GB (estimated) |

Memory grows with the number of distinct names (mostly must-drop paste macros and filler). At 1x it needs a
machine with that much memory free. 1x has not been generated.
