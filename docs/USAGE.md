# CodeSpawner — Usage Guide

A deterministic generator of synthetic code corpora for stress-testing code indexing / search / carving
tools at extreme scale (100 GB+ trees, individual headers over 1 GB), with a machine-checkable
**ground-truth manifest**. This is the complete guide; see also [`knobs.md`](knobs.md) (every knob) and
[`manifest-schema.md`](manifest-schema.md) (the v1 contract, and v2 for C++), and
[`cpp-profile-design.md`](cpp-profile-design.md) (the C++ profile, `gen --lang cpp`).

---

## 1. Mental model — what makes this a real test

A real firmware repo surfaces defects that come from its **structure**, not its raw byte count. The
defining fact: **~90% of the bytes live in ~2% of the files** — a few machine-generated hardware
register-map headers (100 MB to >1 GB each, ~1M `#define`s) amid tens of thousands of tiny files. A
generator that spreads bytes evenly reproduces *none* of the interesting failures. **The bimodality is
the test.** Three principles follow:

1. **Scale by COUNTS, never per-file size.** `--scale` multiplies file *counts*; the giant headers keep
   their pathological size at any scale (even `--scale 0.001` keeps ≥1 giant).
2. **Deterministic** given `--seed`: the same seed produces a byte-identical corpus + manifest, even
   though generation runs multi-threaded. Any failure replays exactly.
3. **Ground truth**: because the generator knows what it emitted, it writes every symbol's definition and
   reference sites (plus a negative set). "find_references returned 4 hits" becomes a pass/fail assertion.

---

## 2. Install / build

Prerequisites: **.NET 8 SDK or newer**, and for Native AOT the Visual Studio **"Desktop development with C++"**
workload (MSVC linker + Windows SDK). Target machines need nothing — the exe is self-contained.

```powershell
.\scripts\build.ps1                 # -> dist\codespawner.exe  (Native AOT, ~2 MB, no runtime needed)
.\scripts\build.ps1 -SelfContained  # fallback: single-file self-contained (bundles runtime; larger exe)
```

The build script adds the VS Installer dir to PATH so the AOT linker can find `vswhere.exe` (a common
gotcha). If AOT still fails because the C++ workload is absent, use `-SelfContained`.

Then drop `dist\codespawner.exe` anywhere and run it.

---

## 3. Quick start

```powershell
# Fast smoke: a small corpus with no giant headers, then self-check it (seconds).
codespawner gen --out .\_fw --scale 0.01 --giant-headers 0
codespawner verify --corpus .\_fw

# One giant header + the .c files that include it (the memory-axis repro).
codespawner gen --out .\_mem --preset memory

# Full work-scale "repo of death": ~90 GB, 12 headers over 1 GB.
codespawner gen --out D:\death --preset death
```

---

## 4. Commands

### `gen --out <dir> [knobs…]`
Generates the corpus and (unless `--manifest false`) the ground-truth manifest. It is a **pure emitter**:
it never shells out to an indexer or carver — each consumer drives its own verification. `--out` is
cleared and recreated; a directory CodeSpawner didn't create is protected unless you pass `--force`.
Some paths are refused even with `--force`: a drive or share root, the current directory, your user
profile or a parent of either, a git checkout, and well-known user/system folders. On Windows these are
compared by directory identity too, so an alias (`\\?\C:\...`, `\\localhost\C$\...`, a `subst` drive, an
8.3 short name) is refused like the path it reaches. Re-generating over an
existing corpus first removes everything the old one left beside it: its manifest, its mutate deltas
(`-delta*.json`, `-conflict.json`), its 3-way variant trees (`_v1`/`_v2`, unless one is a junction or
symlink, which is left alone) and its linked roots (`_rootN`).
The marker is deleted last, so an interrupted regen still leaves a directory CodeSpawner recognises. A
non-empty `<out>_rootN` that the new corpus would write into is refused unless you pass `--force`, when it
isn't part of the CodeSpawner corpus at `--out` (for an `--out` from 1.1.5 or earlier, whose marker records
no count, the `_rootN` its manifest names are its own; without that manifest none are).

Output:
- the corpus tree under `<out>/`,
- `<out>-manifest.json` written **beside** the corpus dir (a sibling),
- a small `.codespawner` marker file at the corpus root (internal bookkeeping; excluded from counts). It
  records the generator version and the `--linked-roots` count; manifest paths into `../<out>_rootN/` are
  accepted only for N below that count.

