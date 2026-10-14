# C++ language profile + manifest v2 — design proposal

**Status:** LOCKED 2026-10-14 (every consumer question answered). Being built for 1.2.0.

## Why

CodeCarver's hardest reachability cases are C++: virtual dispatch, overrides, and templates. Today they are
checked only by ad-hoc real-repo build sweeps (tinyxml2/pugixml/fmt/simdjson), never against a known call
graph. Oracle v1 gave C a known direct + indirect graph; this extends that to C++. It is the change the
schema doc reserves `manifestVersion: 2` for, because C++ needs a symbol identity and an edge meaning that
v1 readers would misread.

## Scope

In: classes with single and multiple inheritance, virtual methods and overrides, pure virtuals, final,
constructors/destructors (incl. implicit calls), static-init objects, function templates and class templates
with explicit sets of instantiations, namespaces, member-function pointers (D10). Out (later or never): overloading on argument types beyond
what templates need, ADL, operator overloading, lambdas / `std::function`, RTTI / `dynamic_cast`,
exceptions, the standard library (the corpus stays self-contained, no `#include <...>`).

## Proposed decisions

### D1. C corpora stay v1, byte-identical

Only a C++ corpus (`gen --lang cpp`, default `c`) writes `manifestVersion: 2`. Every existing knob and
corpus is untouched. A v1-only reader hard-asserts the version, so it rejects a C++ manifest instead of
misreading it.

### D2. Symbol identity: qualified name, no signatures

Symbols are keyed by their qualified name: `ns_1::Cls_4::m_2`, `ns_1::free_7`, `ns_1::Tmpl_3<int>::m_0`.
The generator never emits two overloads with the same qualified name, so a signature is never needed to
tell symbols apart. Each symbol gains `kind`: `function` | `method` | `ctor` | `dtor` | `template` |
`instance`. A `template` symbol is the uninstantiated definition; each `instance` symbol is one concrete
instantiation, and `instanceOf` names its template.

Each symbol also carries `simpleName` (`m_2` for `ns_1::Cls_4::m_2`). Name-keyed indexers (CodeCompass keys
its table on the simple name and searches refs by whole word) map key → `simpleName`. The generator reuses
simple names on purpose (the same `m_2` in several classes, as overrides do), so a name-keyed tool sees
real collisions. Those collisions are derivable by grouping on `simpleName`, so no separate list is needed.

