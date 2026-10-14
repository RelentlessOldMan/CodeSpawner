<#
  Cut a CodeSpawner release in one shot: bump Version (patch) in Program.cs -> run the test suite -> commit +
  tag LOCALLY -> build the Native AOT codespawner.exe and check it reports the new version -> push main + the
  tag -> create the GitHub release with the exe attached (Windows users download one file and run it; no .NET
  runtime needed).

  Nothing public happens until the exe is built and verified. If the tests, the build or the version check
  fail (or the push of main is rejected), the local bump commit and tag are rolled back and the repo is left
  exactly as it was - just fix the problem and re-run the same command.

  Commit your actual changes first (this only commits the version bump), then run:
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1          # bump patch (1.0.0 -> 1.0.1)
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1 1.1.0    # explicit version
    powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1 -DryRun  # every check + test + build, no git writes

  -DryRun runs the pre-flight checks, the tests, the version bump and the AOT build + version check, then
  restores Program.cs. It never commits, tags, pushes or releases (a dirty tree is only a warning). It leaves
  a dist\codespawner.exe that reports the NEXT version, so rebuild before using that exe for anything else.

  Needs: gh (authenticated), a clean working tree on main that is not behind origin/main, and the AOT build
  prerequisites (see build.ps1).
#>
param([string]$Version, [switch]$DryRun)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$verFile = Join-Path $root 'src\CodeSpawner\Program.cs'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Native exit codes don't trip $ErrorActionPreference, so every git/gh/dotnet call is checked explicitly.
function Assert-Exit([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

# Run a native command whose stderr chatter must not become a terminating error under 'Stop' (Windows
# PowerShell 5.1 turns redirected native stderr into error records); returns $true on exit code 0.
function Test-Native([scriptblock]$cmd) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $cmd 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $prev }
}

# 1. Pre-flight: clean tree, on main, not behind origin/main, gh authenticated.
if (git status --porcelain) {
    if ($DryRun) { Write-Warning "Working tree not clean - continuing because this is a dry run." }
    else { throw "Working tree not clean - commit or stash your changes first, then re-run." }
}
$branch = git symbolic-ref --short -q HEAD
if ($branch -ne 'main') {
    throw "Releases are cut from main, but HEAD is '$(if ($branch) { $branch } else { 'detached' })'. Check out main and re-run."
}
git fetch --quiet origin
Assert-Exit 'git fetch origin'
if (-not (Test-Native { git merge-base --is-ancestor origin/main HEAD })) {
    throw "Local main is behind or has diverged from origin/main - pull/rebase first, then re-run."
}
if (-not (Test-Native { gh auth status })) { throw "gh is not authenticated - run 'gh auth login', then re-run." }

# 2. Find the current version in Program.cs (exactly one `const string Version = "X.Y.Z"`) and decide the new one.
$src = [System.IO.File]::ReadAllText($verFile)
$verPattern = 'const\s+string\s+Version\s*=\s*"(\d+)\.(\d+)\.(\d+)"'
$vm = [regex]::Matches($src, $verPattern)
if ($vm.Count -ne 1) { throw "Expected exactly one 'const string Version = `"X.Y.Z`"' in Program.cs, found $($vm.Count)." }
$g = $vm[0].Groups
$cur = "{0}.{1}.{2}" -f $g[1].Value, $g[2].Value, $g[3].Value
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like X.Y.Z" }
    $ver = $Version
} else {
    $ver = "{0}.{1}.{2}" -f $g[1].Value, $g[2].Value, ([int]$g[3].Value + 1)
}
if ($cur -eq $ver) { throw "Program.cs is already at $ver - bump to a new version (a release needs a version change to commit)." }
if ([version]$ver -lt [version]$cur) { throw "$ver is older than the current $cur - a release only moves the version forward." }
if (git tag --list "v$ver") { throw "Tag v$ver already exists locally - pick a new version (or delete the stale tag)." }
if (git ls-remote --tags origin "refs/tags/v$ver") { throw "Tag v$ver already exists on origin - pick a new version." }
Assert-Exit 'git ls-remote'

# 3. Pull this version's section out of CHANGELOG.md now, so a missing/empty section refuses the release
#    before anything is committed. The capture stops at the next '## [' heading, the trailing
#    '[x.y.z]: url' link-reference block, or EOF.
$changelog = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path $changelog)) { throw "CHANGELOG.md not found - add it before releasing." }
$clText = [System.IO.File]::ReadAllText($changelog)
$m = [regex]::Match($clText, "(?ms)^## \[$([regex]::Escape($ver))\][^\r\n]*\r?\n(.*?)(?=^## \[|^\[[^\]]+\]:\s*https?://|\z)")
if (-not $m.Success) { throw "CHANGELOG.md has no '## [$ver]' section - describe the release there before cutting it." }
$changes = $m.Groups[1].Value.Trim()
if (-not $changes) { throw "CHANGELOG.md section for $ver is empty - describe the release before cutting it." }

# 4. The test suite must pass on exactly the code being released.
Write-Host "Running the test suite ..."
dotnet test (Join-Path $root 'CodeSpawner.sln') -c Release --nologo
Assert-Exit 'dotnet test'

# 5. Bump, commit + tag LOCALLY, build, and check the exe really is the new version. Nothing is pushed yet, so
#    any failure here rolls back to the exact starting state.
$exe = Join-Path $root 'dist\codespawner.exe'
$committed = $false
function Undo-LocalRelease {
    if ($committed) {
        # git restores Program.cs itself (in its own line-ending form, so the tree is clean again).
        if (git tag --list "v$ver") { git tag -d "v$ver" | Out-Null }
        git reset --quiet --keep HEAD~1
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not undo the local 'Release v$ver' commit - run: git reset --keep HEAD~1" }
    } else {
        # A failed commit (a hook, no identity) can leave the bump staged: unstage it before restoring the file.
        if (-not $DryRun) { git restore --staged -- $verFile 2>$null }
        [System.IO.File]::WriteAllText($verFile, $src, $utf8NoBom)   # byte-exact original (incl. a dry run's dirty tree)
    }
}
try {
    $newSrc = [regex]::Replace($src, $verPattern, "const string Version = `"$ver`"")
    [System.IO.File]::WriteAllText($verFile, $newSrc, $utf8NoBom)
    if (-not $DryRun) {
        git add $verFile
        Assert-Exit 'git add'
        git commit --quiet -m "Release v$ver"
        Assert-Exit 'git commit'
        $committed = $true
        git tag "v$ver"
        Assert-Exit "git tag v$ver"
    }

    # Build the standalone Native AOT exe from the bumped source, so its version matches the tag. build.ps1
    # deletes the old exe first and throws if the publish fails, so a stale binary can't slip through.
    & (Join-Path $PSScriptRoot 'build.ps1')
    if (-not (Test-Path $exe)) { throw "Build did not produce dist\codespawner.exe." }
    $reported = (& $exe version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $reported -ne $ver) {
        throw "The built exe reports version '$reported' (exit $LASTEXITCODE), expected '$ver' - refusing to ship it."
    }
} catch {
    Write-Warning "Release stopped before anything was pushed; rolling back the local version bump."
    Undo-LocalRelease
    throw
}