Every run prints per-phase timings and the detected disk / concurrency, e.g.:
```
  io-parallelism: 12 (auto for SSD; override with --io-parallelism)
  register headers ... 4.1s
  ...
Done: 51,203 files, 89.74 GB, 5,721 dirs, 1 root(s) in 71.3s.
```

### `gen --lang cpp --out <dir> [--cpp-* knobs…]`
Writes a self-contained C++ program (`<dir>/cpp/`: namespaces, single and multiple inheritance, virtual / pure
/ final methods, ctors/dtors, static objects, function and class templates, member-function pointers) and a
**manifest v2** with its call-graph truth: `edges`, `implicitEdges`, `virtualEdges` (sound `cha` and precise
`rta` target sets), `memberPtrEdges`, `kind`s, `staticInitRoots` and `_meta.cppTruthSha`. The program
compiles and links (`clang++ -std=c++17 -Wall -Wextra -Werror`, all `.cpp` under `cpp/`); it is not meant to
run. Knobs: [knobs.md](knobs.md); contract: [cpp-profile-design.md](cpp-profile-design.md). `verify` checks it;
`mutate` refuses it (exit 2: mutate edits C corpora only).

```powershell
codespawner gen --lang cpp --out C:\corpora\cpp --cpp-classes 40 --cpp-depth 4
codespawner verify --corpus C:\corpora\cpp
```

### `gen --web-units <n> --out <dir> [--web-* knobs…]`
Adds an **include web** beside the C corpus: register-map headers chained deep and overlapping, aggregators,
template headers expanded through `TNAME`, an `#include_next` vendor dir, and `n` `.c` units under `web/`. The
manifest records each unit's include closure (`closures`: files, bytes, reads, readBytes, depth, defines,
templateInstances) and every template instance as a symbol. `verify` recomputes all of it from the text. Contract
and knobs: [include-web-design.md](include-web-design.md). `--preset death` carries it, and `--preset death-lite`
carries it at death's counts in about 0.5 GB.

```powershell
codespawner gen --out C:\corpora\web --scale 0.001 --web-units 200 --web-headers 600
codespawner verify --corpus C:\corpora\web
```

### `verify --corpus <dir> [--manifest <file>]`
Tool-independent self-check: asserts the manifest accurately describes the emitted files — every
`def`/`ref` site exists at `path:line` and contains the expected token; every `unreachableRef` site holds
the call, its file contains an `#ifdef VENDOR_OK` and no `VENDOR_missing_*.h` exists in the tree (it does
not prove the call sits inside that `#ifdef` block); and every `edges` target is a symbol the manifest
declares (it does not re-read the call from the source, so an edge whose call was edited away still
passes). Resolves relative paths against `--corpus` (so a **copied** corpus validates against
the same manifest). Exit 0 = pass, 1 = fail, 2 = bad input. Manifest defaults to the `<corpus>-manifest.json`
sibling. A manifest path that goes through a junction or symlink inside the corpus fails as escaping it, and
a corpus holding a `.codespawner-mutating` journal (an interrupted mutate, below) fails outright. A manifest
with no `_meta` or `symbols` object, or one that declares nothing to check (no symbol sites, dup groups or
indirect edges), is bad input (exit 2); before 1.1.11 the latter passed. A `manifestVersion` other than 1 or
2 is bad input (exit 2), as is a duplicate key anywhere in the manifest or a `_meta` value of the wrong JSON
type (`seed` not an integer, `generatorVersion` not a string). A v2 (C++) manifest of the wrong shape (a field
of the wrong JSON type, a symbol with no `def`, a v1-only field) is bad input too; otherwise it is checked
against its own rules: whole-word simpleName def/ref lines, canonical and unique sites, defs on definition
lines in their owner's files, no site on a declaration, no ref on a definition line, every definition line in
the program some symbol's def (so a deleted symbol fails), kinds that fit their keys, the `_meta.classes`
hierarchy against the `class` lines, edge-free class symbols, no edge into a class or template, `instanceOf`,
`pure`, implicit / member-pointer / static-init targets of the right kind, `cha` exactly the override set below
a receiver with `rta ⊆ cha`, and a recomputed `cppTruthSha`. **What it does not prove:** that `edges`, `rta`,
`implicitEdges`, `staticInitRoots` and member-pointer `dispatched` match the program's calls (they are checked
only for the right kind of target), and `cppTruthSha`, recomputed from the manifest itself, catches only an
edit that was not re-signed. The full list is in [cpp-profile-design.md](cpp-profile-design.md) "As built".
With an include web, verify re-preprocesses every unit (its own small preprocessor: guards, `#if`, comments,
continuations, computed includes, `#include_next`) and compares all seven closure numbers, and requires each
template instance it finds to be declared once with the same `def`, `templateHead` and `includedVia`.

