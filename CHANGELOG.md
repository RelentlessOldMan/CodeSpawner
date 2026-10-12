# Changelog

All notable changes to CodeSpawner are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each release is cut by `scripts\release.ps1`, which injects the matching section
below into the GitHub release notes — so a version with no section here will fail
to release. Keep the top section up to date as you work; the release script only
reads it.

## [Unreleased]

### Fixed (safety)
- `gen --force` no longer deletes any path it is given. A drive or share root,
  the current directory, the user profile (or a parent of either), a git
  checkout, and well-known user/system folders are refused even with `--force`.
  Previously `gen --out . --force` recursively deleted the working directory's
  contents before failing.
- `mutate` only edits or deletes files inside the corpus. Manifest def paths
  that escape it (`..`, absolute or drive-relative paths) are refused before
  any disk change; `--linked-roots` siblings (`../<corpus>_rootN/...`) count as
  inside. Junctions/symlinks inside the corpus are no longer followed when
  selecting targets, scanning, or copying 3-way variants.
- `mutate --three-way` refuses to wipe an existing `<corpus>_v1`/`_v2` that
  CodeSpawner did not create (checked before anything is touched), and the
  stale-delta sweep only deletes `<corpus>-delta.shard-<digits>.json`, not any
  file matching `shard-*`.
- `gen --from-profile` refuses archetype labels/extensions that are not plain
  file-name fragments (e.g. `../x`, `a/b`, `x:stream`), so a profile can no
  longer write outside `--out`.
- `NaN`/`Infinity` are rejected for every numeric option. They used to pass
  the range checks, let `mutate` edit files in place, and then crash the JSON
  writer, leaving a truncated delta.
- Manifests, deltas, shards, the conflict file and scan profiles are written to
  a temp file and renamed into place, so an interrupted run never leaves a
  truncated artifact at the real name. Regenerating over a corpus deletes its
  old sibling manifest first, so a stale manifest never sits beside a new tree.
- `verify` accepts `--linked-roots` corpora (it reported every federated def as
  "path escapes corpus root").
- Malformed JSON input (manifest/profile) is reported as `error: malformed
  JSON input: …` with exit 2 instead of an unhandled-exception stack dump.

### Fixed (diff truth)
- Line-level `mutate` edits are byte-faithful. Before, every text edit kind
  re-wrote the file as UTF-8 without a BOM, so UTF-16 files were silently
  transcoded and BOMs dropped while the truth recorded only line hunks; invalid
  UTF-8 had bytes replaced by U+FFFD; and 3-way rewrote files on a side that
  drew no edits, leaving changes with no record. Now a file keeps its encoding
  and BOM, only the edited lines change, and a file that is not exactly
  round-trippable text (invalid UTF-8, NUL-bearing binary, UTF-32) is left
  untouched and unrecorded by the text kinds (a pure rename under `rename`).
  Plain UTF-8 corpora produce byte-identical output to 1.1.3. Both CodeDiffer
  fixtures re-generate with the same `diffTruthSha`.
- `mutate --target all` never selects the generator's root
  `compile_commands.json`. Its absolute paths made `diffTruthSha` depend on
  where the corpus lived. Leftover `*.mut.tmp` files are also skipped.
- `line-insert` hunks now use the pinned insert convention: `oldStart` is the
  number of old lines before the insertion (git-style), as `--giant-edit
  single` already did. It was one too high, so a file could not be rebuilt
  from its hunks. Deletes are unchanged. The convention is now written out in
  docs/diff-delta-design.md. `line-insert` deltas get a new `diffTruthSha`.
- `--edit-kind binary` output now looks binary to a diff tool: it always has a
  NUL byte in the first 8 KB and never starts with a UTF-16/32 BOM. Before,
  XOR-flipping ASCII left printable text, so diffs classified it as content.
  `binary` deltas get a new `diffTruthSha`.
- The shard count for `--shard-size` no longer overflows near `int.MaxValue`.

### Changed (artifact contract — agreed with CodeDiffer and CodeCompass)
- Symbol deltas now carry `"_meta.deltaKind": "symbol"`, so every delta names
  its kind (`diff`, `conflict-3way`, `symbol`). Older symbol deltas omit it.
- `baseManifestSha` no longer depends on where the corpus lives or which OS
  wrote the manifest. It is SHA-256 of the manifest bytes with CR bytes dropped
  and `_meta.corpusRoot` blanked. Before, it hashed the raw file, so a corpus
  regenerated at another path got a different value for identical content.
  Every `baseManifestSha` value changes once. Symbol deltas made from the same
  seed are now byte-identical wherever the corpus is.
