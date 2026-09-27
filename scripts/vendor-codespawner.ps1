<#
.SYNOPSIS
  Vendor the built CodeSpawner generator into a consumer repo (CodeCompass, CodeCarver, ...).

.DESCRIPTION
  Per the shared plan (§6.1/§10): consumers VENDOR a pinned generator, they do NOT submodule it. This
  copies dist\codespawner.exe + the manifest schema into <Target>\tools\codespawner\ and records a
  GENERATOR_VERSION file (generator version, manifestVersion, source commit-less stamp). The real coupling
  is the manifest contract, so the schema travels with the binary and the version is asserted downstream.

.PARAMETER Target   The consumer repo root to vendor into (e.g. C:\Playground\CodeCompass).
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
Copy-Item (Join-Path $root "docs\manifest-schema.md") (Join-Path $destDir "manifest-schema.md") -Force

$stamp = @"
CodeSpawner generator vendored into this repo.

generatorVersion : $version
manifestVersion  : 1
vendoredFrom     : $root
note             : Vendored, not a submodule. Re-run scripts\vendor-codespawner.ps1 to bump.
                   Consumers MUST assert _meta.manifestVersion == 1 before trusting a manifest.
"@
Set-Content (Join-Path $destDir "GENERATOR_VERSION") $stamp -Encoding utf8

Write-Host "Vendored codespawner $version -> $destDir" -ForegroundColor Green
Write-Host "  codespawner.exe, manifest-schema.md, GENERATOR_VERSION"
