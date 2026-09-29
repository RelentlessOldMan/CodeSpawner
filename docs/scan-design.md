# `scan` / shape-profile — design doc (draft, for CodeCompass + CodeCarver review)

**Status:** design draft. Goal: a privacy-preserving **characterize → regenerate** round-trip. Run `scan`
against a real (proprietary) tree, get a numbers-only **shape profile**, carry it out, and
`gen --from-profile` rebuilds a generic look-alike that reproduces the tree's *cost and shape* — with
**zero proprietary information** in the profile. The profile is the direct machine interchange (no
re-describing).

## Commands

- `codespawner scan <tree> --out profile.json [--structure-only] [--content-stats] [--min-cluster N] [--sample N]`
  Read-only, offline. Never writes into `<tree>`. Emits a small JSON shape profile.
- `codespawner gen --from-profile profile.json --out <dir> [--seed S] [--with-oracle]`
  Synthesizes a generic corpus matching the profile. `--with-oracle` overlays the ground-truth spine.

## Privacy model (the whole point — must be airtight)

Three postures, resolved with CodeCompass (2026-09-29). The **class-labeled default** is the work-tree
default: it keeps the single most cost-driving signal (the content-class enum) while emitting no derived
content numbers, so the profile stays eyeball-clean as "labels + counts, nothing content-derived."

| Posture | Reads bytes? | Emits | Use |
|---|---|---|---|
| `--structure-only` | **No** | size/type/dir/structure only | max paranoia |
| **default (class-labeled)** | yes, to classify | structure + k-anonymized content-class **enum** per cluster; **no numeric content stats** | work-tree default |
| `--content-stats` (opt in) | yes | above + numeric histograms (defineFrac, identUniqueRatio, entropy, avgLineLen, …) | trusted trees / max fidelity |

This retires the old "balanced is the default." Balanced (numeric content stats) is now **opt-in**. The
class label survives everywhere except `--structure-only` — it's content-*derived* but content-*free* (an
enum, k-anonymized per cluster), and it's the part that most changes a consumer's cost.

**NEVER stored, ever (any posture):** file names, directory names, paths, identifiers, string/comment text,
macro names or values, literals, or any byte snippet. Content is read to COMPUTE stats/labels, then discarded.

**Only stored:** counts, histograms, ratios, distributions, and category labels (e.g. `preprocessor-dense`).

Hardening:
- **k-anonymity** (`--min-cluster N`, default e.g. 5) — archetypes with fewer than N files fold into an
  `other` bucket so a unique/identifying file can't be singled out.
- **Rounding / bucketing** — sizes and ratios are bucketed/rounded; no exact per-file values leak.
- **Sampling** (`--sample N`) — content stats/labels from up to N files per (ext, size-band) group; bounds
  time and exposure on huge trees. Structure counts remain exact.
- Read-only; deterministic; offline. The output contains no secret by construction, and is small enough to
  eyeball before it ever leaves the machine.

## What `scan` measures

### Global + structure
- Total file count, total bytes.
- **Size histogram** (log buckets: <1 KB, 1–10 KB, … 100 MB–1 GB, >1 GB): count + bytes per bucket
  (captures the bimodal "90% of bytes in 2% of files").
- **Extension histogram**: count + bytes per extension, and **per-extension size distribution**.
- **Directory model**: dir count, depth histogram, fan-out (children/dir) histogram, files-per-dir histogram.

### Cost-relevant metrics (consumer dimensions — cost, not byte size)

**Two headlines, not one** (CodeCompass, 2026-09-29): `parsedSourceBytes` captures the clang/semantic
sub-cost, but a whole-tree indexer's total cost is driven more by total bytes + trigram density + symbol
extraction. So the profile carries a parse headline AND an index headline.

- **`parsedSourceBytes`** *(parse headline)*: total bytes of parseable source = `.c/.cpp` + headers under the
  streaming cap that are NOT define-dense. Parse-time predictor (~1 MB/s). The number that matters most for a
  carver / semantic sub-cost.
- **`totalIndexedBytes`** *(index headline)*: total bytes the indexer ingests across the whole polyglot tree
  (not just parsed C/C++), so gen doesn't under-shoot index build/size.
- **Trigram cost, per-archetype (+ global roll-up)** — reproduces index size + merge/flush cadence:
  - `distinctTrigramEstimate` — HLL cardinality of byte 3-grams = distinct-key count. One HLL per archetype,
    zero content stored.
  - `trigramOccurrences` — total 3-gram occurrence count (≈ bytes) = posting-list length. Cardinality alone
    under-models the repetitive-blob case (few keys, millions of positions each); on-disk size ≈
    distinctTrigrams × avg-posting-length, so both numbers are required.
- **Header size histogram, 3 buckets**: `>20 MB`, `1–20 MB`, `<1 MB`.
- **`#define`-fraction histogram for headers ≥1 MB** *(content-stats posture only)*: distribution of "% of
  lines that are `#define`" — how many big headers are dense (cheap/skippable) vs. not (expensive).
