# Changelog

All notable changes to CodeSpawner are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each release is cut by `scripts\release.ps1`, which injects the matching section
below into the GitHub release notes — so a version with no section here will fail
to release. Keep the top section up to date as you work; the release script only
reads it.

## [Unreleased]

## [1.4.0] - 2026-10-22

Generated trees and manifests are byte-identical to 1.3.2 without `--firmware`.

### Added
- **Firmware corpus** (`gen --firmware`, `--preset firmware`; [docs/firmware-design.md](docs/firmware-design.md)).
  This is phase 1 of the plan agreed with CodeCarver: a header-carve test tree with its sound-carve truth.
  - **The tree:** under `fw/`, paste-macro register headers past `maxParseBytes`, dense headers, units that reach
    their defines by 18 paste shapes, and traps and hostile files. Beside it: a compile db in `fw-build/` and
    headers outside the root in `ext/`.
  - **Name spelling:** each name carries its class in its spelling: `Z…Z` must drop, `Y…Y` is open, anything else
    must keep. Gen computes the truth from the written tree and fails on any disagreement with a name's spelling.
  - **The truth:** the manifest's top-level `firmware` object holds the counts and bounds, the big, dense and
    whole-from-outside lists, the hostile files, `treeSha256`, and the per-build truth lists (`fw-truth/`, each with
    sha256 and count). It is additive; `manifestVersion` stays 1.
  - **Knobs:** `--fw-big-headers`, `--fw-big-mb`, `--fw-files`, `--fw-families`, `--fw-pasters`,
    `--fw-dense-headers`, `--fw-chain-max` and `--fw-db-paths`. The counts scale with `--scale`, and `_meta.gen`
    records the effective values.
- `verify` recomputes the firmware truth from the tree and FAILs on any difference. `verify --reference-carve <dir>`
  writes the carve the truth describes (every carved define that is not must-keep removed), for compiling against.

### Changed
- `mutate` leaves the firmware dirs (`fw/`, `ext/`, `fw-build/`, `fw-truth/`) alone.

### Docs
- `mutate --three-way --files-changed N`: each variant's delta lists up to N files, not exactly N. A picked file
  can draw no edits on one side, and is then unchanged in that variant. Output is unchanged.

## [1.3.2] - 2026-10-19

Generated trees are byte-identical to 1.3.1. A web corpus's manifest gains `defNotLiteral` on its template instances
(no digest changes). `mutate --three-way` on a corpus with a compile db now rewrites the variants' db, and its delta
and conflict manifests become `manifestVersion` 2.

### Added
- `defNotLiteral: true` on every template instance: its def is an `#include` line, not the name, but its refs are
  real call sites an indexer should resolve. It is not part of `truthSha`; `expectedMiss` is unchanged. Verify
  refuses it (exit 2) when it is not `true` or sits on a symbol without `templateHead`.
- `verify` checks `compile_commands.json` on a web corpus: every unit listed, with the web's `-I` order. A 3-way
  variant whose db names its base is a FAIL; a db naming any other tree (a moved corpus) prints a NOTE.

### Changed
- `mutate --three-way` rewrites each variant's `compile_commands.json` to the variant's own paths. 1.3.1 left it
  naming the base, so a variant compiled the base's files. The db is listed as `_meta.bookkeeping:
  ["compile_commands.json"]` in the v1, v2 and conflict manifests, which are then `manifestVersion` 2. They stay
  version 1 and byte-identical when the corpus has no compile db. No digest changes.

### Fixed
- Verify's preprocessor:
  - honours `#pragma once`, fails on an active `#error`, and stops at 200 nested includes with an error instead
    of overflowing the stack on an unguarded self-include;
  - reads `#if` numbers as C does: octal `010` is 8, `0x`/`0b` and `u`/`l` suffixes work, and `09` or an out-of-range number
    is an error;
  - follows a macro whose value is another macro defined in a header (`#define A B`), and a computed include whose
    macro is defined in a file found later;
  - continues a `//` comment onto the next line when its line ends in `\`, as compilers do;
  - no longer counts identifiers inside string and character literals or pp-numbers as macro uses.
- `gen` refuses a web whose closures would go deeper than 190 (compilers stop at 200), and a template site skips
  chains that would; `--web-depth` is capped at 150. A death-shaped web is
  byte-identical to 1.3.1's (its deepest closure is 49).
- `gen` refuses web knobs that would be ignored or unverifiable: `--manifest false` with `--web-units`, a template
  knob with `--web-templates 0`, and `--web-tail-mb` without `--web-tail`.

### Docs
- A tail header comes out about 1.25× `--web-tail-mb` (death's 48 MB tails are about 60 MB).
- Corrected: which web files get CRLF and mixed line ends, what `--web-ifdef-frac` counts, the gated include's
  "two segments away" (it wraps), the clang `-H` check (a one-off at 1.3.0), death-lite's counts, and
  `_meta.gen` under `--from-profile`.

## [1.3.1] - 2026-10-16

Every corpus is byte-identical to 1.3.0.

### Added
- `gen --from-profile ... --with-oracle` takes the `--web-*` knobs: the include web is written at the root beside the
  profile's tree (which stays byte-identical, as do its oracle symbols and `indirectTruthSha`), and its closures,
  instances, `_meta.includeDirs` and web knobs are added to the oracle manifest. The web without `--with-oracle` is
  refused. This lets the real-shape death keeper carry CodeCarver's web.

## [1.3.0] - 2026-10-16

`--preset death` corpora change (the web is added); every other corpus is byte-identical to 1.2.3.

### Added
- **Include web** (`gen --web-units N`, docs/include-web-design.md), for CodeCarver's include-closure slicing:
  - a deep, overlapping graph of guarded register-map headers, aggregators, unguarded template headers expanded
    through `TNAME`, an `#include_next` vendor dir, shadow decoys, and `.c` units;
  - include traps: cycles, `#if`/`#ifdef` gates, computed and relative includes, long continued macros, comments
    that touch macro names or hide fake includes, and CRLF and mixed line ends;
  - a dial (`--web-*`) for each shape.
- Manifest, all additive:
  - top-level `closures`, with each unit's files, bytes, reads, readBytes, depth, defines and templateInstances;
  - `_meta.includeDirs`;
  - template-instance symbols with `templateHead` and `includedVia`.
  - The web's units are listed in `compile_commands.json` whatever `--compile-db` says.
