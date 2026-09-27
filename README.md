# CodeSpawner 🧬

**Deterministic synthetic code-corpus generator for stress-testing code indexing, search, and carving
tools at 100 GB+ scale — with a machine-checkable ground-truth manifest.** A standalone, native console
app that fabricates source trees matching the *structural shape* of a huge real repo (giant
machine-generated headers over 1 GB amid tens of thousands of tiny files) so you can reproduce
scale/memory failures on demand and assert correctness against known truth.

```console
$ codespawner gen --out D:\death --preset death
  io-parallelism: 12 (auto for SSD; override with --io-parallelism)
Fabricating corpus at D:\death (scale 1, seed 1337, compileDb=None) ...
  register headers ... 42.4s
    ^ 12 giant (<=1229MB) + 1375 big + 1957 medium
  source call graph ... 2.6s
  tiny files ... 4.5s
    ^ D:\death-manifest.json (5124 symbols)
Done: 51,873 files, 97.23 GB, 5,697 dirs, 1 root(s) in 56.4s.

$ codespawner verify --corpus D:\death
  OK  _meta.manifestVersion == 1
  checked: 5124 defs, 5125 refs, 5125 edges, 0 gated refs
verify: PASS (5124 symbols)
```

- **Native AOT single exe** (~1.9 MB) — no .NET runtime needed on the target machine.
- **Deterministic** given `--seed` — same seed produces a byte-identical corpus + manifest, even though
  generation runs multi-threaded.
- **Fast, and disk-aware** — a byte-level header emitter (~440 MB/s single-thread, >1 GB/s parallel) with
  write concurrency auto-tuned to the target disk (SSD → all cores; HDD → sequential, to avoid head
  thrash). A ~90 GB "repo of death" builds in a couple of minutes on NVMe.
- **Scale by COUNTS, not per-file size.** The bimodal shape (~90% of bytes in ~2% of files — giant
  machine-generated register headers amid tens of thousands of tiny files) **is the test**. Even-sized
  corpora reproduce none of the real failures.
- **Ground-truth manifest.** The generator knows exactly what it emitted, so it writes every symbol's
  definition + reference sites (and a negative set), turning "find_references returned 4 hits" into a
  pass/fail assertion. See [`docs/manifest-schema.md`](docs/manifest-schema.md).

**New here? Read [`docs/USAGE.md`](docs/USAGE.md)** — the complete guide (install, commands, knobs,
performance, consumer integration, troubleshooting).

## Download

Grab the latest **`codespawner.exe`** from the [Releases](https://github.com/RelentlessOldMan/CodeSpawner/releases)
page and run it — it's a self-contained Native AOT binary, no .NET runtime required. Or build from source below.

## Build

Requires the .NET 10 SDK and (for Native AOT) the Visual Studio "Desktop development with C++" workload.

```powershell
.\scripts\build.ps1              # -> dist\codespawner.exe  (Native AOT)
.\scripts\build.ps1 -SelfContained   # fallback if the AOT C++ toolchain is unavailable
.\scripts\bench.ps1              # correctness + determinism + throughput regression battery
```

## Use

```powershell
# Generate a corpus + ground-truth manifest
codespawner gen --out .\_fw --scale 0.01

# Self-check the corpus against its manifest (zero tool dependency)
codespawner verify --corpus .\_fw

# Presets
codespawner gen --out .\_fw   --preset ci        # fast smoke, keeps >=1 giant header
codespawner gen --out .\_mem  --preset memory    # preprocessor-memory axis
codespawner gen --out D:\death --preset death    # ~90 GB, 12 headers >1 GB
```

Run `codespawner --help` for the full knob list, or see [`docs/knobs.md`](docs/knobs.md).

## Commands

| Command | Purpose |
|---|---|
| `gen --out <dir> [knobs…]` | Generate corpus + manifest. Pure emitter — no shelling to any analyzer. |
| `verify --corpus <dir> [--manifest <file>]` | Assert the manifest accurately describes the emitted files: def/ref sites exist at `path:line` with the expected token, `unreachableRef`s are genuinely gated, and the `func_i → func_{i-1}` chain is intact. Exit 0 = pass, 1 = fail. |
| `version` | Print the generator version. |

## How consumers couple to it

The stable coupling point is the **v1 manifest contract**, not the corpus bytes or this tool's language.
Each consumer keeps its own thin *verify adapter* and asserts `_meta.manifestVersion` before trusting a
manifest. See [`docs/manifest-schema.md`](docs/manifest-schema.md) for the schema and the dual semantics
of `unreachableRefs` (indexer closed-world vs. carver open-world).

## Consumer integration

Consumers **vendor** a pinned generator (not a submodule); the coupling is the versioned manifest, not the
binary. Run `.\scripts\vendor-codespawner.ps1 -Target <repo>` to copy the exe + schema + a
`GENERATOR_VERSION` stamp into `<repo>\tools\codespawner\`. Each consumer keeps a thin verify adapter and
asserts `_meta.manifestVersion` first:

- **CodeCompass** — `verify-corpus-codecompass.ps1` (index → `find_references` → assert vs manifest +
  unresolved-include disclosure + `doctor`).
- **CodeCarver** — `carver-groundtruth-oracle.ps1` (BFS the explicit `edges` → carve → soundness +
  precision).

## Docs

- [`docs/USAGE.md`](docs/USAGE.md) — **the full usage guide** (start here).
- [`docs/manifest-schema.md`](docs/manifest-schema.md) — the versioned manifest contract.
- [`docs/knobs.md`](docs/knobs.md) — the knob catalogue (each = an observed failure class) + presets.
- [`docs/PLAN.md`](docs/PLAN.md) — the build plan / design rationale.

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🧬

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