- Every JSON artifact (manifest, deltas, shards, index, conflict file,
  profile) is written with LF line endings on every OS. On Windows these used
  CRLF, so the bytes and shard shas differed from Linux. Windows shard shas
  change once. No `truthSha`/`diffTruthSha`/`conflictTruthSha` changes: they
  are digests of the content, not of the file bytes. Corpus files, including
  `compile_commands.json`, are unchanged.

### Changed (release tooling)
- `scripts\release.ps1` runs the test suite and builds + version-checks the
  AOT exe **before** anything is pushed. It commits and tags locally first, and
  rolls that back if the tests, the build, the `codespawner version` check or
  the push of main fail. A failed run can be re-run as-is.
- `release.ps1` refuses to run off `main`, when local main is behind or has
  diverged from `origin/main`, when `gh` is not authenticated, or when the tag
  already exists locally or on origin. The "already at this version" guard now
  works for an explicit version, and the version bump only matches
  `const string Version`.
- If `gh release create` fails, the notes file is kept and the exact command to
  finish by hand is printed. Release notes now include the exe's SHA-256.
- New `release.ps1 -DryRun`: every check, the tests, and the AOT build + version
  check, with no commit, tag, push or release.
- `scripts\build.ps1` deletes the previous `dist\codespawner.exe` before
  publishing and fails when `dotnet publish` fails. Before, a failed publish
  next to an old exe printed "OK", and a release could ship the previous
  version's binary under the new tag.

## [1.1.3] - 2026-10-09

### Fixed
- `gen --from-profile` no longer truncates its per-file RNG stream index to 32
  bits. Profiles with ≥22 archetypes (or any archetype past the per-archetype
  lane width) previously collided file streams — regenerating byte-identical
  size/content for files that should differ — once `archetype*lane + file`
  exceeded `int.MaxValue`. The index is now 64-bit; output is byte-identical for
  all non-overflowing cases (existing corpora unaffected), and an archetype that
  would exceed its lane now fails fast with a clear error instead of silently
  colliding.
- The "manifest disabled" error now names the form the parser actually accepts
  (`--manifest=false`, not `--manifest false`).

### Changed
- Preset list is single-sourced from one registry (`gen --help` and preset
  resolution can no longer drift apart).
- `release.ps1` checks for the CHANGELOG section as a pre-flight step, before any
  commit/tag/push, and correctly captures the oldest section.

### Docs
- Added this CHANGELOG; release notes are now injected from it.
- `manifest-schema.md`: corrected `hot_shared` edge semantics (oracle overlay:
  every `func_i`; plain `gen`: only the giant-including files; none with
  `--giant-headers 0`), documented the `_meta.gen` reproducibility block, and
  completed the `populations` list.
- README / USAGE: the build requirement is the .NET 8 SDK or newer (was ".NET 10").

### Tests
- End-to-end regen-identity guards for `gen`: re-running with the same seed/args
  over its own output is byte-identical in both the file tree and the manifest
  (the regen-in-place / cross-machine workflow), and two gens to different out
  dirs differ only in the absolute `_meta.corpusRoot` — all ground truth stays
  corpus-relative.
- Frozen determinism goldens: a fixed `Rng` stream, `Next` reduction values, and
  the `Category` enum's integer values — a refactor that silently changes the
  generated bytes now fails the suite.
- Preset resolution, verifier negative paths, and a guard that `gen --help`
  lists every registered preset.

## [1.1.2] - 2026-10-07

### Fixed
- Addressed the five findings from the 1.1.1 independent review:
  - Sharded-delta filenames now pad the shard index to a dynamic width, so they
    sort in shard order past 999 shards (was fixed-width `D3`).
  - Removed a dead `--from-profile` branch in the generator (profile generation
    dispatches elsewhere; its recipe is the profile itself).
  - Stale-artifact cleanup now runs once at bulk entry and covers every delta
    shape (2-way, 3-way, and sharded), instead of only the 2-way monolithic path.
  - Hardened the delta-artifact glob against bare-relative corpus paths.
  - Softened the `--edit-kind mixed` doc/help wording (drops the ">=6 => all six"
    overclaim) to describe content-bearing files honestly.

## [1.1.1] - 2026-10-07