$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($DryRun) {
    Undo-LocalRelease
    Write-Host "`nDry run OK: pre-flight passed, tests passed, the AOT exe reports $ver (sha256 $sha)." -ForegroundColor Green
    Write-Host "Nothing was committed, tagged, pushed or released; Program.cs is restored. dist\codespawner.exe now" -ForegroundColor Green
    Write-Host "reports $ver - rebuild (scripts\build.ps1) before using it for anything else." -ForegroundColor Green
    return
}

# 6. Publish: push main, then the tag. A rejected main push is still fully recoverable (roll back locally).
git push --quiet origin main
if ($LASTEXITCODE -ne 0) {
    Undo-LocalRelease
    throw "git push of main failed (exit $LASTEXITCODE) - rolled back the local bump; nothing was published."
}
git push --quiet origin "v$ver"
if ($LASTEXITCODE -ne 0) {
    throw "main is pushed but the tag push failed (exit $LASTEXITCODE). Finish by hand: git push origin v$ver, then gh release create (see step 7 of this script)."
}

# 7. Create the GitHub release with codespawner.exe attached (changelog section + the standing boilerplate).
$notes = @"
**CodeSpawner v$ver** - deterministic synthetic code-corpus generator for stress-testing code indexing,
search, and carving tools at 100 GB+ scale, with a machine-checkable ground-truth manifest.

$changes

**Windows (no runtime needed):** download **codespawner.exe** below and run it. It is a self-contained
Native AOT binary - no .NET install required. First launch may show an unsigned-app SmartScreen prompt --
click **More info -> Run anyway** (once per download).

SHA-256 of codespawner.exe: ``$sha``

Quick start:
    codespawner gen --out .\_fw --scale 0.01
    codespawner verify --corpus .\_fw

See the README for usage: https://github.com/RelentlessOldMan/CodeSpawner#readme
"@
# Pass the notes via a file, never --notes: Windows PowerShell 5.1 does not escape embedded double quotes
# when handing an argument to a native exe, so a changelog line containing a `"` splits the argument and gh
# misreads the remainder as an asset path (v1.1.3 hit exactly this). On failure the notes file is KEPT so
# the release can be finished by hand with the printed command.
$notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "codespawner-release-notes-$ver.md"
[System.IO.File]::WriteAllText($notesFile, $notes, $utf8NoBom)
gh release create "v$ver" $exe --title "CodeSpawner v$ver" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) {
    throw ("gh release create failed (exit $LASTEXITCODE). main and tag v$ver are pushed; the notes are kept at " +
           "$notesFile. Check 'gh release view v$ver' for a half-created draft, then finish with:`n" +
           "  gh release create v$ver `"$exe`" --title `"CodeSpawner v$ver`" --notes-file `"$notesFile`"")
}
Remove-Item $notesFile -ErrorAction SilentlyContinue

Write-Host "`nReleased v$ver -> https://github.com/RelentlessOldMan/CodeSpawner/releases/tag/v$ver" -ForegroundColor Green
Write-Host "codespawner.exe sha256 $sha" -ForegroundColor Green
