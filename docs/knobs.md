# Knob catalogue

Each knob maps to an **observed failure class**. Dial ONE knob against a fixed baseline and a regression
bisect names the subsystem, instead of "something broke in a 90 GB blob."

Counts default to full-repo targets and are multiplied by `--scale` — UNLESS you pass the knob
explicitly, in which case it is taken literally (a one-off run means what it says). Per-file **sizes are
never scaled**: the multi-GB headers are the pathology; even `--scale 0.001` keeps at least one giant.

| Knob | Default | Property it stresses |
|---|---|---|
| `--scale <f>` | `0.01` | Count multiplier for every count knob. |
| `--seed <n>` | `1337` | Deterministic replay. Same seed → identical corpus + manifest. |
| `--macro-density <n>` | `1000000` | `#define`s per giant header, **independent of file size** — preprocessor/macro-table memory. Set it (with a small `--max-header-mb`) for the "many macros, few bytes" axis. |
| `--giant-headers <n>` | `178` | Count of headers filled to `--max-header-mb` (the byte pathology). Kept ≥1 while enabled, even at tiny scale. |
| `--big-headers <n>` | `655` | 10–100 MB headers. |
| `--med-headers <n>` | `1957` | 1–10 MB headers. |
| `--max-header-mb <n>` | `110` | Size of each giant header. **Never scaled.** Set >1024 for the >1 GB single-file pathology. |
| `--ordinary-headers <n>` | `7400` | ~6.6 KB headers (struct + prototypes) — ordinary parse volume. |
| `--cfiles <n>` | `5123` | `.c` files carrying the cross-file cross-directory call chain — reference correctness. Floored at 2. |
| `--giant-includers <n>` | `3` | Of the `.c` files, how many are co-located with + `#include` a giant header and call `hot_shared()` — the aggregate-memory stressor (one query parses every giant TU). |
| `--tiny-files <n>` | `20586` | ~1 KB `.csv` files — per-file overhead / SMB stat pressure. |
| `--blob-files <n>` | `50` | High-byte, zero-symbol data blobs — parser cost / skip heuristics. |
| `--unresolved-includes <n>` | `0` | `.c` TUs that `#include` a vendor header absent from the tree, with a reference gated behind a macro only that header defines — the honest **negative** case (see manifest-schema.md). |
| `--compile-db none\|partial\|full` | `none` | Presence of `compile_commands.json`. `none` is the firmware norm (best-effort-clang trigger); `partial`/`full` are A/B controls. |
| `--build-output true\|false` | `true` | `.o`/`.lst`/`.bak` committed **beside** sources — exclusion rules & lexical noise. |
| `--dirs <n>` | `5700` | Approximate directory count — path handling. |
| `--depth <n>` | `8` | Approximate max path depth — long/deep paths. |
| `--linked-roots <n>` | `1` | Split output across N sibling trees — multi-root / federation. |
| `--manifest true\|false` | `true` | Emit the ground-truth manifest. |
| `--force` | off | Overwrite `--out` even if it exists and was not created by CodeSpawner. Without it, a non-empty directory CodeSpawner didn't create is protected from deletion. Never honoured for a drive/share root, the cwd or home dir (or a parent), a git checkout, or a well-known user/system folder. |

### Batch 1 pathology knobs (all OFF by default; see [ROADMAP.md](ROADMAP.md))

