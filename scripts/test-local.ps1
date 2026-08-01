#Requires -Version 5.1
<#
.SYNOPSIS
    Run this repository's tests locally. THE one command - do not hand-roll a dotnet test invocation.

.DESCRIPTION
    Local is the gate for ordinary changes (issue #1156). GitHub CI takes roughly fifty minutes for the
    .NET job; this machine is far stronger than a CI runner and should be where the answer comes from.

    WHY A SCRIPT RATHER THAN "JUST RUN DOTNET TEST". Two reasons, and the second is the important one.

    1. It is faster than the obvious invocation. Seven test projects exist. Only CcDirector.Gateway.Tests
       serializes itself machine-wide (GatewayTestSuiteLock, and see issue #1156 for why). The other six
       contend with nothing, so they are started IMMEDIATELY and run alongside the Gateway suite while it
       queues for its lock. A plain solution-wide `dotnet test` runs them one project at a time and pays
       the lock wait on top; this pays roughly the slower of the two, not the sum.

    2. It is ONE place to make everyone faster. When the Gateway suite's lock is removed, or in-process
       parallelism becomes safe, this file changes and every agent and person in the fleet gets the
       improvement without being told. A convention that each caller re-implements cannot be improved
       centrally - which is the same argument GatewayTestSuiteLock makes about acquisition being automatic.

.PARAMETER Gateway
    Run ONLY the Gateway suite. Use when the change is Gateway-side and you want its answer first.

.PARAMETER Fast
    Skip the Gateway suite. Use for a change that provably does not touch the Gateway - the six other
    projects together finish in well under a minute. State the reason in the pull request if you rely on it.

.PARAMETER Filter
    An xUnit filter passed through to every project (e.g. "FullyQualifiedName~Dictation").

.EXAMPLE
    .\scripts\test-local.ps1
    .\scripts\test-local.ps1 -Fast
    .\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Tombstone"
#>
param(
    [switch]$Gateway,
    [switch]$Fast,
    [string]$Filter = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repoRoot "cc-director.sln"

if ($Gateway -and $Fast) {
    Write-Error "-Gateway and -Fast are opposites; pass at most one."
    exit 2
}

# The Gateway suite is named separately because it is the only one that serializes machine-wide.
$gatewayProject = "src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj"
$otherProjects = @(
    "src\CcDirector.Core.Tests\CcDirector.Core.Tests.csproj",
    "src\CcDirector.Avalonia.Tests\CcDirector.Avalonia.Tests.csproj",
    "src\CcDirector.Engine.Tests\CcDirector.Engine.Tests.csproj",
    "src\CcDirector.HostedAgent.Tests\CcDirector.HostedAgent.Tests.csproj",
    "src\CcDirector.Launcher.Tests\CcDirector.Launcher.Tests.csproj",
    "src\CcDirector.Terminal.Avalonia.Tests\CcDirector.Terminal.Avalonia.Tests.csproj"
)

$toRun = @()
if (-not $Fast)    { $toRun += $gatewayProject }
if (-not $Gateway) { $toRun += $otherProjects }

Write-Host "Building once, then running $($toRun.Count) test project(s)..."
& dotnet build $sln -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "RESULT: BUILD FAILED - no tests were run."
    exit 1
}

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-local-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$filterArgs = @()
if ($Filter -ne "") { $filterArgs = @("--filter", $Filter) }

# Start every project at once. The Gateway suite may sit in its own queue; the others do not wait for it.
$running = @()
foreach ($proj in $toRun) {
    $name = Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $proj)))
    $out = Join-Path $logDir "$name.log"
    $args = @("test", (Join-Path $repoRoot $proj), "--no-build", "-c", $Configuration, "--nologo", "-v", "q") + $filterArgs
    $p = Start-Process -FilePath "dotnet" -ArgumentList $args -NoNewWindow -PassThru `
                       -RedirectStandardOutput $out -RedirectStandardError "$out.err"

    # Touching .Handle keeps the process handle OPEN, which is the only reason ExitCode is readable
    # after the process ends. Without it Start-Process -PassThru hands back an object whose ExitCode
    # is EMPTY once the child exits - and "$null -eq 0" is false, so every project was classified
    # FAIL. This script is the merge gate, and it was reporting "RESULT: FAILED in 7 project(s)"
    # over seven lines that each said "Passed!  - Failed: 0". A gate that fails on green is worse
    # than no gate: it trains everyone to ignore it, or to go and wait fifty minutes for CI.
    $null = $p.Handle

    $running += [pscustomobject]@{ Name = $name; Process = $p; Log = $out }
    Write-Host "  started $name"
}

Write-Host ""
Write-Host "Waiting. The Gateway suite serializes machine-wide, so it may queue behind another run;"
Write-Host "that is not a hang - it prints its holder every 30s into its log."
Write-Host ""

$failed = @()
foreach ($r in $running) {
    $r.Process.WaitForExit()
    $summary = ""
    if (Test-Path $r.Log) {
        $summary = (Select-String -Path $r.Log -Pattern "^(Passed!|Failed!)" -ErrorAction SilentlyContinue |
                    Select-Object -Last 1).Line
    }
    if ($null -eq $summary -or $summary -eq "") { $summary = "(no summary line - see $($r.Log))" }

    if ($r.Process.ExitCode -eq 0) {
        Write-Host ("  PASS  {0}  {1}" -f $r.Name, $summary.Trim())
    } else {
        Write-Host ("  FAIL  {0}  {1}" -f $r.Name, $summary.Trim())
        $failed += $r
    }
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "RESULT: ALL GREEN. This is the gate - you do not need to wait for GitHub CI to merge."
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    exit 0
}

Write-Host "RESULT: FAILED in $($failed.Count) project(s):"
foreach ($f in $failed) {
    Write-Host "  $($f.Name) -> $($f.Log)"
    $lines = Get-Content $f.Log -Tail 25 -ErrorAction SilentlyContinue
    foreach ($l in $lines) { Write-Host "      $l" }
}
Write-Host ""
Write-Host "Logs kept in $logDir"
exit 1
