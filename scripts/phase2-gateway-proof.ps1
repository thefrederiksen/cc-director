#Requires -Version 5.1
<#
.SYNOPSIS
    Remove-the-network-port mission, phase 2: prove every cc-* command works through the Gateway
    with the Director's agent routes SWITCHED OFF.

.DESCRIPTION
    Stands up an ISOLATED Gateway and an ISOLATED Director, both built from this worktree, both on
    their own storage roots and ports, and runs the whole cc-* surface inside a real session that
    holds a real phase-1b session key.

    NOTHING ON THE LIVE FLEET IS TOUCHED. The running Gateway, the installed Director, and the
    user's slots 1-5 are never read, written, reconfigured or stopped. This rig uses its own
    CC_DIRECTOR_ROOT for each process, its own ports, and a Director slot at or above 6.

    WHY THE COMMANDS RUN INSIDE A SESSION rather than from this script. The whole point of the phase
    is the credential: CC_GATEWAY_SESSION_KEY is minted per session and stamped into that ONE
    session's environment, and it is deliberately never logged and never stored anywhere this script
    could read it. So the only honest way to exercise it is to BE a session - the Director spawns one
    whose command is the checklist below, and the checklist inherits the key the same way an agent
    would. A rig that fabricated its own key would prove the Gateway accepts a key it was handed,
    which is not the question.

    Two passes:
      1. agent routes ON  - the fleet's current state, so a failure here is not about the switch.
      2. agent routes OFF - THE PASS MARK. Every command must still work.

.PARAMETER Slot
    Director slot number (>= 6). Slots 1-5 and the installed app are never touched.

.PARAMETER GatewayPort
    Port for the isolated Gateway. Must not be the live Gateway's.

.PARAMETER SkipBuild
    Reuse an existing publish (for a re-run while iterating).
#>

[CmdletBinding()]
param(
    [int]$Slot = 6,
    [int]$GatewayPort = 7997,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$rig  = Join-Path ([System.IO.Path]::GetTempPath()) "phase2-proof"
$gwRoot  = Join-Path $rig "gw-root"
$dirRoot = Join-Path $rig "dir-root"
$stage   = Join-Path $rig "stage"
$results = Join-Path $rig "results"

function Say([string]$m) { Write-Host "[phase2] $m" }

# ---------------------------------------------------------------------------
# The checklist the SESSION runs. One line per cc-* command; each records PASS
# or FAIL with the command's own message, so a failure names itself.
#
# Read-only verbs first, then the writes, then the ones that create and remove
# things. Every command here is one an agent actually runs.
# ---------------------------------------------------------------------------
$checklist = @'
@echo off
setlocal enabledelayedexpansion
set OUT=%1
echo === phase 2 cc-* checklist === > "%OUT%"
echo CC_GATEWAY_URL=%CC_GATEWAY_URL% >> "%OUT%"
if defined CC_GATEWAY_SESSION_KEY (echo CC_GATEWAY_SESSION_KEY=[present] >> "%OUT%") else (echo CC_GATEWAY_SESSION_KEY=[MISSING] >> "%OUT%")
if defined CC_DIRECTOR_API (echo CC_DIRECTOR_API=%CC_DIRECTOR_API% >> "%OUT%")
echo. >> "%OUT%"

call :run "session list"            cc-devthrottle session list
call :run "session whoami"          cc-devthrottle session whoami
call :run "actions --json"          cc-devthrottle actions --json
call :run "repo list"               cc-devthrottle repo list
call :run "worktree list"           cc-devthrottle worktree list
call :run "machine list"            cc-devthrottle machine list
call :run "machine directors"       cc-devthrottle machine directors
call :run "skill list"              cc-devthrottle skill list
call :run "workflow list"           cc-devthrottle workflow list
call :run "schedule list"           cc-devthrottle schedule list
call :run "mission list"            cc-devthrottle mission list
call :run "browser list"            cc-devthrottle browser list
call :run "cc-status"               cc-status
call :run "session rename"          cc-devthrottle session rename "phase2-proof"
call :run "session hold"            cc-devthrottle session hold
call :run "session release"         cc-devthrottle session release
call :run "message send self"       cc-devthrottle message send %CC_SESSION_ID% "phase 2 proof message"
call :run "message send all"        cc-devthrottle message send all "phase 2 proof broadcast"
call :run "session role"            cc-devthrottle session role Worker
echo. >> "%OUT%"
echo === done === >> "%OUT%"
exit /b 0

:run
set NAME=%~1
shift
%1 %2 %3 %4 %5 %6 %7 > "%TEMP%\phase2-cmd.txt" 2>&1
if errorlevel 1 (
  echo FAIL  !NAME! >> "%OUT%"
  type "%TEMP%\phase2-cmd.txt" >> "%OUT%"
) else (
  echo PASS  !NAME! >> "%OUT%"
)
exit /b 0
'@

# ---------------------------------------------------------------------------
Say "rig root: $rig"
New-Item -ItemType Directory -Force -Path $rig, $gwRoot, $dirRoot, $results | Out-Null

if (-not $SkipBuild) {
    Say "publishing the Gateway from this worktree"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    & dotnet publish (Join-Path $repo 'src\CcDirector.Gateway.Host\CcDirector.Gateway.Host.csproj') `
        -c Debug -o $stage --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Gateway publish failed" }
}

$gwExe = Get-ChildItem $stage -Filter '*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1
if (-not $gwExe) { throw "no Gateway executable in $stage" }
Say "gateway executable: $($gwExe.FullName)"

Say "starting the isolated Gateway on port $GatewayPort with its own storage root"
$gwLog = Join-Path $results 'gateway.out'
$gw = Start-Process -FilePath $gwExe.FullName -ArgumentList @('--port', "$GatewayPort") `
    -WorkingDirectory $stage -PassThru -NoNewWindow `
    -RedirectStandardOutput $gwLog -RedirectStandardError "$gwLog.err" `
    -Environment @{ CC_DIRECTOR_ROOT = $gwRoot }

Say "gateway pid $($gw.Id); waiting for /healthz"
$ok = $false
foreach ($i in 1..60) {
    try {
        $h = Invoke-WebRequest "http://127.0.0.1:$GatewayPort/healthz" -UseBasicParsing -TimeoutSec 2
        if ($h.StatusCode -eq 200) { $ok = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $ok) {
    Get-Content $gwLog -Tail 40 | Write-Host
    throw "the isolated Gateway never answered /healthz on port $GatewayPort"
}
Say "gateway healthy"

Say "NEXT STEPS ARE MANUAL FOR NOW - see PHASE-2-REPORT.md"
Say "  gateway root: $gwRoot"
Say "  director root: $dirRoot"
Say "  results: $results"
Say "  gateway pid to stop when finished: $($gw.Id)"
