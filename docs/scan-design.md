# `scan` / shape-profile — design doc (draft, for CodeCompass + CodeCarver review)

**Status:** design draft. Goal: a privacy-preserving **characterize → regenerate** round-trip. Run `scan`
against a real (proprietary) tree, get a numbers-only **shape profile**, carry it out, and
`gen --from-profile` rebuilds a generic look-alike that reproduces the tree's *cost and shape* — with
**zero proprietary information** in the profile. The profile is the direct machine interchange (no
re-describing).

## Commands

- `codespawner scan <tree> --out profile.json [--no-content] [--min-cluster N] [--sample N]`
  Read-only, offline. Never writes into `<tree>`. Emits a small JSON shape profile.
- `codespawner gen --from-profile profile.json --out <dir> [--seed S] [--with-oracle]`
  Synthesizes a generic corpus matching the profile. `--with-oracle` overlays the ground-truth spine.

## Privacy model (the whole point — must be airtight)

**Balanced is the default**: measure size/type/dir shape AND privacy-safe *content statistics*, storing only
numbers + enum category labels. The profile is a page you can read top-to-bottom and confirm it's clean.

**NEVER stored, ever:** file names, directory names, paths, identifiers, string/comment text, macro names or
values, literals, or any byte snippet. Content is read to COMPUTE statistics, then discarded.

**Only stored:** counts, histograms, ratios, distributions, and category labels (e.g. `define-dense`).

Hardening:
- **`--no-content`** — max-paranoia: structure/size/extension/dir only; never opens a file's bytes.
- **k-anonymity** (`--min-cluster N`, default e.g. 5) — archetypes with fewer than N files fold into an
  `other` bucket so a unique/identifying file can't be singled out.
- **Rounding / bucketing** — sizes and ratios are bucketed/rounded; no exact per-file values leak.
- **Sampling** (`--sample N`) — content stats from up to N files per (ext, size-band) group; bounds time
  and exposure on huge trees. Structure counts remain exact.
- Read-only; deterministic; offline. The output contains no secret by construction, and is small enough to
  eyeball before it ever leaves the machine.

## What `scan` measures

### Global + structure
- Total file count, total bytes.
- **Size histogram** (log buckets: <1 KB, 1–10 KB, … 100 MB–1 GB, >1 GB): count + bytes per bucket
  (captures the bimodal "90% of bytes in 2% of files").
- **Extension histogram**: count + bytes per extension, and **per-extension size distribution**.
- **Directory model**: dir count, depth histogram, fan-out (children/dir) histogram, files-per-dir histogram.

### Cost-relevant metrics (CodeCarver's dimensions — cost, not byte size)
- **Parsed-source volume** *(headline)*: total bytes of parseable source = `.c/.cpp` + headers under the
  streaming cap that are NOT define-dense. This is the parse-time predictor (~1 MB/s). The single number
  that matters most for a carver.
- **Header size histogram, 3 buckets**: `>20 MB`, `1–20 MB`, `<1 MB`.
- **`#define`-fraction histogram for headers ≥1 MB**: distribution of "% of lines that are `#define`" —
  tells a detector directly how many big headers are dense (cheap/skippable) vs. not (expensive).
- **Most-included proxy**: distribution of how often headers are `#include`d (measured as include-line
  counts pointing at same-basename headers — a generic proxy, no names stored), so the profile flags the
  "large AND heavily-included" headers that dominate preprocessing.

### File archetypes (the reusable unit)
Files are clustered by `(extension, size band, content class, feature vector)` into a handful of archetypes.
Each archetype = `{ label(generic), extension, count, sizeDistribution, contentModel }`. This is what
`gen --from-profile` consumes.

## Content classification — reproduce *what's inside*, not just the byte count

CodeCarver's trap: **matching sizes but not content**. A 10 MB header of `#define`s and a 10 MB header of
inline functions cost a carver wildly different amounts; only the latter still hurts. So every archetype
carries a **content class**, detected with generic, content-free line/token heuristics:

| Class | Detected by (generic, nothing stored) | Cost signal |
|---|---|---|
| `preprocessor-dense` | high `#define`-line fraction | cheap to parse, huge macro table (preprocessor memory) |
| `inline-function-heavy` | high density of `){…}` function-body shapes in a header | **expensive** parse |
| `enum-struct-table` | high `enum`/`struct`/`{…},` table-row density | moderate |
| `x-macro` | repeated `FOO(a,b)`-style macro-invocation lines | detector blind spot |
| `tabular-data` | delimiter-regular rows (csv-like) | cheap, count pressure |
| `generic-code` | mixed decl/stmt density | baseline |
| `data-blob` | high-byte, low-symbol numeric arrays | parser skip heuristics |
| `text` / `binary` | printable ratio / entropy | n/a |

