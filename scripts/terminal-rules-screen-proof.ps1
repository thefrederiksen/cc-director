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
# The three lines the turn ENDS on, and the reason there are three of them. Inspection 01, finding 4:
# this row used to assert only that SOMETHING nonblank had been stored, so replacing every screen's rows
# with one constant left the whole suite green and this script still printed ROW 4 PROVEN. The rows have
# to be compared with what was on the terminal, which needs something on that terminal that this run
# authored and that no constant could be. Three lines rather than one so their ORDER is checked too, and
# stamped with this run's own stamp so a row left behind by an earlier run cannot satisfy them.
$markerA = "TR_SCREEN_PROOF_${stamp}_ALPHA"
$markerB = "TR_SCREEN_PROOF_${stamp}_BRAVO"
$markerC = "TR_SCREEN_PROOF_${stamp}_CHARLIE"
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
    # Retried: the Director publish carries agent assets that the just-stopped process can still hold for a
    # moment, and a first-attempt failure here would leave a database stamped with a provisional migration
    # id lying around - the one outcome ruling 8 exists to prevent.
    $removed = $false
    foreach ($attempt in 1..10) {
        try { Remove-Item -Recurse -Force $rig -ErrorAction Stop; $removed = $true; break }
        catch { Start-Sleep -Milliseconds 1000 }
    }
    if ($removed) { Say "removed the rig root $rig, database included" }
    else { Say "COULD NOT REMOVE $rig - delete it by hand; it holds a database stamped with a provisional migration id" }
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
        # CcDirector.Gateway, NOT CcDirector.Gateway.Host. The Host project carries a compiled-in
        # HostedGatewayImageAttribute, which makes it the immutable HOSTED identity: it asserts the full
        # hosted contract at startup - hosted mode on, auth enabled, an HTTPS public URL, PostgreSQL - and
        # REFUSES to run without them rather than silently downgrading to single-tenant no-auth semantics.
        # That refusal is correct and must not be worked around with environment variables; a rig that set
        # CC_GATEWAY_HOSTED=1 would be proving something about a hosted Gateway it is not entitled to say.
        # CcDirector.Gateway is the same Gateway with the same entry point and no hosted marker, which is
        # what a self-host install runs.
        Say 'publishing the Gateway from this worktree'
        & dotnet publish (Join-Path $repo 'src\CcDirector.Gateway\CcDirector.Gateway.csproj') `
            -c Debug -o $gwStage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'the Gateway publish failed' }

        Say 'publishing the Director from this worktree'
        & dotnet publish (Join-Path $repo 'src\CcDirector.Avalonia\CcDirector.Avalonia.csproj') `
            -c Debug -o $dirStage --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail 'the Director publish failed' }
    }

    $gwExe = Get-ChildItem $gwStage -Filter 'CcDirector.Gateway.exe' | Select-Object -First 1
    if (-not $gwExe) { $gwExe = Get-ChildItem $gwStage -Filter '*.exe' | Where-Object { $_.Name -notlike 'createdump*' } | Select-Object -First 1 }
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
    # WAITED FOR, not sampled once: /healthz answers as soon as the listener is up, and the database file
    # appears a moment later on the first store open. A single check right after healthz is a race, and it
    # lost one.
    $gwDb = $null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $gwDb) {
        $direct = Join-Path $gwRoot 'gateway.db'
        if (Test-Path $direct) { $gwDb = $direct; break }
        $found = Get-ChildItem $gwRoot -Recurse -Filter '*.db' -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -notlike '*stats*' } | Select-Object -First 1
        if ($found) { $gwDb = $found.FullName; break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $gwDb) { Fail "could not find the throwaway Gateway's database under $gwRoot" }
    Say "gateway database: $gwDb"

    $tokenFile = Join-Path $gwRoot 'config\director\gateway-token.txt'
    if (-not (Test-Path $tokenFile)) { Fail "the Gateway did not write its machine token at $tokenFile" }
    $script:GatewayToken = (Get-Content $tokenFile -Raw).Trim()
    if (-not $script:GatewayToken) { Fail 'the Gateway machine token file is empty' }

    # ---- the throwaway Director, pointed at THAT Gateway --------------------
    # THE INSTANCE HOME, not the root. A Director re-points CC_DIRECTOR_ROOT at
    # {sharedRoot}\instances\{slug} first thing in Main (InstanceContext.Initialize), so its whole data
    # tree - config.json included - lives one level down. Writing to <root>\config instead leaves the
    # Director reporting "no gateway.url configured" while the file sits there unread, which looks exactly
    # like a Director that could not reach the Gateway.
    $dirConfigDir = Join-Path $dirRoot 'instances\default\config'
    New-Item -ItemType Directory -Force -Path $dirConfigDir | Out-Null
    $dirConfigJson = @{
        gateway = @{
            url   = "http://127.0.0.1:$GatewayPort"
            token = $script:GatewayToken
        }
    } | ConvertTo-Json -Depth 5
    # WITHOUT A BYTE-ORDER MARK. Set-Content -Encoding utf8 on Windows PowerShell 5.1 writes one, and a
    # leading BOM makes the Director's JSON parse fail - so it would boot with NO Gateway configured, dial
    # nothing, and look exactly like a Director that could not reach the Gateway.
    [System.IO.File]::WriteAllText(
        (Join-Path $dirConfigDir 'config.json'), $dirConfigJson, (New-Object System.Text.UTF8Encoding($false)))
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
    # Generous: a cold Director runs a full tool-health scan before it settles, and the dial happens after.
    $deadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deadline -and -not $connected) {
        try {
            $directors = Invoke-Gw '/directors'
            $mine = @($directors | Where-Object { $_.directorId -ieq $directorId })
            if ($mine.Count -gt 0) { $connected = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $connected) {
        # Say what WAS seen rather than only that the expectation was not met - a bare "never connected"
        # sends the next reader to guess between "the Director did not dial", "it dialled another Gateway"
        # and "it connected under a different id".
        try {
            $raw = Invoke-WebRequest -Uri "http://127.0.0.1:$GatewayPort/directors" -Headers @{ Authorization = "Bearer $script:GatewayToken" } -UseBasicParsing -TimeoutSec 20
            Say "raw /directors body: $($raw.Content)"
        } catch { Say "the /directors read itself failed: $($_.Exception.Message)" }
        # Searched from the root DOWN: an instance-scoped Director puts its log under
        # <root>\instances\<slug>\logs\director, not directly under <root>\logs\director.
        $dirLog = Get-ChildItem $dirRoot -Recurse -Filter 'director-*.log' -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime | Select-Object -Last 1
        if ($dirLog) {
            Say "--- every GATEWAY line in the rig Director log ($($dirLog.Name)) ---"
            $gwLines = @(Select-String -Path $dirLog.FullName -Pattern 'Gateway|gateway' | Select-Object -Last 40)
            if ($gwLines.Count -eq 0) { Say '  (none - the Director never mentioned a Gateway at all)' }
            else { $gwLines | ForEach-Object { Say "  $($_.Line)" } }
            Say "--- last 10 lines ---"
            Get-Content $dirLog.FullName -Tail 10 | ForEach-Object { Say "  $_" }
        } else { Say "no rig Director log under $dirRoot" }
        Fail "the Gateway never saw the rig Director connect (looking for $directorId)"
    }
    Say 'STEP 1 PASS: the throwaway Director is connected to the throwaway Gateway'

    # ---- a real session, a real turn ---------------------------------------
    function Get-RigLogText([string]$root) {
        $files = @(Get-ChildItem $root -Recurse -Filter 'director-*.log' -ErrorAction SilentlyContinue)
        if ($files.Count -eq 0) { return '' }
        return ($files | ForEach-Object { Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue }) -join "`n"
    }

    Say 'creating a session on the rig Director'
    # The session must actually WORK and then WAIT, because the capture trigger is the Working ->
    # WaitingForInput flip and nothing else. A bare shell sitting at its prompt never goes Working, so it
    # never ends a turn and no screen is ever captured - measured, not assumed: a first attempt with a plain
    # cmd.exe produced a Director log containing only "[TurnReviewLogger] Start".
    #
    # So the session is started ON a command that emits output for several seconds and then returns to the
    # prompt. That is a genuine turn: output makes the detector say Working, and the quiet that follows makes
    # it say WaitingForInput. No prompt route is involved, so nothing here depends on the submit verifier.
    $created = Invoke-Gw "/directors/$directorId/sessions" 'POST' @{
        repoPath    = $rig
        agent       = 'RawCli'
        command     = $env:ComSpec
        commandArgs = "/k echo TERMINAL_RULES_SCREEN_PROOF_$stamp"
        name        = 'terminal-rules screen proof'
    }
    $sid = $created.sessionId
    if (-not $sid) { Fail 'the Gateway did not return a session id' }
    Say "session $sid"

    Say 'waiting for the session to end a turn (Working -> WaitingForInput), which is the capture trigger'
    # The prompt is a NUDGE, not the thing under test, and its HTTP result is deliberately not the gate.
    # The prompt route answers 502 when its submit verifier cannot see the keystroke start a TURN - which is
    # a real and documented case for input that is not a turn (issue #2644 records the same 502 for a
    # keystroke that answered a picker and had in fact worked). The assertion that matters is below and it is
    # a PRESENCE: did a screen actually reach the store. So a failed nudge is reported and the run continues
    # to ask the real question, rather than being reported as the row failing.
    # A SUBMITTED keystroke is what makes the detector say Working - Session.SetActivityState(Working) runs
    # on submission, not on output - so the session must be typed into for a turn to exist at all. Measured
    # rather than assumed: a session started on a long-running command and never typed into sat at
    # WaitingForInput for three minutes and produced no capture.
    #
    # The prompt route's HTTP result is deliberately NOT the gate. It answers 502 when its submit verifier
    # cannot see the keystroke start a turn, which is a real and documented case for input that is not a turn
    # (issue #2644 records the same 502 for a keystroke that answered a picker and had in fact worked). The
    # state change happens on submit either way, and the assertion that matters is below and is a PRESENCE:
    # did a screen actually reach the store.
    Start-Sleep -Seconds 5
    try {
        # The command must produce a LOT of output, FAST. The Director's submit verifier confirms a keystroke
        # started a turn by watching the terminal grow by 2048 bytes within eight beats of about 1.2 seconds
        # each - and it says so itself when it gives up. Measured rather than guessed: a one-line echo left
        # it reporting "dead window (49 bytes in 1200ms)" and nudging with Enter five times, and a ping that
        # trickled sixty bytes a second did not clear the bar either. A recursive directory listing floods
        # the terminal immediately, which is an honest turn - output while it runs, quiet when it stops,
        # which is exactly the Working then WaitingForInput the capture triggers on.
        Invoke-Gw "/sessions/$sid/prompt" 'POST' @{
            # The flood comes FIRST and the markers LAST, deliberately. The flood is what makes the
            # Director's submit verifier see a turn at all (it wants 2048 bytes inside eight beats); the
            # markers have to be the last thing printed, or they scroll off and the captured turn-end
            # screen - which is the screen this row is about - would not contain them.
            text      = "dir /s /b C:\Windows\System32 & echo $markerA & echo $markerB & echo $markerC"
            timeoutMs = 120000
        } | Out-Null
        Say 'prompt accepted'
    } catch {
        Say "prompt answered $($_.Exception.Message) - continuing; the store assertion below is the gate"
        Start-Sleep -Seconds 3
        $promptHits = @([regex]::Matches((Get-RigLogText $gwRoot), '(?m)^.*(prompt|Prompt|SubmitVerifier|DirectorCommand).*$') | ForEach-Object { $_.Value })
        Say '--- Gateway: prompt lines ---'
        if ($promptHits.Count -eq 0) { Say '  (none)' } else { $promptHits | Select-Object -Last 12 | ForEach-Object { Say "  $_" } }
    }

    # ---- did the screen reach the store? -----------------------------------
    # The store writes through FileLog, which is the Gateway's LOG FILE under its own storage root - not the
    # process stdout this script redirected. Looking in stdout would report "never stored" for a screen that
    # had been stored perfectly well.

    $stored = $false
    $states = New-Object System.Collections.ArrayList
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and -not $stored) {
        # Sample the session's activity state as we wait. The capture fires on Working -> WaitingForInput and
        # on nothing else, so the sequence of states IS the diagnosis when no screen appears: it says whether
        # the turn happened at all, which "no screen was stored" on its own cannot.
        try {
            $me = @(Invoke-Gw '/sessions') | Where-Object { $_.sessionId -ieq $sid } | Select-Object -First 1
            if ($me -and ($states.Count -eq 0 -or $states[$states.Count - 1] -ne $me.activityState)) {
                [void]$states.Add($me.activityState)
            }
        } catch { }
        $gwText = Get-RigLogText $gwRoot
        if ($gwText -and $gwText -match "\[SessionScreenStore\].*$([regex]::Escape($sid)).*stored screen captured") { $stored = $true; break }
        Start-Sleep -Milliseconds 2000
    }
    Say "session activity states observed, in order: $($states -join ' -> ')"
    if (-not $stored) {
        # Name which HALF of the push path went quiet, rather than reporting the whole chain as broken. The
        # Director's own lines say whether the capture fired and whether the sink sent; the Gateway's say
        # whether the hub method was reached.
        Say '--- Director: capture and sink lines ---'
        $dirText = Get-RigLogText $dirRoot
        $dirHits = @([regex]::Matches($dirText, '(?m)^.*(TurnReviewLogger|GatewayScreenSink|PushScreen).*$') | ForEach-Object { $_.Value })
        if ($dirHits.Count -eq 0) { Say '  (none - the capture never fired and the sink was never called)' }
        else { $dirHits | Select-Object -Last 20 | ForEach-Object { Say "  $_" } }

        Say '--- Gateway: screen lines ---'
        $gwHits = @([regex]::Matches((Get-RigLogText $gwRoot), '(?m)^.*(SessionScreenStore|PushScreen|DirectorHub).*$') | ForEach-Object { $_.Value })
        if ($gwHits.Count -eq 0) { Say '  (none - nothing about a screen reached the Gateway)' }
        else { $gwHits | Select-Object -Last 20 | ForEach-Object { Say "  $_" } }

        Fail "the Gateway never logged storing a screen for session $sid - the push path did not reach the store"
    }
    Say 'STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director'

    # THE BEFORE HALF of the machine-went-away comparison, taken while it is still up.
    #
    # The comparison is on the READER'S OWN VERDICT, not on the waiting-screen route's kind. A raw shell has
    # no recognisable composer, so the classifier fails closed and answers "blocked" whether the machine is
    # up or down - measured, not assumed - which would make a before-and-after on that value prove nothing.
    # GatewayScreenReader writes exactly which of its three answers it gave and why, so that is what is
    # compared: a screen obtained (STORED or TUNNEL) while up, and UNREADABLE naming the disconnected
    # tunnel once the machine is gone. Two positive artifacts, not one absence.
    try {
        $ws = Invoke-Gw "/sessions/$sid/wingman/waiting-screen"
        Say "waiting-screen answered: kind=$($ws.kind) canType=$($ws.canType)"
    } catch { Say "waiting-screen call FAILED: $($_.Exception.Message)" }
    Start-Sleep -Seconds 2
    $readerUp = @([regex]::Matches((Get-RigLogText $gwRoot),
        "(?m)^.*\[GatewayScreenReader\].*$([regex]::Escape($sid)).*$") | ForEach-Object { $_.Value })
    $gotScreenWhileUp = @($readerUp | Where-Object { $_ -match 'STORED screen|TUNNEL' })
    if ($gotScreenWhileUp.Count -eq 0) {
        Say '--- every GatewayScreenReader line for this session ---'
        if ($readerUp.Count -eq 0) { Say '  (none)' } else { $readerUp | ForEach-Object { Say "  $_" } }
        Say '--- every GatewayScreenReader line, ANY session ---'
        $anyReader = @([regex]::Matches((Get-RigLogText $gwRoot), '(?m)^.*GatewayScreenReader.*$') | ForEach-Object { $_.Value })
        if ($anyReader.Count -eq 0) { Say '  (none at all)' } else { $anyReader | Select-Object -Last 8 | ForEach-Object { Say "  $_" } }
        Say '--- every waiting-screen / WaitingScreenReader line ---'
        $wsHits = @([regex]::Matches((Get-RigLogText $gwRoot), '(?m)^.*(waiting-screen|WaitingScreenReader|GatewayWingmanVoice).*$') | ForEach-Object { $_.Value })
        if ($wsHits.Count -eq 0) { Say '  (none)' } else { $wsHits | Select-Object -Last 10 | ForEach-Object { Say "  $_" } }
        Fail "with the machine UP the reader never reported obtaining a screen, so the same reader " +
             "reporting UNREADABLE after it is stopped would prove nothing about the machine going away"
    }
    Say "STEP 2b PASS, machine up: $($gotScreenWhileUp[-1])"
    $readerLinesBefore = $readerUp.Count

    # ---- now take the machine away -----------------------------------------
    # THE TERMINAL'S OWN TEXT, read while the machine is still up and through a DIFFERENT path from the
    # one the capture took: this is the Director's raw buffer over the "buffer" verb, where the capture is
    # a parser grid snapshot pushed over "PushScreen". Comparing the stored rows against it is what makes
    # the row a claim about CONTENT rather than about a row existing. Taken now because in a moment there
    # will be no machine to ask.
    $terminalFile = Join-Path $results 'terminal-buffer.txt'
    try {
        $buf = Invoke-Gw "/sessions/$sid/buffer?lines=400"
        if (-not $buf -or [string]::IsNullOrWhiteSpace($buf.Text)) {
            Fail 'the Director returned an EMPTY terminal buffer, so there is nothing to compare the stored rows against'
        }
        Set-Content -Path $terminalFile -Value $buf.Text -Encoding utf8
        Say "read $($buf.Text.Length) characters of terminal buffer into $terminalFile"
    } catch {
        Fail "could not read the session's terminal buffer while the machine was up: $($_.Exception.Message)"
    }
    # A PRESENCE check on the instrument itself before it is trusted: the markers this run printed must be
    # in the terminal's own text. If they are not, the turn did not run the command this row thinks it ran,
    # and every comparison below would be comparing two wrong things to each other.
    $terminalText = Get-Content -Path $terminalFile -Raw
    foreach ($m in @($markerA, $markerB, $markerC)) {
        if ($terminalText -notmatch [regex]::Escape($m)) {
            Fail "the marker $m is NOT in the session's own terminal buffer - the proof command did not run as expected"
        }
    }
    Say 'the three run markers are present in the terminal buffer, so the comparison below has a real subject'

    Say 'stopping the rig Director - this is the "machine offline" half of the row'
    Stop-RigDirector
    $script:DirectorPid = 0

    # TWO refusals are acceptable here and BOTH are positive, named facts rather than absences. Which one
    # happens depends on how far the request gets before the missing Director stops it:
    #   - the reader itself answers UNREADABLE, naming the disconnected tunnel; or
    #   - the route refuses earlier, because a session whose Director is gone cannot be LOCATED at all, and
    #     answers "session not found on any director".
    # The second is the ordinary outcome and is just as good an observation of the machine having gone: it
    # is the Gateway saying so, not this script inferring it from silence. What is NOT acceptable is the
    # request still being served a screen, which is what the assertion below rules out.
    $refusal = $null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not $refusal) {
        $routeSaid = $null
        try {
            $ws = Invoke-Gw "/sessions/$sid/wingman/waiting-screen"
            $routeSaid = "served kind=$($ws.kind)"
        } catch {
            $routeSaid = "refused: $($_.Exception.Message)"
            try {
                $body = (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd()
                if ($body) { $routeSaid = "refused: $body" }
            } catch { }
        }
        Start-Sleep -Seconds 2
        $readerNow = @([regex]::Matches((Get-RigLogText $gwRoot),
            "(?m)^.*\[GatewayScreenReader\].*$([regex]::Escape($sid)).*$") | ForEach-Object { $_.Value })
        if ($readerNow.Count -gt $readerLinesBefore) {
            $fresh = $readerNow[$readerLinesBefore..($readerNow.Count - 1)]
            $hit = @($fresh | Where-Object { $_ -match 'UNREADABLE' }) | Select-Object -Last 1
            if ($hit) { $refusal = "the reader answered UNREADABLE: $hit" }
            $served = @($fresh | Where-Object { $_ -match 'served the STORED screen' }) | Select-Object -Last 1
            if ($served) { Fail "the reader SERVED a stored screen as live for a Director that is gone: $served" }
        }
        if (-not $refusal -and $routeSaid -match 'not found on any director') {
            $refusal = "the route refused before the read: $routeSaid"
        }
        if (-not $refusal -and $routeSaid -match '^served kind=') {
            Fail "with the machine stopped the live-screen route still answered ($routeSaid)"
        }
    }
    if (-not $refusal) { Fail 'with the machine stopped the Gateway neither refused nor answered - no verdict at all' }
    Say "STEP 3 PASS, machine stopped: $refusal"

    # ---- read the screen back, with the machine gone ------------------------
    Say 'reading the stored screen back out of the Gateway store, with the machine offline'
    $env:CC_SCREEN_RIG_DB = $gwDb
    $env:CC_SCREEN_RIG_SESSION = $sid
    $env:CC_SCREEN_RIG_MARKERS = ($markerA + '|' + $markerB + '|' + $markerC)
    $env:CC_SCREEN_RIG_TERMINAL = $terminalFile
    $rowFile = Join-Path $results 'stored-row.txt'
    $env:CC_SCREEN_RIG_OUT = $rowFile
    $readLog = Join-Path $results 'readback.log'
    # The UNIT project, deliberately: CcDirector.Gateway.Tests takes a machine-wide lock and this step
    # queued behind two other worktrees' suites for ten minutes with the rig alive the whole time.
    # THE ERROR PREFERENCE IS LOWERED FOR THIS ONE CALL, and that is not a nicety. This script runs with
    # ErrorActionPreference = Stop, and PowerShell 5.1 turns a native executable's stderr into a terminating
    # NativeCommandError under that setting - so a FAILING read-back test aborted the script right here, with
    # its own diagnosis still inside dotnet's output stream and the log file holding nothing but the two
    # header lines. Measured, on a deliberate known-bad run: the row failed, and the reason it failed was
    # unreadable. A proof whose failure path prints nothing is the same defect as a proof that cannot fail.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet test (Join-Path $repo 'src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj') `
        --filter 'FullyQualifiedName~StoredScreenRigRead' --nologo -v n 2>&1 |
        Out-File -FilePath $readLog -Encoding utf8
    $readExit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    # And when it fails, SAY WHY in the transcript rather than pointing at a file the caller may not have.
    if ($readExit -ne 0) {
        Say '--- the read-back test failure, in full ---'
        # Narrow on purpose: -v n also prints the build's own chatter, and a dump that buries the one line
        # that matters is only marginally better than printing nothing.
        Get-Content $readLog | Where-Object { $_ -match '\[FAIL\]|Error Message|Assert\.|Expected:|Actual:' } |
            Select-Object -First 20 | ForEach-Object { Say "  $_" }
    }
    # Print the ROW the test read back, not just the pass line. The acceptance for this row is "show the
    # stored row and the read", and a summary line shows neither.
    Say '--- the stored screen, read back from the Gateway with the machine offline ---'
    if (Test-Path $rowFile) { Get-Content $rowFile | ForEach-Object { Say "  $_" } }
    else { Say '  (the read-back test wrote no row file)' }

    $summary = (Select-String -Path $readLog -Pattern '(Passed!|Failed!)' | Select-Object -Last 1).Line
    if ($readExit -ne 0) { Fail "the read-back test did not pass (exit $readExit). Its log is $readLog" }

    # A SKIP IS NOT A PASS, and this is where that is enforced. It used to be enforced by matching
    # "Passed: 1" in the runner's summary line, which made this row's verdict depend on the wording of
    # somebody else's console output - and a run whose comparison had genuinely PASSED then failed here,
    # because that line was not where the parser expected it. The verdict now rests on an ARTIFACT THE RUN
    # PRODUCED: the read-back test writes the row file only on its success path, so that file existing and
    # naming THIS run's session and THIS run's three markers cannot happen unless the test really ran and
    # really made the comparison. A skipped test writes nothing and fails this at once.
    if (-not (Test-Path $rowFile)) {
        Fail "the read-back test wrote no row file, so it never reached its success path (runner said: $summary)"
    }
    $rowText = Get-Content $rowFile -Raw
    if ($rowText -notmatch [regex]::Escape("session=$sid")) {
        Fail "the row file does not name the session this run drove ($sid), so it describes some other read"
    }
    foreach ($m in @($markerA, $markerB, $markerC)) {
        if ($rowText -notmatch [regex]::Escape($m)) {
            Fail "the row file does not carry this run's marker $m, so what it reports is not this run's comparison"
        }
    }
    Say "read-back verdict rests on $rowFile, which names session $sid and all three of this run's markers"
    if ($summary) { Say "runner summary: $summary" }
    Say 'STEP 4 PASS: the real store read the screen back over the real migrated schema, machine offline'

    Say ''
    Say 'ROW 4 PROVEN. Chain covered: real TurnReviewLogger capture -> real GatewayScreenSink ->'
    Say 'real PushScreen hub method -> real SessionScreenStore -> real migrated database -> read back'
    Say 'with the owning Director positively observed disconnected.'
    Say ''
    Say 'And the rows READ BACK are the rows that were on that terminal: the three lines this run printed'
    Say 'are in the stored screen, in order, and every nonblank stored row appears in the terminal buffer'
    Say 'the Director itself reported over a different verb. Both sides are quoted in stored-row.txt.'
}
finally {
    Remove-Item Env:\CC_SCREEN_RIG_DB -ErrorAction SilentlyContinue
    Remove-Item Env:\CC_SCREEN_RIG_SESSION -ErrorAction SilentlyContinue
    Remove-Item Env:\CC_SCREEN_RIG_MARKERS -ErrorAction SilentlyContinue
    Remove-Item Env:\CC_SCREEN_RIG_TERMINAL -ErrorAction SilentlyContinue
    Remove-Item Env:\CC_SCREEN_RIG_OUT -ErrorAction SilentlyContinue
    Invoke-Teardown
}
