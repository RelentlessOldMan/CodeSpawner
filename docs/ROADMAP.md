# CodeSpawner Roadmap

Catalogue of pathological corpus shapes, agreed with the CodeCompass session (2026-09-27, via the
`claudes-chatroom` relay). Each shape maps to a failure axis in a real code index/search/carve tool.

**Design invariants that hold for every item here:**
- **Determinism**: same `--seed` → byte-identical corpus + manifest, regardless of which knobs are set or
  core count. Each population draws from its own `Category` RNG stream (`Hash(seed, category, index)`).
- **Knobs-primary, presets = pure bundles.** Every pathology is a set of knobs; the named preset just
  expands to those knob tokens (and your own knobs after a preset still override it).
- **Manifest versioning rule**: *additive optional fields never bump `manifestVersion`; renames, removals,
  or semantic changes do.* New optional fields ship at v1. The v2 bump came with the C++ language profile
  (1.2.0) and applies only to `gen --lang cpp`; C corpora stay v1. Both consumers hard-assert
  `_meta.manifestVersion`.

---

## Batch 1 — SHIPPED (v1.0.1; all deterministic, manifest-safe, no version change)

| Preset | Knobs | Failure axis it exercises |
|---|---|---|
| `dense-band` | `--dense-headers 40 --dense-under-mb 127` | Posting/trigram memory explosion: headers parked *just under* the indexer's stream threshold (128 MB) with maximally-unique identifiers, so the whole-file (non-streaming) path runs N-wide and blows a single-alloc heap ceiling. **This is the shape that caught CodeCompass's real OOM.** |
| `broad-token` | `--broad-token-files <n> --hot-token-share 0.5` | Result capping/ranking + network block-selective ("sidecar") reads. A hot shared token seeded into 0.5 of a **2–8 MB** carrier band (2 MB is CodeCompass's sidecar cutoff), a few times per file at deterministic offsets incl. near-EOF; every ref site recorded → a `find_references` expected-set AND a read-amplification test. |
| `long-lines` | `--long-line-files <n> --max-line-bytes <m>` [`--no-newline`] | Line-aligned block building; guards against >2 GB string materialization (minified JS, single-line JSON, blobs with no newline). |
| `encoding-mix` | `--encoding-mix <n>` | Trigram extraction on multibyte + BOM handling: UTF-16LE/BE, UTF-8-BOM, mixed/invalid byte runs, non-ASCII identifiers. |
| `many-tiny` | (bundles `--tiny-files 500000`, rest off) | Walker throughput / per-file overhead / SMB stat pressure. Already covered by the existing `--tiny-files` count knob — the preset just makes it a one-liner. |

**New `_meta.populations` stat block** (additive, v1): `{ "<population>": {fileCount, totalBytes, identCount} }`
so a consumer's verify adapter can assert the corpus has the intended *shape* before trusting any pass/fail.

Frozen regression defaults (agreed): `dense-under-mb 127` (threshold is 128 MB constant), `dense-headers 40`
(reliable margin over the ~36-file failure point, ~5 GB corpus), `hot-token-share 0.5`, carriers 2–8 MB.

---

## Batch 2 — SHIPPED (v1.0.2), plus one deferred item

- ✅ **pathological-symbols (C-subset)** + **`expectedMiss`** (`--pathological-symbols <n>`, preset
  `pathological-symbols`) — token-paste macros (`CS_MK_HANDLER(k)` → `patho{i}_handler_{k}`, flagged
  `expectedMiss`), extreme-length identifiers, deep scope nesting. Per-symbol optional `expectedMiss` bool =
  the honest-miss dual of `unreachableRefs`. Additive, stayed v1.
- ✅ **dup-content** (`--dup-groups <n> --dup-copies <n>`, preset `dup-content`) — N groups of byte-identical
  copies scattered across dirs + a near-identical control each. Top-level `dupGroups` map records each
  group's `sha256` + `paths` (must collapse) + `nearVariants` (must not). Additive, stayed v1.
- ✅ **C++ language profile** — shipped in v1.2.0 (see below); it brought **manifestVersion 2**.

### Batch 2.1 refinement (backlog)
- Near-identical dedup currently ships one control per group. A `--dup-near <n>` knob for a tunable
  non-collapse population could follow if CodeCompass wants to stress the near-miss boundary harder.

## `mutate` / churn — SHIPPED v1 (v1.0.4, 2026-09-28)

Design signed off in `docs/mutate-design.md` (golden vector verified byte-for-byte on both sides), built,
and released. `mutate` command: deterministic in-place edits emitting symbol-overlay delta manifests
(`truth = base ⊕ delta`). v1 edits: remove, line-shift modify, add, 4-grow (sidecar CREATE), 4-shrink
(sidecar DELETE, 9 MB→<2 MB), opt-in 4-restream (sidecar REWRITE). `--step k` chain / `--through` cumulative;
`baseManifestSha` + `prevTruthSha` chain binding; gen `--shrink-seeds`/`--restream-seeds` targets;
`digest-selftest` guards the canonical form. Validated: full bench 16/16, restream 129 MB→100 MB smoke,
prevTruthSha chains across `--step`. CodeCompass consumes it (watcher/reconcile/sidecar-delete);
CodeCarver doesn't need it. (The rename/move edit from the v1.1 backlog shipped in the diff-delta mode below.)

- **`mutate` / churn** — `codespawner mutate --corpus X --edits N --seed S`: deterministically edit / add /
  remove files IN PLACE (guarded by the `.codespawner` marker, no clobber) and emit a **delta manifest**
  (added/removed/modified files + symbol-level def/ref/edge changes) that composes with the base so a
  consumer can assert *post-edit ground truth = base ⊕ delta*. This is the incremental-index / watcher /
  reconcile / sidecar-delete oracle CodeCompass currently can only eyeball. Gets its own design doc first.

## diff-delta (bulk diff-oracle mutate) — SHIPPED (v1.1.0, 2026-10-02; extended through v1.1.12)

Built for CodeDiffer. Spec locked in `docs/diff-delta-design.md`. `mutate` bulk mode emits a base→variant
diff delta with hunk-level truth and `diffTruthSha`, `--edit-kind` reason classes, rename/move with graded
`similarityMilli` plus near-duplicate decoys, and native 3-way (`--three-way`, `conflictTruthSha`,
`--conflict-edges`). v1.1.12 added `gen --source-bodies varied` and a 20-milli rename margin, so every
recorded rename is the unique best match against the decoys.

## `scan` / shape-profile — SHIPPED v1 (v1.0.5, 2026-09-29)

Both consumers signed off; design frozen and BUILT. `scan <tree> --out profile.json` (read-only, 3 privacy
postures) → numbers-only profile; `gen --from-profile` regenerates a look-alike (shape replication +
`--with-oracle` spine + `--oracle-scale` body inflation). Code in `src/CodeSpawner/{Scan,Profile}` +
`Generation/{ProfileGenerator,ArchetypeSynthesizer,OracleOverlay}.cs`; bench gate added. Validated on the JIT
path (determinism, privacy-no-leak, round-trip class fidelity, oracle verify PASS); full AOT bench pending a
free machine (gated). **Backlog:** per-class sub-split of mixed clusters; C++ content classes; profile diff.

### Original design notes (retained)

Privacy-preserving **characterize → regenerate** round-trip: `scan <tree> --out profile.json` measures a
real tree's *shape and cost* (numbers + category labels only, no names/tokens/content ever stored), and
`gen --from-profile` rebuilds a generic look-alike. Default privacy posture: **balanced** (size/type/dir +
privacy-safe content stats, k-anonymity, `--no-content` opt-out). Fidelity: **shape replication + optional
`--with-oracle`** overlay (inject the ground-truth spine so verify/carve run at the real tree's cost/shape).
Design in `docs/scan-design.md`, folding in CodeCarver's cost dimensions (parsed-source volume as the
headline; header 3-bucket size hist; `#define`-% hist for headers ≥1 MB; **content-class fidelity** so a
10 MB inline-fn header regenerates as inline fns, not `#define`s — match content, not just bytes). Process:
design doc → loop CodeCompass + CodeCarver → build.

**CodeCompass sign-off (2026-09-29, `claudes-chatroom`):** shape approved; design updated with —
- **Two headlines**: `parsedSourceBytes` (clang/semantic sub-cost) + `totalIndexedBytes` (whole polyglot
  tree) so gen doesn't under-shoot index build/size.
- **Per-archetype trigram pair**: `distinctTrigramEstimate` (HLL cardinality = keys) + `trigramOccurrences`
  (≈ bytes = posting length) — both needed; on-disk size ≈ distinct × avg-posting-length.
- **Include fan-out** promoted to first-class: bounded 2-hop within-tree basename resolution,
  `unresolvedIncludeRate` (headline; the false-zero driver), `duplicateBasenameAmbiguity` (visible inflation).
- **New content-classes**: `data-blob` entropy split (high vs repetitive), `minified-longline`,
  `template-metaprogramming` (labeled now; gen degrades to inline-fn-heavy until C++ profile lands).
- **Privacy reframed to 3 postures**: `--structure-only` / **class-labeled (work-tree default)** /
  `--content-stats` (opt-in). Class enum is content-derived but content-free; survives everywhere but
  `--structure-only`.
- scan stat names mirror doctor/probe vocabulary.

**CodeCarver sign-off (2026-09-29, `claudes-chatroom`), thumbs UP:**
- Q1: `parsedSourceBytes` stays the headline but **class-decomposed** with a `heavy`/`cheap` flag,
  `inline-function-heavy` surfaced (the node-minting class; `#define`-dense is auto-skipped).
- Q2: `--with-oracle` spine defaults **compact**; opt-in size knob from the measured `.c` distribution
  inflates function BODIES only — spine + `expectedMiss` invariant (reachability never perturbed).
- **New axis (the gap CodeCarver caught):** peak graph RAM tracks **symbol/edge density**, not bytes. Added
  `symbolDensity` (`functionsPerKB`/`callsPerFunction`/`globalRefsPerFile`) — **coarse low/med/high in the
  class-labeled default** so the profile people actually run reproduces the memory failure mode; precise
  numerics under `--content-stats`; fixed+documented thresholds off the same single-pass content-class heuristic.

Both consumers signed off. Design frozen; built and shipped in v1.0.5.

## Ground-truth oracle v1 extension — SHIPPED (v1.0.8 + v1.0.9, 2026-09-30)

3-way design chat (`claudes-chatroom`: CodeSpawner ↔ CodeCarver ↔ CodeCompass) locked an **additive v1**
extension that makes carve **correctness** testable against the corpus for the first time. Full spec:
`docs/oracle-v1-design.md`. The gap: the `--with-oracle` spine was a linear chain, so precision was trivially
~100% and indirect edges (fn-pointers/vector-tables/init_array) — where every real carve bug lives — weren't
in the ground truth at all. Shipped in order **1+3 → 2 → 4** (all four phases in v1.0.8; v1.0.9 guarantees a reachable undispatched
indirect edge), all stay v1 (verifier/reader ignore unknown fields):

1. **Non-linear C call graph** — seeded DAG; `--oracle-fanout/-depth/-shared-leaves`; **default off = linear
   spine** (existing runs byte-identical); no schema change.
3. **Reachable-fraction dial** — `--oracle-reachable-frac`; new optional `_meta.roots` (emitted even for the
   linear default; undeclared root = hard error); reachability = closure over `(edges ∪ indirectEdges)`.
2. **Indirect-edge ground truth** — per-symbol `indirectEdges` `{target, via∈{fnptr|vector-table|init_array},
   dispatched, resolved}`, real C constructs emitted; separate `_meta.indirectTruthSha` component digest
   (frozen `7de5e47…` untouched) + a 2nd `digest-selftest` vector. `dispatched` splits soundness (keep all)
   from the counted indirection tax (reachable + undispatched).
4. **byte-mass** (optional, last) — per-symbol size + `_meta` total for the GB-reduction goal.

## C++ language profile + manifest v2 — SHIPPED (v1.2.0, 2026-10-14)

`gen --lang cpp`: a self-contained C++ program (namespaces, single + multiple inheritance, virtual / pure /
final, ctors/dtors, static objects, function and class templates, member-function pointers) with a v2
manifest: `virtualEdges` {static, cha, rta}, `implicitEdges`, `memberPtrEdges`, template instances, `pure`,
`kind: class` symbols, `staticInitRoots`, `_meta.classes`, `cppTruthSha`. Designed with and agreed by
CodeCarver + CodeCompass ([cpp-profile-design.md](cpp-profile-design.md)). `bench.ps1` compiles and links a
spread of cases under `clang++ -Werror`.

Follow-ups (backlog; each is a contract change, so build only when a consumer asks):
- Overloads on argument types (CodeCompass's first ask after 1.2.0), then operator overloading.
- Member-function pointers to VIRTUAL methods (D10's dispatch rule is written but not generated).
- Lambdas / `std::function` (CodeCarver: can wait).
- `mutate` for a C++ corpus (mutate refuses v2 today).

## Consumer demand signals (2026-09-27)

Recorded so we build on real need, not speculation. Nothing below is green-lit; no speculative builds.

| Item | CodeCarver | CodeCompass | Decision |
|---|---|---|---|
| **mutate / churn** | Not useful — stateless batch carver, no watcher/incremental mode; "small change → small carve" is already covered by the determinism test + re-running (no cached state to go stale). | **Green-lit** — the one worth building: the incremental-index / watcher oracle CodeCompass currently cannot verify. | ✅ **SHIPPED** v1.0.4; diff-delta mode for CodeDiffer followed in v1.1.0. |
| **C++ profile** | **Would consume** — highest-leverage for it (vtable/override/template reachability is where carving is hardest; today only validated via ad-hoc real-repo build sweeps: tinyxml2/pugixml/fmt/simdjson, never a known C++ call graph). | Park (adopted 2026-10-14 when the design came round). | ✅ **SHIPPED** v1.2.0 (above). |
| **dup-near** | Not useful — content similarity is irrelevant to reachability. | Park. | ⏸ **PARKED**. |

**C++ profile — CodeCarver-side plan (recorded 2026-09-27; v1.2.0 now provides it):** extend `carver-groundtruth-oracle.ps1` to
carve `--lang cpp` and assert against the manifest's C++ `edges`, treating virtual/override targets as the
**sound over-approximation** set (a soundness-first carver keeps all possible dispatch targets). The
generator side would emit known vtable/override/template edges as ground truth, mirroring the C `func_i`
chain. This is the trigger to design the manifest v2 edge semantics for polymorphic dispatch.
