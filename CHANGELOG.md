# Changelog

All notable changes to CodeSpawner are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each release is cut by `scripts\release.ps1`, which injects the matching section
below into the GitHub release notes — so a version with no section here will fail
to release. Keep the top section up to date as you work; the release script only
reads it.

## [Unreleased]

### Changed
- `baseManifestSha` no longer depends on the `--out` folder name of a
  `--linked-roots` corpus. Its manifest paths into sibling roots are written
  `../<leaf>_rootN/...`; the leaf is now dropped before hashing, so the same
  corpus generated under another folder name binds the same deltas. Agreed
  with CodeCompass and CodeDiffer (neither reads the sha). Manifests without
  linked roots hash exactly as before; record paths and every truth digest
  are unchanged.

## [1.1.6] - 2026-10-12

### Fixed (reproducibility)
- Replaying `_meta.gen` no longer shrinks giant headers. `macroDensity` was
  recorded even when `--macro-density` was not passed, and passing it back
  turns on a define cap the original run never had, so a death-scale giant
  replayed at ~56 MB instead of ~1.2 GB. It is now recorded only when set. For
  a pre-1.1.6 manifest, drop a recorded `1000000` when replaying unless the
  original run set it (e.g. `--preset memory`).

### Fixed (mutate)
- `mutate --step k` writes the step's delta before editing the file, and
  removes the delta if the edit fails. Before, a failed delta write left the
  edit on disk unrecorded, and retrying the step applied it twice (24 line-shift
  filler lines with a delta saying 12). The default per-step mode commits each
  step the same way.
- Malformed manifests of the right JSON syntax but the wrong shape (no `_meta`,
  a symbol without `def`, `null` where a string belongs, `1.5` or `1e40` where
  an integer belongs) now give a clean `error: malformed input` and exit 2 from
  `mutate`, instead of a stack trace.
- `--edits` below 1 is an argument error (exit 2); a huge `--edits` reports
  "not enough source files" instead of running out of memory.

### Fixed (crash safety)
- An interrupted mutate is no longer silent. Mutate keeps a
  `.codespawner-mutating` journal in each tree it edits and deletes it once the
  run's last artifact is written; if the run dies part-way, the next mutate
  refuses the corpus and verify FAILs it, instead of the tree passing as a
  corpus that matches neither its base manifest nor any delta.
- Every in-place edit (line edits, eol, encoding, binary, giant files,
  line-shift, add, grow, shrink, restream) writes a temp file and renames it
  over the original, so a failure never leaves a truncated file. The giant and
  binary edits no longer leave a `.mut.tmp` behind when they fail, and a giant
  edit no longer deletes the original before moving the new file in.
- Manifests, deltas, shards and profiles are flushed to disk before they are
  renamed into place.
- `gen --from-profile --with-oracle` checks that the manifest can be written
  before synthesizing anything.

### Fixed (regen)
- Regenerating over a corpus also removes its mutate deltas (including
  `--step` deltas), its 3-way variant trees and its stale `--linked-roots`
  siblings. Before, they survived beside the new tree, and leftover files in
  `<out>_rootN` were mixed into the new corpus.
- The marker is deleted last, so an interrupted regen leaves a directory the
  next run recognises without `--force`.
- The marker now records the `--linked-roots` count. A non-empty `_rootN` the
  new corpus would write into is refused without `--force` unless it belongs
  to the CodeSpawner corpus at `--out` (for a 1.1.5 or older corpus, which
  records no count, its `_rootN` in the new range are taken as its own).

### Fixed (containment)
- A manifest path through a junction or symlink inside the corpus (or a
  linked root that is one) is refused by mutate and fails verify, so a link
  planted in the tree can't steer an edit or delete outside it.
- Manifest paths into `../<corpus>_rootN/` are accepted only for N below the
  recorded `--linked-roots` count.
- verify's "vendor header is absent" check no longer follows junctions.

### Fixed (verify)
- A symbol with no `def` (or a non-string one) is a FAIL. Before, it was
  skipped, so a truncated manifest could drop def sites and still pass.
- Malformed manifest values no longer crash verify.

### Docs
- diff-delta-design.md: the 3-way conflict JSON example now matches what is
  written (`v1`/`v2` objects with op/newStart/newLines; flat `mergedClean`
  records with `side`); the edit kinds are `line-insert`/`line-delete`; an
  empty digest section is zero bytes (there is no header); the
  `conflictTruthSha` golden vector's input is spelled out.
