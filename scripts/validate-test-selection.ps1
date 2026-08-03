#Requires -Version 5.1
<#
.SYNOPSIS
    Replay the last N merged changes through select-tests.ps1 and prove it never skips a suite it
    should have run.

.DESCRIPTION
    Selection is only safe if it is wrong in ONE direction. Running too much costs seconds; skipping
    a suite that mattered puts a regression on main behind a green gate, and a silent skip looks
    exactly like a pass. So this harness does not ask "was the selection minimal" - it asks "was the
    selection ever too small", which is the only question that can hurt.

    THE GROUND TRUTH IS DELIBERATELY INDEPENDENT OF THE MAP. Checking the selector against its own
    dependency graph would be circular and would pass by construction. Instead the required set is
    derived from the changed PATHS alone:

      - a change that edits files inside a test project's own directory REQUIRES that suite. Nobody
        needs a graph to know that editing Engine.Tests means running Engine.Tests.
      - a change that edits any file under src/ REQUIRES a non-empty selection. A .NET source change
        that selects nothing is a bug in the map however plausible its reasoning.
      - a change that edits apps/ or packages/ REQUIRES the web tests.

    That is a weaker statement than "the selection is correct", and it is honest about being so: it
    catches the class of error that is dangerous, not every error. A suite that SHOULD have run
    because of a subtle runtime coupling the reference graph cannot see will not be caught here, and
    no path-based harness could catch it.

.PARAMETER Count
    How many merges into main to replay. Default 100.

.EXAMPLE
    .\scripts\validate-test-selection.ps1
    .\scripts\validate-test-selection.ps1 -Count 250
#>
param(
    [int]$Count = 100
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$selector = Join-Path $PSScriptRoot "select-tests.ps1"

# The merges into main, newest first. First-parent keeps this to real landings rather than every
# commit that rode in on a branch.
$shas = @(& git -C $repoRoot log --first-parent --format=%H -n $Count origin/main)
Write-Host "Replaying $($shas.Count) merged changes through the selector..."
Write-Host ""

$violations = @()
$stats = [ordered]@{ total = 0; runAll = 0; webOnly = 0; noTests = 0; narrowed = 0 }
$suiteCounts = @{}

foreach ($sha in $shas) {
    $files = @(& git -C $repoRoot diff --name-only "$sha^" $sha 2>$null)
    if ($files.Count -eq 0) { continue }
    $stats.total++

    $r = & $selector -Files $files
    foreach ($s in $r.Suites) { $suiteCounts[$s] = 1 + [int]$suiteCounts[$s] }

    if ($r.RunAll) { $stats.runAll++ }
    elseif ($r.Suites.Count -eq 0 -and $r.Web) { $stats.webOnly++ }
    elseif ($r.Suites.Count -eq 0) { $stats.noTests++ }
    else { $stats.narrowed++ }

    $subject = (& git -C $repoRoot log -1 --format=%s $sha)

    # GROUND TRUTH 1: a suite whose OWN files changed must be selected.
    $ownTouched = @($files | ForEach-Object { ($_ -replace '\\','/') } |
        Where-Object { $_ -match '^src/(CcDirector\.[A-Za-z.]*?(?:Unit)?Tests)/' } |
        ForEach-Object { [regex]::Match(($_ -replace '\\','/'), '^src/(CcDirector\.[A-Za-z.]*?(?:Unit)?Tests)/').Groups[1].Value } |
        Sort-Object -Unique)
    foreach ($o in $ownTouched) {
        if ($r.Suites -notcontains $o) {
            $violations += [pscustomobject]@{ Sha=$sha.Substring(0,9); Kind="own files changed but suite not selected"; Detail=$o; Subject=$subject }
        }
    }

    # GROUND TRUTH 2: any .NET source change must select at least one suite.
    $touchedDotNet = @($files | Where-Object { ($_ -replace '\\','/') -match '^src/.*\.(cs|axaml|csproj)$' })
    if ($touchedDotNet.Count -gt 0 -and $r.Suites.Count -eq 0) {
        $violations += [pscustomobject]@{ Sha=$sha.Substring(0,9); Kind="src changed but NO suite selected"; Detail="$($touchedDotNet.Count) files"; Subject=$subject }
    }

    # GROUND TRUTH 3: a web workspace change must select the web tests.
    $touchedWeb = @($files | Where-Object { ($_ -replace '\\','/') -match '^(apps|packages)/.*\.(ts|tsx|js|jsx|css)$' })
    if ($touchedWeb.Count -gt 0 -and -not $r.Web) {
        $violations += [pscustomobject]@{ Sha=$sha.Substring(0,9); Kind="web changed but web tests not selected"; Detail="$($touchedWeb.Count) files"; Subject=$subject }
    }
}

Write-Host "How the $($stats.total) changes were classified:"
Write-Host ("  run everything (fail-safe) : {0,4}   ({1:P0})" -f $stats.runAll,  ($stats.runAll / [Math]::Max(1,$stats.total)))
Write-Host ("  narrowed to some suites    : {0,4}   ({1:P0})" -f $stats.narrowed,($stats.narrowed / [Math]::Max(1,$stats.total)))
Write-Host ("  web tests only             : {0,4}   ({1:P0})" -f $stats.webOnly, ($stats.webOnly / [Math]::Max(1,$stats.total)))
Write-Host ("  no tests at all            : {0,4}   ({1:P0})" -f $stats.noTests, ($stats.noTests / [Math]::Max(1,$stats.total)))
Write-Host ""
Write-Host "How often each suite would have run:"
foreach ($k in ($suiteCounts.Keys | Sort-Object { -$suiteCounts[$_] })) {
    Write-Host ("  {0,-40} {1,4} / {2}   ({3:P0})" -f $k, $suiteCounts[$k], $stats.total, ($suiteCounts[$k] / [Math]::Max(1,$stats.total)))
}
Write-Host ""

if ($violations.Count -eq 0) {
    Write-Host "RESULT: PASS - across $($stats.total) merged changes the selector never skipped a suite it was required to run."
    exit 0
}

Write-Host "RESULT: FAIL - $($violations.Count) violation(s). The selector skipped something it should have run:"
$violations | Select-Object -First 25 | ForEach-Object {
    Write-Host ("  {0}  {1}: {2}" -f $_.Sha, $_.Kind, $_.Detail)
    Write-Host ("      {0}" -f $_.Subject)
}
exit 1
