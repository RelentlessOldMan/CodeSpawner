# diff-delta — design doc (LOCKED contract for the large-repo diff tool)

**Status:** contract **locked** with the CodeDiffer session in the `code-spawner` chat channel (2026-10-02,
`stop_reason: agreed`). This is the ground-truth shape CodeDiffer's 2-/3-way diff tool asserts against. It
extends bulk-mode `mutate` (see [mutate-design.md](mutate-design.md) → "Bulk mode (diff-oracle)"); the
file-level truth there is step 0, this doc freezes the hunk/rename/3-way truth that was deferred.

## Consumer & contract basis

- **Consumer:** CodeDiffer — a diff tool for very large repos. Operates at **LINE/HUNK** granularity, with a
  **byte/metadata summary** track for binary files. Structural/syntactic diff is a deferred v2 that would
  reuse the existing symbol/edges truth — **not** in scope here.
- **Patch-applicable:** CodeDiffer's output is unified diffs verified against `git apply`/`patch`. So every
  hunk below is emitted in **1-based unified-diff coordinates** and must materialize to an applyable patch.
- **Honesty contract (CodeDiffer's headline bug class):** never report "modified" with no reason / empty
  diff. That is why the **`reason` label is itself ground truth** — a reason-flip must be a test failure, so
  `reason` is digested (below), not just the hunks.

## Command surface (extends bulk mode)

```
codespawner mutate --corpus <dir> --target <source|headers|giant|all>
                   [--files-changed N] [--edit-density f] [--giant-min-mb N]
                   [--edit-kind <kind>] [--seed S]
# 3-way (joint generator; step 4):
codespawner mutate --corpus <dir> --target ... --three-way --overlap-fraction f --seed S
```

- **`--edit-kind`** selects the *mechanism* of the edit; it maps onto a *classification* (`reason`) that
  CodeDiffer asserts against. Locked **reason tokens**: `content | eol | whitespace | encoding | binary |
  metadata`. `line-add` / `line-remove` are edit-kinds that classify as **`reason=content`** with
  `op=insert|delete` — they exist to exercise the diff tool's line-renumber path.
- **`--three-way` + `--overlap-fraction f`** — the only 3-way dial (step 4); see below.

## Reason classes (step 2 — CodeDiffer's top-value ask)

Each changed file in the delta carries its `reason`. One small carve-style labeled fixture ships per class.

| reason | mechanism | bytes | textual hunks | notes |
|---|---|---|---|---|
| `content` | append marker / add / remove lines | differ | yes (replace, or insert/delete for line-add/remove) | the existing bulk edit; line-add/remove exercise renumbering |
| `eol` | flip LF↔CRLF | differ | **zero** | the "modified-with-empty-diff" honesty case |
| `whitespace` | reindent / trailing spaces | differ | **yes** (all changes whitespace-only) | feeds the ws-insensitive collapse path |
| `encoding` | UTF-8↔UTF-16, add/remove BOM | differ | **zero** (decoded text identical) | tests decode-before-diff |
| `binary` | edit bytes in a blob file | differ | **zero** | metadata-only record (shas+sizes), no byte-range truth in v1 |
| `metadata` | synthetic mode/metadata change | **identical** (`oldSha==newSha`) | **zero** | fixture-sized; NTFS has no real POSIX mode so this is synthetic. Generic field, mode is just the first instance. Proves classify-as-`metadata_only` + the opt-in metadata-diff path |

## Delta schema — modified-files + hunks

Per changed file, in `fileOps.modified` (now objects, not bare paths):

```json
{
  "path": "blockC/.../src_3.c",
  "reason": "content",
  "oldSha": "<sha256 lowercase hex of raw old bytes>",
  "newSha": "<sha256 lowercase hex of raw new bytes>",
  "oldSize": 40960,
  "newSize": 41008,
  "hunks": [
    { "op": "replace", "oldStart": 120, "oldLines": 1, "newStart": 120, "newLines": 1 }
  ]
}
```

- **op** ∈ `insert | delete | replace`. insert ⇒ `oldLines==0`; delete ⇒ `newLines==0`; replace ⇒ both ≥1.
- **Coalescing:** contiguous touched lines collapse into ONE replace hunk (`oldLines==newLines==run`), proper
  unified semantics — not N 1-line hunks.
- **oldSha/newSha** = SHA-256 of the raw file bytes, **lowercase hex** (= CodeDiffer's size+hash prefilter
  hash). **oldSize/newSize** = decimal bytes, feeds the size prefilter + the binary summary.
- `eol`/`encoding`/`metadata`/`binary` records carry shas+sizes with an **empty** `hunks` list.

### Giant files — compact run-rule (the scale decision)

An explicit hunk list for `--edit-density 0.05` on a >1 GB header is hundreds of thousands of entries. For
files ≥ `--giant-min-mb`, selection switches from per-line-random to a **deterministic STRIDE** so a single
run-rule describes it exactly:

```json
{ "op": "replace", "kind": "run", "stride": 20, "rangeStart": 1, "rangeEnd": 5000000, "perHunk": 1 }
```

**Byte-exact expansion (pinned, both sides mirror):** touched lines =
`{ rangeStart + k*stride : k=0,1,… while ≤ rangeEnd }`, `rangeStart`/`rangeEnd` **1-based INCLUSIVE**, each a
`perHunk`-length replace. `perHunk=1` ⇒ one 1-line replace every `stride` lines. `density → stride` (0.05 ⇒
stride 20). Tiny delta, fully reconstructable, **no coupling to CodeSpawner's RNG**. Normal files stay
explicit-hunks; the byte track is reserved for actual binary files.

## Rename / move (step 3)

```json
"renamed": [ { "from": "…/src_9.c", "to": "…/moved_9.c", "similarityMilli": 1000 } ]
```

- Both **pure renames** (`similarityMilli==1000`, identical bytes, no hunks) AND **rename+edit**
  (`similarityMilli<1000`); rename+edit hunks live in `fileOps.modified` keyed by the **`to`** path (so the
  `renamed` section stays flat).
- **Similarity metric (pinned):** `commonLines / max(oldLineCount, newLineCount)` over **EOL-normalized**
  lines, `commonLines` = multiset intersection; encoded as `similarityMilli = round(sim*1000)` (integer
  thousandths — dodges float-format ambiguity, same spirit as bools-as-0/1). Matches CodeDiffer's own
  line-hash/MinHash engine so the threshold test is apples-to-apples.
- **Graded:** emit renames near ~0.9 / ~0.6 / ~0.3 to probe CodeDiffer's ~0.5 default threshold boundary.
- **Decoys:** scatter near-duplicate **non-renamed** files (dup `nearVariants`) so CodeDiffer scores false
  **positives**, not just recall. Truth = the `{from,to}` set ⇒ a precision/recall oracle (a missed rename
  shows as delete+add; a bad one as a spurious rename).

## 3-way (step 4 — joint generator)

From base B, two variants V1, V2 via two edit-seeds. **`--overlap-fraction f`** makes V2's line selection
aware of V1's: V1's lines are picked, then V2's so fraction `f` of them land on V1's touched lines →
graded **0%** (all clean-merge) / **50%** / **100%** (all conflict). Emits two deltas (B→V1, B→V2) + a
**separate conflict artifact** with its own `conflictTruthSha` (kept off the 2-way `diffTruthSha`, exactly as
`indirectTruthSha` is kept off `prevTruthSha`):

```json
{
  "conflicts": [ { "path": "…", "baseStart": 120, "baseLines": 3, "v1Hunk": {…}, "v2Hunk": {…} } ],
  "mergedClean": [ { "path": "…", "hunk": {…} } ]
}
```

- A region **conflicts** iff both sides modify overlapping base line ranges with **differing** content.
  Non-overlapping → clean. **Identical** change both sides → clean (same result).
- `mergedClean` = the expected merged-result hunk set for clean regions, so CodeDiffer asserts the **full
  merge**, not only the conflict set.
- **identical-overlap-clean** (both sides make the byte-identical change on an overlapping range → diff3
  resolves clean) is a **single labeled fixture**, not a dial — the only 3-way dial is `--overlap-fraction`.
- `conflictTruthSha` sections `[conflicts-3way, merged-clean]`: field orders pinned when step 4 is built;
  canonical form principles identical to `diffTruthSha` below.

## Integrity digest — `_meta.diffTruthSha`

Reuses the **exact** canonical form locked for `indirectTruthSha` (no new dialect — same `IndirectDigest`
machinery):

- **Ordinal** (byte-wise UTF-8) sort everywhere; separators are literal bytes **US `0x1F`** (field) /
  **RS `0x1E`** (record) / **GS `0x1D`** (section); bools as `0/1`; **dedup-then-sort** each section;
  sha256 **lowercase hex**; **ALL sections always emitted** (empty = header + zero records).
- File-level metadata (`reason`, shas, sizes) is digested — a reason-flip or sha-flip **trips the digest**,
  not just the hunks.

**Four sections, this fixed order:**

| # | section | fields (US-joined, in order) |
|---|---|---|
| 1 | modified-files | `path · reason · oldSha · newSha · oldSize · newSize` |
| 2 | hunks-explicit | `path · op · oldStart · oldLines · newStart · newLines` (op ∈ insert\|delete\|replace) |
| 3 | hunks-run | `path · op · stride · rangeStart · rangeEnd · perHunk` |
| 4 | renames | `from · to · similarityMilli` |

A `diffTruthSha` golden vector is frozen into `digest-selftest` so CodeDiffer's verify-adapter reproduces it
independently before step (1) is called locked — the same bar CodeCarver/CodeCompass held CodeSpawner to on
`indirectTruthSha`.

**Golden vector (frozen 2026-10-02, `digest-selftest` + `DiffDigestTests`):** three modified files inserted
out of order (proves the ordinal sort) — `z/last.c` (reason content, o1/n1, 100→110, one `replace 5,2`),
`a/first.c` (o2/n2, 200→205, two hunks `replace 1,1` + `replace 9,3`), `big.h` (o3/n3, 1048576→1050000, a
run-rule `replace stride=20 range=1..5000 perHunk=1`), and **empty renames**:
**`diffTruthSha = 66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542`** (sha256, lowercase hex).
Reproduce this exact hash before wiring further.

**Step 1 status:** IMPLEMENTED. Bulk `mutate` emits the `deltaKind:"diff"` delta above (modified-files with
reason/shas/sizes + coalesced explicit hunks, run-rule for giant files, empty `renamed`, `_meta.diffTruthSha`).

**Step 2 status:** IMPLEMENTED. `--edit-kind <content|line-insert|line-delete|whitespace|eol|encoding|binary|
metadata>` selects the mechanism; each maps to its locked `reason`. `line-insert`/`line-delete` emit
`insert`/`delete` hunks that renumber following lines (reason stays `content`); `whitespace` emits replace
hunks (reason `whitespace`); `eol`/`encoding`/`binary` emit shas+sizes with ZERO hunks; `metadata` is
content-identical (`oldSha==newSha`) with a synthetic `metadata:{field,old,new}` (not digested — the locked
modified-files section is exactly the six fields above). The honesty guard drops any file whose bytes did not
actually change, and the content rewrite preserves LF/CRLF + a missing final newline so a no-op is a true
no-op.

**Step 3 status:** IMPLEMENTED. `--edit-kind rename` renames chosen files with GRADED `similarityMilli` (a
rotating band {1000,900,600,300}; the emitted value is the REALIZED overlap `round((L-changed)/L*1000)`, not
the target, so tiny files honestly report low similarity). Pure renames (1000) are identical bytes with no
`modified` record; rename+edit emit their hunks in `modified` keyed by the `to` path (reason content), so
`renamed` stays flat. `--decoy-fraction f` emits near-duplicate ADDs (`*_dup*`, original kept) into
`fileOps.added` as rename false-positive traps — `added` is NOT digested (not a locked digest section), it is
scoring truth for CodeDiffer: the `{from,to}` set is the recall oracle, the decoys the precision oracle.
**Step 4 status:** IMPLEMENTED. `--three-way --overlap-fraction f` leaves base B pristine and emits two
mutated sibling trees `B_v1/` + `B_v2/`, their standard `deltaKind:"diff"` deltas `B-delta-v1.json` /
`B-delta-v2.json`, and `B-conflict.json` (`deltaKind:"conflict-3way"`, `_meta.conflictTruthSha`). All edits
land on **odd base lines** so every even line is a stable anchor (the stable-separator guarantee ⇒ each
conflict region is one base line, region-level ≡ line-level); `f` = fraction of V2's edited lines that coincide
with V1's. `conflictTruthSha` canonical form (identical discipline to `diffTruthSha`), two sections in this
fixed order:

| # | section | fields (US-joined) |
|---|---|---|
| 1 | conflicts-3way | `path · baseStart · baseLines · v1Op · v1NewStart · v1NewLines · v2Op · v2NewStart · v2NewLines` |
| 2 | merged-clean | `path · side · op · oldStart · oldLines · newStart · newLines` (side ∈ v1\|v2) |

`new*` coords reference the respective variant tree (`B_v1`/`B_v2`), `base*`/`old*` reference B — so every
coordinate reads off a real on-disk file and CodeDiffer fetches replacement text from the variant trees (no
text in the JSON). The conflict `kind` (modify/modify, modify/delete, add/add) is derivable from (ops,
baseLines), not stored. Golden vector `68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a`
frozen in `digest-selftest` + `ConflictDigestTests`. The dial default produces replace/replace conflicts;
modify/delete, add/add, adjacent-edit, and identical-overlap-clean ride the `--conflict-edges` fixture (§3-way-edges).

### §3-way-edges (step 4b — the edge conflict kinds)

**Status: IMPLEMENTED.** `--three-way --conflict-edges` replaces the random odd-only single-line model with a
deterministic layout that exercises every conflict kind as a region **cleanly separated by ≥1 base line
untouched by both sides**. No digest-format change — same `Conflict`/`CleanMerge` records and
`conflictTruthSha` as step 4.

Truth is computed by the **union-span coalescer** (= diff3 maximal-change hunk), locked with CodeDiffer
2026-10-02:

- A conflict/clean **region** is a maximal run of consecutive base lines touched by *either* side; a base line
  untouched by *both* terminates the region. (Isolated edits ⇒ `baseLines=1`, degenerating to the step-4 model.)
- For overlapping/adjacent edits the region is the **union span**: v1 edits base 3–5, v2 edits 4–6 ⇒ ONE
  `Conflict{baseStart=3, baseLines=4}`. Each side's `newLines` = how many lines the region occupies in *its*
  tree (replace: `=baseLines`; delete side: `<baseLines`; insert side: `>baseLines`); `newStart` = that region's
  start in that tree (so a delete/insert elsewhere correctly shifts it).
- **kind** is derived by CodeDiffer from (ops, baseLines), never stored: modify/modify `(replace,replace)`;
  modify/delete `(replace,delete)` with `newLines=0` on the delete side; add/add `baseLines=0, (insert,insert)`.
- **identical-overlap** — both sides make the byte-identical edit to a region ⇒ it is **clean, not a conflict**:
  omitted from `conflicts`, emitted **once** in `merged-clean` with the canonical **`side="v1"`** (hard-coded on
  both tools, since `side` is in the digest bytes). CodeDiffer still recovers "agreed edit" from the trees
  (`B_v1 ≡ B_v2 ≠ B` over the region) — no manifest flag needed.
- The delete-side `newStart` = the 1-based line in that variant where the deleted region would begin (surviving
  lines before the cut + 1) — byte-identical to the 2-way delete convention. The add/add anchor `baseStart` =
  the base line *after which* both insert (= the unified `@@ -L,0 +M,k @@` anchor).

Fixture: `diff_fixture_3way_edge/` — one source file carrying all kinds (modify/modify, adjacent, modify/delete,
add/add, identical-overlap, one-sided-clean each side), with its own `conflictTruthSha` and a README mapping
each region to its kind.

**All four steps (+ the edge kinds) are now implemented and locked end-to-end.**

## Sequencing (locked)

1. **hunk schema** + shas/sizes + `diffTruthSha` + the `digest-selftest` golden vector. ← CodeDiffer wires
   its verify-adapter against this first and reproduces the vector.
2. **reason-classes** + `--edit-kind` (one labeled fixture per class).
3. **rename** + graded similarities + decoys.
4. **joint 3-way** + `--overlap-fraction` + `conflictTruthSha` (pin its field orders here).

Each step lands with a labeled fixture + golden vector before the next. CodeSpawner pings the `code-spawner`
channel when (1)'s bytes are on disk.

## CodeDiffer side (mirrored, for reference)

- verify-adapter: assert `manifestVersion`, compose `base ⊕ delta`, independently reproduce `diffTruthSha`
  from the four canonical sections; reproduce the golden vector.
- regression suite seeded from the prototype-review bugs (eol/encoding "modified-with-empty-diff", one-sided
  binary, missing-path, doubled-newline/patch-applicability) so the step-2 reason-classes drop onto existing
  failing tests.
