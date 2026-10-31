# Oracle call graph and indirect edges

The `--with-oracle` overlay's default spine is a *linear* C call chain (`func_i → func_{i-1}`). That alone
makes carve correctness hard to test: precision is trivially ~100%, "reduction" is just chain position, and it
never exercises over-keep, indirect-edge soundness, or a graded reduction target. Real carve bugs mostly come
from **indirect edges** (function pointers, vector/dispatch tables, `init_array`). The knobs below add a
non-linear graph, declared roots, a reachable-fraction dial, indirect-edge truth and byte-mass truth, so a
carver (CodeCarver, for example) can check carve correctness against a known corpus without a real build.

All of it is additive to manifest schema **v1**: no `manifestVersion` bump. With every knob off, the
overlay is the plain linear spine, and its `_meta` still carries `roots`. C++ vtables, overrides and
templates are the v2 profile ([cpp-profile-design.md](cpp-profile-design.md)).

## 1. Non-linear C call graph

A seeded DAG replaces the chain when enabled. Knobs (all off by default):
- `--oracle-fanout <n>`: out-degree per node.
- `--oracle-depth <n>`: layers from the root.
- `--oracle-shared-leaves <n>`: count of shared sink nodes multiple callers edge into (diamonds), so a
  reachable subset has real precision hazards.

Default OFF = the linear spine, byte-identical. No schema change: `edges` is already a per-symbol list; a DAG is
just more edges.

## 2. Roots and the reachable-fraction dial

- `--oracle-reachable-frac <0..1>`: generate so ~25/50/75% of symbols are reachable from the declared
  root(s); the rest are dead subgraphs, for graded reduction assertions.
- **`_meta.roots`**: array of declared entry-point symbol names. **Emitted even for the linear default**, so a
  consumer never has to guess where the chain starts. A root that is not a declared symbol is a **hard gen-time
  error**.
- **Reachability = closure over `(edges ∪ indirectEdges)` from `_meta.roots`**, not over the call graph alone.
  The dial's soundness property is "the sound reachable set includes the indirect closure." The generator hits
  the dial's fraction over the function nodes of the direct graph; the closure over `indirectEdges` is the
  consumer's to compute.
- **Dial scatter:** indirect targets are spread across the cross-product
  `{reachable-source, dead-source} × {dispatched, not-dispatched}`, so a 25/50/75% corpus carries genuine
  over-keep rather than regrading a clean chain.

## 3. Indirect-edge ground truth (C)

`--oracle-indirect <n>` adds n indirect edges, recorded as per-symbol **`indirectEdges`**, co-located beside `edges`, same keying. Each entry:

```json
{ "target": "<symbol>", "via": "fnptr|vector-table|init_array", "dispatched": true, "resolved": true }
```

- `via` ∈ `{ fnptr, vector-table, init_array }`, **C only**. `vtable`/`override` are C++ (v2).
- `dispatched: bool` (address-taken is true by construction). It encodes what a carver asserts:
  - **SOUNDNESS**: a sound carve keeps **every** `indirectEdges` target, dispatched or not (statically you
    cannot prove a table entry is never invoked; keeping it is correct, not over-keep-as-bug).
  - **PRECISION / indirection tax**: the reachable + `dispatched:false` set is *counted* over-keep: a
    number (`safe − minimal`), the set a future trace/tightness tier could legitimately drop. Carried as a
    real population, never a failure.
- `resolved: bool`: **closed-world**. An external target (e.g. into libc/vendor) is `resolved:false` and is
  a **terminal leaf** (no outgoing edges; the reachability closure stops there).
- The generator emits the **real C constructs** so the address-taken targets exist in source: a function
  pointer table, a vector/dispatch table, an `__attribute__((constructor))` / `init_array` entry, including
  genuine never-dispatched address-taken targets. Resolved targets are defined in `indirect_targets.c`;
  external targets are left undefined.

### Indirect-edge digest: `_meta.indirectTruthSha`

The primary digest `prevTruthSha` ([mutate-design.md](mutate-design.md)) does not cover indirect edges.
Indirect drift is caught by a **separate component digest**:

- **`_meta.indirectTruthSha`**: present only when any `indirectEdges` is. A rewired indirect edge or a moved
  root trips it. Guarded by a golden vector in `digest-selftest`.

**Canonical form (byte-exact):**
- Ordinal (byte-wise) sort throughout.
- `US` = `0x1F` between fields; `RS` = `0x1E` between records (a trailing `RS` terminates every record).
- Per-indirect-edge field order: **source · target · via · dispatched · resolved**.
- Bools serialized as `0` / `1`.
- Dedup-then-sort each population before hashing.
- `GS` = `0x1D` between the `indirectEdges` section and the `roots` section.
- Both sections always emitted (empty section = zero records, i.e. it contributes nothing but the `GS` is
  always present between them).
- `sha256` of the assembled byte string, lowercase hex.

**Golden vector** (`digest-selftest`):
```
roots  = [ func_0 ]
symbols:
  func_0.indirectEdges = [ {itgt_0, fnptr,        dispatched, resolved},
                           {iext_1, vector-table, !dispatched, !resolved} ]
  func_1.indirectEdges = [ {itgt_0, init_array,   dispatched, resolved} ]
=> indirectTruthSha = fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f
```
(Byte string: three edge records — each `source␟target␟via␟dispatched␟resolved␞` — ordinal-sorted, then `␝`,
then the one root record `func_0␞`.) Reproduce this before trusting your implementation.

## 4. Byte-mass ground truth

Per-symbol `bytes` + a `_meta.totalOracleBytes`, behind **`--oracle-bytes`** (default off, so the default
manifest stays byte-identical). Lets a carve's reduction target be asserted in **bytes**, not just symbol
count, and turns the indirection tax into a *cost*. Most useful with `--oracle-scale`, where body inflation
makes per-symbol sizes non-uniform.

`bytes` is the **definition span** of each symbol (opener line → closing brace), and `totalOracleBytes` is the
exact sum of those. Reachable-bytes / total is therefore well-defined, but it excludes inter-function bytes
(includes, forward declarations), so it is not the whole-file or whole-corpus byte total.

## Oracle properties this gives a carver

- **SOUNDNESS**: a sound carve keeps every direct + indirect reachable target (FAIL if any dropped).
- **PRECISION / TAX**: the reachable + `dispatched:false` set is counted over-keep (the indirection tax).
- **GRADED reduction**: assertions at 25/50/75% via the dial.

## What verify checks

`ManifestVerifier` and `ManifestReader` ignore unknown fields, so these additions never break an older reader.
`verify` checks them as follows:

- every `_meta.roots` entry is a declared symbol;
- each `indirectEdges` target: a `resolved` target is a declared symbol, and an unresolved (external) target
  is not;
- `_meta.indirectTruthSha`, when present, is recomputed from the manifest and must match.

Per-symbol `bytes` and `_meta.totalOracleBytes` are not checked by verify.