- **Include fan-out / unresolved-include risk** *(first-class — drives TU parse cost + false-zero/coverage
  more than `#define`-fraction alone)*: bounded **2-hop** include expansion resolved within-tree by basename
  only (one-hop + unresolved-rate is the guaranteed floor if a tree is pathological). Emits distributions
  only — a fan-out histogram, an **`unresolvedIncludeRate`** (the headline number; system/vendor `<foo.h>`
  genuinely outside the tree, the real false-zero driver), and a **`duplicateBasenameAmbiguity`** count so the
  "reported-resolved but the real `-I` pick differs" inflation is visible rather than silent. No names/paths.

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
| `template-metaprogramming` (C++) | high `template<`/nested-angle density in a header | **expensive** — libclang parse blows up; own class beyond inline-fn |
| `tabular-data` | delimiter-regular rows (csv-like) | cheap, count pressure |
| `minified-longline` | few lines, MB-long lines | changes line-scan + block-index path; must not hide in `generic-code` |
| `generic-code` | mixed decl/stmt density | baseline |
| `data-blob-high-entropy` | high-byte, low-symbol + high trigram entropy (hex/base64 dumps) | **balloons** trigram index build + on-disk size |
| `data-blob-repetitive` | high-byte, low-symbol + low trigram entropy | cheap (low cardinality) |
| `text` / `binary` | printable ratio / entropy | n/a |

Two of these came from CodeCompass (2026-09-29): the `data-blob` entropy split is an index-cost axis
orthogonal to parse cost; `minified-longline` and `template-metaprogramming` are cost paths that
`generic-code`/`inline-function-heavy` would otherwise mask. `template-metaprogramming` is **labeled by scan
now**; `gen --from-profile` reproduces it only once the C++ profile lands (parked) — until then it degrades
to `inline-function-heavy` with a logged note so parse cost is at least in the right ballpark.

Extra content features per archetype (numbers only): line count, avg/max line length, blank fraction,
comment fraction, `#include` fraction, **identifier-uniqueness ratio** (distinct/total tokens) and **avg
identifier length** (captures dense-unique register maps and long-ident pathologies — computed, then
discarded), encoding, BOM, newline style.

## Profile schema (sketch)

```json
{
  "_meta": { "profileVersion": 1, "scannedAt": "<date>", "minCluster": 5, "posture": "class-labeled" },
  "totals": { "files": 51873, "bytes": 96500000000,
              "parsedSourceBytes": 175000000, "totalIndexedBytes": 41000000000,
              "distinctTrigramEstimate": 210000000, "trigramOccurrences": 40800000000 },
  "sizeHistogram": [ { "bucket": "100MB-1GB", "files": 168, "bytes": 88000000000 }, … ],
  "extensions": [ { "ext": ".h", "files": 9500, "bytes": 90000000000 }, … ],
  "headers": {
    "sizeBuckets": { "gt20MB": 178, "1to20MB": 2600, "lt1MB": 7400 },
    "defineFractionHistogram_ge1MB": [ { "range": "0.9-1.0", "count": 178 }, … ]
  },
  "includes": {
    "fanoutHistogram": { "0-4": 3100, "5-16": 5200, "17-64": 1050, "65+": 150 },
    "unresolvedIncludeRate": 0.11,
    "duplicateBasenameAmbiguity": 240,
    "hops": 2
  },
  "dirs": { "count": 5700, "depthHistogram": {…}, "fanoutHistogram": {…}, "filesPerDirHistogram": {…} },
  "archetypes": [
    { "label": "a1", "extension": ".h", "count": 178, "class": "preprocessor-dense",
      "sizeDistribution": { "p50": 115000000, "p90": 120000000, "max": 130000000 },
      "trigram": { "distinctEstimate": 4200000, "occurrences": 20800000000 },
      "content": { "defineFrac": 0.98, "commentFrac": 0.01, "identUniqueRatio": 0.96,
                   "avgIdentLen": 22, "avgLineLen": 60, "encoding": "ascii", "newline": "lf" } },
    …
  ]
}
```

Note: the `content` block is present only under `--content-stats`; the class-labeled default emits everything
else (including per-archetype `trigram` and `class`), but drops that numeric `content` object.

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

**CodeCompass — RESOLVED 2026-09-29 (via `claudes-chatroom`):**
- Q3 (shape vocabulary): no frozen schema to copy, but scan mirrors doctor/probe names where they measure the
  same thing — `candidateTUs`/`parsedTUs`/`unresolvedIncludes`/`memoryStopped`, `filesOverCap`/
  `filesSymbolSkipped`, sidecar boundary at 8 MB, probe's `peakRSS`/tail-slope/semantic-vs-lexical/
  determinism.
- Q4 (missing classes): added `data-blob` entropy split, `minified-longline`, `template-metaprogramming`;
  include fan-out promoted to a first-class stat.
- Q5 (privacy): three postures, **class-labeled is the work-tree default**; numeric content stats are opt-in
  (`--content-stats`).
- Also folded in: two headlines (`parsedSourceBytes` + `totalIndexedBytes`), per-archetype trigram pair
  (`distinctEstimate` + `occurrences`), 2-hop include fan-out with `unresolvedIncludeRate` +
  `duplicateBasenameAmbiguity`.

**CodeCarver — STILL OPEN (not yet in the room):**
1. Is `parsedSourceBytes` (=.c/.cpp + non-dense-headers-under-cap) the right headline, or do you also want
   inline-fn-header bytes broken out separately (since those are the expensive ones)?
2. For `--with-oracle` at profile scale, should the call-graph archetype's SIZE track a real archetype (e.g.
   make `src_i.c` match the measured `.c` size distribution) so parse cost is realistic, or stay the current
   compact chain?

## Scope

- **v1:** `scan` (balanced + `--no-content`), profile schema, `gen --from-profile` shape replication with the
  content-class synthesizer, `--with-oracle` overlay, determinism.
- **Later:** richer per-include graph modeling; C++-specific content classes; profile "diff" (compare two
  trees / drift over time).