- knobs.md: the bulk and 3-way mutate options and the `--from-profile` /
  oracle knobs are listed; `--io-parallelism` is in its own section with its
  real auto default; `--preset memory` has 3 includers, not 5.
- mutate-design.md: status, output and edit-kind list brought up to date;
  symbol-mode rename is marked as never built.
- `digest-selftest` is in `--help`, the README and USAGE; manifest-schema.md
  says it prints all four vectors.
- `vendor-codespawner.ps1` also copies mutate-design.md, diff-delta-design.md
  and oracle-v1-design.md, not just manifest-schema.md.

### Tests
- The emitted `diffTruthSha` and `conflictTruthSha` are recomputed from the
  delta/conflict JSON alone (every edit kind, giant run rule, reassembled
  shards, both 3-way modes), and each record's old/new sha and size is checked
  against the real files.
- The `prevTruthSha` chain is re-derived from base truth plus each delta's
  overlay, `--step 1..n` is checked byte-identical to the default run, and a
  failed step's retry is checked to apply once.
- The scanner privacy contract is tested in all three postures: no file,
  directory, identifier, string or comment text reaches the profile; small
  clusters fold into `other`; posture decides which blocks exist; a re-scan is
  byte-identical.
- `gen --from-profile` is checked against its archetype counts and sizes, and
  for byte-identical output from the same profile and seed.
- Crash-safety tests: an interrupted bulk mutate, failed in-place rewrites of
  every kind, regen sweeps, linked-root guards, an interrupted wipe, and
  junction containment.

## [1.1.5] - 2026-10-12

### Fixed (CLI)
- `codespawner <command> --help` prints the help instead of failing with
  "unknown knob --help".
- Every command parses arguments the same way: `--knob v` or `--knob=v`, and
  boolean flags as `--flag`, `--flag=false` or `--flag false`. Before,
  `gen --manifest false` was rejected, and mutate/scan/verify did not accept
  `--knob=v` at all. The space form only takes a boolean word, so it never
  swallows the next option or scan's positional tree.
- `--help` now lists every option the parser accepts, including the
  pathology knobs, `--big-headers`/`--med-headers`/`--ordinary-headers`,
  `--giant-includers`, `--blob-files`, `--build-output`, `--dirs`,
  `--depth`, `--manifest`, and scan's `--tree`. A test fails if an option is
  added without help.

### Fixed (reproducibility)
- `_meta.gen` now replays to a byte-identical tree. `dirs` was recorded as
  the unscaled default (5700) instead of the effective scaled count, so
  feeding the recipe back to `gen` placed every file in a different
  directory. A test now regenerates a corpus from its own `_meta.gen` and
  compares every file. `docs/diff-delta-design.md` shows how to replay.

### Fixed (scale)
- `--edit-kind eol`, `encoding` and `binary` no longer crash on giant files.
  Before, they read the whole file into one string or array, so a >1 GB
  header (for example under `--edit-kind mixed` at death scale) ran out of
  memory. `binary` now always streams. `eol`/`encoding` stream files over
  512 MiB and keep the in-memory path below that; the bytes written are the
  same either way.
- `whitespace`, `line-insert` and `line-delete` skip files over 512 MiB,
  leaving them untouched and unrecorded, the same as non-text files. They
  need explicit hunks, which have no compact form for giants; before, these
  files crashed. `content` on giants is unchanged (run-rule or `--giant-edit
  single`).
- JSON artifacts are written out every ~1 MiB instead of being buffered whole
  in memory, which capped them near 2 GiB. The truth digests hash
  incrementally instead of building one string (capped near 1G chars). Bytes
  and shas are unchanged; the golden vectors still pass.

### Changed (build)
- The exe's FileVersion/ProductVersion now come from `Program.Version`
  instead of reading 1.0.0.0.
- `scripts/bench.ps1` fails when any `gen` exits non-zero. Before, a failed
  gen could let the determinism and seed-variance checks compare two empty
  trees and pass. Its fallback exe path now points at net8.0.

## [1.1.4] - 2026-10-12

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

[Unreleased]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.6...HEAD
[1.1.6]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.5...v1.1.6
[1.1.5]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/RelentlessOldMan/CodeSpawner/compare/v1.1.3...v1.1.4
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
