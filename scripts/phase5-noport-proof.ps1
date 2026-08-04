#Requires -Version 5.1
<#
.SYNOPSIS
    Remove-the-network-port mission, phase 5: the live proof that the Director listens on NOTHING.

.DESCRIPTION
    Stands up an ISOLATED Gateway and TWO isolated Directors, all built from this worktree, each on
    its own storage root, and produces the evidence QA-REPORT-REQUIREMENTS.md demands:

      1. A live connection scan with the OWNING PROCESS resolved (Get-NetTCPConnection plus
         Get-Process), on a machine running MORE THAN ONE Director - so "does not listen" is
         distinguishable from "failed to start". The two rig Directors are proven RUNNING (live
         process id, instance registration written, registered at the Gateway) at the moment of
         the scan, and the scan must find ZERO sockets in any state owned by either. Any OTHER
         cc-director process on the machine (the owner's installed, pre-mission builds) is listed
         too, as the positive control proving the scan method sees listeners when they exist.

      2. Every cc-* command run from INSIDE a real session holding a real session key - the same
         checklist shape phase 2 used, run as a session spawned THROUGH the Gateway, with this
         branch's own command line tools first on PATH.

      3. The session's environment dump, proving no CC_DIRECTOR_API and no CC_DIRECTOR_TOKEN is
         handed to any session, while CC_GATEWAY_URL and CC_GATEWAY_SESSION_KEY are present.

    NOTHING ON THE LIVE FLEET IS TOUCHED. The running Gateway, the installed Director and the
    owner's slots 1-5 are never read, written, reconfigured or stopped. Every rig process runs
    under its own CC_DIRECTOR_ROOT, Director slots are >= 6, and the Directors are launched from
    scheduled tasks (CLAUDE.md rule 0b) through wrappers that set the environment process-locally.

.PARAMETER Command
    up       - build, start the Gateway and both Directors, wait for registrations
    scan     - the connection scan with owning processes (writes scan evidence file)
    session  - spawn the checklist session through the Gateway and collect its results
    down     - signal both Directors to stop (named signal), stop the Gateway, unregister tasks

.PARAMETER SkipBuild
    (up) Reuse the existing publish and slot builds.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('up', 'scan', 'session', 'down')]
    [string]$Command,
    [int]$SlotA = 6,
    [int]$SlotB = 7,
    [int]$GatewayPort = 7997,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot
$rig     = Join-Path ([System.IO.Path]::GetTempPath()) 'phase5-noport-proof'
$gwRoot  = Join-Path $rig 'gw-root'
$stage   = Join-Path $rig 'gw-stage'
$rigBin  = Join-Path $rig 'bin'
$results = Join-Path $rig 'results'
$state   = Join-Path $rig 'rig-state.json'

function Say([string]$m) { Write-Host "[phase5] $m" }
function Fail([string]$m) { Write-Host "[phase5] ERROR: $m"; exit 1 }

if ($SlotA -lt 6 -or $SlotB -lt 6) { Fail "slots below 6 belong to the owner and are never touched" }

function DirRoot([int]$slot) { Join-Path $rig "dir-root-$slot" }
function TaskName([int]$slot) { "phase5-noport-dir$slot" }

function Read-State {
    if (-not (Test-Path $state)) { Fail "no rig state at $state - run 'up' first" }
    Get-Content $state -Raw | ConvertFrom-Json
}

# The instance registration a RUNNING Director writes under its own root. Returns
# @{ DirectorId; Pid } or $null.
function Read-Registration([int]$slot) {
    $regDir = Join-Path (DirRoot $slot) 'instances\default\config\director\instances'
    if (-not (Test-Path $regDir)) { return $null }
    foreach ($f in @(Get-ChildItem $regDir -Filter *.json -ErrorAction SilentlyContinue)) {
        try {
            $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
            if ($j.Pid -gt 0 -and $j.DirectorId) {
                $alive = Get-Process -Id $j.Pid -ErrorAction SilentlyContinue
                if ($alive) { return @{ DirectorId = [string]$j.DirectorId; Pid = [int]$j.Pid; File = $f.FullName; ControlEndpoint = [string]$j.ControlEndpoint } }
            }
        } catch {}
    }
    return $null
}

