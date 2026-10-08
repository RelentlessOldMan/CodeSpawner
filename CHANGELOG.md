# Changelog

All notable changes to CodeSpawner are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each release is cut by `scripts\release.ps1`, which injects the matching section
below into the GitHub release notes — so a version with no section here will fail
to release. Keep the top section up to date as you work; the release script only
reads it.

## [Unreleased]

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
  - Softened the `--edit-kind mixed` doc/help wording (drops the "≥6 ⇒ all six"
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

[Unreleased]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.2...HEAD
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