- `verify` recomputes every closure with its own small preprocessor and checks each template instance both ways.
  clang `-H` agrees with the closures.
- `--preset death` now includes the web. New `--preset death-lite` has all of death's shapes in about 0.5 GB
  (32 MB giants, and the web at death's counts with small headers), with no compute gate needed.
- `mutate` never edits the web's directories.

## [1.2.3] - 2026-10-15

Generated corpora are unchanged (C and C++). `verify` is stricter on hand-edited manifests.

### Fixed
- `verify` no longer hangs on a v2 manifest whose `_meta.classes` is a deep lattice of diamonds: resolving a
  method visits each class once instead of walking every path (40 levels used to mean 2^40 paths).
- `verify` exits 2 (bad input), not 1, for:
  - a `_meta` that is not an object, a `seed` that is not a 32-bit integer, or a `generatorVersion` /
    `corpusRoot` that is not a string;
  - a duplicate key anywhere in the manifest (before 1.2.3, only symbol and class keys were checked);
  - a v2 symbol with no `def`, and the v1-only `dupGroups` / `_meta.indirectTruthSha` in a v2 manifest;
  - a value of the wrong JSON type anywhere the checks read.
- `mutate` exits 2, not 1, for a non-integer `manifestVersion` or `seed`.
- An inheritance-cycle FAIL names the classes on the cycle; it could name a class below the cycle.
- The `gen --lang cpp` free-function cap names the `--oracle-reachable-frac` that would fit; "raise it" was
  wrong at the default 0.
- The C gen error for a directory where the manifest goes also suggests `--manifest=false`.

### Changed
- `verify` checks where a v2 manifest's sites sit, not just that their lines contain the name:
  - a def is a definition line, in its class's files for a member and in no class or template file for a
    function;
  - no def or ref is a declaration (the `cpp/rt/*.hpp` prototypes, an in-class declaration);
  - no ref is a definition line (an override's def, or `Cls::~Cls() {` as a ctor ref);
  - every definition line in the program is some symbol's def, so a symbol deleted from the manifest fails;
  - no `edges` entry targets a class or a template;
  - no `edges`, `implicitEdges` or `memberPtrEdges` list, and neither `_meta.roots` nor
    `_meta.staticInitRoots`, names an entry twice (the generator never writes one).
- `docs/cpp-profile-design.md` words the dispatch-only construction and the base-only member more exactly:
  they add a pass to the RTA solve (at least three passes in all), they are skipped in more cases than it
  listed, and "never constructed" means in reachable code.
- Tests: every `verify` tamper case asserts the FAIL message of the check it targets, not just a failing exit.
  New cases cover reordered bases, a ctor relabelled as a dtor, a moved instance def, a member pointer to a
  function, duplicate list entries, and more exit-2 shapes. New model tests cover transitive member liveness,
  virtual records that differ only in `rta`, the dispatch-only construction over 30 seeds, acyclic header
  dependencies, and the oracle's duplicate-free lists on a dense shape.
- The docs say what `verify` does not prove: `edges`, `rta`, `implicitEdges`, `staticInitRoots` and
  member-pointer `dispatched` are checked only for target kind, and `cppTruthSha` catches an unsigned edit,
  not a re-signed one (`docs/cpp-profile-design.md` "As built", `docs/USAGE.md`).

## [1.2.2] - 2026-10-15

**`gen --lang cpp` output changes for most seeds** (program bytes and manifest truth; the new default-seed
`cppTruthSha` is pinned as `2cf4070d…`). C corpora are byte-identical.

### Changed
- One class is now live only as a member of a base subobject: an abstract class with a constructed subclass
  holds a member of a never-constructed class, so a carver whose RTA counts only a class's own members drops
  it. 1.2.1 fixed that rule in the truth, but no generated program exercised it
  (`docs/cpp-profile-design.md`, "Members").

### Tests
- A test that the shape exists (seeds 2, 3, 1337); dropping the base-member rule from the solve now fails the
  independent recompute on 5 of its 9 shapes.

## [1.2.1] - 2026-10-15

Review round 6 of the C++ profile. **`gen --lang cpp` output changes for every seed** (program bytes and
manifest truth; the new default-seed `cppTruthSha` is pinned as `d3a38e31…`). C corpora are byte-identical.

### Fixed
- An unused template's body now records refs: a free-function call there is a ref of the function, and
  `tf_k<T>()` / `tv_n.m_0()` are refs of the template symbol (`ns::tf_k` / `ns::Tmpl_k::m_0`). They were
  missing, so a whole-word search found lines no symbol owned. Still no edges (the body is never instantiated).
- RTA liveness: the member objects declared by a live class's base classes are live too (only the class's
  own members counted).
- `--cpp-classes 1`: the lone class is concrete, so it is constructed and `--cpp-static-init` objects exist
  (it was abstract 30% of the time, which dropped both), and no mixin is made that nothing inherits.
- `gen` refuses a directory at `<out>-manifest.json` before it touches `--out` (C and C++). It used to wipe
  and regenerate `--out`, then fail on the final rename.
- `mutate` on a C++ corpus exits 2 (bad input) and says it edits C corpora only; it exited 1 with
  "manifest version 2 != 1". An unknown `manifestVersion` is exit 2 in `verify` too (was a FAIL, exit 1).

### Changed
- One construction is now reachable only through virtual dispatch, so the RTA solve really iterates
  (`docs/cpp-profile-design.md`, "Construction").
- `verify` on a v2 manifest is much stricter. A wrong JSON shape (wrong field type, duplicate key, a v1-only
  field) is exit 2. New checks: `cha` is EXACTLY the override set below a receiver (it accepted a dropped
  override), no pure method in `cha`, `_meta.classes` acyclic and matching the `class` lines and the class
  symbols (a cycle used to hang it), `simpleName` is the key's last name, `kind` fits the key, an instance's
  def is its template's, `pure` exactly on `= 0;` lines, defs unique, canonical sites, no def or duplicate in
  `refs`, ctor sites not after a `~`, implicit and `staticInitRoots` targets are ctors/dtors and member-pointer
  targets are methods.
- Knob caps: `--oracle-fanout`, `--oracle-depth` and `--oracle-shared-leaves` at most 1,000,000 with
  `--lang cpp`, and at most 2,000,000 free functions in all (chain + shared leaves + dead block).
- Generation no longer goes quadratic in `--cpp-classes` (the same bytes, about a third faster at 3000).

### Docs
- The receiver semantics of a virtual record (`static` is the resolved method; `cha`/`rta` come from the
  receiver's subtree; the receiver is not recorded; `cha` can be empty), the `cppTruthSha` golden fixture in
  `manifest-schema.md`, and the knob-extreme behavior the "always" claims missed. Stale v1-only wording fixed;
  `vendor-codespawner.ps1` now copies `cpp-profile-design.md`.

### Tests
- An independent recompute of the C++ truth from the emitted source alone (cha, rta, live classes, implicit
  edges, refs, both closures) across 9 shapes, a ref-completeness check by name, the pinned digest, and 25 more
  tampering / malformed-manifest cases. `bench.ps1` 4h also pins the default-seed `cppTruthSha`.

## [1.2.0] - 2026-10-14

### Added
- **C++ language profile + manifest v2** (`gen --lang cpp`; design in `docs/cpp-profile-design.md`, agreed with
  CodeCarver and CodeCompass). It writes a self-contained C++ program with namespaces, single and multiple
  inheritance, virtual / pure / final methods, ctors and dtors, static-storage objects, function and class
  templates, and member-function pointers. The program compiles and links with
  `clang++ -std=c++17 -Wall -Wextra -Werror`. Its manifest is `manifestVersion: 2` and adds:
  - `kind: class` symbols for every class and mixin: def = the class line, refs = every other whole-word use of
    the name; no edges, skipped by reachability;
  - per symbol: `kind`, `simpleName`, `instanceOf`, `pure`, `implicitEdges`, `virtualEdges` (`static` plus the
    sound `cha` and precise `rta` sets), and `memberPtrEdges`;
  - in `_meta`: `lang`, `staticInitRoots`, `classes`, and `cppTruthSha`.
- Knobs `--lang`, `--cpp-classes`, `--cpp-depth`, `--cpp-multi-inherit`, `--cpp-virtual-frac`,
  `--cpp-instantiated-frac`, `--cpp-templates`, `--cpp-instances`, `--cpp-static-init`. The `--oracle-*` graph
  knobs shape its free-function graph.
- `digest-selftest` checks a fifth golden vector, `cppTruthSha` `28b4e1de…`.
- `verify` accepts v2 and checks the C++ facts: whole-word simpleName sites, kinds, instances, `pure`, implicit
  and member-pointer targets, that each `cha` target overrides `static` from at or below its class, that
  `rta ⊆ cha`, and that `cppTruthSha` recomputes.

### Unchanged
- C corpora and their manifests are byte-identical to 1.1.13 (still v1). `mutate` refuses a v2 manifest.

## [1.1.13] - 2026-10-14

### Corpus bytes change (regenerate and re-freeze)
Classic `gen` output differs from 1.1.12 for the same seed and knobs, in these ways:
- **Every register header** (giant, big, med, dense, shrink and restream seeds) now stops one register earlier.
  The last register used to cross the byte cap by up to ~200 B. Now a header never exceeds `--max-header-mb`,
  `--dense-under-mb`, or a band's drawn size. The other files are unchanged.
- **Block family numbers are disjoint** (agreed with CodeCompass). Each band starts past the previous band's end,
  so no two headers share a `HWIO_BLK{n}_*` macro family. This changes corpora with more than 100 giant headers
  or more than 900 big headers; there the big band (and everything after it) moves up. Below those counts the
  historical bases (100 / 1000 / 100000 / 200000 / 300000) stand.
- An explicit `--macro-density N` emits exactly N defines in a giant. It used to round up to a multiple of 3.
- `gen --from-profile` draws sizes in the top doubling of each size band by width. Before, about half of the
  band's files landed just under its upper bound when that doubling was a sliver. Profile regenerations change
  file sizes.

### Changed
- Rename `similarityMilli` follows the metric pinned with CodeDiffer: lines are counted on the decoded text (a
  BOM honoured, so UTF-16 files score by their characters, not their bytes), CR/CRLF count as LF, and rounding is
  half-up in integers, `(1000·common + m/2) / m` (9/16 now records 563, not 562). The value is measured from the
  old and new files rather than from the planned edit count. Rename deltas whose files hit a .5 tie, or which
  are UTF-16, record a different `similarityMilli` (and so a different `diffTruthSha`) than 1.1.12.
- A rename decoy is never a byte-exact copy of its source: a short file the 10% marker draw missed gets one
  marked line, and a non-text or empty file gets no decoy.
- `mutate --files-changed N` edits N files whenever the target has them. A chosen file the edit kind cannot change
  (empty, or binary for a text kind) used to be dropped silently, so `--files-changed 1` could write an empty
  delta and exit 0. The next file in the shuffled pool now takes its place. Runs with no such file are
  unchanged. It warns when the target has fewer than N files, or fewer than N could be edited.
- `mutate` refuses knobs the chosen mode would ignore: `--edits`, `--through` and `--restream` with `--target`;
  `--edit-density`, `--giant-min-mb` and `--overlap-fraction` without it; `--overlap-fraction` without
  `--three-way`; and `--giant-edit` outside 2-way content or mixed edits.
- `scan` refuses the tree given twice (`scan a --tree b` used to scan b), and refuses `--sample` below 0.
- `codespawner` with no arguments exits 2 (usage error), like any other bad command line. It used to exit 1.

### Fixed
- A directory named `<out>_v1`/`_v2` is no longer deleted by a regen of `<out>` or replaced by
  `mutate --three-way` just because it holds a corpus. It must be a 3-way variant (its mutation state says so,
  or a variant copy was interrupted). A variant written by 1.1.8 or earlier has no state file, so it is left in
  place; delete it by hand.
- The rename margin check holds far less memory (one flat sorted index instead of a dictionary per file), and
  has a budget of 32M indexed lines: renames past it are not checked and a stderr warning says how many.
- The rename cross-match warning names the real cause: identical or near-identical files in the target set
  (default `--source-bodies same` sources, or `--target all`'s `.bak/.o/.lst` copies). The best-match guarantee
  is documented for `--source-bodies varied` corpora mutated with `--target source`.
- `scan` refuses an `--out` that reaches the scanned tree through a junction, subst drive or other alias.
- `scan` no longer records name-like suffixes as extensions: a dotfile (`.name`) counts as `(dotfile)`, and an
  extension outside the common C/C++/build set that appears in fewer than 3 directories folds into `(other)`.
- `scripts\release.ps1` unstages the version bump if the release commit itself fails.
- `gen --from-profile --with-oracle` refuses `--oracle-depth`, `--oracle-shared-leaves` and
  `--oracle-reachable-frac` without `--oracle-fanout`. The default linear chain ignored them silently.
- The `--from-profile --with-oracle` manifest records its seed and oracle knobs in `_meta.gen`. The profile path
  is not recorded.
- `verify` checks for `VENDOR_missing_*.h` in the linked roots (`<corpus>_rootN`) too, not only the main tree.
- `_meta.gen.dirs` is at least 1. A tiny `--scale` recorded `dirs: 0`, which a replay refused.
- Help text: the `--decoy-fraction` note no longer splits its sentence, and the smoke example shows `.\_fw`
  (not `.\\_fw`).
- `scripts\bench.ps1`: the profile privacy check could not fail on a leaked path, because JSON doubles the
  backslashes. It now matches the escaped and slash forms. The script no longer calls itself a release gate,
  since `release.ps1` runs `dotnet test`, not the bench.
- `scripts\release.ps1` refuses a version older than the current one.
- `scripts\build.ps1 -Rid linux-x64` (any non-Windows RID) looks for `codespawner`, not `codespawner.exe`.
- Tests: the indirect edges' `dispatched` is checked against the emitted C call sites; a second `prevTruthSha`
  golden pins the `expectedMiss` byte; `mutate` with a newer manifest version is refused before it edits.

## [1.1.12] - 2026-10-14

### Added
- `gen --source-bodies varied` gives every `src_i.c` its own body lines. By default all sources share one
  repeated filler line, so any two are ~98.5% line-similar and a rename can't be told from a near-copy.
  Default output is unchanged (byte-identical); the knob is recorded in `_meta.gen` only when `varied`.

### Fixed
- `mutate --edit-kind rename` no longer lets a decoy out-score a recorded rename: any decoy within 20 milli
  of a rename's `similarityMilli` against its `from` is dropped from disk and from `added`. Renames that
  cross-match each other (sources near-copies of each other) get a stderr warning recommending
  `--source-bodies varied`. Existing rename corpora may now carry fewer decoys; `diffTruthSha` is unchanged.

## [1.1.11] - 2026-10-14

### Fixed
- `mutate --giant-edit single` on a giant whose last line has no newline no
  longer glues the inserted line onto it: the line gains a `\n` and the delta
  records ONE replace hunk (old `mid,1` → new `mid,2`), as a line diff would.
- `--three-way --conflict-edges`: each variant's hunks and each file's
  conflict and clean-merge records are listed in file order; an add/add insert
  could come after a later hunk. `conflictTruthSha` is unchanged.
- Symbol deltas now carry each changed symbol's `indirectEdges`, as the base
  manifest does. They stay out of `prevTruthSha`.
- A Remove records only the files it deleted: the source plus whichever of its
  `.o`/`.lst`/`.bak` siblings existed, not all four every time.
- A Grow that comes round to a header already at the target size changes
  nothing, and is no longer listed in `modified`.
- Line-shift keeps the file's encoding, BOM, each line's terminator and a
  missing final newline (it rewrote the file as LF, without a BOM, with a final
  newline).
- The `--step` chain record includes `--restream`, so continuing a chain with
  a different `--restream` (a different plan) is refused.
- The `_v1`/`_v2` trees of a `--three-way` run are recorded as mutated, so one
  cannot be mutated again against the base manifest; a regen clears that record.
- `scan` measures directory depth from the tree root, so a trailing separator on
  the tree argument no longer shifts the depth histogram.
- `--oracle-scale` pads bodies to the `.c` size band with the most files, not
  whichever `.c` archetype the profile lists first.
- `verify` docs describe what the gating and edge checks actually prove.
- A manifest def with a NUL or other invalid path character is refused (mutate
  exit 2, a verify FAIL) instead of crashing the process.
- mutate refuses a manifest def naming the `.codespawner` marker or the mutate
  journal; a Remove would have deleted the marker.
- `verify` no longer PASSes a manifest that declares nothing to check; that, and
  a manifest with no `_meta` or `symbols` object, is bad input (exit 2).
- Bad input exits 2 everywhere: a missing `--from-profile` file, a missing
  `scan` tree, and an invalid profile (wrong version, unsafe or duplicate
  archetype) used to exit 1.
- `--scale 1e12` no longer overflows every scaled count to a disabled
  population.
- gen checks that the sibling manifest can be written before it wipes the old
  corpus, not after, so a regen that cannot write its manifest leaves the old
  corpus in place.
- After a corpus folder is deleted by hand, the next gen at that path drops the
  manifest, deltas and mutate record it left behind, when they name that path.
  Before, a stale mutate record made mutate refuse the new tree as already
  mutated.
- `scan` no longer classifies a header of prototypes (`int f(int a, int b);`
  lines) as tabular data: commas inside a call or prototype don't count as
  columns, and such a header is generic code.
- Docs: the diff delta's `_meta` fields, which writes `--io-parallelism`
  governs, and exit codes for `mutate` and `scan`.

### Changed
- `gen --from-profile` draws sizes log-uniformly within each percentile band.
  A uniform draw put the top 10% at (p90+max)/2 on average, so one big file
  inflated a cluster's bytes several-fold. Sizes for a given seed differ from
  1.1.10.
- gen refuses out-of-range knobs instead of clamping or ignoring them: `--scale`
  must be in (0, 1000], counts >= 0, `--depth` 1..12, `--dup-copies` >= 2, and
  so on (see `docs/knobs.md`).
- An option given twice is refused on every command, as is a second `--preset`.
  A knob after a `--preset` still overrides the preset's value.
- `gen --from-profile` refuses the knobs it would ignore (the classic count and
  size knobs, `--io-parallelism`, `--manifest`, a preset's tokens). The
  `--oracle-*` knobs need `--with-oracle`, and plain gen refuses a non-default
  oracle knob (it never runs the overlay). Defaults are still accepted, so a
  `_meta.gen` replay works.
- `gen --from-profile` places files per the profile's `filesPerDirHistogram`
  (which it used to ignore) instead of uniformly over the directories, so empty
  and crowded directories come back in the measured proportions. File placement
  for a given seed differs from 1.1.10.

## [1.1.10] - 2026-10-13

### Fixed
- Regenerating a corpus no longer empties what a `<out>_v1`/`_v2` junction or
  symlink points at; a linked variant is left alone.
- Two `mutate` runs on one corpus can no longer interleave edits: mutate holds
  an exclusive `<corpus>-mutating.lock` for its whole run and refuses (exit 1)
  while another run holds it.
- On Windows the never-delete guards (drive root, current directory, user
  profile, executable directory, system folders) also compare directory
  identity, so `\\?\`, `\\localhost\C$`, `subst` and 8.3 aliases are refused.
- Regenerating over a 1.1.5-or-earlier corpus with a larger `--linked-roots`
  no longer clears a non-empty `_rootN` the old corpus never wrote without
  `--force`: the roots it owns are read from its manifest.
- `gen --from-profile` minified files are never cut below the 100,000-byte
  line that makes them minified (a 100,000–100,005 byte draw could land just
  under it and re-classify as text).

### Changed
- `scan` k-anonymizes the extension histogram: extensions fewer than
  `--min-cluster` files share, and suffixes that aren't extension-shaped, are
  counted as `(other)` (generated as `.dat`). Ties sort by extension.
- `scan` rounds archetype p50/p90/max to 3 significant digits.
- `scan` refuses an `--out` inside the scanned tree, and refuses
  `--structure-only` together with `--content-stats`.

## [1.1.9] - 2026-10-13

### Changed
- `mutate` refuses a tree it has already mutated, except for the next `--step`
  of the same chain. What has been applied is recorded in
  `<corpus>-mutated.txt` beside the corpus (not inside it: the tree and its
  `.codespawner` marker are unchanged, so base-vs-variant diffs see only the
  delta's changes). `--step k` needs steps 1..k-1 recorded with the same
  `--seed` and `--edits`; a repeated, skipped or other-chain step, a second
  default/`--through` run, and a second bulk run are refused with exit 1 and a
  message to regenerate. Before, these edited files that no longer matched the
  base and wrote a delta that could not apply to it. `--three-way` leaves the
  tree as the base, records nothing, and can repeat; a 2-way run after it is
  allowed. Regenerating clears the record; a corpus mutated by 1.1.8 or earlier
  has none and is treated as unmutated. Agreed with CodeCompass (strictly
  in-order chains, regenerated each run); CodeDiffer (a fresh copy per
  mutation) asked for the record to stay out of the tree.
- `gen --from-profile` files now end at the line boundary nearest their drawn
  size. They were written in whole units, which overshot by up to one unit:
  ~1.2 KB for generic code, so on a real profile those files came out 1.7%
  too large in total and 2.5% of them landed in the next size bucket. Tiny
  targets are kept at the least that still re-classifies to the class (66
  bytes for generic code, 119 for inline-function-heavy, which used to read
  as generic code below 60 bytes). **This changes from-profile output**: a
  corpus regenerated from the same profile and seed with 1.1.9 differs in its
  synthesized files' bytes and sizes from one made by 1.1.8 or earlier. The
  oracle files, the manifest and truth, and `--preset` corpora are unchanged.

### Added
- `scan` prints how many files it could not read (and directories it could
  not list), which it leaves out of the profile. Nothing is added to the
  profile itself.

## [1.1.8] - 2026-10-12

### Changed (cross-OS determinism)
- Symbol-mode `mutate` (default, `--step`, `--through`) now plans the same
  edits on Windows as on Linux. Its source-file pool was sorted by OS path
  before the seeded shuffle, and `\` sorts after digits where `/` sorts before
  them, so Windows put `block10\` ahead of `block1\`. The pool is now sorted in
  its `/` form on every OS. **Windows symbol-mutate output changes** for a
  corpus with 11 or more numbered sibling directories at some level (any
  death-scale or carve corpus): different files are removed and line-shifted
  for the same seed. Linux output, and corpora with 10 or fewer siblings, are
  unchanged. Bulk/diff mode (`--target`) already sorted `/` paths and is
  unaffected. Agreed with CodeCompass (no frozen symbol-mutate outputs).
- `scan --sample` picks the same files per cluster on Windows as on Linux, for
  the same reason.

### Fixed
- `compile_commands.json` is written with LF line endings on every OS, like
  the manifest and deltas (it was CRLF on Windows).
- Tests that shrink mutate's grow/shrink/restream size targets no longer
  change them for test classes running in parallel.

## [1.1.7] - 2026-10-12

### Changed
- `baseManifestSha` no longer depends on the `--out` folder name of a
  `--linked-roots` corpus. Its manifest paths into sibling roots are written
  `../<leaf>_rootN/...`; the leaf is now dropped before hashing, so the same
  corpus generated under another folder name binds the same deltas. Agreed
  with CodeCompass and CodeDiffer (neither reads the sha). Manifests without
  linked roots hash exactly as before; record paths and every truth digest
  are unchanged.

## [1.1.6] - 2026-10-12

### Fixed (reproducibility)
- Replaying `_meta.gen` no longer shrinks giant headers. `macroDensity` was
  recorded even when `--macro-density` was not passed, and passing it back
  turns on a define cap the original run never had, so a death-scale giant
  replayed at ~56 MB instead of ~1.2 GB. It is now recorded only when set. For
  a pre-1.1.6 manifest, drop a recorded `1000000` when replaying unless the
  original run set it (e.g. `--preset memory`).

### Fixed (mutate)
- `mutate --step k` writes the step's delta before editing the file, and
  removes the delta if the edit fails. Before, a failed delta write left the
  edit on disk unrecorded, and retrying the step applied it twice (24 line-shift
  filler lines with a delta saying 12). The default per-step mode commits each
  step the same way.
- Malformed manifests of the right JSON syntax but the wrong shape (no `_meta`,
  a symbol without `def`, `null` where a string belongs, `1.5` or `1e40` where
  an integer belongs) now give a clean `error: malformed input` and exit 2 from
  `mutate`, instead of a stack trace.
- `--edits` below 1 is an argument error (exit 2); a huge `--edits` reports
  "not enough source files" instead of running out of memory.

### Fixed (crash safety)
- An interrupted mutate is no longer silent. Mutate keeps a
  `.codespawner-mutating` journal in each tree it edits and deletes it once the
  run's last artifact is written; if the run dies part-way, the next mutate
  refuses the corpus and verify FAILs it, instead of the tree passing as a
  corpus that matches neither its base manifest nor any delta.
- Every in-place edit (line edits, eol, encoding, binary, giant files,
  line-shift, add, grow, shrink, restream) writes a temp file and renames it
  over the original, so a failure never leaves a truncated file. The giant and
  binary edits no longer leave a `.mut.tmp` behind when they fail, and a giant
  edit no longer deletes the original before moving the new file in.
- Manifests, deltas, shards and profiles are flushed to disk before they are
  renamed into place.
- `gen --from-profile --with-oracle` checks that the manifest can be written
  before synthesizing anything.

### Fixed (regen)
- Regenerating over a corpus also removes its mutate deltas (including
  `--step` deltas), its 3-way variant trees and its stale `--linked-roots`
  siblings. Before, they survived beside the new tree, and leftover files in
  `<out>_rootN` were mixed into the new corpus.
- The marker is deleted last, so an interrupted regen leaves a directory the
  next run recognises without `--force`.
- The marker now records the `--linked-roots` count. A non-empty `_rootN` the
  new corpus would write into is refused without `--force` unless it belongs
  to the CodeSpawner corpus at `--out` (for a 1.1.5 or older corpus, which
  records no count, its `_rootN` in the new range are taken as its own).

### Fixed (containment)
- A manifest path through a junction or symlink inside the corpus (or a
  linked root that is one) is refused by mutate and fails verify, so a link
  planted in the tree can't steer an edit or delete outside it.
- Manifest paths into `../<corpus>_rootN/` are accepted only for N below the
  recorded `--linked-roots` count.
- verify's "vendor header is absent" check no longer follows junctions.

### Fixed (verify)
- A symbol with no `def` (or a non-string one) is a FAIL. Before, it was
  skipped, so a truncated manifest could drop def sites and still pass.
- Malformed manifest values no longer crash verify.

### Docs
- diff-delta-design.md: the 3-way conflict JSON example now matches what is
  written (`v1`/`v2` objects with op/newStart/newLines; flat `mergedClean`
  records with `side`); the edit kinds are `line-insert`/`line-delete`; an
  empty digest section is zero bytes (there is no header); the
  `conflictTruthSha` golden vector's input is spelled out.
- knobs.md: the bulk and 3-way mutate options and the `--from-profile` /
  oracle knobs are listed; `--io-parallelism` is in its own section with its
  real auto default; `--preset memory` has 3 includers, not 5.
- mutate-design.md: status, output and edit-kind list brought up to date;
  symbol-mode rename is marked as never built.
- `digest-selftest` is in `--help`, the README and USAGE; manifest-schema.md
  says it prints all four vectors.
- `vendor-codespawner.ps1` also copies mutate-design.md, diff-delta-design.md
  and oracle-v1-design.md, not just manifest-schema.md.

### Tests
- The emitted `diffTruthSha` and `conflictTruthSha` are recomputed from the
  delta/conflict JSON alone (every edit kind, giant run rule, reassembled
  shards, both 3-way modes), and each record's old/new sha and size is checked
  against the real files.
- The `prevTruthSha` chain is re-derived from base truth plus each delta's
  overlay, `--step 1..n` is checked byte-identical to the default run, and a
  failed step's retry is checked to apply once.
- The scanner privacy contract is tested in all three postures: no file,
  directory, identifier, string or comment text reaches the profile; small
  clusters fold into `other`; posture decides which blocks exist; a re-scan is
  byte-identical.
- `gen --from-profile` is checked against its archetype counts and sizes, and
  for byte-identical output from the same profile and seed.
- Crash-safety tests: an interrupted bulk mutate, failed in-place rewrites of
  every kind, regen sweeps, linked-root guards, an interrupted wipe, and
  junction containment.

## [1.1.5] - 2026-10-12

### Fixed (CLI)
- `codespawner <command> --help` prints the help instead of failing with
  "unknown knob --help".
- Every command parses arguments the same way: `--knob v` or `--knob=v`, and
  boolean flags as `--flag`, `--flag=false` or `--flag false`. Before,
  `gen --manifest false` was rejected, and mutate/scan/verify did not accept
  `--knob=v` at all. The space form only takes a boolean word, so it never
  swallows the next option or scan's positional tree.
- `--help` now lists every option the parser accepts, including the
  pathology knobs, `--big-headers`/`--med-headers`/`--ordinary-headers`,
  `--giant-includers`, `--blob-files`, `--build-output`, `--dirs`,
  `--depth`, `--manifest`, and scan's `--tree`. A test fails if an option is
  added without help.

### Fixed (reproducibility)
- `_meta.gen` now replays to a byte-identical tree. `dirs` was recorded as
  the unscaled default (5700) instead of the effective scaled count, so
  feeding the recipe back to `gen` placed every file in a different
  directory. A test now regenerates a corpus from its own `_meta.gen` and
  compares every file. `docs/diff-delta-design.md` shows how to replay.

### Fixed (scale)
- `--edit-kind eol`, `encoding` and `binary` no longer crash on giant files.
  Before, they read the whole file into one string or array, so a >1 GB
  header (for example under `--edit-kind mixed` at death scale) ran out of
  memory. `binary` now always streams. `eol`/`encoding` stream files over
  512 MiB and keep the in-memory path below that; the bytes written are the
  same either way.
- `whitespace`, `line-insert` and `line-delete` skip files over 512 MiB,
  leaving them untouched and unrecorded, the same as non-text files. They
  need explicit hunks, which have no compact form for giants; before, these
  files crashed. `content` on giants is unchanged (run-rule or `--giant-edit
  single`).
- JSON artifacts are written out every ~1 MiB instead of being buffered whole
  in memory, which capped them near 2 GiB. The truth digests hash
  incrementally instead of building one string (capped near 1G chars). Bytes
  and shas are unchanged; the golden vectors still pass.

### Changed (build)
- The exe's FileVersion/ProductVersion now come from `Program.Version`
  instead of reading 1.0.0.0.
- `scripts/bench.ps1` fails when any `gen` exits non-zero. Before, a failed
  gen could let the determinism and seed-variance checks compare two empty
  trees and pass. Its fallback exe path now points at net8.0.

## [1.1.4] - 2026-10-12

### Fixed (safety)
- `gen --force` no longer deletes any path it is given. A drive or share root,
  the current directory, the user profile (or a parent of either), a git
  checkout, and well-known user/system folders are refused even with `--force`.
  Previously `gen --out . --force` recursively deleted the working directory's
  contents before failing.
- `mutate` only edits or deletes files inside the corpus. Manifest def paths
  that escape it (`..`, absolute or drive-relative paths) are refused before
  any disk change; `--linked-roots` siblings (`../<corpus>_rootN/...`) count as
  inside. Junctions/symlinks inside the corpus are no longer followed when
  selecting targets, scanning, or copying 3-way variants.
- `mutate --three-way` refuses to wipe an existing `<corpus>_v1`/`_v2` that
  CodeSpawner did not create (checked before anything is touched), and the
  stale-delta sweep only deletes `<corpus>-delta.shard-<digits>.json`, not any
  file matching `shard-*`.
- `gen --from-profile` refuses archetype labels/extensions that are not plain
  file-name fragments (e.g. `../x`, `a/b`, `x:stream`), so a profile can no
  longer write outside `--out`.
- `NaN`/`Infinity` are rejected for every numeric option. They used to pass
  the range checks, let `mutate` edit files in place, and then crash the JSON
  writer, leaving a truncated delta.
- Manifests, deltas, shards, the conflict file and scan profiles are written to
  a temp file and renamed into place, so an interrupted run never leaves a
  truncated artifact at the real name. Regenerating over a corpus deletes its
  old sibling manifest first, so a stale manifest never sits beside a new tree.
- `verify` accepts `--linked-roots` corpora (it reported every federated def as
  "path escapes corpus root").
- Malformed JSON input (manifest/profile) is reported as `error: malformed
  JSON input: …` with exit 2 instead of an unhandled-exception stack dump.

### Fixed (diff truth)
- Line-level `mutate` edits are byte-faithful. Before, every text edit kind
  re-wrote the file as UTF-8 without a BOM, so UTF-16 files were silently
  transcoded and BOMs dropped while the truth recorded only line hunks; invalid
  UTF-8 had bytes replaced by U+FFFD; and 3-way rewrote files on a side that
  drew no edits, leaving changes with no record. Now a file keeps its encoding
  and BOM, only the edited lines change, and a file that is not exactly
  round-trippable text (invalid UTF-8, NUL-bearing binary, UTF-32) is left
  untouched and unrecorded by the text kinds (a pure rename under `rename`).
  Plain UTF-8 corpora produce byte-identical output to 1.1.3. Both CodeDiffer
  fixtures re-generate with the same `diffTruthSha`.
- `mutate --target all` never selects the generator's root
  `compile_commands.json`. Its absolute paths made `diffTruthSha` depend on
  where the corpus lived. Leftover `*.mut.tmp` files are also skipped.
- `line-insert` hunks now use the pinned insert convention: `oldStart` is the
  number of old lines before the insertion (git-style), as `--giant-edit
  single` already did. It was one too high, so a file could not be rebuilt
  from its hunks. Deletes are unchanged. The convention is now written out in
  docs/diff-delta-design.md. `line-insert` deltas get a new `diffTruthSha`.
- `--edit-kind binary` output now looks binary to a diff tool: it always has a
  NUL byte in the first 8 KB and never starts with a UTF-16/32 BOM. Before,
  XOR-flipping ASCII left printable text, so diffs classified it as content.
  `binary` deltas get a new `diffTruthSha`.
- The shard count for `--shard-size` no longer overflows near `int.MaxValue`.

### Changed (artifact contract — agreed with CodeDiffer and CodeCompass)
- Symbol deltas now carry `"_meta.deltaKind": "symbol"`, so every delta names
  its kind (`diff`, `conflict-3way`, `symbol`). Older symbol deltas omit it.
- `baseManifestSha` no longer depends on where the corpus lives or which OS
  wrote the manifest. It is SHA-256 of the manifest bytes with CR bytes dropped
  and `_meta.corpusRoot` blanked. Before, it hashed the raw file, so a corpus
  regenerated at another path got a different value for identical content.
  Every `baseManifestSha` value changes once. Symbol deltas made from the same
  seed are now byte-identical wherever the corpus is.
- Every JSON artifact (manifest, deltas, shards, index, conflict file,
  profile) is written with LF line endings on every OS. On Windows these used
  CRLF, so the bytes and shard shas differed from Linux. Windows shard shas
  change once. No `truthSha`/`diffTruthSha`/`conflictTruthSha` changes: they
  are digests of the content, not of the file bytes. Corpus files, including
  `compile_commands.json`, are unchanged.

### Changed (release tooling)
- `scripts\release.ps1` runs the test suite and builds + version-checks the
  AOT exe **before** anything is pushed. It commits and tags locally first, and
  rolls that back if the tests, the build, the `codespawner version` check or
  the push of main fail. A failed run can be re-run as-is.
- `release.ps1` refuses to run off `main`, when local main is behind or has
  diverged from `origin/main`, when `gh` is not authenticated, or when the tag
  already exists locally or on origin. The "already at this version" guard now
  works for an explicit version, and the version bump only matches
  `const string Version`.
- If `gh release create` fails, the notes file is kept and the exact command to
  finish by hand is printed. Release notes now include the exe's SHA-256.
- New `release.ps1 -DryRun`: every check, the tests, and the AOT build + version
  check, with no commit, tag, push or release.
- `scripts\build.ps1` deletes the previous `dist\codespawner.exe` before
  publishing and fails when `dotnet publish` fails. Before, a failed publish
  next to an old exe printed "OK", and a release could ship the previous
  version's binary under the new tag.

## [1.1.3] - 2026-10-09

### Fixed
- `gen --from-profile` no longer truncates its per-file RNG stream index to 32
  bits. Profiles with ≥22 archetypes (or any archetype past the per-archetype
  lane width) previously collided file streams — regenerating byte-identical
  size/content for files that should differ — once `archetype*lane + file`
  exceeded `int.MaxValue`. The index is now 64-bit; output is byte-identical for
  all non-overflowing cases (existing corpora unaffected), and an archetype that
  would exceed its lane now fails fast with a clear error instead of silently
  colliding.
- The "manifest disabled" error now names the form the parser actually accepts
  (`--manifest=false`, not `--manifest false`).

### Changed
- Preset list is single-sourced from one registry (`gen --help` and preset
  resolution can no longer drift apart).
- `release.ps1` checks for the CHANGELOG section as a pre-flight step, before any
  commit/tag/push, and correctly captures the oldest section.

### Docs
- Added this CHANGELOG; release notes are now injected from it.
- `manifest-schema.md`: corrected `hot_shared` edge semantics (oracle overlay:
  every `func_i`; plain `gen`: only the giant-including files; none with
  `--giant-headers 0`), documented the `_meta.gen` reproducibility block, and
  completed the `populations` list.
- README / USAGE: the build requirement is the .NET 8 SDK or newer (was ".NET 10").

### Tests
- End-to-end regen-identity guards for `gen`: re-running with the same seed/args
  over its own output is byte-identical in both the file tree and the manifest
  (the regen-in-place / cross-machine workflow), and two gens to different out
  dirs differ only in the absolute `_meta.corpusRoot` — all ground truth stays
  corpus-relative.
- Frozen determinism goldens: a fixed `Rng` stream, `Next` reduction values, and
  the `Category` enum's integer values — a refactor that silently changes the
  generated bytes now fails the suite.
- Preset resolution, verifier negative paths, and a guard that `gen --help`
  lists every registered preset.

## [1.1.2] - 2026-10-07

### Fixed
- Addressed the five findings from the 1.1.1 independent review:
  - Sharded-delta filenames now pad the shard index to a dynamic width, so they
    sort in shard order past 999 shards (was fixed-width `D3`).
  - Removed a dead `--from-profile` branch in the generator (profile generation
    dispatches elsewhere; its recipe is the profile itself).
  - Stale-artifact cleanup now runs once at bulk entry and covers every delta
    shape (2-way, 3-way, and sharded), instead of only the 2-way monolithic path.
  - Hardened the delta-artifact glob against bare-relative corpus paths.
  - Softened the `--edit-kind mixed` doc/help wording (drops the ">=6 => all six"
    overclaim) to describe content-bearing files honestly.

## [1.1.1] - 2026-10-07

### Added
- `--edit-kind mixed` — bulk mutate assigns one reason class per file, cycling the
  six reasons (content, eol, whitespace, encoding, binary, metadata) across the
  changed set so a single delta spans every reason class.
- `_meta.gen` reproducibility block in generated manifests, recording the
  effective generation knobs so a corpus can be regenerated exactly.
- `--shard-size N` paged/sharded delta transport — emits a `<corpus>-delta.index.json`
  plus `<corpus>-delta.shard-NNN.json` pages of ≤N modified records each. The
  `diffTruthSha` is **sharding-invariant** (identical to the monolithic delta),
  so a death-scale delta can be sharded for transport without changing the truth.
- `--giant-edit single` — a single localized one-line insert into a giant file,
  emitted as one explicit `insert` hunk (the content-defined-chunker locality
  case), alongside the existing `strided` run-rule mode.

### Fixed
- Bulk `mutate` now fails at plan time with a clear, actionable `ArgException`
  when a plan needs more shrink/restream seeds than the corpus carries, instead
  of crashing with a raw `ArgumentOutOfRangeException` (reported by CodeCompass:
  `--edits 10` against a corpus with one shrink seed).

## [1.1.0] - 2026-10-02

### Added
- Bulk diff-oracle mutate mode — deterministic, targeted, count/density-controlled
  in-place edits emitting a base→variant diff delta.
- diff-delta ground truth built in four steps: hunk-level truth + `diffTruthSha`
  (step 1), `--edit-kind` reason classes (step 2), rename/move + decoys (step 3),
  and native 3-way diff3 + `conflictTruthSha` (step 4).
- `--conflict-edges` — 3-way edge-case conflict kinds (adjacent multi-line
  union-span, modify/delete, add/add, identical-overlap).
- Honesty guards for the rename and giant-file paths.

### Changed
- Byte-faithful content rewrite — no more spurious empty-diff records.

### Tests
- Raised line coverage to ~96% (verifier failure modes, profile-gen oracle/guards,
  generation/scan/CLI surface, full ArgParser branch coverage, symbol-overlay
  mutate path).

## [1.0.9] - 2026-09-30

### Added
- Oracle guarantees a reachable, undispatched indirect edge (the "indirection
  tax"); documented the oracle-v1 manifest schema.

## [1.0.8] - 2026-09-30

### Added
- Oracle v1: seeded DAG call graph with `_meta.roots` and a reachable-fraction
  dial (phase 1+3), indirect-edge ground truth with `indirectTruthSha` (phase 2),
  and byte-mass ground truth via `--oracle-bytes` (phase 4).
- xUnit test suite (164 tests) and a `bench` oracle-v1 gate.

### Fixed
- Ungraceful CLI crash on bad numeric args.

## [1.0.7] - 2026-09-29

### Fixed
- `gen --from-profile` directory-count inflation and data-blob round-trip.

### Changed
- `.gitignore` never commits local scan profiles of real trees (`private/`,
  `*work-profile*.json`).

## [1.0.6] - 2026-09-29

### Changed
- Target `net8.0` (the fleet floor) instead of `net10.0`.

## [1.0.5] - 2026-09-29

### Added
- `scan` / shape-profile: privacy-preserving characterize → regenerate, with a
  design doc and CodeCarver + CodeCompass sign-off folded in.

## [1.0.4] - 2026-09-27

### Added
- mutate/churn v1 — delta manifests with the `--step` chain and `--through`
  cumulative modes, with a design doc and CodeCompass sign-off.

## [1.0.3] - 2026-09-27

### Changed
- Hardened the Batch 1/2 pathology presets after a review pass.

## [1.0.2] - 2026-09-27

### Added
- Batch 2 pathology presets: `pathological-symbols` (expectedMiss) and
  `dup-content`.

## [1.0.1] - 2026-09-27

### Added
- Batch 1 pathology presets and `_meta.populations`.

## [1.0.0] - 2026-09-27

### Added
- Initial release: deterministic synthetic code-corpus generator with a
  machine-checkable ground-truth manifest, `gen` / `verify`, and the release
  tooling.

[Unreleased]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.3.2...v1.4.0
[1.2.3]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.13...v1.2.0
[1.1.13]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.12...v1.1.13
[1.1.12]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.11...v1.1.12
[1.1.11]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.10...v1.1.11
[1.1.10]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.9...v1.1.10
[1.1.9]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.8...v1.1.9
[1.1.8]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.7...v1.1.8
[1.1.7]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.6...v1.1.7
[1.1.6]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.5...v1.1.6
[1.1.5]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.9...v1.1.0
[1.0.9]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.8...v1.0.9
[1.0.8]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.7...v1.0.8
[1.0.7]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.6...v1.0.7
[1.0.6]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.5...v1.0.6
[1.0.5]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/RelentlessOldMan/CodeSpawner/releases/tag/v1.0.0
