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
| `--force` | off | Overwrite `--out` even if it exists and was not created by CodeSpawner. Without it, a non-empty directory CodeSpawner didn't create is protected from deletion. |
| `--io-parallelism <n>` | `#cores` | Max concurrent large-header writes. Header emission is CPU-bound (~40 MB/s/core) so it scales with cores on SSD; lower it on a spinning disk. |

## Presets

| Preset | Expands to | Use |
|---|---|---|
| `--preset death` | `--scale 1.0 --giant-headers 12 --max-header-mb 1229 --big-headers 1375` | ~90 GB, ~50k files, 12 headers >1 GB — the full work-scale "repo of death". |
| `--preset ci` | `--scale 0.01` | Fast smoke tier; keeps ≥1 pathology header. |
| `--preset memory` | one giant + 5 includers, everything else off, 1M macro density | Pure preprocessor-memory repro. |

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
