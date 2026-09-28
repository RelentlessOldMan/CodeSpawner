# `mutate` / churn — design doc (v1, for review)

**Status:** design for CodeCompass sign-off. No code until this is approved. Agreed shape from the
`claudes-chatroom` design session (2026-09-28).

## Why

Everything CodeSpawner emits today tests a **static** index. CodeCompass's incremental `update`, file
watcher, reconcile pass, and sidecar-delete / orphan-cleanup have **no ground-truth test** — they're
eyeballed. `mutate` deterministically edits a corpus in place and emits a **delta manifest** that composes
with the base (`post-edit truth = base ⊕ delta`), turning "looks right after an edit" into a pass/fail
assertion. CodeCompass is the consumer; CodeCarver (stateless batch carver) does not need it.

## Command surface

```
codespawner mutate --corpus <dir> --seed <S> --edits <N> [--step <k>] [--through]
```

- **In place.** Operates on an existing corpus; **guarded by the `.codespawner` marker** — refuses any dir
  CodeSpawner didn't create (never `--force`-clobbers here; mutate only edits its own corpus).
- Reads the base `<corpus>-manifest.json` to know ground truth, then selects **N distinct target files**
  deterministically from `(S, k)` (distinct so edit order never conflicts).
- **`--step k`** — apply exactly edit `k` to the current on-disk state; emit `<corpus>-delta-{k}.json`
  (state_{k-1} → state_k, **prior-state relative**). This is the CHAIN driver.
- **`--through`** — apply all N edits; emit one cumulative `<corpus>-delta.json` (base → final). The
  batch / reconcile-in-one-pass case.
- **Determinism:** same `(corpus, N, S)` → byte-identical mutation + delta. Edit `k` is a pure function of
  `(S, k, targetFile[k])`; targets are chosen up front from S.
- **Fresh mtime is a hard contract:** every edited / added / renamed file gets a natural OS mtime bump
  (no preserve/restore). CodeCompass's quick-update + watcher paths are mtime-gated, so this is the whole
  point — a byte change under a stale mtime would (correctly) be missed by the incremental path. Remove
  drops the mtime with the file.

## Edit taxonomy

Ranked by how much each stresses a real incremental indexer (top = where incrementals silently rot).

| # | Edit | v1? | What it catches |
|---|---|---|---|
| 1 | **remove** a file others reference | ✅ v1 | Stale entries lingering: reconcile must drop the file's symbols AND every ref site located in it. Absence assertion. |
| 3 | **modify (line-shift)** — insert lines above a symbol | ✅ v1 | Stale line numbers: symbol still exists at a new `path:line`; ripples to every symbol with a site in the file. |
| 5 | **add** a new file | ✅ v1 | Basic incremental pickup. |
| 4-grow | grow a small file **past the 8 MB sidecar threshold** | ✅ v1 | Sidecar **CREATE** path. |
| 4-shrink | shrink a straddle file **under the 128 MB stream threshold** | ✅ v1 | Sidecar **DELETE** / orphan-cleanup — CodeCompass's riskiest untested path. Needs the straddle seed (below). |
| 2 | **rename / move** | v1.1 | FS-level remove+add; a naive incremental keeps BOTH paths. |

### threshold-straddle seed (enables 4-shrink in v1)

4-shrink needs a deterministic, already-indexed file straddling 128 MB. A gen option emits one deterministic
**~129 MB** file (just over the stream threshold) into the base corpus so it gets a sidecar; mutate's
4-shrink truncates it under 128 MB and CodeCompass's orphan-cleanup must delete the now-orphaned sidecar.
(4-grow needs no seed — mutate appends filler to any small file to cross 8 MB.)

## Delta manifest schema

Sibling file, same convention as `<corpus>-manifest.json`:

```json
{
  "_meta": {
    "manifestVersion": 1,
    "baseSeed": 1337,
    "editSeed": 42,
    "step": 3,
    "baseManifestSha": "<sha256 of the base manifest this delta composes against>"
  },
  "fileOps": {
    "added":    ["blockA/.../new_7.c"],
    "removed":  ["blockB/.../src_5.c"],
    "renamed":  [{ "from": "…/src_9.c", "to": "…/moved_9.c" }],
    "modified": ["blockC/.../src_3.c"]
  },
  "symbols": {
    "func_5":  "TOMBSTONE",
    "func_4":  { "def": "…/src_4.c:10", "refs": ["…"], "edges": ["func_3"], "removedSites": ["…/src_5.c:412"] },
    "func_3":  { "def": "…/src_3.c:20", "refs": ["…/src_4.c:12"], "edges": ["func_2"] }
  }
}
```

