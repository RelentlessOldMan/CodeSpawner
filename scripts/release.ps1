<#
  Cut a CodeSpawner release in one shot: bump Version (patch) in Program.cs -> commit + tag + push
  -> build the Native AOT codespawner.exe -> create the GitHub release with the exe attached
  (Windows users download one file and run it; no .NET runtime needed).

  Commit your actual changes first (this only commits the version bump), then run:
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1          # bump patch (1.0.0 -> 1.0.1)
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1 1.1.0    # explicit version

  Needs: gh (authenticated), a clean working tree, and the AOT build prerequisites (see build.ps1).
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$verFile = Join-Path $root 'src\CodeSpawner\Program.cs'

# 1. Require a clean tree so the tag captures your committed work (not half-finished edits).
if (git status --porcelain) { throw "Working tree not clean - commit or stash your changes first, then re-run." }

# 2. Find the current version in Program.cs and decide the new one.
$src = [System.IO.File]::ReadAllText($verFile)
if ($src -notmatch 'Version\s*=\s*"(\d+)\.(\d+)\.(\d+)"') { throw "Could not find a Version = X.Y.Z declaration in Program.cs" }
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like X.Y.Z" }
    $ver = $Version
} else {
    $ver = "{0}.{1}.{2}" -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
}
if ("$($Matches[1]).$($Matches[2]).$($Matches[3])" -eq $ver) {
    throw "Program.cs is already at $ver - bump to a new version (a release needs a version change to commit)."
}

# 3. Pre-flight: pull this version's section out of CHANGELOG.md BEFORE we commit/tag/push or build,
#    so a missing/empty section refuses the release cleanly instead of after a tag is already public.
#    The capture stops at the next '## [' heading, the trailing '[x.y.z]: url' link-reference block, or EOF.
$changelog = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path $changelog)) { throw "CHANGELOG.md not found - add it before releasing." }
$clText = [System.IO.File]::ReadAllText($changelog)
$m = [regex]::Match($clText, "(?ms)^## \[$([regex]::Escape($ver))\][^\r\n]*\r?\n(.*?)(?=^## \[|^\[[^\]]+\]:\s*https?://|\z)")
if (-not $m.Success) { throw "CHANGELOG.md has no '## [$ver]' section - describe the release there before cutting it." }
$changes = $m.Groups[1].Value.Trim()
if (-not $changes) { throw "CHANGELOG.md section for $ver is empty - describe the release before cutting it." }

# 4. Write the bumped version back (preserving the file exactly, UTF-8 no BOM).
$src = [regex]::Replace($src, 'Version\s*=\s*"\d+\.\d+\.\d+"', "Version = `"$ver`"")
[System.IO.File]::WriteAllText($verFile, $src, (New-Object System.Text.UTF8Encoding($false)))

# 5. Commit the bump, tag it, push both.
git add $verFile
git commit --quiet -m "Release v$ver"
if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)." }
git tag "v$ver"
if ($LASTEXITCODE -ne 0) { throw "git tag v$ver failed (exit $LASTEXITCODE)." }
git push --quiet origin main
if ($LASTEXITCODE -ne 0) { throw "git push of main failed (exit $LASTEXITCODE)." }
git push --quiet origin "v$ver"
if ($LASTEXITCODE -ne 0) { throw "git push of tag v$ver failed (exit $LASTEXITCODE)." }

# 6. Build the standalone Native AOT exe from the just-bumped source, so its version matches the tag.
& (Join-Path $PSScriptRoot 'build.ps1')
$exe = Join-Path $root 'dist\codespawner.exe'
if (-not (Test-Path $exe)) { throw "Build did not produce dist\codespawner.exe - aborting release." }

# 7. Create the GitHub release with codespawner.exe attached (changelog section + the standing boilerplate).
$notes = @"
**CodeSpawner v$ver** - deterministic synthetic code-corpus generator for stress-testing code indexing,
search, and carving tools at 100 GB+ scale, with a machine-checkable ground-truth manifest.

$changes

**Windows (no runtime needed):** download **codespawner.exe** below and run it. It is a self-contained
Native AOT binary - no .NET install required. First launch may show an unsigned-app SmartScreen prompt --
click **More info -> Run anyway** (once per download).

Quick start:
    codespawner gen --out .\_fw --scale 0.01
    codespawner verify --corpus .\_fw

See the README for usage: https://github.com/RelentlessOldMan/CodeSpawner#readme
"@
# Pass the notes via a file, never --notes: Windows PowerShell 5.1 does not escape embedded double quotes
# when handing an argument to a native exe, so a changelog line containing a `"` splits the argument and gh
# misreads the remainder as an asset path (v1.1.3 hit exactly this). Native exit codes don't trip
# $ErrorActionPreference, so check gh's explicitly instead of announcing a release that never happened.
$notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "codespawner-release-notes-$ver.md"
[System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))
try {
    gh release create "v$ver" $exe --title "CodeSpawner v$ver" --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE) - tag v$ver is pushed; create the release manually with --notes-file." }
} finally {
    Remove-Item $notesFile -ErrorAction SilentlyContinue
}

Write-Host "`nReleased v$ver -> https://github.com/RelentlessOldMan/CodeSpawner/releases/tag/v$ver" -ForegroundColor Green