### Added
- `--edit-kind mixed` — bulk mutate assigns one reason class per file, cycling the
  six reasons (content, eol, whitespace, encoding, binary, metadata) across the
  changed set so a single delta spans every reason class.
- `_meta.gen` reproducibility block in generated manifests, recording the
  effective generation knobs so a corpus can be regenerated exactly.
- `--shard-size N` paged/sharded delta transport — emits a `<corpus>-delta.index.json`
  plus `<corpus>-delta.shard-NNN.json` pages of ≤N modified records each. The
  `diffTruthSha` is **sharding-invariant** (identical to the monolithic delta),
  so a death-scale delta can be sharded for transport without changing the truth.
- `--giant-edit single` — a single localized one-line insert into a giant file,
  emitted as one explicit `insert` hunk (the content-defined-chunker locality
  case), alongside the existing `strided` run-rule mode.

### Fixed
- Bulk `mutate` now fails at plan time with a clear, actionable `ArgException`
  when a plan needs more shrink/restream seeds than the corpus carries, instead
  of crashing with a raw `ArgumentOutOfRangeException` (reported by CodeCompass:
  `--edits 10` against a corpus with one shrink seed).

## [1.1.0] - 2026-10-02

### Added
- Bulk diff-oracle mutate mode — deterministic, targeted, count/density-controlled
  in-place edits emitting a base→variant diff delta.
- diff-delta ground truth built in four steps: hunk-level truth + `diffTruthSha`
  (step 1), `--edit-kind` reason classes (step 2), rename/move + decoys (step 3),
  and native 3-way diff3 + `conflictTruthSha` (step 4).
- `--conflict-edges` — 3-way edge-case conflict kinds (adjacent multi-line
  union-span, modify/delete, add/add, identical-overlap).
- Honesty guards for the rename and giant-file paths.

### Changed
- Byte-faithful content rewrite — no more spurious empty-diff records.

### Tests
- Raised line coverage to ~96% (verifier failure modes, profile-gen oracle/guards,
  generation/scan/CLI surface, full ArgParser branch coverage, symbol-overlay
  mutate path).

## [1.0.9] - 2026-09-30

### Added
- Oracle guarantees a reachable, undispatched indirect edge (the "indirection
  tax"); documented the oracle-v1 manifest schema.

## [1.0.8] - 2026-09-30

### Added
- Oracle v1: seeded DAG call graph with `_meta.roots` and a reachable-fraction
  dial (phase 1+3), indirect-edge ground truth with `indirectTruthSha` (phase 2),
  and byte-mass ground truth via `--oracle-bytes` (phase 4).
- xUnit test suite (164 tests) and a `bench` oracle-v1 gate.

### Fixed
- Ungraceful CLI crash on bad numeric args.

## [1.0.7] - 2026-09-29

### Fixed
- `gen --from-profile` directory-count inflation and data-blob round-trip.

### Changed
- `.gitignore` never commits local scan profiles of real trees (`private/`,
  `*work-profile*.json`).

## [1.0.6] - 2026-09-29

### Changed
- Target `net8.0` (the fleet floor) instead of `net10.0`.

## [1.0.5] - 2026-09-29

### Added
- `scan` / shape-profile: privacy-preserving characterize → regenerate, with a
  design doc and CodeCarver + CodeCompass sign-off folded in.

## [1.0.4] - 2026-09-27

### Added
- mutate/churn v1 — delta manifests with the `--step` chain and `--through`
  cumulative modes, with a design doc and CodeCompass sign-off.

## [1.0.3] - 2026-09-27

### Changed
- Hardened the Batch 1/2 pathology presets after a review pass.

## [1.0.2] - 2026-09-27

### Added
- Batch 2 pathology presets: `pathological-symbols` (expectedMiss) and
  `dup-content`.

## [1.0.1] - 2026-09-27

### Added
- Batch 1 pathology presets and `_meta.populations`.

## [1.0.0] - 2026-09-27

### Added
- Initial release: deterministic synthetic code-corpus generator with a
  machine-checkable ground-truth manifest, `gen` / `verify`, and the release
  tooling.

[Unreleased]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.3...HEAD
[1.1.3]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.9...v1.1.0
[1.0.9]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.8...v1.0.9
[1.0.8]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.7...v1.0.8
[1.0.7]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.6...v1.0.7
[1.0.6]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.5...v1.0.6
[1.0.5]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/RelentlessOldMan/CodeSpawner/releases/tag/v1.0.0
