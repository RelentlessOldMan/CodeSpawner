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

## Batch 2 — backlog (coordinated; some touch the manifest)

- **pathological-symbols (C-subset)** + **`expectedMiss`** — token-paste macros (`handler_##id`), extreme
  identifier length, deep scope nesting. Adds an optional per-symbol `expectedMiss` set = "a correct indexer
  is allowed/expected NOT to resolve this" (the honest-miss dual of `unreachableRefs`). Additive → v1.
- **dup-content** (`--dup-groups <n>`) — N groups of byte-identical / near-identical files scattered across
  dirs (vendored copies, generated variants). Manifest gains a `dupGroups` map (group → [paths]) so dedup /
  segment-merge posting collapse can be asserted against truth. Additive → v1.
- **C++ language profile** — nested templates, vtables/overrides, token-paste at C++ scale. This is the
  deferred language seam; it is the trigger for the formal **manifestVersion 2** bump.

## Longer-term — highest-value after the pathologies

- **`mutate` / churn** — `codespawner mutate --corpus X --edits N --seed S`: deterministically edit / add /
  remove files IN PLACE (guarded by the `.codespawner` marker, no clobber) and emit a **delta manifest**
  (added/removed/modified files + symbol-level def/ref/edge changes) that composes with the base so a
  consumer can assert *post-edit ground truth = base ⊕ delta*. This is the incremental-index / watcher /
  reconcile / sidecar-delete oracle CodeCompass currently can only eyeball. Gets its own design doc first.
