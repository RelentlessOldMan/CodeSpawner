# CodeSpawner Roadmap

Catalogue of pathological corpus shapes, agreed with the CodeCompass session (2026-09-27, via the
`claudes-chatroom` relay). Each shape maps to a failure axis in a real code index/search/carve tool.

**Design invariants that hold for every item here:**
- **Determinism**: same `--seed` → byte-identical corpus + manifest, regardless of which knobs are set or
  core count. Each population draws from its own `Category` RNG stream (`Hash(seed, category, index)`).
- **Knobs-primary, presets = pure bundles.** Every pathology is a set of knobs; the named preset just
  expands to those knob tokens (and your own knobs after a preset still override it).
- **Manifest versioning rule**: *additive optional fields never bump `manifestVersion`; renames, removals,
  or semantic changes do.* New optional fields ship at v1; the next real v2 bump is reserved for the C++
  language profile, when the schema grows for real. Both consumers hard-assert `_meta.manifestVersion`.

---

## Batch 1 — shipping now (all deterministic, manifest-safe, no version change)

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
- ⏳ **C++ language profile** — nested templates, vtables/overrides, token-paste at C++ scale. Still the
  deferred language seam; it is the trigger for the formal **manifestVersion 2** bump.

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
CodeCarver doesn't need it. **v1.1 backlog: rename/move edit.**

- **`mutate` / churn** — `codespawner mutate --corpus X --edits N --seed S`: deterministically edit / add /
  remove files IN PLACE (guarded by the `.codespawner` marker, no clobber) and emit a **delta manifest**
  (added/removed/modified files + symbol-level def/ref/edge changes) that composes with the base so a
  consumer can assert *post-edit ground truth = base ⊕ delta*. This is the incremental-index / watcher /
  reconcile / sidecar-delete oracle CodeCompass currently can only eyeball. Gets its own design doc first.

## Consumer demand signals (2026-09-27)

Recorded so we build on real need, not speculation. Nothing below is green-lit; no speculative builds.

| Item | CodeCarver | CodeCompass | Decision |
|---|---|---|---|
| **mutate / churn** | Not useful — stateless batch carver, no watcher/incremental mode; "small change → small carve" is already covered by the determinism test + re-running (no cached state to go stale). | **Green-lit** — the one worth building: the incremental-index / watcher oracle CodeCompass currently cannot verify. | ✅ **NEXT** — build it. Gated on the death-run finishing so its result folds into the design. |
| **C++ profile** | **Would consume** — highest-leverage for it (vtable/override/template reachability is where carving is hardest; today only validated via ad-hoc real-repo build sweeps: tinyxml2/pugixml/fmt/simdjson, never a known C++ call graph). But not urgent, not worth the v2 bump yet. | Park. | ⏸ **PARKED** — revisit when C++ carving becomes a priority (triggers manifest v2). |
| **dup-near** | Not useful — content similarity is irrelevant to reachability. | Park. | ⏸ **PARKED**. |

**C++ profile — CodeCarver-side plan when green-lit (small):** extend `carver-groundtruth-oracle.ps1` to
carve `--lang cpp` and assert against the manifest's C++ `edges`, treating virtual/override targets as the
**sound over-approximation** set (a soundness-first carver keeps all possible dispatch targets). The
generator side would emit known vtable/override/template edges as ground truth, mirroring the C `func_i`
chain. This is the trigger to design the manifest v2 edge semantics for polymorphic dispatch.
