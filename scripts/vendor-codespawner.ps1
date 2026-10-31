<#
.SYNOPSIS
  Vendor the built CodeSpawner generator into a consumer repo (an indexer, a carver, ...).

.DESCRIPTION
  Consumers VENDOR a pinned generator, they do NOT submodule it. This
  copies dist\codespawner.exe + the contract docs into <Target>\tools\codespawner\ and records a
  GENERATOR_VERSION file (generator version, manifestVersion, source commit-less stamp). The real coupling
  is the manifest + delta contracts, so their docs travel with the binary and the version is asserted downstream.

.PARAMETER Target   The consumer repo root to vendor into (e.g. ..\MyIndexer).
.PARAMETER Exe      Path to a prebuilt codespawner.exe (default: .\dist\codespawner.exe).
#>
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Exe
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) { $Exe = Join-Path $root "dist\codespawner.exe" }
if (-not (Test-Path $Exe)) { throw "generator not built: $Exe (run .\scripts\build.ps1 first)" }
if (-not (Test-Path $Target)) { throw "target repo not found: $Target" }

$version = (& $Exe version).Trim()
$destDir = Join-Path $Target "tools\codespawner"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

Copy-Item $Exe (Join-Path $destDir "codespawner.exe") -Force
# The contract docs: the manifest, the symbol/diff/conflict deltas, the oracle overlay they cite, and the C++
# profile (manifest v2).
$docs = "manifest-schema.md", "mutate-design.md", "diff-delta-design.md", "oracle-v1-design.md", "cpp-profile-design.md"
foreach ($d in $docs) { Copy-Item (Join-Path $root "docs\$d") (Join-Path $destDir $d) -Force }

$stamp = @"
CodeSpawner generator vendored into this repo.

generatorVersion : $version
manifestVersion  : 1 (C corpora), 2 (gen --lang cpp)
note             : Vendored, not a submodule. Re-run scripts\vendor-codespawner.ps1 to bump.
                   Consumers MUST assert the _meta.manifestVersion they expect before trusting a manifest.
"@
Set-Content (Join-Path $destDir "GENERATOR_VERSION") $stamp -Encoding utf8

Write-Host "Vendored codespawner $version -> $destDir" -ForegroundColor Green
Write-Host "  codespawner.exe, $($docs -join ', '), GENERATOR_VERSION"