- **`_meta`** — `manifestVersion` stays **1** (the base schema is untouched; the delta is a new additive
  artifact). `baseManifestSha` binds a delta to the exact base it composes against, so it can't be misapplied.
- **`fileOps`** — the FS-effect checklist. Composition never needs it; it drives CodeCompass's orphan-cleanup
  / old-path-gone asserts AND doubles as the changed-paths to replay as synthetic FS events into the watcher.
  mutate emits **no** changed-list to `update` itself — that reconcile is the thing under test.
- **`symbols`** — the churn set only: every symbol whose def/refs/edges changed, as its full new entry, or
  the string `"TOMBSTONE"` if removed. Optional **`removedSites`** per symbol lists ref sites that vanished
  (point-precise LEAK asserts: "this `path:line` must be gone from `find_references(X)`"), reusing the
  existing negative-oracle machinery.

### The ripple invariant (why symbol-keyed, not path-keyed)

> **Any symbol with a def OR ref site physically in the touched file changes** — even symbols *defined*
> elsewhere.

A ref site is recorded at the CALL site, in the caller's file. So editing `src_5.c` moves `func_5.def`
(defined there), `func_4`'s ref site (the call to func_4 lives in src_5), and `hot_shared`'s ref site if
src_5 is a giant-includer. All three are listed in `symbols`. Path-keying by def location cannot express
`func_4`/`hot_shared` changes; symbol-keying does, with no special cases.

### Composition rule (the adapter's `base ⊕ delta`)

```
truth.symbols = copy(base.symbols)
for (name, entry) in delta.symbols:
    if entry == "TOMBSTONE": truth.symbols.remove(name)
    else:                    truth.symbols[name] = entry     # add or replace
```

A trivial dict overlay — no ripple special-casing. Chain: `truth_k = truth_{k-1} ⊕ delta_k`.

## Adapter flow (CodeCompass side)

**Chain (incremental) test:**
```
index base corpus
truth = base
for k in 1..N:
    codespawner mutate --corpus X --seed S --edits N --step k   # applies edit k, emits delta_k
    truth = truth ⊕ delta_k
    codecompass update X                                        # incremental; self-diffs (under test)
    assert live_index == truth                                  # + fileOps: orphaned sidecars gone, old paths absent, removedSites not in find_references
```

**Batch (reconcile-in-one-pass) test:** `mutate --through` → one `update` → assert against base ⊕ delta.

**Watcher test:** replay `fileOps` as synthetic FS events into the targeted-update path; assert same
composed truth.

## Determinism guarantees

- Target files chosen up front from `editSeed` (N distinct); edit `k` is pure in `(S, k, target[k])`.
- Same `(corpus, N, S)` ⇒ identical edits, identical delta bytes (path-normalized), identical mtimes-ordering
  is irrelevant (each file gets its own fresh mtime; determinism is over CONTENT + the manifest, not wall-clock
  timestamps — timestamps are never written into the delta).
- New RNG `Category.Mutate` (appended; existing streams unchanged).

## Out of v1 scope

- Recomputing `_meta.populations` / `dupGroups` after edits (edits target symbol-bearing source files; the
  delta focuses on `symbols` + `fileOps`). The doc will state populations may drift post-mutate and are not
  asserted in v1.
- rename/move (2) → v1.1.

## Open implementation notes (not blocking sign-off)

- **Line-shift edit** inserts a deterministic block of filler lines above the target symbol's def; recompute
  every in-file site's new line from the insertion point.
- **remove** must scan the base manifest for any symbol with a site in the removed file and emit its updated
  entry (or tombstone if defined there) — this is the ripple set.
- **baseManifestSha** is computed over the on-disk base manifest bytes at mutate time; the adapter checks it
  before composing.
- mutate updates `<corpus>-manifest.json` in place too? **No** — the base manifest stays the immutable base;
  deltas are the record of change. (A `--rebase` to fold deltas into a new base manifest could be a later
  convenience.)