| Knob | Default | Property it stresses |
|---|---|---|
| `--dense-headers <n>` | `0` | Count of dense sub-threshold headers — maximally-unique idents, sized just under `--dense-under-mb`. Posting/trigram memory explosion under parallelism (the OOM shape). |
| `--dense-under-mb <m>` | `127` | Byte ceiling each dense header sits just under (aim just below the indexer's stream threshold, e.g. 128 MB → 127). |
| `--broad-token-files <n>` | `0` | Count of 2–8 MB carrier files for the hot token. Result capping/ranking + block-selective (sidecar) reads. |
| `--hot-token-share <f>` | `0.5` | Fraction of carriers that actually contain the hot token (rest are same-size controls). Token seeded at deterministic offsets incl. near-EOF; every site recorded under `broad_hot`. |
| `--long-line-files <n>` | `0` | Count of pathological single-long-line files (minified-JS shaped). Line-aligned block building / >2 GB string-materialization guard. |
| `--max-line-bytes <m>` | `8388608` | Bytes in the single giant line of each long-line file. |
| `--no-newline` | off | Force ALL long-line files to have no newline at all (default alternates newline / no-newline). |
| `--encoding-mix <n>` | `0` | Count of encoding-stress files: UTF-16LE/BE + BOM, UTF-8-BOM, invalid byte runs, non-ASCII identifiers. Multibyte trigram extraction / BOM handling. |
| `--pathological-symbols <n>` | `0` | Count of files with pathological symbol shapes: token-paste macros (`handler_##id` → **expectedMiss** honest-miss symbols), extreme-length identifiers, deep scope nesting. Symbol-extractor cost + honest-miss correctness. |
| `--dup-groups <n>` | `0` | Count of duplicate-content groups (byte-identical copies scattered across dirs + a near-identical control each). Content-hash dedup / segment-merge posting collapse. Recorded under top-level `dupGroups`. |
| `--dup-copies <n>` | `4` | Byte-identical copies per dup group (the set that must collapse). |
| `--shrink-seeds <n>` | `0` | Emit n ~9 MB sidecar-straddle headers (`mut_shrink_*.h`) — targets for `mutate` **4-shrink** (sidecar DELETE). |
| `--restream-seeds <n>` | `0` | Emit n ~129 MB headers (`mut_restream_*.h`) — targets for `mutate` **4-restream** (sidecar REWRITE). |

## `mutate` (incremental / watcher oracle)

`mutate` deterministically edits an existing corpus in place and emits **delta manifests** that compose with
the base (`truth = base ⊕ delta`). See [mutate-design.md](mutate-design.md). Guarded by the `.codespawner`
marker. Options: `--corpus <dir>`, `--seed <n>` (edit seed), `--edits <n>`, `--step <k>` (apply one edit →
`<corpus>-delta-{k}.json`, the chain driver), `--through` (all edits → one cumulative delta), `--restream`.
Generate the base with `--shrink-seeds`/`--restream-seeds` to enable the threshold-crossing edits.

**Bulk mode** (the diff oracle; `--target` switches it on — see [diff-delta-design.md](diff-delta-design.md)):

| Option | Default | What it does |
|---|---|---|
| `--target source\|headers\|giant\|all` | — | Population to change. Its presence selects bulk mode. |
| `--files-changed <n>` | all matching | How many target files to change (chosen deterministically from `--seed`). |
| `--edit-density <f>` | `0.05` | Fraction of each file's lines changed, 0..1. |
| `--giant-min-mb <n>` | `100` | Size floor for `--target giant`, and above which a `content` edit uses the giant-file path. |
| `--giant-edit strided\|single` | `strided` | Giant `content` edit: every stride-th line (one run-rule hunk), or one inserted line mid-file. |
| `--edit-kind <k>` | `content` | `content`, `line-insert`, `line-delete`, `whitespace`, `eol`, `encoding`, `binary`, `metadata`, `mixed` (one kind per file, cycling the six reasons), `rename`. |
| `--decoy-fraction <f>` | `0` | `rename` only: fraction of files copied as near-duplicate ADD decoys instead of renamed. |
| `--shard-size <n>` | `0` | `0` = one `<corpus>-delta.json`; `>0` = an index + shards of ≤ n modified records each. |
| `--three-way` | off | Leave the base pristine; emit `<corpus>_v1/` + `<corpus>_v2/`, their deltas and `<corpus>-conflict.json`. |
| `--overlap-fraction <f>` | `0.5` | 3-way: fraction of V2's edits that land on V1's lines (conflicts). |
| `--conflict-edges` | off | 3-way: the fixed edge-case layout (adjacent, modify/delete, add/add, identical-overlap) instead. |

## Generation performance

| Knob | Default | Property it stresses |
|---|---|---|
| `--io-parallelism <n>` | auto | Max concurrent large-header writes. Auto picks all cores on an SSD, 1 on a spinning disk, and min(cores, 4) when the disk type can't be detected (e.g. a network share); the choice is printed. |

## `gen --from-profile` and the oracle overlay

See [scan-design.md](scan-design.md) and [oracle-v1-design.md](oracle-v1-design.md).

| Knob | Default | What it does |
|---|---|---|
| `--from-profile <p>` | — | Regenerate a look-alike tree from a `scan` profile instead of the knobs above. |
| `--with-oracle` | off | Overlay the ground-truth call-graph spine and write a manifest. |
| `--oracle-scale` | off | Scale spine function bodies to the profile's measured `.c` size. |
| `--oracle-chain <n>` | `12` | `func_i` node budget. |
| `--oracle-fanout <n>` | `0` | Out-degree per node; >0 builds a seeded DAG instead of a linear chain. |
| `--oracle-depth <n>` | `0` | Cap on DAG depth (0 = sized by `--oracle-chain`). |
| `--oracle-shared-leaves <n>` | `0` | Shared sink nodes reached by several callers (diamonds). |
| `--oracle-reachable-frac <f>` | `0` | Target reachable fraction from `_meta.roots` (adds dead subgraphs). |
| `--oracle-indirect <n>` | `0` | Indirect edges (fnptr / vector-table / init_array) with `_meta.indirectTruthSha`. |
| `--oracle-bytes` | off | Per-symbol byte mass and `_meta.totalOracleBytes`. |

## Presets

| Preset | Expands to | Use |
|---|---|---|
| `--preset death` | `--scale 1.0 --giant-headers 12 --max-header-mb 1229 --big-headers 1375` | ~90 GB, ~50k files, 12 headers >1 GB — the full work-scale "repo of death". |
| `--preset ci` | `--scale 0.01` | Fast smoke tier; keeps ≥1 pathology header. |
| `--preset memory` | one giant header, 5 `.c` files of which 3 include it (`--giant-includers` default), everything else off, 1M macro density | Pure preprocessor-memory repro. |
| `--preset dense-band` | `--dense-headers 40 --dense-under-mb 127` (noise off) | Posting/trigram memory explosion — the OOM regression (~5 GB). |
| `--preset broad-token` | `--broad-token-files 200 --hot-token-share 0.5` (noise off) | `find_references`-at-scale + sidecar read amplification (~1 GB). |
| `--preset long-lines` | `--long-line-files 8 --max-line-bytes 8388608` (noise off) | Long-line / no-newline block-building stress. |
| `--preset encoding-mix` | `--encoding-mix 40` (noise off) | Multibyte / BOM / invalid-byte extraction. |
| `--preset many-tiny` | `--tiny-files 500000` (noise off) | Walker throughput / per-file & SMB stat pressure. |
| `--preset pathological-symbols` | `--pathological-symbols 50` (noise off) | Symbol-extractor cost + honest-miss (`expectedMiss`) correctness. |
| `--preset dup-content` | `--dup-groups 200 --dup-copies 4` (noise off) | Content-hash dedup / posting-collapse (`dupGroups`). |

Your own knobs after a `--preset` override it (e.g. `--preset death --giant-headers 4`).

## Examples

```powershell
# Fast correctness smoke (no giant headers): seconds.
codespawner gen --out .\_fw --scale 0.01 --giant-headers 0 --big-headers 0 --med-headers 0
codespawner verify --corpus .\_fw

# Memory-axis repro: one giant header + a .c that includes it.
codespawner gen --out .\_fw --preset memory

# Density-only stressor: 1M macros in a small file (memory without the bytes).
codespawner gen --out .\_fw --giant-headers 1 --macro-density 1000000 --max-header-mb 20

# Full work-scale repo of death (tens of GB — minutes).
codespawner gen --out D:\death --preset death
```
