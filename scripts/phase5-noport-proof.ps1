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
    #
    # IT MUST BE THIS BRANCH'S SOURCE, AND PROVING THAT TOOK A CHECK. Running the obvious
    # `python tools\cc-devthrottle\main.py` does NOT do it: main.py says
    # `from cc_devthrottle.cli import app`, which resolves to the INSTALLED package in the pyenv's
    # site-packages - pre-mission code that still knows about CC_DIRECTOR_API. A checklist run that
    # way would have proved the old tools work, which is worse than proving nothing. The branch
    # package is `src`, so the shim puts the tool directory on PYTHONPATH and enters through it.
    # (The installed pyenv is used only as an interpreter with the right dependencies; it is read,
    # never written.)
    $py = Join-Path $env:LOCALAPPDATA 'cc-director\pyenv\Scripts\python.exe'
    if (-not (Test-Path $py)) { Fail "no python environment at $py" }
    $toolsDir = Join-Path $repo 'tools'
    $ccdtDir  = Join-Path $toolsDir 'cc-devthrottle'
    $shim = "@echo off`r`nset PYTHONPATH=$ccdtDir;$toolsDir`r`n`"$py`" -c `"from src.cli import app; app()`" %*"
    Set-Content (Join-Path $rigBin 'cc-devthrottle.cmd') $shim -Encoding ascii

    # ---- gateway ---------------------------------------------------------------------------
    # -SkipBuild means REUSE WHAT EXISTS, so it cannot skip a build whose output is not there -
    # skipping into a missing stage would fail with a path error that says nothing about the cause.
    # CcDirector.GatewayApp (devthrottle-gateway.exe) is the SELF-HOSTED Gateway. Not
    # CcDirector.Gateway.Host: that is the HOSTED image and it fails closed when the hosted
    # contract is absent, by design, rather than downgrading to single-tenant no-auth semantics -
    # phase 2's report records the same correction.
    $stagedExe = if (Test-Path $stage) { @(Get-ChildItem $stage -Filter 'devthrottle-gateway.exe') } else { @() }
    if (-not $SkipBuild -or $stagedExe.Count -eq 0) {
        if ($SkipBuild) { Say 'no staged Gateway to reuse - publishing it anyway' }
        else { Say 'publishing the self-hosted Gateway from this worktree' }
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        & dotnet publish (Join-Path $repo 'src\CcDirector.GatewayApp\CcDirector.GatewayApp.csproj') -c Debug -o $stage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'Gateway publish failed' }
    }
    $gwExe = Get-ChildItem $stage -Filter 'devthrottle-gateway.exe' | Select-Object -First 1
    if (-not $gwExe) { Fail "no devthrottle-gateway.exe in $stage" }

    Say "starting the isolated Gateway on port $GatewayPort (own root: $gwRoot)"
    $gwLog = Join-Path $results 'gateway.out'
    # A wrapper sets CC_DIRECTOR_ROOT process-locally. Start-Process -Environment is PowerShell 7
    # only and this fleet's scripts run under Windows PowerShell 5.1, where it fails with an
    # unhelpful "parameter cannot be found" - the same wrapper shape the Directors below use.
    # --no-autostart is NOT optional here: without it the rig Gateway would write the user's HKCU
    # Run key and this throwaway build would be launched on every login. Never touch the owner's
    # autostart. --managed is deliberately absent too, so it supervises no Cockpit and never
    # self-updates.
    $gwWrapper = Join-Path $rig 'start-gateway.cmd'
    Set-Content $gwWrapper "@echo off`r`nset CC_DIRECTOR_ROOT=$gwRoot`r`n`"$($gwExe.FullName)`" --port $GatewayPort --no-autostart" -Encoding ascii
    $gw = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', "`"$gwWrapper`"") `
        -WorkingDirectory $stage -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $gwLog -RedirectStandardError "$gwLog.err"
    $ok = $false
    foreach ($i in 1..60) {
        try { if ((Invoke-WebRequest "http://127.0.0.1:$GatewayPort/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ok = $true; break } } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $ok) { Get-Content $gwLog -Tail 30 | Write-Host; Fail "the isolated Gateway never answered /healthz on $GatewayPort" }

    # The pid recorded must be the GATEWAY's, not the cmd.exe wrapper's - 'down' stops what this
    # records, and stopping the wrapper would leave the Gateway running as an orphan. Resolved by
    # exact image path so it can only ever be the executable this rig staged.
    $gwProc = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $gwExe.FullName) }) | Select-Object -First 1
    if (-not $gwProc) { Fail "the Gateway answered /healthz but no process is running $($gwExe.FullName)" }
    Say "gateway pid $($gwProc.Id) (wrapper cmd was $($gw.Id))"

    $tokenFile = Join-Path $gwRoot 'config\director\gateway-token.txt'
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
        # The Director reads its config from its INSTANCE HOME, not the storage root - phase 2 lost
        # time to exactly this, so it is written where the running Director actually looks.
        $instanceConfig = Join-Path $root 'instances\default\config'
        New-Item -ItemType Directory -Force -Path $instanceConfig | Out-Null
        Set-Content (Join-Path $instanceConfig 'config.json') (@{ gateway = @{ url = "http://127.0.0.1:$GatewayPort"; token = $token } } | ConvertTo-Json) -Encoding utf8

        # THIS BRANCH'S TOOLS WHERE THE DIRECTOR ACTUALLY LOOKS. SessionManager puts its OWN
        # instance bin FIRST on every session's PATH (deliberately - a stale copy elsewhere on the
        # machine PATH must never win), so a shim in the rig's bin loses to whatever lives here.
        # The first run of this rig proved it: the session resolved
        # <root>\instances\default\bin\cc-devthrottle.cmd - the INSTALLED pre-mission tool - and
        # every fleet command failed with "CC_DIRECTOR_API is not set". Installing the branch shim
        # here is what an upgraded machine looks like, and it is the only way the checklist tests
        # the command line under proof.
        $instanceBin = Join-Path $root 'instances\default\bin'
        New-Item -ItemType Directory -Force -Path $instanceBin | Out-Null
        Set-Content (Join-Path $instanceBin 'cc-devthrottle.cmd') $shim -Encoding ascii

        $exe = Join-Path $repo "local_builds\cc-director$slot.exe"
        if (-not $SkipBuild -or -not (Test-Path $exe)) {
            Say "building the slot $slot Director from this worktree"
            & powershell -NoProfile -File (Join-Path $repo 'scripts\local-build-avalonia.ps1') -Slot $slot -OutputDir (Join-Path $repo 'local_builds')
            if ($LASTEXITCODE -ne 0) { Fail "slot $slot build failed" }
        }
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

    @{ gatewayPid = $gwProc.Id; gatewayExe = $gwExe.FullName; gatewayPort = $GatewayPort; token = $token; slotA = $SlotA; slotB = $SlotB } |
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
    if ($others.Count -eq 0) { $lines += "  (none running - the positive control is then absent, and this scan proves less; say so rather than reading zero as proof)" }
    $lines += ""

    # THE LAUNCHER IS PHASE 6, NOT THIS PHASE. The mission's requirements name both the Director and
    # the launcher, so a scan that silently omitted the launcher would let a reader take this file as
    # proof of something it never measured. Reported explicitly, whatever it says.
    $lines += "Scope note - cc-launcher processes (PHASE 6's listener, deliberately still present):"
    $launchers = @(Get-Process | Where-Object { $_.ProcessName -like 'cc-launcher*' })
    foreach ($p in $launchers) {
        $listens = @($tcpAll | Where-Object { [int]$_.OwningProcess -eq $p.Id -and $_.State -eq 'Listen' })
        $lines += ("  pid={0} exe={1} listeners={2}" -f $p.Id, $p.Path, $listens.Count)
        $listens | ForEach-Object { $lines += "    LISTEN $($_.LocalAddress):$($_.LocalPort)" }
    }
    if ($launchers.Count -eq 0) { $lines += "  (no launcher running on this machine right now)" }
    $lines += "  This phase removed the DIRECTOR's listener only. Any launcher listener above is expected"
    $lines += "  and is phase 6's work; it is NOT evidence about phase 5 either way."
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

    # The checklist the SESSION runs, in PowerShell rather than batch. The first version used a
    # `call :run` subroutine with `shift`, and it produced the environment dump but not one single
    # command line - a checklist that silently ran nothing while looking like it had run. Debugging
    # batch quoting is not worth it when the failure mode is "reports nothing and looks fine";
    # PowerShell records each command's exit code and its own output directly.
    $checklistPs = Join-Path $rig 'checklist.ps1'
    @'
param([Parameter(Mandatory=$true)][string]$Out)
$ErrorActionPreference = 'Continue'
function W([string]$line) { Add-Content -Path $Out -Value $line -Encoding utf8 }

W '=== phase 5 cc-* checklist, run inside a real session ==='
W '--- the environment the session was handed ---'
foreach ($pair in @(
  @('CC_GATEWAY_URL', $true), @('CC_GATEWAY_SESSION_KEY', $true), @('CC_DIRECTOR_ID', $true),
  @('CC_SESSION_ID', $true), @('CC_DIRECTOR_API', $false), @('CC_DIRECTOR_TOKEN', $false))) {
  $name, $wanted = $pair
  $present = [bool](Get-Item "env:$name" -ErrorAction SilentlyContinue)
  $verdict = if ($present -eq $wanted) { 'correct' } else { 'WRONG' }
  W ("  {0,-26} present={1,-5} {2}" -f $name, $present, $verdict)
}
W ''
W ('--- which cc-devthrottle PATH resolves (must be this branch, not the install) ---')
$resolved = (Get-Command cc-devthrottle -ErrorAction SilentlyContinue)
W ("  {0}" -f ($(if ($resolved) { $resolved.Source } else { '(NOT ON PATH)' })))
W ''

$commands = @(
  @('session list',    @('session','list')),
  @('session whoami',  @('session','whoami')),
  @('actions --json',  @('actions','--json')),
  @('repo list',       @('repo','list')),
  @('worktree list',   @('worktree','list')),
  @('machine list',    @('machine','list')),
  @('director list',   @('director','list')),
  @('skill list',      @('skill','list')),
  @('workflow list',   @('workflow','list')),
  @('schedule list',   @('schedule','list')),
  @('mission list',    @('mission','list')),
  @('browser list',    @('browser','list')),
  @('session rename',  @('session','rename','phase5-proof')),
  @('session hold',    @('session','hold')),
  # `session release` is NOT a command - `hold --release` is. The first run of this checklist
  # invented it and the tool answered "Usage:" with exit 2, which is the command line being right
  # and the rig being wrong. Recorded rather than quietly corrected, because a rig error that
  # looks like a product failure is worth exactly one sentence the next reader will not have to
  # re-derive.
  @('session hold --release', @('session','hold','--release')),
  @('session role',    @('session','role','Worker')),
  @('message send all',@('message','send','all','phase 5 proof broadcast'))
)
foreach ($c in $commands) {
  $name, $cmdArgs = $c
  $output = & cc-devthrottle @cmdArgs 2>&1 | Out-String
  if ($LASTEXITCODE -eq 0) {
    W ("PASS  {0}" -f $name)
  } else {
    W ("FAIL  {0}  (exit {1})" -f $name, $LASTEXITCODE)
    foreach ($l in ($output -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 4)) { W ("        {0}" -f $l.TrimEnd()) }
  }
}
W ''
W '=== done ==='
'@ | Set-Content $checklistPs -Encoding utf8

    Say "creating the checklist session THROUGH the Gateway (POST /directors/$($regA.DirectorId)/sessions)"
    $psArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$checklistPs`" -Out `"$outFile`""
    $body = @{ repoPath = $rig; agent = 'RawCli'; command = 'powershell'; commandArgs = $psArgs; name = 'phase5 proof checklist' } | ConvertTo-Json
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
    # Exact image path, so this can only ever stop the executable this rig staged - never a Gateway
    # the owner is running.
    $gw = Get-Process -Id $s.gatewayPid -ErrorAction SilentlyContinue
    if ($gw -and $gw.Path -and ($gw.Path -ieq $s.gatewayExe)) {
        Say "stopping the isolated Gateway (pid $($s.gatewayPid))"
        Stop-Process -Id $s.gatewayPid -Force -Confirm:$false
    } elseif ($gw) {
        Say "NOT stopping pid $($s.gatewayPid): its image $($gw.Path) is not this rig's Gateway"
    }
    Say 'rig is DOWN'
}
}
