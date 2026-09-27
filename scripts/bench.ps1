<#
.SYNOPSIS
  Benchmark + regression harness for CodeSpawner. Guards correctness, determinism, and a throughput floor
  so an accidental change that breaks a contract or tanks performance fails here.

.DESCRIPTION
  Runs a fixed battery against a temp scratch dir:
    1. CORRECTNESS   gen a mixed corpus, then `verify` must PASS.
    2. DETERMINISM   two same-seed runs produce byte-identical manifests (path-normalized) and file hashes.
    3. SEED VARIANCE two different seeds produce DIFFERENT output (the seed actually does something).
    4. NEGATIVE      a tampered manifest must FAIL verify (the oracle isn't vacuous).
    5. THROUGHPUT    a header-heavy run must exceed a floor MB/s (regression guard, generous bound).

  Exit 0 only if every check passes. Intended for CI and pre-release (release gate).

.PARAMETER Exe        codespawner.exe (default: dist, else the Release build output).
.PARAMETER Work       Scratch dir (default: %TEMP%\cs-bench). Cleared each run.
.PARAMETER MinMBps    Throughput floor in MB/s for the header run (default 150; SSD dev boxes far exceed this).
#>
param(
    [string]$Exe,
    [string]$Work = (Join-Path $env:TEMP "cs-bench"),
    [double]$MinMBps = 150
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) {
    $Exe = Join-Path $root "dist\codespawner.exe"
    if (-not (Test-Path $Exe)) { $Exe = Join-Path $root "src\CodeSpawner\bin\Release\net10.0\win-x64\publish\codespawner.exe" }
}
if (-not (Test-Path $Exe)) { throw "codespawner.exe not found - run scripts\build.ps1 (got: $Exe)" }

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# We invoke a native exe and deliberately run a FAILING verify below; under 'Stop', a native command's
# stderr becomes a terminating NativeCommandError (PS 5.1 hazard). Gate on $LASTEXITCODE explicitly instead.
$ErrorActionPreference = 'Continue'
$fails = 0
function Check($name, [bool]$ok, $detail = "") {
    if ($ok) { Write-Host "  PASS  $name $detail" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name $detail" -ForegroundColor Red; $script:fails++ }
}
function HashTree($dir) {
    Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -ne '.codespawner' } | Sort-Object FullName |
        ForEach-Object { $rel = $_.FullName.Substring($dir.Length); "$rel=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash } |
        Out-String
}

Write-Host "== CodeSpawner bench ($Exe) ==" -ForegroundColor Cyan

# 1. CORRECTNESS
$c1 = Join-Path $Work "c1"
& $Exe gen --out $c1 --scale 0.01 --unresolved-includes 3 --compile-db full | Out-Null
& $Exe verify --corpus $c1 | Out-Null
Check "correctness (verify)" ($LASTEXITCODE -eq 0)

# 2. DETERMINISM (same seed -> identical)
$d1 = Join-Path $Work "d1"; $d2 = Join-Path $Work "d2"
& $Exe gen --out $d1 --scale 0.015 --giant-headers 1 --unresolved-includes 2 | Out-Null
& $Exe gen --out $d2 --scale 0.015 --giant-headers 1 --unresolved-includes 2 | Out-Null
# Manifests are identical except _meta.corpusRoot (the absolute out path); strip it before comparing.
function StripRoot($p) { (Get-Content $p -Raw) -replace '"corpusRoot":\s*".*?"', '"corpusRoot":"X"' }
Check "determinism (manifest)" ((StripRoot "$d1-manifest.json") -eq (StripRoot "$d2-manifest.json"))
Check "determinism (file hashes)" ((HashTree $d1).Replace('\d1\', '\dX\') -eq (HashTree $d2).Replace('\d2\', '\dX\'))

# 3. SEED VARIANCE (different seed -> different)
$s1 = Join-Path $Work "s1"; $s2 = Join-Path $Work "s2"
& $Exe gen --out $s1 --scale 0.01 --seed 1 --giant-headers 0 | Out-Null
& $Exe gen --out $s2 --scale 0.01 --seed 2 --giant-headers 0 | Out-Null
Check "seed variance" (((HashTree $s1).Replace('\s1\', '\sX\')) -ne ((HashTree $s2).Replace('\s2\', '\sX\')))

# 4. NEGATIVE (tampered manifest must fail)
$t = Join-Path $Work "t"
& $Exe gen --out $t --scale 0.01 --giant-headers 0 | Out-Null
$mf = "$t-manifest.json"
(Get-Content $mf -Raw) -replace 'src_1\.c:(\d+)', 'src_1.c:99999' | Set-Content $mf
& $Exe verify --corpus $t 2>&1 | Out-Null
Check "negative oracle (tamper detected)" ($LASTEXITCODE -ne 0)

# 4b. PATHOLOGY PRESET (broad-token): verifies + deterministic + records _meta.populations.
$g1 = Join-Path $Work "bt1"; $g2 = Join-Path $Work "bt2"
& $Exe gen --out $g1 --preset broad-token --broad-token-files 8 | Out-Null
& $Exe verify --corpus $g1 | Out-Null
$btVerify = ($LASTEXITCODE -eq 0)
& $Exe gen --out $g2 --preset broad-token --broad-token-files 8 | Out-Null
function StripRoot2($p) { (Get-Content $p -Raw) -replace '"corpusRoot":\s*".*?"', '"corpusRoot":"X"' }
$btDet = ((StripRoot2 "$g1-manifest.json") -eq (StripRoot2 "$g2-manifest.json"))
$hasPops = ((Get-Content "$g1-manifest.json" -Raw) -match '"populations"') -and ((Get-Content "$g1-manifest.json" -Raw) -match '"broad-token"')
Check "broad-token verify" $btVerify
Check "broad-token deterministic" $btDet
Check "_meta.populations present" $hasPops

# 5. THROUGHPUT (header-heavy)
$p = Join-Path $Work "p"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $Exe gen --out $p --giant-headers 6 --big-headers 0 --med-headers 0 --ordinary-headers 0 --tiny-files 0 --blob-files 0 --cfiles 2 | Out-Null
$sw.Stop()
$bytes = (Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum
$mbps = [math]::Round(($bytes / 1MB) / $sw.Elapsed.TotalSeconds, 0)
Check "throughput >= $MinMBps MB/s" ($mbps -ge $MinMBps) "($mbps MB/s)"

Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($fails -eq 0) { Write-Host "BENCH: ALL PASS" -ForegroundColor Green; exit 0 }
Write-Host "BENCH: $fails FAILURE(S)" -ForegroundColor Red; exit 1