`def` is the line of the **definition with a body**, never a bodiless declaration: for a method declared
in-class and defined out-of-class, the out-of-class `void Cls_4::m_2() {` line; for an in-class inline
definition, that line. (Matches CodeCompass's extractor, 2026-10-14.)

### D3. Virtual dispatch: a call site records both sound and precise target sets

A virtual call is recorded on the calling symbol as a `virtualEdges` record:

```json
{ "static": "ns_1::Base_0::m_2", "cha": ["ns_1::Base_0::m_2", "ns_1::D_1::m_2", "ns_1::D_3::m_2"],
  "rta": ["ns_1::D_1::m_2"] }
```

- `static`: the method named at the call site (the receiver's static type).
- `cha`: every override reachable in the class hierarchy below the static type (class-hierarchy analysis).
  This is the **sound** set; a carver that drops one fails soundness.
- `rta`: the subset whose class is actually constructed somewhere reachable (true rapid type analysis, not
  "constructed anywhere in the corpus"). This is the **precise** set. `cha − rta` is the C++ indirection tax: a counted over-keep, never a failure, just
  like `dispatched:false` in oracle v1.
- A pure-virtual method is never in either set. A `final` class or method bounds `cha`.

### D4. Direct edges, and implicit ctor/dtor edges in their own list

`edges` keeps its v1 meaning: direct calls written in the source (non-virtual methods, free functions, calls
through a qualified name like `Base::m()`, and an explicit construction `Cls_4 x;` / `new Cls_4` to its ctor).
The calls the compiler adds go in a separate `implicitEdges` list on the calling symbol: a ctor to its base
and member ctors, a dtor to its member and base dtors, and leaving scope to the dtor. `implicitEdges` are
ALWAYS part of both reachability sets (dropping one breaks the link), and cannot be turned off; they are kept
apart only so a consumer can count the keeps that came from implicit calls alone, the edges a name-based tool
most often misses. An implicit call has no name token, so it adds no ref site.

Ref sites follow the source's name tokens (whole word, outside comments and strings). An explicit construction
`Cls_4 x(…)` / `new Cls_4` is a ref site of the ctor `ns_1::Cls_4::Cls_4` (simpleName `Cls_4`), and an
explicit `x.~Cls_4()` call is a ref site of the dtor. A static-storage object's ctor is reached from a new root kind (D6), the C++ analog of
`init_array`.

### D5. Templates

A template body is reachable only through its instances. `edges` from an `instance` point at other
instances, never at the template. Each template has a fixed, generator-chosen instance set, and the
manifest lists them all, so an "instantiated but unreachable" instance is a real dead-code population.
Unused templates (no instances) are a population a carver can drop entirely. Truth for a per-template carver
(CodeCarver grades per source definition): a template is reachable iff at least one of its instances is.

### D6. Roots

`_meta.roots` stays an array of symbol names, plus a new optional `_meta.staticInitRoots` for the ctors of
static-storage objects. Reachability is `BFS(roots ∪ staticInitRoots)`
over `edges ∪ implicitEdges ∪ virtualEdges.cha ∪ memberPtrEdges` (sound) or over
`edges ∪ implicitEdges ∪ virtualEdges.rta ∪ memberPtrEdges[dispatched]` (precise). Oracle-v1
`indirectEdges` (C fn pointers) stay valid in a C++ corpus and keep their meaning.

### D7. Component digest

A new `_meta.cppTruthSha` over a locked canonical form (US/RS/GS, ordinal sort, dedup, the same
construction as `indirectTruthSha`): virtual-edge records, then `implicitEdges`, then `memberPtrEdges`, then
`kind`/`instanceOf` records, then `staticInitRoots`. It ships with a golden vector in `digest-selftest`. `prevTruthSha` and
`indirectTruthSha` keep their exact current definitions.

### D8. Knobs (all classic-gen; `--lang cpp` required)

`--cpp-classes <n>`, `--cpp-depth <n>` (inheritance depth), `--cpp-multi-inherit <f>`,
`--cpp-virtual-frac <f>`, `--cpp-instantiated-frac <f>` (drives `cha − rta`), `--cpp-templates <n>`,
`--cpp-instances <n>`, `--cpp-static-init <n>`. The existing oracle knobs (fanout, depth, reachable-frac)
apply to the free-function and method graph. Defaults are picked to be small and to populate every
quadrant at least once.

### D9. Verify

`verify` checks every new field: each `cha` target overrides `static` and sits below it; `rta ⊆ cha`;
each `instance` has an existing `instanceOf`; every def/ref line really contains the name; `cppTruthSha`
recomputes.

### D10. Member-function pointers

The C++ form of oracle-v1 `indirectEdges`: `&Cls_4::m_2` stored in a table or passed as data, and called
through `->*` / `.*`. Recorded on the symbol that takes the address as `memberPtrEdges`:
`{ "target": "ns_1::Cls_4::m_2", "via": "table" | "arg", "dispatched": true|false }`. Taking the address keeps
the target in the sound set; `dispatched:false` (address taken, never called through) is a counted over-keep,
exactly like v1. A pointer to a virtual method dispatches over that method's `cha`/`rta` sets.
The `&Cls_4::m_2` token names the method, so that line is recorded in `m_2`'s `refs` (as an address-taken
function name is in v1). The `->*` / `.*` call has no name token: no ref site, an expected miss for a
name-based indexer that `dispatched` lets it classify.

## Consumer answers so far

**CodeCompass (2026-10-14):** indexes C++ today via tree-sitter, but C/C++ `find_references` is a whole-word
simple-name search, and its tables key on the simple name; it does not consume a C++ corpus yet (adoption is
its user's call). It asked for a key → simple-name mapping and a way to see which declarations share a simple
name (now `simpleName`, D2), and records only definitions with a body (now the D2 `def` rule). D1 is right for
it: its verify adapter hard-asserts `manifestVersion == 1`, so a v2 manifest is refused, not misread.
Its user has decided to ADOPT the C++ corpus: CodeCompass will add a v2 path to its verify adapter and keep
the hard v1 assert for C.

CodeCompass, second round (2026-10-14): needs none of "Out" in the first cut. If they are added later, the
order that helps it most is (1) overloading on argument types (every overload of `m_2` comes back as one
simpleName group), then (2) operator overloading (`a + b` has no name token: a good expected-miss case). It
needs none of the rest. `implicitEdges` and `memberPtrEdges` both suit its v2 path: implicit calls are
reachability-only, with no ref and no callee; explicit `Cls_4 x(…)`, `new Cls_4` and `~Cls_4()` are ordinary
ref sites (D4); the `&Cls::m` line is a ref of `m` (D10).

**CodeCarver (2026-10-14):** binds C++ calls by SIMPLE name, with no hierarchy and no overload resolution,
and carves per function and per file; a template is one source definition to it.
- Q1: CHA is the sound set; its oracle asserts kept ⊇ cha. Keep the deliberate `simpleName` reuse across
  UNRELATED classes (D2): that over-keep is what it wants measured against cha and rta. `rta` = true RTA
  (constructed somewhere reachable); the "constructed anywhere" variant is not needed.
- Q2: grades per template, but wants the instances listed (D5) to derive "template reachable iff any instance
  is". An unused template is droppable.
- Q3: implicit ctor/dtor/base/member edges always in the sound truth, in a separate `implicitEdges` list that
  is part of both sets (D4). No off switch.
- Q5: D1 is right.
- Q6: member-function pointers in the first cut, with a `dispatched` flag like v1 (now D10). Lambdas and
  `std::function` can wait.

## Open questions for consumers

All answered (2026-10-14): Q1–3, Q5, Q6 by CodeCarver; Q4–6 and the `implicitEdges` / `memberPtrEdges` fit by
CodeCompass. The questions are kept below for the record.

1. **CodeCarver:** is CHA the right sound set for you, or do you carve with a different hierarchy
   analysis? Is RTA the right precise set, or should `rta` be "constructed anywhere in the corpus"
   instead of "constructed somewhere reachable"?
2. **CodeCarver:** do you need template-instance granularity (D5), or is per-template enough?
3. **CodeCarver:** should implicit ctor/dtor edges (D4) be in `edges`, or a separate list you can turn off?
4. **CodeCompass:** do you index C++ at all today, and would you consume a C++ corpus? If yes, does a
   qualified-name key (D2) match what your symbol table uses, and what `def` line do you expect for an
   out-of-class method definition (`void Cls::m() {...}`) versus its in-class declaration?
5. **Both:** is "C stays v1, only C++ is v2" (D1) right, or would you rather every manifest move to v2
   together?
6. **Both:** anything in "Out" that you need in the first cut?
