#Requires -Version 5.1
<#
.SYNOPSIS
    Terminal Rules mission (issue #2644), phase 0, row 4: a screen captured on one machine is read back
    from the Gateway WHILE THAT MACHINE IS OFFLINE.

.DESCRIPTION
    Stands up a THROWAWAY Gateway and a THROWAWAY Director, both built from this worktree, both on their
    own storage roots and their own port, has the Director really end a turn, really stops it, and only
    then reads the screen back out of the Gateway's own store.

    NOTHING THAT SURVIVES THE RUN IS TOUCHED, and that is a hard constraint rather than good manners.
    This mission's session_screens migration is PROVISIONAL: when pull request 2643 lands it is deleted
    and regenerated, so its migration id will never exist again. EF stamps __EFMigrationsHistory by id,
    so any database this rig opened would be left holding a row for a migration that no longer exists and
    missing the one that replaced it - permanently, with nothing to warn anyone later. So:

      - the Gateway gets its OWN CC_DIRECTOR_ROOT, created here and deleted in the teardown, which is
        where its SQLite database lives. Never the installed Gateway, never the hosted one, never a
        database any other person, session or machine uses, and never one on a shared file share.
      - the Director gets its OWN CC_DIRECTOR_ROOT and its own executable, and it dials THIS Gateway.
        It is not one of the fleet's Directors and it does not join the live fleet - a test Gateway with
        a live Director attached would put the owner's real sessions behind an unlanded build.
      - TEARDOWN IS PART OF THE ROW, not a tidy-up after it. It runs in a finally block, so a failure
        half way through still stops both processes and removes both roots.

    WHAT EACH STEP PROVES, because the point of the row is the chain no in-process test covers: the
    screen is captured by the real TurnReviewLogger, sent by the real GatewayScreenSink, carried by the
    real PushScreen hub method, and stored by the real SessionScreenStore on a real MIGRATED database.

.PARAMETER GatewayPort
    Port for the throwaway Gateway. Must not be the live Gateway's.

.PARAMETER Slot
    Director slot number, 6 or above. Slots 1-5 and the installed application are never touched.

.PARAMETER KeepRig
    Leave the rig root on disk after the run, for inspection. The processes are still stopped. Use this
    only when a run has failed and you need to read its logs; it leaves a database stamped with a
    provisional migration id, so delete it yourself afterwards.

.PARAMETER SkipBuild
    Reuse an existing publish while iterating on the script.
#>

[CmdletBinding()]
param(
    [int]$GatewayPort = 7996,
    [int]$Slot = 6,
    [switch]$KeepRig,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ($Slot -lt 6) { throw "Slot must be 6 or above - slots 1 to 5 belong to the owner's own Directors." }

$repo    = Split-Path -Parent $PSScriptRoot
$stamp   = (Get-Date -Format 'yyyyMMdd-HHmmss')
$rig     = Join-Path ([System.IO.Path]::GetTempPath()) "screen-proof-$stamp-$([guid]::NewGuid().ToString('N').Substring(0,8))"
$gwRoot  = Join-Path $rig 'gw-root'
$dirRoot = Join-Path $rig 'dir-root'
$stage   = Join-Path $rig 'stage'
$results = Join-Path $rig 'results'

$script:GatewayProcess  = $null
$script:DirectorPid     = 0
$script:DirectorExe     = ''
$script:TaskName        = "terminal-rules-screen-proof-$Slot"
$script:TaskRegistered  = $false

function Say([string]$m) { Write-Host "[screen-proof] $m" }
function Fail([string]$m) { throw "[screen-proof] ROW 4 FAILED: $m" }

# ---------------------------------------------------------------- teardown ----
# Ruling 8: teardown is part of the row. It runs whatever happened above.
function Invoke-Teardown {
    Say '--- teardown ---'

    if ($script:DirectorPid -gt 0) {
        try { Stop-RigDirector } catch { Say "director stop raised: $($_.Exception.Message)" }
    }

    if ($script:TaskRegistered) {
        try {
            Unregister-ScheduledTask -TaskName $script:TaskName -Confirm:$false -ErrorAction Stop
            Say "unregistered scheduled task $($script:TaskName)"
        } catch { Say "could not unregister $($script:TaskName): $($_.Exception.Message)" }
    }

    if ($null -ne $script:GatewayProcess) {
        try {
            $gw = Get-Process -Id $script:GatewayProcess.Id -ErrorAction SilentlyContinue
            # Confirm the image path before stopping anything - never kill a process this rig did not start.
            if ($gw -and $gw.Path -and $gw.Path.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $gw.Id -Force -Confirm:$false
                Say "stopped the throwaway Gateway (pid $($gw.Id))"
            }
        } catch { Say "could not stop the Gateway: $($_.Exception.Message)" }
    }

    if ($KeepRig) {
        Say "KEEPING the rig root at $rig - it holds a database stamped with a PROVISIONAL migration id; delete it yourself"
        return
    }
    # The database goes with it. That is the point: a provisional migration id must not outlive the run.
    try {
        Remove-Item -Recurse -Force $rig -ErrorAction Stop
        Say "removed the rig root $rig, database included"
    } catch { Say "could not remove $($rig): $($_.Exception.Message)" }
}

# Which Director a process id is, read from the registration the running process writes into ITS OWN
# root. The fleet helper looks under %LOCALAPPDATA%\cc-director; this rig's Director writes under its
# own CC_DIRECTOR_ROOT, so the lookup is scoped there and can never name one of the owner's Directors.
function Get-RigDirectorId([int]$DirectorPid) {
    # NOT $home - that is a read-only PowerShell automatic variable and assigning it in the loop throws.
    $instanceHomes = @($dirRoot)
    $instancesRoot = Join-Path $dirRoot 'instances'
    if (Test-Path $instancesRoot) {
        $instanceHomes += @(Get-ChildItem $instancesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    foreach ($instanceHome in $instanceHomes) {
        $regDir = Join-Path $instanceHome 'config\director\instances'
        if (-not (Test-Path $regDir)) { continue }
        foreach ($f in @(Get-ChildItem $regDir -Filter *.json -ErrorAction SilentlyContinue)) {
            try {
                $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
                if ($j.Pid -eq $DirectorPid -and $j.DirectorId) { return [string]$j.DirectorId }
            } catch {}
        }
    }
    return ''
}

# Stop the rig's Director the right way - a named signal, so it kills its own sessions and deletes its
# crash journal instead of leaving a phantom "interrupted" entry. Force-kill is the last resort and only
# ever against this rig's own executable path.
function Stop-RigDirector {
    $p = Get-Process -Id $script:DirectorPid -ErrorAction SilentlyContinue
    if ($null -eq $p) { Say "director pid $($script:DirectorPid) already gone"; return }

    $directorId = Get-RigDirectorId $script:DirectorPid
    $exited = $false
    if ($directorId) {
        $signal = "Local\cc-director-shutdown-$($directorId.ToLowerInvariant())"
        Say "signalling $signal (pid $($script:DirectorPid))"
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting($signal)
            $evt.Set() | Out-Null
            $evt.Dispose()
            $deadline = (Get-Date).AddSeconds(25)
            while ((Get-Date) -lt $deadline) {
                if ($null -eq (Get-Process -Id $script:DirectorPid -ErrorAction SilentlyContinue)) { $exited = $true; break }
                Start-Sleep -Milliseconds 250
            }
        } catch { Say "nothing is listening for $signal" }
    } else {
        Say "no registration under $dirRoot names pid $($script:DirectorPid)"
    }

    if (-not $exited) {
        $alive = Get-Process -Id $script:DirectorPid -ErrorAction SilentlyContinue
        if ($alive -and $alive.Path -and ($alive.Path -ieq $script:DirectorExe)) {
            Say "clean shutdown did not exit in time - force killing pid $($script:DirectorPid) (last resort)"
            Stop-Process -Id $alive.Id -Force -Confirm:$false
        }
    }
    Say "director stopped"
}

function Invoke-Gw([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $headers = @{ Authorization = "Bearer $script:GatewayToken" }
    $uri = "http://127.0.0.1:$GatewayPort$Path"
    if ($null -ne $Body) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -ContentType 'application/json' `
            -Body ($Body | ConvertTo-Json -Depth 6) -TimeoutSec 30
    }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 30
}

# ================================================================== the run ====
try {
    Say "rig root: $rig"

    # The slot must be genuinely free before anything is built. The fleet's own isolation harness reserves
    # a slot by registering a task named cc-director<N>-launch and by running an image called
    # cc-director<N>.exe, so both are checked - a rig that took an occupied slot would have this script's
    # process checks matching somebody else's Director, and its teardown is the part that matters there.
    if (Get-Process -Name ("cc-director$Slot") -ErrorAction SilentlyContinue) {
        Fail "slot $Slot is in use - a cc-director$Slot process is already running. Re-run with -Slot <a free one>."
    }
    if (Get-ScheduledTask -TaskName ("cc-director$Slot-launch") -ErrorAction SilentlyContinue) {
        Fail "slot $Slot is reserved - the scheduled task cc-director$Slot-launch exists. Re-run with -Slot <a free one>."
    }
    if (Get-NetTCPConnection -LocalPort $GatewayPort -State Listen -ErrorAction SilentlyContinue) {
        Fail "port $GatewayPort is already listening - something else is there. Re-run with -GatewayPort <a free one>."
    }

    New-Item -ItemType Directory -Force -Path $rig, $gwRoot, $dirRoot, $stage, $results | Out-Null

    # ---- build both halves from THIS worktree -------------------------------
    $gwStage  = Join-Path $stage 'gateway'
    $dirStage = Join-Path $stage 'director'
    if (-not $SkipBuild) {
        Say 'publishing the Gateway from this worktree'
        & dotnet publish (Join-Path $repo 'src\CcDirector.Gateway.Host\CcDirector.Gateway.Host.csproj') `
            -c Debug -o $gwStage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'the Gateway publish failed' }

        Say 'publishing the Director from this worktree'
        & dotnet publish (Join-Path $repo 'src\CcDirector.Avalonia\CcDirector.Avalonia.csproj') `
            -c Debug -o $dirStage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'the Director publish failed' }
    }

    $gwExe = Get-ChildItem $gwStage -Filter '*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1
    if (-not $gwExe) { Fail "no Gateway executable in $gwStage" }

    $dirSource = Get-ChildItem $dirStage -Filter 'cc-director*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1
    if (-not $dirSource) { $dirSource = Get-ChildItem $dirStage -Filter '*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1 }
    if (-not $dirSource) { Fail "no Director executable in $dirStage" }
    # Its own slot name, so every process check in this script can match on an exact path that belongs to
    # nothing else on the machine.
    $script:DirectorExe = Join-Path $dirStage "cc-director$Slot.exe"
    if ($dirSource.FullName -ine $script:DirectorExe) { Copy-Item $dirSource.FullName $script:DirectorExe -Force }
    Say "director executable: $($script:DirectorExe)"

    # ---- the throwaway Gateway ---------------------------------------------
    Say "starting the throwaway Gateway on port $GatewayPort with its own storage root"
    $gwLog = Join-Path $results 'gateway.out'
    # The storage root is handed over by INHERITANCE, set on this process just long enough to start the
    # child and then put back. Start-Process has no -Environment parameter on Windows PowerShell 5.1 - that
    # is PowerShell 7 - and this machine has only 5.1, so the parameter version fails outright rather than
    # quietly ignoring the root, which would point the throwaway Gateway at the REAL storage root and stamp
    # the owner's database with this mission's provisional migration id. Restoring it in a finally is not
    # tidiness: this script runs inside a session whose own root must not be changed.
    $prevRoot = $env:CC_DIRECTOR_ROOT
    try {
        $env:CC_DIRECTOR_ROOT = $gwRoot
        $script:GatewayProcess = Start-Process -FilePath $gwExe.FullName -ArgumentList @('--port', "$GatewayPort") `
            -WorkingDirectory $gwStage -PassThru -NoNewWindow `
            -RedirectStandardOutput $gwLog -RedirectStandardError "$gwLog.err"
    }
    finally { $env:CC_DIRECTOR_ROOT = $prevRoot }

    $healthy = $false
    foreach ($i in 1..90) {
        try {
            $h = Invoke-WebRequest "http://127.0.0.1:$GatewayPort/healthz" -UseBasicParsing -TimeoutSec 2
            if ($h.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) {
        if (Test-Path $gwLog) { Get-Content $gwLog -Tail 40 | Write-Host }
        Fail "the throwaway Gateway never answered /healthz on port $GatewayPort"
    }
    Say "gateway healthy (pid $($script:GatewayProcess.Id))"

    # CcStorage.GatewayDb() is Root()/gateway.db, and Root() honours CC_DIRECTOR_ROOT - so the throwaway
    # Gateway's database is inside the rig root and goes away with it. The search below is a safety net for
    # an instance-scoped root, not a guess.
    $gwDb = Join-Path $gwRoot 'gateway.db'
    if (-not (Test-Path $gwDb)) {
        $found = Get-ChildItem $gwRoot -Recurse -Filter '*.db' -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -notlike '*stats*' } | Select-Object -First 1
        if ($found) { $gwDb = $found.FullName }
    }
    if (-not (Test-Path $gwDb)) { Fail "could not find the throwaway Gateway's database under $gwRoot" }
    Say "gateway database: $gwDb"

    $tokenFile = Join-Path $gwRoot 'config\director\gateway-token.txt'
    if (-not (Test-Path $tokenFile)) { Fail "the Gateway did not write its machine token at $tokenFile" }
    $script:GatewayToken = (Get-Content $tokenFile -Raw).Trim()
    if (-not $script:GatewayToken) { Fail 'the Gateway machine token file is empty' }

    # ---- the throwaway Director, pointed at THAT Gateway --------------------
    $dirConfigDir = Join-Path $dirRoot 'config'
    New-Item -ItemType Directory -Force -Path $dirConfigDir | Out-Null
    @{
        gateway = @{
            url   = "http://127.0.0.1:$GatewayPort"
            token = $script:GatewayToken
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $dirConfigDir 'config.json') -Encoding utf8
    Say "director configured to dial http://127.0.0.1:$GatewayPort - and nothing else"

    # Launched through Task Scheduler, never from this process tree: a Director started inside an agent's
    # pseudo-console gives its grandchild agents a nested one, and they exit within seconds. The wrapper
    # carries CC_DIRECTOR_ROOT, which a scheduled task cannot set on its own.
    $launcher = Join-Path $stage 'launch-rig-director.cmd'
    @(
        '@echo off',
        "set CC_DIRECTOR_ROOT=$dirRoot",
        "cd /d `"$dirStage`"",
        "start `"`" `"$($script:DirectorExe)`""
    ) -join "`r`n" | Set-Content -Path $launcher -Encoding ascii

    Say "registering scheduled task $($script:TaskName)"
    $action  = New-ScheduledTaskAction -Execute $launcher -WorkingDirectory $dirStage
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)
    Register-ScheduledTask -TaskName $script:TaskName -Action $action -Trigger $trigger -Force | Out-Null
    $script:TaskRegistered = $true

    $before = @(Get-Process -Name ("cc-director$Slot") -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-ScheduledTask -TaskName $script:TaskName
    Say 'waiting for the Director to register itself (its readiness signal - it binds no port)'

    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and $script:DirectorPid -eq 0) {
        $now = @(Get-Process -Name ("cc-director$Slot") -ErrorAction SilentlyContinue |
                 Where-Object { $_.Path -and ($_.Path -ieq $script:DirectorExe) })
        foreach ($p in $now) {
            if ($before -notcontains $p.Id) { $script:DirectorPid = $p.Id; break }
        }
        if ($script:DirectorPid -eq 0) { Start-Sleep -Milliseconds 500 }
    }
    if ($script:DirectorPid -eq 0) { Fail 'the rig Director never started' }
    Say "director pid $($script:DirectorPid)"

    $directorId = ''
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $directorId) {
        $directorId = Get-RigDirectorId $script:DirectorPid
        if (-not $directorId) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $directorId) { Fail 'the rig Director never wrote its instance registration' }
    Say "director id $directorId"

    # POSITIVE liveness, read off the Gateway rather than assumed from having started something.
    $connected = $false
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $connected) {
        try {
            $directors = Invoke-Gw '/directors'
            $mine = @($directors | Where-Object { $_.directorId -ieq $directorId })
            if ($mine.Count -gt 0) { $connected = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $connected) { Fail 'the Gateway never saw the rig Director connect' }
    Say 'STEP 1 PASS: the throwaway Director is connected to the throwaway Gateway'

    # ---- a real session, a real turn ---------------------------------------
    Say 'creating a session on the rig Director'
    $created = Invoke-Gw "/directors/$directorId/sessions" 'POST' @{
        repoPath = $rig
        agent    = 'RawCli'
        command  = $env:ComSpec
        name     = 'terminal-rules screen proof'
    }
    $sid = $created.sessionId
    if (-not $sid) { Fail 'the Gateway did not return a session id' }
    Say "session $sid"

    Say 'waiting for the session to end a turn (Working -> WaitingForInput), which is the capture trigger'
    Start-Sleep -Seconds 5
    Invoke-Gw "/sessions/$sid/prompt" 'POST' @{ text = "echo TERMINAL_RULES_SCREEN_PROOF_$stamp" } | Out-Null

    # ---- did the screen reach the store? -----------------------------------
    $stored = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $stored) {
        $gwText = if (Test-Path $gwLog) { Get-Content $gwLog -Raw -ErrorAction SilentlyContinue } else { '' }
        if ($gwText -and $gwText -match "\[SessionScreenStore\].*$([regex]::Escape($sid)).*stored screen captured") { $stored = $true; break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $stored) {
        if (Test-Path $gwLog) { Get-Content $gwLog -Tail 60 | Write-Host }
        Fail "the Gateway never logged storing a screen for session $sid - the push path did not reach the store"
    }
    Say 'STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director'

    # The BEFORE half of step 3's comparison, taken while the machine is still up. Without it, "blocked
    # after stopping" would be satisfied by a session that was never readable in the first place.
    $script:BeforeKind = ''
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $script:BeforeKind = (Invoke-Gw "/sessions/$sid/wingman/waiting-screen").kind
            if ($script:BeforeKind -and $script:BeforeKind -ne 'blocked') { break }
        } catch { }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $script:BeforeKind -or $script:BeforeKind -eq 'blocked') {
        Fail "the live-screen read answered '$script:BeforeKind' while the machine was UP, so the same " +
             "answer after stopping it would prove nothing about the machine going away"
    }
    Say "STEP 2b PASS: with the machine up, the live-screen read answers '$script:BeforeKind'"

    # ---- now take the machine away -----------------------------------------
    Say 'stopping the rig Director - this is the "machine offline" half of the row'
    Stop-RigDirector
    $script:DirectorPid = 0

    # The machine's absence is read POSITIVELY off the Gateway, by asking the SAME question that was asked
    # while it was up and showing the answer change. DirectorDto carries no "connected" flag - a registration
    # survives a disconnect on purpose - so the honest probe is a route that actually needs the tunnel.
    # GET /sessions/{sid}/wingman/waiting-screen runs the live-truth read: with the machine gone it can be
    # answered by neither the store (which refuses to certify a screen whose Director is not connected) nor
    # the tunnel, so it must come back blocked. That is row 5's live half, proved over HTTP, in the same run.
    $afterKind = ''
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $afterKind = (Invoke-Gw "/sessions/$sid/wingman/waiting-screen").kind
            if ($afterKind -eq 'blocked') { break }
        } catch { $afterKind = 'error' }
        Start-Sleep -Milliseconds 1000
    }
    if ($afterKind -ne 'blocked') {
        Fail "with the machine stopped the waiting-screen read answered '$afterKind', not 'blocked' - " +
             "something served a screen for a Director that is gone"
    }
    Say "STEP 3 PASS: the same live-screen question answered '$script:BeforeKind' with the machine up and 'blocked' with it stopped"

    # ---- read the screen back, with the machine gone ------------------------
    Say 'reading the stored screen back out of the Gateway store, with the machine offline'
    $env:CC_SCREEN_RIG_DB = $gwDb
    $env:CC_SCREEN_RIG_SESSION = $sid
    $readLog = Join-Path $results 'readback.log'
    & dotnet test (Join-Path $repo 'src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj') `
        --filter 'FullyQualifiedName~StoredScreenRigRead' --nologo -v q *> $readLog
    $readExit = $LASTEXITCODE
    Get-Content $readLog | Write-Host

    $summary = (Select-String -Path $readLog -Pattern '^(Passed!|Failed!)' | Select-Object -Last 1).Line
    if ($readExit -ne 0) { Fail "the read-back test did not pass (exit $readExit): $summary" }
    # A SKIP is not a pass. Without this the row would report success on a test that never ran, which is
    # the exact defect this mission spent its rulings on.
    if (-not $summary -or $summary -notmatch 'Passed:\s*1\b') {
        Fail "the read-back test did not RUN as one passing test - summary was: $summary"
    }
    Say 'STEP 4 PASS: the real store read the screen back over the real migrated schema, machine offline'

    Say ''
    Say 'ROW 4 PROVEN. Chain covered: real TurnReviewLogger capture -> real GatewayScreenSink ->'
    Say 'real PushScreen hub method -> real SessionScreenStore -> real migrated database -> read back'
    Say 'with the owning Director positively observed disconnected.'
}
finally {
    Remove-Item Env:\CC_SCREEN_RIG_DB -ErrorAction SilentlyContinue
    Remove-Item Env:\CC_SCREEN_RIG_SESSION -ErrorAction SilentlyContinue
    Invoke-Teardown
}
