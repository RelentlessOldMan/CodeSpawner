# CodeSpawner — Usage Guide

A deterministic generator of synthetic code corpora for stress-testing code indexing / search / carving
tools at extreme scale (100 GB+ trees, individual headers over 1 GB), with a machine-checkable
**ground-truth manifest**. This is the complete guide; see also [`knobs.md`](knobs.md) (every knob) and
[`manifest-schema.md`](manifest-schema.md) (the v1 contract).

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

Prerequisites: **.NET 10 SDK**, and for Native AOT the Visual Studio **"Desktop development with C++"**
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

Output:
- the corpus tree under `<out>/`,
- `<out>-manifest.json` written **beside** the corpus dir (a sibling),
- a small `.codespawner` marker file at the corpus root (internal bookkeeping; excluded from counts).

Every run prints per-phase timings and the detected disk / concurrency, e.g.:
```
  io-parallelism: 12 (auto for SSD; override with --io-parallelism)
  register headers ... 4.1s
  ...
Done: 51,203 files, 89.74 GB, 5,721 dirs, 1 root(s) in 71.3s.
```

### `verify --corpus <dir> [--manifest <file>]`
Tool-independent self-check: asserts the manifest accurately describes the emitted files — every
`def`/`ref` site exists at `path:line` and contains the expected token, every `unreachableRef` is
genuinely gated (behind `#ifdef VENDOR_OK` with its vendor header absent), and the `func_i → func_{i-1}`
edge chain is intact. Resolves relative paths against `--corpus` (so a **copied** corpus validates against
the same manifest). Exit 0 = pass, 1 = fail, 2 = bad input. Manifest defaults to the `<corpus>-manifest.json`
sibling.

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
it says. Setting a count to `0` disables that population. **Principle:** adding a knob for one consumer
must never force another to change; tool-specific knobs stay optional and defaulted.

### Presets
Scale tiers: `--preset ci` (≈1/100, keeps a giant), `--preset memory` (one giant + includers, nothing
else), `--preset death` (~90 GB, 12 headers >1 GB).

Pathology presets (each isolates one failure axis — see [ROADMAP.md](ROADMAP.md)):
`--preset dense-band` (posting/trigram memory OOM), `--preset broad-token` (`find_references` at scale +
sidecar reads), `--preset long-lines` (long-line / no-newline), `--preset encoding-mix` (UTF-16/BOM/
invalid bytes / non-ASCII idents), `--preset many-tiny` (walker / stat pressure).

Your own knobs after a preset override it (e.g. `--preset dense-band --dense-headers 80`). Pathology
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
    compile_commands.json      # only if --compile-db partial|full
  <out>-manifest.json          # ground-truth manifest (SIBLING of the corpus dir)
```

`--linked-roots N` adds sibling trees `<out>_root1 … _root{N-1}`; their files appear in the manifest with
`../`-prefixed relative paths (consumers key on the symbol/basename, which is unaffected).

---

## 10. Integrating a consumer (vendoring)

Consumers **vendor** a pinned generator; they do not submodule it (the real coupling is the manifest
contract, versioned in [`manifest-schema.md`](manifest-schema.md)).

```powershell
# From the CodeSpawner repo, after building:
.\scripts\vendor-codespawner.ps1 -Target C:\Playground\CodeCompass
.\scripts\vendor-codespawner.ps1 -Target C:\Playground\CodeCarver
```

This copies `codespawner.exe` + `manifest-schema.md` into `<repo>\tools\codespawner\` and writes a
`GENERATOR_VERSION` stamp. Each consumer keeps its own thin verify adapter and **asserts
`_meta.manifestVersion == 1`** before trusting a manifest:

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

# Release gate: run the full battery.
.\scripts\bench.ps1
```

---

## 12. Troubleshooting

| Symptom | Fix |
|---|---|
| AOT build fails with `vswhere.exe is not recognized` | Use `scripts\build.ps1` (it fixes PATH), or install the VS "Desktop development with C++" workload; else `build.ps1 -SelfContained`. |
| `--out … was not created by CodeSpawner. Refusing to delete` | Intended guard. Choose a fresh `--out`, or pass `--force` if you really mean to wipe it. |
| Generation slow on a spinning disk | It auto-detects HDD → `--io-parallelism 1`. If it guessed wrong (e.g. RAID), set `--io-parallelism` explicitly. |
| Slow when writing to a network share | Generate locally, then `robocopy` to the share. Per-file writes over SMB are far slower. |
| `verify` fails after a copy | Point `--corpus` at the copied dir; relative paths resolve against it. Ensure you copied `<out>-manifest.json` too (it's a sibling). |
| Out of disk mid-run | The partial `--out` is safe to delete (re-running clobbers it). Estimate size first (§8). |

---

## 13. Exit codes

| Command | 0 | 1 | 2 |
|---|---|---|---|
| `gen` | success | IO/emit failure | bad arguments / protected `--out` |
| `verify` | manifest matches corpus | mismatch found | bad input (missing corpus/manifest, invalid JSON) |