### `mutate --corpus <dir> [--seed n] [--edits n] [--step k | --through] [--restream]`
Deterministically edits an existing corpus **in place** (guarded by the `.codespawner` marker) and emits a
**delta manifest** that composes with the base: `truth = base ⊕ delta`. The incremental-index / watcher /
reconcile / sidecar-orphan oracle. `--step k` applies one edit and emits `<corpus>-delta-{k}.json` (the
chain driver — the consumer runs its incremental `update` and asserts between steps); `--through` applies
all N and emits one cumulative delta. Edits: remove, line-shift modify, add, 4-grow (sidecar CREATE),
4-shrink (sidecar DELETE), opt-in 4-restream (REWRITE). Generate the base with
`--shrink-seeds N [--restream-seeds N]` so the threshold edits have targets. See
[mutate-design.md](mutate-design.md) for the delta schema, composition rule, and `prevTruthSha` chain guard.

While it edits, mutate keeps a `.codespawner-mutating` journal in the tree (in each `_v1`/`_v2` for a 3-way
run) and deletes it once the run's last artifact is written. If a run dies part-way, the journal stays: the
tree then matches neither its base manifest nor any delta, so mutate refuses the corpus and verify fails it
until you regenerate it. Each file is rewritten through a temp file and renamed into place, so a single file
is never left half-written.

For its whole run mutate also holds an exclusive lock, `<corpus>-mutating.lock` beside the tree, so a
second mutate of the same corpus is refused (exit 1) until the first ends. The OS releases it if the process
dies, and the file is deleted when the run ends.

