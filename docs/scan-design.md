# Scan and profiles

`scan` and `gen --from-profile` are a privacy-preserving **characterize → regenerate** round-trip. Run `scan`
against a real (proprietary) tree, get a numbers-only **shape profile**, carry it out, and
`gen --from-profile` rebuilds a generic look-alike that reproduces the tree's *cost and shape* — with
**zero proprietary information** in the profile. The profile is the direct machine interchange (no
re-describing).

## Commands

- `codespawner scan <tree> --out profile.json [--structure-only] [--content-stats] [--min-cluster N] [--sample N]`
  Read-only, offline. Never writes into `<tree>`. Emits a small JSON shape profile. Files it cannot read
  (and the contents of directories it cannot list) are left out, and the run prints how many.
- `codespawner gen --from-profile profile.json --out <dir> [--seed S] [--with-oracle] [--oracle-scale] [--oracle-chain N]`
  Synthesizes a generic corpus matching the profile. `--with-oracle` overlays the ground-truth spine.

The code lives in `src/CodeSpawner/Scan/` (`Scanner`, `ContentClassifier`, `TrigramMeter`, `IncludeAggregate`),
`src/CodeSpawner/Profile/` (model, reader, posture-gated streaming writer) and
`src/CodeSpawner/Generation/` (`ProfileGenerator`, `ArchetypeSynthesizer`, `OracleOverlay`).

## Privacy model (the whole point — must be airtight)

Three postures. The **class-labeled default** is meant for private trees: it keeps the single most cost-driving signal (the content-class enum) while emitting no derived
content numbers, so the profile stays eyeball-clean as "labels + counts, nothing content-derived."

| Posture | Reads bytes? | Emits | Use |
|---|---|---|---|
| `--structure-only` | **No** | size/type/dir/structure only | max paranoia |
| **default (class-labeled)** | yes, to classify | structure + k-anonymized content-class **enum** per cluster; **no numeric content stats** | default (private trees) |
| `--content-stats` (opt in) | yes | above + numeric histograms (defineFrac, identUniqueRatio, entropy, avgLineLen, …) | trusted trees / max fidelity |

Numeric content stats are **opt-in**. The class label survives everywhere except `--structure-only` — it's content-*derived* but content-*free* (an
enum, k-anonymized per cluster), and it's the part that most changes a consumer's cost.

**NEVER stored, ever (any posture):** file names, directory names, paths, identifiers, string/comment text,
macro names or values, literals, or any byte snippet. Content is read to COMPUTE stats/labels, then discarded.

**Only stored:** counts, histograms, ratios, distributions, and category labels (e.g. `preprocessor-dense`).

Hardening:
- **k-anonymity** (`--min-cluster N`, default 5) — archetypes with fewer than N files fold into an
  `other` bucket so a unique/identifying file can't be singled out. The extension histogram is folded the
  same way: an extension fewer than N files share is counted under `(other)`, and so is any suffix that
  isn't extension-shaped (`^\.[a-z0-9_+-]{1,16}$`), since it is part of a file name. A dotfile (`.name`, all
  name) counts as `(dotfile)`. An extension outside the common C/C++/build set must also appear in at least
  3 directories: a suffix confined to fewer is more likely a person's or project's name
  (`notes.jsmith`) than a file type, so it folds into `(other)`.
