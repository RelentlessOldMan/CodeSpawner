# CodeSpawner — Build Plan

**What:** A standalone C# (Native AOT) console app that fabricates synthetic code corpora at insane
scale (100 GB+ trees, headers >1 GB) with a deterministic **ground-truth manifest**, for testing code
indexing / search / carving tools. Ports and supersedes CodeCompass's `make-firmware-corpus.ps1`.

**Runtime decision:** C# / .NET 10, **Native AOT** single-file `.exe` (no runtime install on target
machines). Build machine has .NET 10 SDK + VS2022; AOT needs the "Desktop development with C++"
workload — if the AOT link step fails we fall back to `PublishSingleFile` self-contained (bundled
runtime). Target machines need nothing either way.

**Contract, not bytes:** The coupling point for consumers (CodeCompass `-Verify`, CodeCarver oracle) is
the **v1 manifest JSON**, per §10 of the common plan — not the script language or exact file bytes.
CodeSpawner emits an identical v1 manifest, so it's a drop-in for both. (Note: switching to per-file
deterministic RNG for parallelism means output bytes won't match the old PS script byte-for-byte, but
every structural invariant + the manifest contract hold. Same seed → identical output within CodeSpawner.)

---

## 1. Project layout

```
C:\Playground\CodeSpawner\
  CodeSpawner.sln
  src/CodeSpawner/
    CodeSpawner.csproj          # net10.0, PublishAot=true, InvariantGlobalization
    Program.cs                  # entry + subcommand dispatch
    Cli/
      ArgParser.cs              # minimal, dependency-free arg parsing
      GenOptions.cs             # all knobs (mirrors PS params)
    Generation/
      CorpusGenerator.cs        # orchestrator
      DirTree.cs                # block/sub/mod tree, linked roots, deterministic PickDir
      Rng.cs                    # splittable per-file deterministic RNG (seed, category, index)
      RegHeaderEmitter.cs       # byte-level batched giant/big/med header writer
      OrdinaryHeaderEmitter.cs
      BlobEmitter.cs            # zero-symbol data blobs
      TinyFileEmitter.cs        # csv tiny-file pressure
      SourceEmitter.cs          # src_i.c call chain + giant-include stress + hot_shared
      UnresolvedIncludeEmitter.cs  # vendor_gated negative case
      BuildOutputEmitter.cs     # .o/.lst/.bak beside sources
      CompileDbEmitter.cs       # compile_commands.json (none|partial|full)
    Manifest/
      ManifestModel.cs          # Meta + Symbol records
      ManifestWriter.cs         # Utf8JsonWriter streaming, v1 schema, relative paths
    Verify/
      ManifestVerifier.cs       # tool-independent self-check (the `verify` subcommand)
  docs/
    PLAN.md                     # this file
    manifest-schema.md          # v1 contract (from §10)
    knobs.md                    # knob catalogue (each = an observed failure class)
  scripts/build.ps1             # dotnet publish -c Release -r win-x64 -> dist\codespawner.exe
  README.md
```

Out of scope for v1: `make-megacorpus.ps1` (a robocopy *stacker* of real repos, not synthetic
generation — a different tool). C++/C# language profiles (kept as a seam, not built; C-firmware only).

## 2. Subcommands

- `codespawner gen --out <dir> [knobs…]` — **generation only** (core). Emits corpus + manifest.
  No `-Run`/`-Verify` shelling to any tool (per §8.D — core stays a pure emitter).
- `codespawner verify --corpus <dir> [--manifest <file>]` — tool-independent self-check (§8.B / §10).
- `codespawner gen --preset death|ci|memory …` — presets replacing the wrapper scripts
  (`death` = ~90 GB, 12 headers >1 GB, matching `deathrepo-test.ps1`).

**Knobs** (1:1 with the PS params, all optional + defaulted): `--scale --seed --macro-density
--giant-headers --big-headers --med-headers --ordinary-headers --cfiles --giant-includers
--tiny-files --blob-files --max-header-mb --compile-db --build-output --dirs --depth
--linked-roots --unresolved-includes --manifest --io-parallelism`. Principle preserved: a knob for one
consumer must never force another to change.

## 3. Manifest v1 (the deliverable contract, from §10)