Mutate also records what it has applied in `<corpus>-mutated.txt`, beside the corpus (never inside it, so a
diff of base against variant sees only the delta's changes). A mutated tree no longer matches its base, so
mutate refuses it except for the next `--step` of the same chain: `--step k` needs steps 1..k-1 recorded
with the same `--seed`, `--edits` and `--restream`, and anything else (a repeated or skipped step, another
default, `--through` or bulk run) is refused until you regenerate. A `--three-way` run leaves the tree as the
base and records nothing for it, but records each `_v1`/`_v2` variant, so a variant is not mutated again.
Regenerating clears the record, the variants' included; a corpus mutated by 1.1.8 or earlier has none.

### `digest-selftest`
Recomputes the five golden digest vectors (`prevTruthSha`, `indirectTruthSha`, `diffTruthSha`,
`conflictTruthSha`, `cppTruthSha`) from their frozen fixtures and prints them; exit 1 if any drifted. Consumers reproduce
the same vectors to prove their own recompute matches (see [manifest-schema.md](manifest-schema.md) and
[diff-delta-design.md](diff-delta-design.md)).

### `version`
Prints the generator version (used by the vendoring stamp).

Run `codespawner --help` for a compact knob list.

---

## 5. Knobs (summary)

Full table with defaults in [`knobs.md`](knobs.md). The essentials:

| Knob | Purpose |
|---|---|
| `--scale <f>` | Multiplies count knobs (default 0.01). Per-file sizes are never scaled. |
| `--seed <n>` | Deterministic replay (default 1337). |
| `--giant-headers <n>` / `--max-header-mb <n>` | The byte pathology. Set `--max-header-mb 1229` for >1 GB headers. |
| `--macro-density <n>` | `#define`s per giant header, independent of size — the preprocessor-memory axis. |
| `--cfiles <n>` | `.c` files carrying the cross-file call chain. |
| `--tiny-files <n>` | Tiny `.csv` — file-count / SMB stat pressure. |
| `--unresolved-includes <n>` | The honest negative case (references that must NOT resolve). |
| `--compile-db none\|partial\|full` | Presence of `compile_commands.json`. |
| `--linked-roots <n>` | Split output across N sibling trees (federation). |
| `--io-parallelism <n>` | Header write concurrency (auto-tuned; see §7). |
| `--force` | Overwrite an `--out` CodeSpawner didn't create. |

A knob passed **explicitly** is taken literally (not multiplied by `--scale`) — a one-off run means what
it says. Setting a count to `0` disables that population. An out-of-range value (a negative count, a
`--scale` outside (0, 1000], …; see [`knobs.md`](knobs.md)) or an option given twice is refused with exit 2,
on every command. **Principle:** adding a knob for one consumer
must never force another to change; tool-specific knobs stay optional and defaulted.

### Presets
Scale tiers: `--preset ci` (≈1/100, keeps a giant), `--preset memory` (one giant + includers, nothing
else), `--preset death` (~90 GB, 12 headers >1 GB, plus the include web), `--preset death-lite` (all of
death's shapes in ~0.5 GB: 32 MB giants, the web at death's counts with small headers).

Pathology presets (each isolates one failure axis — see [ROADMAP.md](ROADMAP.md)):
`--preset dense-band` (posting/trigram memory OOM), `--preset broad-token` (`find_references` at scale +
sidecar reads), `--preset long-lines` (long-line / no-newline), `--preset encoding-mix` (UTF-16/BOM/
invalid bytes / non-ASCII idents), `--preset many-tiny` (walker / stat pressure),
`--preset pathological-symbols` (symbol-extractor cost + `expectedMiss` honest-miss),
`--preset dup-content` (content-hash dedup / posting collapse).

Your own knobs after a preset override it (e.g. `--preset dense-band --dense-headers 80`); one preset per
run. Pathology
corpora record their shape under `_meta.populations` so a consumer can assert the corpus before trusting
pass/fail.

---

## 6. Determinism & seeds

Same `--seed` + same knobs ⇒ byte-identical corpus and manifest, regardless of core count or thread
scheduling (each file derives its own RNG stream from `(seed, category, index)`). Different seeds move
files between directories and change random content but preserve every structural invariant. The
`bench.ps1` harness asserts both properties (and that a tampered manifest fails verify).

---

## 7. Performance & the disk auto-tune

Header emission is the dominant cost; everything else (source, tiny files, blobs, manifest) is a rounding
error at scale. Two things make it fast:

- **A byte-level emitter** that formats ASCII straight into a reused buffer and flushes ~8 MB at a time —
  no per-line writes, no `Stream.Length` syscall per register. Single-thread throughput is ~440 MB/s.
- **Auto-tuned write concurrency.** Parallel multi-GB writes are a big win on SSD/NVMe but *thrash* a
  spinning disk's heads (measured ~5× slower at full fan-out). So when `--io-parallelism` is not set,
  CodeSpawner probes the target volume: **SSD → all cores, HDD → 1, unknown/network → a safe middle (4)**.
  Override anytime with `--io-parallelism <n>`.

Rough numbers on a 12-core box, NVMe: ~1.7 GB/s for pure-header runs, ~870 MB/s for mixed corpora — a
full ~90 GB death corpus in roughly 1–2 minutes. On a slower disk it is disk-bound; the auto-tune keeps
it from being *needlessly* slow.

**Generate local, then copy.** Per-file writes over SMB are far slower than local. Generate on a local
SSD, then `robocopy` the tree to a share and index/carve *that* — the manifest's relative paths validate
the copy unchanged.

---

## 8. Disk space planning

You need roughly `--out` size free locally (plus the same on a share if staging). `--preset death` is
~90 GB. To estimate a custom run: `giants × max-header-mb` + `big-headers × ~55 MB` +
`med-headers × ~5 MB` dominate; small files are negligible. Start small and read the `Done:` line, which
reports actual size.

---

## 9. Output layout

```
<parent>/
  <out>/                       # the corpus (what you point tools at)
    block0/sub0/mod0/lvl3/.../ # deep dir tree; files scattered by seed
      regmap_block0.h          #   giant register headers (the byte pathology)
      src_42.c                 #   call-chain sources (func_42 -> func_41)
      hdr_7.h  blob_3.c  data_9.csv  ...
    .codespawner               # marker (internal; identifies a CodeSpawner corpus)
    inc/  inc_vnd/  web/       # only with --web-units: the include web (headers, vendor dir, units)
    compile_commands.json      # if --compile-db partial|full, or with the include web (its units)
  <out>-manifest.json          # ground-truth manifest (SIBLING of the corpus dir)
```

`--linked-roots N` adds sibling trees `<out>_root1 … _root{N-1}`; their files appear in the manifest with
`../<out-leaf>_rootN/`-prefixed relative paths (consumers key on the symbol/basename, which is unaffected).
The folder name in those paths is left out of `baseManifestSha` (since 1.1.7), so deltas bind the same base
whatever `--out` folder name the corpus was generated under. Truth digests still hash the paths as written.

---

## 10. Integrating a consumer (vendoring)

Consumers **vendor** a pinned generator; they do not submodule it (the real coupling is the manifest
contract, versioned in [`manifest-schema.md`](manifest-schema.md)).

```powershell
# From the CodeSpawner repo, after building:
.\scripts\vendor-codespawner.ps1 -Target C:\Playground\CodeCompass
.\scripts\vendor-codespawner.ps1 -Target C:\Playground\CodeCarver
```

This copies `codespawner.exe` and the contract docs (`manifest-schema.md`, `mutate-design.md`,
`diff-delta-design.md`, `oracle-v1-design.md`, `cpp-profile-design.md`) into `<repo>\tools\codespawner\` and
writes a `GENERATOR_VERSION` stamp. Each consumer keeps its own thin verify adapter and **asserts
`_meta.manifestVersion`** before trusting a manifest (`1` for a C corpus; a consumer that reads C++ corpora
adds a separate `2` path):

- **CodeCompass** — `verify-corpus-codecompass.ps1`: indexes the corpus, runs `find_references`, and
  asserts against the manifest's `refs` + the unresolved-include disclosure + `doctor`'s scan.
- **CodeCarver** — `carver-groundtruth-oracle.ps1`: reads the explicit `edges`, BFS's the reachable set
  from a root, carves, and asserts kept ⊇ reachable (soundness) + measures over-keep (precision).

When you change the generator, re-run `build.ps1` then `vendor-codespawner.ps1` to bump each consumer.

---

## 11. Common workflows

```powershell
# CI smoke tier (seconds): correctness without the giant-header cost.
codespawner gen --out .\_ci --preset ci --giant-headers 0 ; codespawner verify --corpus .\_ci

# Attribute a regression to one axis: vary ONE knob against a fixed baseline.
codespawner gen --out .\_base --scale 0.05
codespawner gen --out .\_more --scale 0.05 --macro-density 4000000   # only the macro axis changed

# Pure >1 GB single-file path (a few GB, fast): 2 giant headers over 1 GB.
codespawner gen --out D:\big --giant-headers 2 --max-header-mb 1229 --big-headers 0 --med-headers 0 --cfiles 5

# Full work-scale over SMB: generate local, stage to a share, index THAT.
codespawner gen --out D:\death --preset death
robocopy D:\death \\SERVER\share\death /E /MT:16 /R:1 /W:1
# (then run your tool against \\SERVER\share\death)

# Before a release: run the full battery (release.ps1 runs only `dotnet test`).
.\scripts\bench.ps1
```

---

## 12. Troubleshooting

| Symptom | Fix |
|---|---|
| AOT build fails with `vswhere.exe is not recognized` | Use `scripts\build.ps1` (it fixes PATH), or install the VS "Desktop development with C++" workload; else `build.ps1 -SelfContained`. |
| `--out … was not created by CodeSpawner. Refusing to delete` | Intended guard. Choose a fresh `--out`, or pass `--force` if you really mean to wipe it. |
| `--out … Refusing to delete it (--force does not override this)` | `--out` resolved to a drive root, the cwd/home or a parent of them, a git checkout or a system folder. Use a dedicated directory. |
| `3-way variant directory … was not created by CodeSpawner` | `mutate --three-way` writes `<corpus>_v1`/`_v2`; something else already lives there. Move it aside. |
| Generation slow on a spinning disk | It auto-detects HDD → `--io-parallelism 1`. If it guessed wrong (e.g. RAID), set `--io-parallelism` explicitly. |
| Slow when writing to a network share | Generate locally, then `robocopy` to the share. Per-file writes over SMB are far slower. |
| `verify` fails after a copy | Point `--corpus` at the copied dir; relative paths resolve against it. Ensure you copied `<out>-manifest.json` too (it's a sibling). |
| Out of disk mid-run | The partial `--out` is safe to delete (re-running clobbers it). Estimate size first (§8). |

---

## 13. Exit codes

| Command | 0 | 1 | 2 |
|---|---|---|---|
| `gen` | success | IO/emit failure | bad arguments / protected `--out` / missing or malformed `--from-profile` |
| `verify` | manifest matches corpus | mismatch found | bad input (missing corpus/manifest, invalid JSON, not a manifest, nothing to check) |
| `mutate` | success | refused tree (mutated, locked, interrupted) / IO failure | bad arguments / missing corpus or manifest / malformed or hostile manifest |
| `scan` | success | IO failure | bad arguments / tree not a directory |