- **Rounding / bucketing** — sizes and ratios are bucketed/rounded; no exact per-file values leak.
  Archetype p50/p90/max (each one file's size) are rounded to 3 significant digits.
- `--out` may not be inside the scanned tree, and `--structure-only` with `--content-stats` is refused
  rather than letting the last one win.
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

**Two headlines, not one:** `parsedSourceBytes` captures the clang/semantic
sub-cost, but a whole-tree indexer's total cost is driven more by total bytes + trigram density + symbol
extraction. So the profile carries a parse headline AND an index headline.

- **`parsedSourceBytes`** *(parse headline, class-decomposed)*: total bytes of parseable source = `.c/.cpp` +
  headers under the streaming cap that are NOT define-dense. Parse-time predictor (~1 MB/s). Reported as ONE
  headline **decomposed by content class**, each class carrying a `heavy`/`cheap` flag — `inline-function-heavy`
  surfaced explicitly (to a carver, `#define`-dense is auto-skipped and mints no graph nodes, but
  inline-fn parses in full and mints real function/call nodes, so its bytes are the single most predictive
  number for the blowup; `enum-struct-table` + `template-metaprogramming` also carry `heavy`). Not a second
  free-floating scalar — a decomposition of the one headline.
- **`symbolDensity` per archetype** *(graph-RAM axis — peak memory, not parse time)*: `functionsPerKB`,
  `callsPerFunction`, `globalRefsPerFile`. A carver's typical blowup is a NODE-count explosion; peak graph RAM
  tracks symbol/edge density, so two trees with equal `parsedSourceBytes` but 10× call density cost very
  different memory. Emitted as a **coarse `low`/`med`/`high` ordinal in the class-labeled DEFAULT** (a 3-way
  ordinal leaks no more than the content-class label — no names/tokens — so it's consistent with the default
  posture and reproduces the primary failure mode on the profile people actually run); precise numerics gated
  behind `--content-stats`. Bucket thresholds are **fixed** so a coarse profile still regenerates
  a tree in the right RAM ballpark, and are read off the **same single-pass heuristic** that assigns the
  content class (density + class derived from one read). `low` is below the first threshold, `high` at or
  above the second: `functionsPerKB` 0.5 / 2.0, `callsPerFunction` 1 / 5, `globalRefsPerFile` 100 / 1000.
- **`totalIndexedBytes`** *(index headline)*: total bytes the indexer ingests across the whole polyglot tree
  (not just parsed C/C++), so gen doesn't under-shoot index build/size.
- **Trigram cost, per-archetype (+ global roll-up)** — reproduces index size + merge/flush cadence:
  - `distinctTrigramEstimate` — distinct byte 3-grams = distinct-key count, counted exactly with a 2 MB
    presence bitset (one bit per possible trigram). One per archetype plus a whole-tree union, zero content
    stored.
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

The trap is **matching sizes but not content**. A 10 MB header of `#define`s and a 10 MB header of
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

The `data-blob` entropy split is an index-cost axis orthogonal to parse cost; `minified-longline` and
`template-metaprogramming` are cost paths that `generic-code`/`inline-function-heavy` would otherwise mask.
`template-metaprogramming` is **labeled by scan**, but `gen --from-profile` does not synthesize it: it degrades
to `inline-function-heavy` with a logged note, so parse cost is at least in the right ballpark.

Extra content features per archetype (numbers only): line count, avg/max line length, blank fraction,
comment fraction, `#include` fraction, **identifier-uniqueness ratio** (distinct/total tokens) and **avg
identifier length** (captures dense-unique register maps and long-ident pathologies — computed, then
discarded), encoding, BOM, newline style.

## Profile schema (sketch, illustrative values)

```json
{
  "_meta": { "profileVersion": 1, "scannedAt": "<date>", "minCluster": 5, "posture": "class-labeled" },
  "totals": { "files": 50000, "bytes": 100000000000,
              "parsedSourceBytes": 200000000, "totalIndexedBytes": 40000000000,
              "distinctTrigramEstimate": 200000000, "trigramOccurrences": 40000000000 },
  "sizeHistogram": [ { "bucket": "100MB-1GB", "files": 150, "bytes": 90000000000 }, … ],
  "extensions": [ { "ext": ".h", "files": 10000, "bytes": 90000000000 }, … ],
  "headers": {
    "sizeBuckets": { "gt20MB": 150, "1to20MB": 2500, "lt1MB": 7500 },
    "defineFractionHistogram_ge1MB": [ { "range": "0.9-1.0", "count": 150 }, … ]
  },
  "includes": {
    "fanoutHistogram": { "0-4": 3100, "5-16": 5200, "17-64": 1050, "65+": 150 },
    "unresolvedIncludeRate": 0.11,
    "duplicateBasenameAmbiguity": 240,
    "hops": 2
  },
  "dirs": { "count": 5700, "depthHistogram": {…}, "fanoutHistogram": {…}, "filesPerDirHistogram": {…} },
  "archetypes": [
    { "label": "a1", "extension": ".h", "count": 150, "class": "preprocessor-dense", "parseCost": "cheap",
      "sizeDistribution": { "p50": 115000000, "p90": 120000000, "max": 130000000 },
      "trigram": { "distinctEstimate": 4200000, "occurrences": 20800000000 },
      "symbolDensity": { "functionsPerKB": "low", "callsPerFunction": "low", "globalRefsPerFile": "high" },
      "content": { "defineFrac": 0.98, "commentFrac": 0.01, "identUniqueRatio": 0.96,
                   "avgIdentLen": 22, "avgLineLen": 60, "encoding": "ascii", "newline": "lf",
                   "symbolDensityExact": { "functionsPerKB": 0.02, "callsPerFunction": 0.3, "globalRefsPerFile": 900 } } },
    …
  ],
  "parsedSourceByClass": [ { "class": "inline-function-heavy", "bytes": 62000000, "parseCost": "heavy" },
                           { "class": "preprocessor-dense", "bytes": 0, "parseCost": "cheap" }, … ]
}
```

Note: the `content` block (and `symbolDensityExact`) is present only under `--content-stats`; the class-labeled
default emits everything else — including per-archetype `class`, `parseCost`, `trigram`, and the COARSE
`symbolDensity` ordinal — but drops the numeric `content` object.

## `gen --from-profile`

For each archetype: mint `count` files of `extension`, sizes drawn from `sizeDistribution`, content
synthesized to the archetype's **class + feature vector** — e.g. `preprocessor-dense` emits generic
`#define`s at the measured density and identifier-uniqueness; `inline-function-heavy` emits generic inline
function bodies (so parse cost matches); `tabular-data` emits csv rows; etc. Files scatter into a
synthesized dir tree matching the depth histogram, weighted so the files-per-dir histogram comes back too
(each directory is dealt a bucket in the measured proportions). Per-class synthesizers emit content that
re-classifies to the same class (round-trip stable), each file ending at the line boundary nearest its drawn
size, never below the least that still re-classifies (66 bytes for generic code, 119 for inline-heavy).
Deterministic via `--seed`. A scan of the same tree gives the same profile apart from `scannedAt`. All
identifiers, paths, and content are generic and fake.

### Fidelity modes
- **Shape replication (default):** reproduces size/type/dir/content-class/cost. Generic content, no answer
  key. Answers "does my tool handle a tree shaped like this?"
- **`--with-oracle` (shape + oracle overlay):** also injects the ground-truth spine (`func_i` call chain,
  `hot_shared`, `vendor_gated`, `expectedMiss`) with a manifest, so `verify` and a carver's
  soundness+precision oracle (CodeCarver's, for example) run *at the real tree's cost/shape*. Lets a carver or
  indexer catch correctness regressions that only appear at realistic scale/content.
  **Spine size:** default **COMPACT** — small correctness-focused files, so the
  constantly-run correctness tier stays fast. An opt-in **size knob** drives the call-graph archetype's file
  size from the SAME measured `.c` distribution from scan (not an arbitrary N) for periodic scale runs; that knob
  is `--oracle-scale`. **Hard
  constraint:** the size knob inflates function BODIES ONLY (more intra-fn statements / generic filler carrying
  the measured `symbolDensity`) and adds ZERO call edges / defs / refs — the `func_i` spine + `expectedMiss`
  stay invariant to size, so the reachability the oracle exists to check is never perturbed. Size and spine are
  orthogonal by construction.

## Limitations

- Clusters are `(ext, size-band)` with the modal sampled class; a mixed cluster is not split per class.
  Counts stay exact.
- `distinctTrigramEstimate` is measured on the sampled prefixes (≤4 MB/file); `occurrences` is exact from
  full byte counts. 2-hop include fan-out expands only over sampled files' include maps (one-hop +
  `unresolvedIncludeRate` is the floor for unsampled targets).