```json
{
  "_meta":   { "manifestVersion": 1, "generatorVersion": "…", "seed": 1337, "corpusRoot": "<-out>" },
  "symbols": {
    "func_42":      { "def": "block3/sub0/…/src_42.c:10", "refs": ["…/src_43.c:12"], "edges": ["func_41"] },
    "vendor_gated": { "def": "…/vendor_gated.c:1", "refs": [], "unreachableRefs": ["…/unres_0.c:5"], "edges": [] }
  }
}
```

Changes vs the current PS output, built in from the start:
- **Repo-relative paths** rooted at `--out` (kills the shared basename hack).
- **`_meta`** block; both adapters hard-assert `manifestVersion` (and `seed`).
- **`edges` = symbol → [symbols]** (path-independent call graph): `func_i → [func_{i-1}]`.
- Symbols nested under `symbols`. Written with a streaming `Utf8JsonWriter` (AOT-safe, no reflection,
  handles 5k+ symbols cheaply).

## 4. Performance & determinism model

- **Giant/big/med headers:** byte-level emitter writing directly to a `FileStream` (large buffer),
  formatting ASCII into a reused `byte[]` via `Utf8Formatter`, flushed in ~4–8 MB chunks (no
  `StringBuilder→string→encode`). Headers emitted in parallel, bounded by `--io-parallelism`
  (default ~min(cores, 4) to avoid disk thrash on multi-GB sequential writes).
- **Tiny files / csv / ordinary headers / blobs / src files:** `Parallel.ForEach` across all cores —
  this is the main speedup over the single-threaded PS loop (syscall-bound small-file writes).
- **Determinism under parallelism:** each file `i` gets its own RNG seeded from
  `Hash(masterSeed, category, i)` — output is reproducible regardless of thread scheduling. The call
  chain, giant-include co-location, and per-file structure are all pure functions of `(seed, i)`, so
  the manifest is assembled by index join after emission with no cross-thread ordering dependency.

## 5. Verify self-check (`ManifestVerifier`, zero tool dependency)

Given a generated corpus + manifest, assert:
- `_meta.manifestVersion == 1`; `corpusRoot`/`seed` present.
- every `def`/`ref` site exists at `path:line` and the line contains the expected token
  (`func_i(` etc.).
- every `unreachableRef` is genuinely gated: behind `#ifdef VENDOR_OK` and its vendor header is absent
  from the tree.
- the `func_i → func_{i-1}` edge chain is intact.
- Nonzero exit on any failure. This is the contract's guardian, independent of any consumer.

## 6. Milestones (I'll build in this order, checking in after each)

1. **Scaffold:** sln + csproj (AOT config), arg parser, `gen` skeleton that builds the dir tree and
   reports counts/size. Confirms the toolchain end to end.
2. **Emitters:** reg headers (byte-level) → ordinary → blobs → tiny/csv → src call chain (+ giant
   include + hot_shared) → build output → compile_commands → unresolved-includes.
3. **Manifest v1 writer:** relative paths, `_meta`, `edges`, streaming JSON.
4. **`verify` subcommand:** the self-check above.
5. **Parallelism + determinism pass:** per-file RNG, `Parallel.ForEach`, `--io-parallelism`; perf tune.
6. **Package + docs:** `manifest-schema.md`, `knobs.md`, `README.md`, `scripts/build.ps1`; AOT publish
   to `dist\codespawner.exe`; presets.

## 7. Acceptance tests

- `gen --scale 0.01 --giant-headers 0 …` then `verify` → passes in seconds.
- `gen --giant-headers 1 --max-header-mb 1200 --cfiles 5` → one >1 GB header emitted & correct.
- **Determinism:** two runs, same seed → identical manifest + identical file hashes.
- **Perf:** meet/beat PS (PS: ~111 MB header in ~3 s) via byte-level + parallel emit.
- **Contract:** a generated v1 manifest validates unchanged against CodeCompass `-Verify`
  (if its Release CLI is available) — otherwise the built-in `verify` stands as the guard.

## 8. Build prerequisite note

Native AOT needs the VS2022 "Desktop development with C++" workload (MSVC linker + Windows SDK) on the
*build* machine. Verified at milestone 1; if absent, `build.ps1` falls back to self-contained
single-file publish (larger exe, still no runtime install on targets).