Extra content features per archetype (numbers only): line count, avg/max line length, blank fraction,
comment fraction, `#include` fraction, **identifier-uniqueness ratio** (distinct/total tokens) and **avg
identifier length** (captures dense-unique register maps and long-ident pathologies — computed, then
discarded), encoding, BOM, newline style.

## Profile schema (sketch)

```json
{
  "_meta": { "profileVersion": 1, "scannedAt": "<date>", "minCluster": 5, "content": true },
  "totals": { "files": 51873, "bytes": 96500000000, "parsedSourceBytes": 175000000 },
  "sizeHistogram": [ { "bucket": "100MB-1GB", "files": 168, "bytes": 88000000000 }, … ],
  "extensions": [ { "ext": ".h", "files": 9500, "bytes": 90000000000 }, … ],
  "headers": {
    "sizeBuckets": { "gt20MB": 178, "1to20MB": 2600, "lt1MB": 7400 },
    "defineFractionHistogram_ge1MB": [ { "range": "0.9-1.0", "count": 178 }, … ]
  },
  "dirs": { "count": 5700, "depthHistogram": {…}, "fanoutHistogram": {…}, "filesPerDirHistogram": {…} },
  "archetypes": [
    { "label": "a1", "extension": ".h", "count": 178, "class": "preprocessor-dense",
      "sizeDistribution": { "p50": 115000000, "p90": 120000000, "max": 130000000 },
      "content": { "defineFrac": 0.98, "commentFrac": 0.01, "identUniqueRatio": 0.96,
                   "avgIdentLen": 22, "avgLineLen": 60, "encoding": "ascii", "newline": "lf" } },
    …
  ]
}
```

## `gen --from-profile`

For each archetype: mint `count` files of `extension`, sizes drawn from `sizeDistribution`, content
synthesized to the archetype's **class + feature vector** — e.g. `preprocessor-dense` emits generic
`#define`s at the measured density and identifier-uniqueness; `inline-function-heavy` emits generic inline
function bodies (so parse cost matches); `tabular-data` emits csv rows; etc. Files scatter into a
synthesized dir tree matching the depth/fan-out histograms. Deterministic via `--seed`. All identifiers,
paths, and content are generic and fake.

### Fidelity modes
- **Shape replication (default):** reproduces size/type/dir/content-class/cost. Generic content, no answer
  key. Answers "does my tool handle a tree shaped like this?"
- **`--with-oracle` (shape + oracle overlay):** also injects the ground-truth spine (`func_i` call chain,
  `hot_shared`, `vendor_gated`, `expectedMiss`) with a manifest, so `verify` and CodeCarver's
  soundness+precision oracle run *at the real tree's cost/shape*. Lets a carver catch correctness
  regressions that only appear at realistic scale/content — the high-value mode for CodeCarver/CodeCompass.

## Open questions for the consumers

1. **CodeCarver:** is `parsedSourceBytes` (=.c/.cpp + non-dense-headers-under-cap) the right headline, or
   do you also want inline-fn-header bytes broken out separately (since those are the expensive ones)?
2. **CodeCarver:** for `--with-oracle` at profile scale, should the call-graph archetype's SIZE track a real
   archetype (e.g. make `src_i.c` match the measured `.c` size distribution) so parse cost is realistic, or
   stay the current compact chain?
3. **CodeCompass:** does your `doctor` / `profile-repo.ps1` already emit a shape vocabulary we should adopt
   verbatim for the profile (so scan output and doctor speak the same language)?
4. **Both:** content-class taxonomy above — any class missing that changes YOUR cost (e.g. deeply-nested
   templates for a C++ front-end)?
5. **Privacy:** is the balanced default acceptable to run at work, or should the work-tree scan default to
   `--no-content` and opt IN to content stats?

## Scope

- **v1:** `scan` (balanced + `--no-content`), profile schema, `gen --from-profile` shape replication with the
  content-class synthesizer, `--with-oracle` overlay, determinism.
- **Later:** richer per-include graph modeling; C++-specific content classes; profile "diff" (compare two
  trees / drift over time).