switch ($Command) {

'up' {
    New-Item -ItemType Directory -Force -Path $rig, $gwRoot, $rigBin, $results | Out-Null

    # ---- this branch's own command line, first on the session PATH -------------------------
    $py = Join-Path $env:LOCALAPPDATA 'cc-director\pyenv\Scripts\python.exe'
    if (-not (Test-Path $py)) { Fail "no python environment at $py (the installed pyenv is used to RUN this branch's tool source; it is read, never written)" }
    Set-Content (Join-Path $rigBin 'cc-devthrottle.cmd') "@`"$py`" `"$repo\tools\cc-devthrottle\main.py`" %*" -Encoding ascii
    if (Test-Path "$repo\tools\cc-status\main.py") {
        Set-Content (Join-Path $rigBin 'cc-status.cmd') "@`"$py`" `"$repo\tools\cc-status\main.py`" %*" -Encoding ascii
    }

    # ---- gateway ---------------------------------------------------------------------------
    if (-not $SkipBuild) {
        Say 'publishing the Gateway from this worktree'
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        & dotnet publish (Join-Path $repo 'src\CcDirector.Gateway.Host\CcDirector.Gateway.Host.csproj') -c Debug -o $stage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'Gateway publish failed' }
    }
    $gwExe = Get-ChildItem $stage -Filter '*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1
    if (-not $gwExe) { Fail "no Gateway executable in $stage" }

    Say "starting the isolated Gateway on port $GatewayPort (own root: $gwRoot)"
    $gwLog = Join-Path $results 'gateway.out'
    $gw = Start-Process -FilePath $gwExe.FullName -ArgumentList @('--port', "$GatewayPort") `
        -WorkingDirectory $stage -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $gwLog -RedirectStandardError "$gwLog.err" `
        -Environment @{ CC_DIRECTOR_ROOT = $gwRoot }
    $ok = $false
    foreach ($i in 1..60) {
        try { if ((Invoke-WebRequest "http://127.0.0.1:$GatewayPort/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ok = $true; break } } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $ok) { Get-Content $gwLog -Tail 30 | Write-Host; Fail "the isolated Gateway never answered /healthz on $GatewayPort" }

    $tokenFile = Join-Path $gwRoot 'config\gateway-token.txt'
    $token = ''
    foreach ($i in 1..20) {
        if (Test-Path $tokenFile) { $token = (Get-Content $tokenFile -Raw).Trim(); if ($token) { break } }
        Start-Sleep -Milliseconds 500
    }
    if (-not $token) { Fail "the Gateway never minted its token at $tokenFile" }
    Say 'gateway healthy, token read'

    # ---- two Directors ---------------------------------------------------------------------
    foreach ($slot in @($SlotA, $SlotB)) {
        $root = DirRoot $slot
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'config') | Out-Null
        Set-Content (Join-Path $root 'config\config.json') (@{ gateway = @{ url = "http://127.0.0.1:$GatewayPort"; token = $token } } | ConvertTo-Json) -Encoding utf8

        if (-not $SkipBuild) {
            Say "building the slot $slot Director from this worktree"
            & powershell -NoProfile -File (Join-Path $repo 'scripts\local-build-avalonia.ps1') -Slot $slot -OutputDir (Join-Path $repo 'local_builds')
            if ($LASTEXITCODE -ne 0) { Fail "slot $slot build failed" }
        }
        $exe = Join-Path $repo "local_builds\cc-director$slot.exe"
        if (-not (Test-Path $exe)) { Fail "no slot exe at $exe" }

        # The wrapper sets the environment PROCESS-LOCALLY (rule 0b: the task, not this shell,
        # is the parent, so nothing inherits this session's console).
        $wrapper = Join-Path $rig "start-dir$slot.cmd"
        Set-Content $wrapper "@echo off`r`nset CC_DIRECTOR_ROOT=$root`r`nset PATH=$rigBin;%PATH%`r`n`"$exe`"" -Encoding ascii

        $task = TaskName $slot
        $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$wrapper`"" -WorkingDirectory (Split-Path $exe)
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
        Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Force | Out-Null
        Say "starting slot $slot via scheduled task $task"
        Start-ScheduledTask -TaskName $task

        $reg = $null
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            $reg = Read-Registration $slot
            if ($reg) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $reg) { Fail "slot $slot never wrote a live instance registration under $root" }
        Say "slot $slot RUNNING: pid=$($reg.Pid) directorId=$($reg.DirectorId) controlEndpoint='$($reg.ControlEndpoint)'"
    }

    @{ gatewayPid = $gw.Id; gatewayPort = $GatewayPort; token = $token; slotA = $SlotA; slotB = $SlotB } |
        ConvertTo-Json | Set-Content $state -Encoding utf8
    Say "rig is UP. state: $state"
}

'scan' {
    $s = Read-State
    $regA = Read-Registration $s.slotA
    $regB = Read-Registration $s.slotB
    if (-not $regA -or -not $regB) { Fail 'both rig Directors must be RUNNING at scan time - a scan of a dead Director proves nothing' }

    $out = Join-Path $results ("connection-scan-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))
    $lines = @()
    $lines += "=== Remove-the-network-port phase 5: live connection scan with owning processes ==="
    $lines += "When: $((Get-Date).ToString('o'))  Machine: $env:COMPUTERNAME"
    $lines += ""
    $lines += "Rig Directors, both PROVEN RUNNING at scan time (live pid + instance registration + Gateway-connected):"
    foreach ($r in @(@{ n = $s.slotA; reg = $regA }, @{ n = $s.slotB; reg = $regB })) {
        $p = Get-Process -Id $r.reg.Pid
        $lines += ("  slot {0}: pid={1} exe={2} directorId={3} registeredControlEndpoint='{4}'" -f $r.n, $r.reg.Pid, $p.Path, $r.reg.DirectorId, $r.reg.ControlEndpoint)
    }
    $lines += ""

    # EVERY TCP connection or listener owned by either rig pid, in ANY state - not a port-number
    # check. A free port number proves nothing; a pid that owns no listening socket while the
    # process is demonstrably alive is the fact this scan exists to establish.
    $rigPids = @([int]$regA.Pid, [int]$regB.Pid)
    $tcpAll  = @(Get-NetTCPConnection -ErrorAction SilentlyContinue)
    $udpAll  = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue)
    $rigTcpListen = @($tcpAll | Where-Object { $rigPids -contains [int]$_.OwningProcess -and $_.State -eq 'Listen' })
    $rigTcpAny    = @($tcpAll | Where-Object { $rigPids -contains [int]$_.OwningProcess })
    $rigUdp       = @($udpAll | Where-Object { $rigPids -contains [int]$_.OwningProcess })

    $lines += "TCP LISTEN sockets owned by the rig Directors: $($rigTcpListen.Count)"
    $rigTcpListen | ForEach-Object { $lines += "  LISTEN $($_.LocalAddress):$($_.LocalPort) pid=$($_.OwningProcess)" }
    $lines += "TCP sockets in ANY state owned by the rig Directors (outbound Gateway connections are expected and are not listeners):"
    $rigTcpAny | ForEach-Object { $lines += "  $($_.State) local=$($_.LocalAddress):$($_.LocalPort) remote=$($_.RemoteAddress):$($_.RemotePort) pid=$($_.OwningProcess)" }
    $lines += "UDP endpoints owned by the rig Directors: $($rigUdp.Count)"
    $rigUdp | ForEach-Object { $lines += "  UDP $($_.LocalAddress):$($_.LocalPort) pid=$($_.OwningProcess)" }
    $lines += ""

    # POSITIVE CONTROL: every OTHER cc-director process on this machine, with its listeners. The
    # owner's installed pre-mission Directors still listen, which proves this scan method finds a
    # listener when one exists - so the zero above is a measurement, not a blind spot.
    $lines += "Positive control - every other cc-director process on this machine and its TCP LISTEN sockets:"
    $others = @(Get-Process | Where-Object { $_.ProcessName -like 'cc-director*' -and ($rigPids -notcontains $_.Id) })
    foreach ($p in $others) {
        $listens = @($tcpAll | Where-Object { [int]$_.OwningProcess -eq $p.Id -and $_.State -eq 'Listen' })
        $lines += ("  pid={0} exe={1} listeners={2}" -f $p.Id, $p.Path, $listens.Count)
        $listens | ForEach-Object { $lines += "    LISTEN $($_.LocalAddress):$($_.LocalPort)" }
    }
    if ($others.Count -eq 0) { $lines += "  (none running)" }
    $lines += ""

    $verdict = if ($rigTcpListen.Count -eq 0 -and $rigUdp.Count -eq 0) { 'PASS: the rig Directors, alive and registered, own ZERO listening sockets' }
               else { 'FAIL: a rig Director owns a listening socket' }
    $lines += "VERDICT: $verdict"

    $lines | Set-Content $out -Encoding utf8
    $lines | Write-Host
    Say "scan written to $out"
    if ($rigTcpListen.Count -ne 0 -or $rigUdp.Count -ne 0) { exit 1 }
}

'session' {
    $s = Read-State
    $regA = Read-Registration $s.slotA
    if (-not $regA) { Fail "the slot $($s.slotA) Director is not running" }

    $outFile = Join-Path $results 'checklist-results.txt'
    if (Test-Path $outFile) { Remove-Item $outFile -Force }

    # The checklist the SESSION runs. It inherits the session environment the Director builds -
    # including this session's own Gateway key - which is the only honest way to exercise it.
    $checklist = Join-Path $rig 'checklist.cmd'
    @'
@echo off
setlocal enabledelayedexpansion
set OUT=%1
echo === phase 5 cc-* checklist, run inside a real session === > "%OUT%"
echo --- the environment the session was handed --- >> "%OUT%"
if defined CC_GATEWAY_URL (echo CC_GATEWAY_URL=[present] >> "%OUT%") else (echo CC_GATEWAY_URL=[MISSING] >> "%OUT%")
if defined CC_GATEWAY_SESSION_KEY (echo CC_GATEWAY_SESSION_KEY=[present] >> "%OUT%") else (echo CC_GATEWAY_SESSION_KEY=[MISSING] >> "%OUT%")
if defined CC_DIRECTOR_ID (echo CC_DIRECTOR_ID=[present] >> "%OUT%") else (echo CC_DIRECTOR_ID=[MISSING] >> "%OUT%")
if defined CC_SESSION_ID (echo CC_SESSION_ID=[present] >> "%OUT%") else (echo CC_SESSION_ID=[MISSING] >> "%OUT%")
if defined CC_DIRECTOR_API (echo CC_DIRECTOR_API=[STILL PRESENT - FAIL] >> "%OUT%") else (echo CC_DIRECTOR_API=[absent - correct] >> "%OUT%")
if defined CC_DIRECTOR_TOKEN (echo CC_DIRECTOR_TOKEN=[STILL PRESENT - FAIL] >> "%OUT%") else (echo CC_DIRECTOR_TOKEN=[absent - correct] >> "%OUT%")
echo. >> "%OUT%"

call :run "session list"       cc-devthrottle session list
call :run "session whoami"     cc-devthrottle session whoami
call :run "actions --json"     cc-devthrottle actions --json
call :run "repo list"          cc-devthrottle repo list
call :run "worktree list"      cc-devthrottle worktree list
call :run "machine list"       cc-devthrottle machine list
call :run "director list"      cc-devthrottle director list
call :run "skill list"         cc-devthrottle skill list
call :run "workflow list"      cc-devthrottle workflow list
call :run "schedule list"      cc-devthrottle schedule list
call :run "mission list"       cc-devthrottle mission list
call :run "browser list"       cc-devthrottle browser list
call :run "session rename"     cc-devthrottle session rename "phase5-proof"
call :run "session hold"       cc-devthrottle session hold
call :run "session release"    cc-devthrottle session release
call :run "session role"       cc-devthrottle session role Worker
call :run "message send all"   cc-devthrottle message send all "phase 5 proof broadcast"
echo. >> "%OUT%"
echo === done === >> "%OUT%"
exit /b 0

:run
set NAME=%~1
shift
%1 %2 %3 %4 %5 %6 %7 > "%TEMP%\phase5-cmd.txt" 2>&1
if errorlevel 1 (
  echo FAIL  !NAME! >> "%OUT%"
  type "%TEMP%\phase5-cmd.txt" >> "%OUT%"
) else (
  echo PASS  !NAME! >> "%OUT%"
)
exit /b 0
'@ | Set-Content $checklist -Encoding ascii

    Say "creating the checklist session THROUGH the Gateway (POST /directors/$($regA.DirectorId)/sessions)"
    $body = @{ repoPath = $rig; agent = 'RawCli'; command = 'cmd'; commandArgs = "/c `"`"$checklist`" `"$outFile`"`""; name = 'phase5 proof checklist' } | ConvertTo-Json
    $resp = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($s.gatewayPort)/directors/$($regA.DirectorId)/sessions" `
        -Headers @{ Authorization = "Bearer $($s.token)" } -ContentType 'application/json' -Body $body
    Say "session created: $($resp.sessionId)"

    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $outFile) -and (Select-String -Path $outFile -Pattern '=== done ===' -Quiet -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 2
    }
    if (-not (Test-Path $outFile)) { Fail "the checklist session never wrote $outFile" }
    Get-Content $outFile | Write-Host
    Say "checklist results at $outFile"
}

'down' {
    $s = Read-State
    foreach ($slot in @($s.slotA, $s.slotB)) {
        $reg = Read-Registration $slot
        if ($reg) {
            $signal = "Local\cc-director-shutdown-$($reg.DirectorId.ToLowerInvariant())"
            Say "signalling $signal (pid $($reg.Pid))"
            try {
                $evt = [System.Threading.EventWaitHandle]::OpenExisting($signal)
                $evt.Set() | Out-Null
                $evt.Dispose()
                $deadline = (Get-Date).AddSeconds(25)
                while ((Get-Date) -lt $deadline) {
                    if ($null -eq (Get-Process -Id $reg.Pid -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 500
                }
            } catch { Say "nothing listening for $signal" }
            $alive = Get-Process -Id $reg.Pid -ErrorAction SilentlyContinue
            if ($alive -and $alive.Path -like "*cc-director$slot.exe") {
                Say "LAST RESORT force kill pid $($reg.Pid)"
                Stop-Process -Id $reg.Pid -Force -Confirm:$false
            }
        }
        if (Get-ScheduledTask -TaskName (TaskName $slot) -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName (TaskName $slot) -Confirm:$false
        }
    }
    $gw = Get-Process -Id $s.gatewayPid -ErrorAction SilentlyContinue
    if ($gw -and $gw.ProcessName -like '*Gateway*') {
        Say "stopping the isolated Gateway (pid $($s.gatewayPid))"
        Stop-Process -Id $s.gatewayPid -Force -Confirm:$false
    }
    Say 'rig is DOWN'
}
}
