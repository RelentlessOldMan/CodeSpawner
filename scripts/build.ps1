<#
.SYNOPSIS
  Publish CodeSpawner as a single self-contained Native AOT executable to .\dist\codespawner.exe.

.DESCRIPTION
  Native AOT needs the MSVC toolchain (VS "Desktop development with C++" workload). The AOT link step
  invokes vswhere.exe to locate it, but vswhere is not on PATH by default — so this script adds the VS
  Installer dir to PATH for the publish. If the AOT link fails (e.g. the C++ workload is missing), pass
  -SelfContained to fall back to a single-file self-contained publish (bundles the .NET runtime; larger
  exe, but no runtime install needed on target machines either).

.PARAMETER Rid            Runtime identifier (default win-x64).
.PARAMETER SelfContained  Skip AOT; publish a single-file self-contained exe instead.
#>
param(
    [string]$Rid = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\CodeSpawner\CodeSpawner.csproj"
$dist = Join-Path $root "dist"

# Make vswhere resolvable for the AOT linker probe.
$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Remove the previous exe FIRST: if this publish fails, there must be no stale binary left at the output path
# for the existence check below (or release.ps1) to mistake for a fresh build. A running codespawner.exe can't
# be deleted on Windows - that fails here, loudly, instead of silently shipping the old binary.
$exeName = if ($Rid -like 'win-*') { "codespawner.exe" } else { "codespawner" }   # no .exe off Windows
$exe = Join-Path $dist $exeName
if (Test-Path $exe) {
    try { Remove-Item $exe -Force }
    catch { throw "Cannot replace $exe (is a codespawner.exe still running?): $($_.Exception.Message)" }
}

if ($SelfContained) {
    Write-Host "Publishing single-file self-contained ($Rid) ..."
    dotnet publish $proj -c Release -r $Rid `
        -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $dist
} else {
    Write-Host "Publishing Native AOT ($Rid) ..."
    dotnet publish $proj -c Release -r $Rid -o $dist
}
# Native exit codes don't trip $ErrorActionPreference - check dotnet's explicitly.
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE) - see the output above." }

if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "OK: $exe ($mb MB)"
} else {
    throw "publish did not produce $exeName (see output above)"
}
