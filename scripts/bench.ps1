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
       (4b-4h: pathology presets, mutate, scan, oracle v1, and the C++ profile.)
    5. THROUGHPUT    a header-heavy run must exceed a floor MB/s (regression guard, generous bound).

  Exit 0 only if every check passes. Intended for CI and for running by hand before a release:
  release.ps1 runs `dotnet test`, not this battery.

.PARAMETER Exe        codespawner.exe (default: dist, else the Release build output).
.PARAMETER Work       Scratch dir (default: %TEMP%\cs-bench). Cleared each run.
.PARAMETER MinMBps    Throughput floor in MB/s for the header run (default 150; SSD dev boxes far exceed this).
.PARAMETER Clang      clang++ for the C++ compile check (default: PATH, else %ProgramFiles%\LLVM\bin). If none
                      is found, the compile checks are SKIPPED (reported, not failed); -RequireClang fails them.
#>
param(
    [string]$Exe,
    [string]$Work = (Join-Path $env:TEMP "cs-bench"),
    [double]$MinMBps = 150,
    [string]$Clang,
    [switch]$RequireClang
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) {
    $Exe = Join-Path $root "dist\codespawner.exe"
    if (-not (Test-Path $Exe)) { $Exe = Join-Path $root "src\CodeSpawner\bin\Release\net8.0\win-x64\publish\codespawner.exe" }
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
# Every gen must succeed: a failed gen would otherwise surface only as a confusing downstream check (or, for
# determinism/variance, compare two empty trees and PASS).
function Gen {
    & $Exe gen @args | Out-Null
    if ($LASTEXITCODE -ne 0) { Check "gen $($args -join ' ')" $false "(exit $LASTEXITCODE)" }
}
function HashTree($dir) {
    Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -ne '.codespawner' } | Sort-Object FullName |
        ForEach-Object { $rel = $_.FullName.Substring($dir.Length); "$rel=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash } |
        Out-String
}

Write-Host "== CodeSpawner bench ($Exe) ==" -ForegroundColor Cyan

# 1. CORRECTNESS
$c1 = Join-Path $Work "c1"
Gen --out $c1 --scale 0.01 --unresolved-includes 3 --compile-db full
& $Exe verify --corpus $c1 | Out-Null
Check "correctness (verify)" ($LASTEXITCODE -eq 0)

# 2. DETERMINISM (same seed -> identical)
$d1 = Join-Path $Work "d1"; $d2 = Join-Path $Work "d2"
Gen --out $d1 --scale 0.015 --giant-headers 1 --unresolved-includes 2
Gen --out $d2 --scale 0.015 --giant-headers 1 --unresolved-includes 2
# Manifests are identical except _meta.corpusRoot (the absolute out path); strip it before comparing.
function StripRoot($p) { (Get-Content $p -Raw) -replace '"corpusRoot":\s*".*?"', '"corpusRoot":"X"' }
Check "determinism (manifest)" ((StripRoot "$d1-manifest.json") -eq (StripRoot "$d2-manifest.json"))
Check "determinism (file hashes)" ((HashTree $d1).Replace('\d1\', '\dX\') -eq (HashTree $d2).Replace('\d2\', '\dX\'))

# 3. SEED VARIANCE (different seed -> different)
$s1 = Join-Path $Work "s1"; $s2 = Join-Path $Work "s2"
Gen --out $s1 --scale 0.01 --seed 1 --giant-headers 0
Gen --out $s2 --scale 0.01 --seed 2 --giant-headers 0
Check "seed variance" (((HashTree $s1).Replace('\s1\', '\sX\')) -ne ((HashTree $s2).Replace('\s2\', '\sX\')))

# 4. NEGATIVE (tampered manifest must fail)
$t = Join-Path $Work "t"
Gen --out $t --scale 0.01 --giant-headers 0
$mf = "$t-manifest.json"
(Get-Content $mf -Raw) -replace 'src_1\.c:(\d+)', 'src_1.c:99999' | Set-Content $mf
& $Exe verify --corpus $t 2>&1 | Out-Null
Check "negative oracle (tamper detected)" ($LASTEXITCODE -ne 0)

# 4b. PATHOLOGY PRESET (broad-token): verifies + deterministic + records _meta.populations.
$g1 = Join-Path $Work "bt1"; $g2 = Join-Path $Work "bt2"
Gen --out $g1 --preset broad-token --broad-token-files 8
& $Exe verify --corpus $g1 | Out-Null
$btVerify = ($LASTEXITCODE -eq 0)
Gen --out $g2 --preset broad-token --broad-token-files 8
function StripRoot2($p) { (Get-Content $p -Raw) -replace '"corpusRoot":\s*".*?"', '"corpusRoot":"X"' }
$btDet = ((StripRoot2 "$g1-manifest.json") -eq (StripRoot2 "$g2-manifest.json"))
$hasPops = ((Get-Content "$g1-manifest.json" -Raw) -match '"populations"') -and ((Get-Content "$g1-manifest.json" -Raw) -match '"broad-token"')
Check "broad-token verify" $btVerify
Check "broad-token deterministic" $btDet
Check "_meta.populations present" $hasPops

# 4c. PATHOLOGICAL SYMBOLS: verifies + manifest carries expectedMiss.
$ps = Join-Path $Work "ps"
Gen --out $ps --preset pathological-symbols --pathological-symbols 6
& $Exe verify --corpus $ps | Out-Null
Check "pathological-symbols verify" ($LASTEXITCODE -eq 0)
Check "expectedMiss present" ((Get-Content "$ps-manifest.json" -Raw) -match '"expectedMiss"\s*:\s*true')

# 4d. DUP-CONTENT: verifies + manifest carries dupGroups with sha256.
$dc = Join-Path $Work "dc"
Gen --out $dc --preset dup-content --dup-groups 6
& $Exe verify --corpus $dc | Out-Null
Check "dup-content verify" ($LASTEXITCODE -eq 0)
Check "dupGroups present" ((Get-Content "$dc-manifest.json" -Raw) -match '"dupGroups"' -and (Get-Content "$dc-manifest.json" -Raw) -match '"sha256"')

# 4e. MUTATE: digest golden vector + a deterministic delta with correct ripples.
& $Exe digest-selftest | Out-Null
Check "digest golden vector" ($LASTEXITCODE -eq 0)
$mc = Join-Path $Work "mc"
Gen --out $mc --scale 0.01 --giant-headers 0 --big-headers 0 --med-headers 0 --cfiles 30 --shrink-seeds 1
& $Exe mutate --corpus $mc --seed 42 --edits 5 --through | Out-Null
$mutOk = ($LASTEXITCODE -eq 0) -and (Test-Path "$mc-delta.json")
$dj = if (Test-Path "$mc-delta.json") { Get-Content "$mc-delta.json" -Raw } else { "" }
Check "mutate emits delta" $mutOk
Check "delta has prevTruthSha + fileOps" (($dj -match '"prevTruthSha"') -and ($dj -match '"fileOps"') -and ($dj -match '"removedSites"'))

# 4f. SCAN round-trip + privacy: characterize a corpus, regenerate a look-alike, verify the oracle.
$scSrc = Join-Path $Work "sc-src"
Gen --out $scSrc --scale 0.01 --giant-headers 0 --cfiles 20 --tiny-files 60 --long-line-files 1 --blob-files 3
& $Exe scan $scSrc --out "$Work\prof.json" | Out-Null
$scanOk = ($LASTEXITCODE -eq 0) -and (Test-Path "$Work\prof.json")
& $Exe scan $scSrc --out "$Work\prof2.json" | Out-Null
function StripScannedAt($p) { (Get-Content $p -Raw) -replace '"scannedAt":\s*"[^"]*"', '"scannedAt":"X"' }
$scanDet = ((StripScannedAt "$Work\prof.json") -eq (StripScannedAt "$Work\prof2.json"))
# Privacy: the profile must be numbers-only — no absolute path, no generated identifiers, no content tokens.
$prof = Get-Content "$Work\prof.json" -Raw
# The profile is JSON, so a leaked Windows path appears with its backslashes doubled: match every spelling.
$pathForms = @($scSrc, $scSrc.Replace('\', '\\'), $scSrc.Replace('\', '/'), (Split-Path $Work -Leaf))
$leak = ($pathForms | Where-Object { $prof.Contains($_) }).Count -gt 0 -or ($prof -match 'HWIO|hot_shared|regmap|vendor_gated|CS_MK_HANDLER|func_\d')
$scRegen = Join-Path $Work "sc-regen"
Gen --from-profile "$Work\prof.json" --out $scRegen --with-oracle
& $Exe verify --corpus $scRegen | Out-Null
$scVerify = ($LASTEXITCODE -eq 0)
# Dir-count fidelity: regen must reproduce ~the source dir count (guards the depth-fold inflation bug).
$srcDirs = (Get-ChildItem $scSrc -Recurse -Directory).Count
$regenDirs = (Get-ChildItem $scRegen -Recurse -Directory).Count
$dirFidelityOk = ($srcDirs -gt 0) -and ($regenDirs -ge [math]::Floor($srcDirs * 0.5)) -and ($regenDirs -le [math]::Ceiling($srcDirs * 1.5) + 2)
Check "scan emits profile" $scanOk
Check "scan deterministic" $scanDet
Check "profile leaks nothing (privacy)" (-not $leak)
Check "gen --from-profile + oracle verify" $scVerify
Check "regen dir-count fidelity" $dirFidelityOk "(src=$srcDirs regen=$regenDirs)"

# 4g. ORACLE v1 (non-linear DAG + indirect edges + reachable-fraction dial + byte mass): verify, the new
# _meta fields, and determinism. Guards docs/oracle-v1-design.md end to end on the native exe.
$ov1 = Join-Path $Work "ov1"; $ov2 = Join-Path $Work "ov2"
$oArgs = @("--with-oracle","--oracle-chain","16","--oracle-fanout","3","--oracle-shared-leaves","3",
           "--oracle-reachable-frac","0.5","--oracle-indirect","8","--oracle-bytes")
Gen --from-profile "$Work\prof.json" --out $ov1 @oArgs
& $Exe verify --corpus $ov1 | Out-Null
$ov1Verify = ($LASTEXITCODE -eq 0)
$ov1mf = Get-Content "$ov1-manifest.json" -Raw
$ov1Fields = ($ov1mf -match '"roots"') -and ($ov1mf -match '"indirectEdges"') -and `
             ($ov1mf -match '"indirectTruthSha"') -and ($ov1mf -match '"totalOracleBytes"')
Gen --from-profile "$Work\prof.json" --out $ov2 @oArgs
$ov1Det = ((StripRoot2 "$ov1-manifest.json") -eq (StripRoot2 "$ov2-manifest.json"))
Check "oracle-v1 DAG+indirect+bytes verify" $ov1Verify
Check "oracle-v1 new _meta fields present" $ov1Fields
Check "oracle-v1 deterministic" $ov1Det

# 4h. C++ PROFILE (gen --lang cpp, manifest v2): each case verifies, carries the v2 fields, and compiles and
# links under clang++ -std=c++17 -Wall -Wextra -Werror (the program is not run: its receivers are null by
# design). The C++ truth is only meaningful for a program that compiles, and the unit tests have no compiler,
# so this is the gate for it. Cases span seeds and the knob extremes that once broke the build.
if (-not $Clang) {
    $cmd = Get-Command clang++ -ErrorAction SilentlyContinue
    if ($cmd) { $Clang = $cmd.Source }
    elseif (Test-Path "$env:ProgramFiles\LLVM\bin\clang++.exe") { $Clang = "$env:ProgramFiles\LLVM\bin\clang++.exe" }
}
if (-not $Clang -or -not (Test-Path $Clang)) {
    if ($RequireClang) { Check "C++ compile: clang++ found" $false "(pass -Clang <path>)" }
    else { Write-Host "  SKIP  C++ compile checks: no clang++ (pass -Clang <path>, or -RequireClang to fail)" -ForegroundColor Yellow }
    $Clang = $null
}
$cppCases = [ordered]@{
    "default"  = @()
    "seed14"   = @("--seed","14")
    "deep"     = @("--seed","9","--cpp-classes","60","--cpp-depth","5","--cpp-templates","10","--cpp-instances","5","--cpp-static-init","5")
    "flat"     = @("--cpp-depth","0")
    "allvirt"  = @("--cpp-virtual-frac","1","--cpp-multi-inherit","1")
    "novirt"   = @("--cpp-virtual-frac","0","--cpp-multi-inherit","0")
    "one"      = @("--cpp-classes","1","--cpp-templates","0","--cpp-static-init","0")
    "dag"      = @("--oracle-fanout","3","--oracle-chain","30","--oracle-shared-leaves","4","--oracle-reachable-frac","0.5")
}
foreach ($name in $cppCases.Keys) {
    $d = Join-Path $Work "cpp_$name"
    $cArgs = $cppCases[$name]
    Gen --lang cpp --out $d @cArgs
    & $Exe verify --corpus $d | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $mf = Get-Content "$d-manifest.json" -Raw
    $v2 = ($mf -match '"manifestVersion":\s*2') -and ($mf -match '"cppTruthSha"') -and ($mf -match '"kind":\s*"class"')
    Check "cpp $name verify + v2 fields" ($ok -and $v2)
    if ($Clang) {
        $srcs = Get-ChildItem (Join-Path $d "cpp") -Recurse -Filter *.cpp | ForEach-Object FullName
        $out = & $Clang -std=c++17 -Wall -Wextra -Werror -o (Join-Path $Work "cpp_$name.exe") @srcs 2>&1 | Out-String
        Check "cpp $name compiles + links (-Werror)" ($LASTEXITCODE -eq 0) $(if ($LASTEXITCODE -ne 0) { "`n$($out.Trim())" })
    }
}
$cppA = Join-Path $Work "cpp_det"
Gen --lang cpp --out $cppA
$cppDet = ((StripRoot2 "$cppA-manifest.json") -eq (StripRoot2 "$(Join-Path $Work 'cpp_default')-manifest.json")) -and `
          ((HashTree $cppA) -eq (HashTree (Join-Path $Work "cpp_default")))
Check "cpp deterministic (same seed -> identical program + manifest)" $cppDet

# 5. THROUGHPUT (header-heavy)
$p = Join-Path $Work "p"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Gen --out $p --giant-headers 6 --big-headers 0 --med-headers 0 --ordinary-headers 0 --tiny-files 0 --blob-files 0 --cfiles 2
$sw.Stop()
$bytes = (Get-ChildItem $p -Recurse -File | Measure-Object Length -Sum).Sum
$mbps = [math]::Round(($bytes / 1MB) / $sw.Elapsed.TotalSeconds, 0)
Check "throughput >= $MinMBps MB/s" ($mbps -ge $MinMBps) "($mbps MB/s)"

Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($fails -eq 0) { Write-Host "BENCH: ALL PASS" -ForegroundColor Green; exit 0 }
Write-Host "BENCH: $fails FAILURE(S)" -ForegroundColor Red; exit 1
