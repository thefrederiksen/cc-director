# Run Stage 0 (the floor) against a running LoadRig (devthrottle_internal issue #1173).
#
# Stage 0 is the cheapest and most valuable measurement the harness makes, and the only one that needs no
# k6 and no quiet machine: it produces COUNTS. The 31 July baseline's headline fact is one - 1,032 snooze
# database reads for 30 roster polls plus 13 sweeps over 8 sessions, (30 + 13) x 8 x 3 with no remainder -
# and a count means the same thing on a loaded machine as on an idle one. A latency needs quiet; this
# does not.
#
# The baseline's Stage 0 was run by hand. This script exists so the re-run is the SAME procedure rather
# than a similar one, and so it records its own configuration beside its numbers - configuration drift is
# the one threat to comparability that flatters the new run and is undetectable afterwards.
#
# The rig this script expects - BOTH the rig and the Director must already be up, or a viewer sees no
# sessions, folds nothing, takes no read, and the zero it produces is meaningless:
#   LoadRig     LOADTEST_TENANTS=1  LOADTEST_DIRECTORS_PER_TENANT=1   (Debug build, console mirror OFF)
#   DirectorSim DIRECTORS=1  SESSIONS_PER_DIRECTOR=8  EVENTS_PER_SEC=0
#   one viewer  = this script, 30 polls at the real 2 s client cadence
# The full executable sequence, in order, is in tools/loadtest/README.md.
#
# ONE DIRECTOR, EIGHT SESSIONS, ONE VIEWER, 30 POLLS reproduces the 31 July baseline's Stage 0 deliberately.
# THE SEEDED TENANT COUNT DOES NOT: it is not recorded anywhere for that run - its rig-provenance.json
# describes a rig booted three and a half minutes AFTER the Stage 0 artifact was captured - so one tenant is
# THIS recipe's choice rather than a known baseline setting. It is mechanically harmless for these numbers,
# because the sweep folds only tenants with a tunnel-bound Director and the roster serves only the caller's
# own tenant: with one Director connected, exactly one tenant is folded per sweep whichever count was seeded.
#
# Usage (every configuration argument is REQUIRED - see the parameter block):
#   powershell -NoProfile -File tools/loadtest/scripts/run-stage0.ps1 `
#       -GatewayUrl http://127.0.0.1:7891 -OutDir "$env:TEMP\loadtest-out" `
#       -BuildConfiguration Debug -ConsoleMirror off `
#       -Tenants 1 -DirectorsConnected 1 -SessionsPerDirector 8 -Label "what this run is"
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Polls = 30,
    [int]$IntervalSeconds = 2,
    # Free-text note for the provenance block, e.g. "after the batched-read fix, machine busy".
    [string]$Label = "",
    # THE CONFIGURATION FACTS THIS SCRIPT CANNOT SEE FROM INSIDE. They are required, not defaulted: a run
    # whose build configuration nobody wrote down is not comparable to a baseline captured under a stated
    # one, and unlike machine noise - which makes a run look WORSE and shows up in the numbers - a
    # configuration difference makes it look BETTER and is undetectable from the figures afterwards.
    [string]$BuildConfiguration = "",
    [string]$ConsoleMirror = "",
    [int]$Tenants = 0,
    [int]$DirectorsConnected = 0,
    [int]$SessionsPerDirector = 0
)
$ErrorActionPreference = "Stop"

$missing = @()
if (-not $BuildConfiguration) { $missing += "-BuildConfiguration (the 31 July baseline was Debug)" }
if (-not $ConsoleMirror)      { $missing += "-ConsoleMirror (on/off; the baseline had it OFF - LOADTEST_MIRROR_CONSOLE unset)" }
if ($Tenants -le 0)           { $missing += "-Tenants (tenants SEEDED in the rig)" }
if ($DirectorsConnected -le 0){ $missing += "-DirectorsConnected (the baseline's Stage 0 had exactly 1)" }
if ($SessionsPerDirector -le 0) { $missing += "-SessionsPerDirector (the baseline's Stage 0 had 8)" }
if ($missing.Count -gt 0) {
    throw "This run would not be comparable to the baseline, because these facts were not stated: $($missing -join '; '). Pass them; they are written into the artifact's provenance block."
}

. (Join-Path $PSScriptRoot "loadtarget-guard.ps1")
Assert-LoadTargetAllowed -GatewayUrl $GatewayUrl

$viewersFile = Join-Path $OutDir "viewers.json"
if (-not (Test-Path $viewersFile)) {
    throw "No viewers.json in $OutDir. Start the LoadRig first (tools/loadtest/README.md) - it writes the key files there."
}
$viewerKey = (Get-Content $viewersFile -Raw | ConvertFrom-Json)[0].deviceKey
$headers = @{ Authorization = "Bearer $viewerKey" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactFile = Join-Path $OutDir "stage0-$stamp-artifact.json"

# MEASURE THE MACHINE, DO NOT ASSERT IT IS QUIET. A report that says "the machine was at 19 percent with
# 45 GB free" is checkable; one that says "the machine was quiet" is not. Sampled before and after.
function Get-MachineState {
    $os = Get-CimInstance Win32_OperatingSystem
    return [ordered]@{
        atUtc              = (Get-Date).ToUniversalTime().ToString("o")
        freeMemoryMb       = [int]($os.FreePhysicalMemory / 1024)
        totalMemoryMb      = [int]($os.TotalVisibleMemorySize / 1024)
        processCount       = (Get-Process).Count
        processorCount     = [Environment]::ProcessorCount
    }
}

$machineBefore = Get-MachineState

# Fresh metrics window, so this run reads its own numbers and not a mix with the rig's startup.
Invoke-RestMethod -Uri "$GatewayUrl/diag/loadmetrics?reset=true" -Headers $headers -TimeoutSec 15 | Out-Null
Write-Host "[run-stage0] metrics window reset; polling GET /sessions $Polls times every $IntervalSeconds s"

$clientRosterMs = @()
for ($i = 1; $i -le $Polls; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-RestMethod -Uri "$GatewayUrl/sessions" -Headers $headers -TimeoutSec 60 | Out-Null
    $sw.Stop()
    $clientRosterMs += [int]$sw.ElapsedMilliseconds
    if ($i -lt $Polls) { Start-Sleep -Seconds $IntervalSeconds }
}

$metrics = Invoke-RestMethod -Uri "$GatewayUrl/diag/loadmetrics" -Headers $headers -TimeoutSec 15
$healthz = Invoke-RestMethod -Uri "$GatewayUrl/healthz" -TimeoutSec 15
$machineAfter = Get-MachineState

# The provenance block. RECORD THE CONFIGURATION BESIDE THE NUMBERS: contention makes a run look WORSE
# and is visible in the numbers, but a configuration difference makes it look BETTER and cannot be seen
# afterwards by anyone reading the figures. Anything that could not be matched to the baseline belongs
# here, stated, not in a footnote.
$provenance = [ordered]@{
    label               = $Label
    stamp               = $stamp
    gatewayUrl          = $GatewayUrl
    polls               = $Polls
    intervalSeconds     = $IntervalSeconds
    gatewayVersion      = $healthz.version
    machineName         = $env:COMPUTERNAME
    buildConfiguration  = $BuildConfiguration
    consoleMirror       = $ConsoleMirror
    tenantsSeeded       = $Tenants
    directorsConnected  = $DirectorsConnected
    sessionsPerDirector = $SessionsPerDirector
    machineBefore       = $machineBefore
    machineAfter        = $machineAfter
}

$artifact = [ordered]@{
    provenance     = $provenance
    clientRosterMs = $clientRosterMs
    loadmetrics    = $metrics
    healthz        = $healthz
}
($artifact | ConvertTo-Json -Depth 12) | Set-Content -Path $artifactFile -Encoding utf8

# The two numbers Stage 0 exists for, printed so the operator reads the RESULT before writing a label
# about it. The identity to check: with one set-based read per fold, snoozeDbReads equals the number of
# folds (foldDurationMs.count) exactly, with no remainder - as long as every fold had at least one
# session, since a fold over nothing takes no read.
$reads = $metrics.counters.snoozeDbReads
$folds = $metrics.foldDurationMs.count
$rosters = $metrics.counters.rosterRequests
$sweeps = $metrics.counters.sweepTicks
$skipped = $metrics.counters.sweepSkipped
$overlaps = $metrics.counters.sweepOverlaps
Write-Host ""
Write-Host "[run-stage0] snoozeDbReads=$reads  folds=$folds  rosterRequests=$rosters  sweepTicks=$sweeps"
Write-Host "[run-stage0] reads per roster request = $([math]::Round($reads / [math]::Max($rosters,1), 3))"
Write-Host "[run-stage0] reads per fold           = $([math]::Round($reads / [math]::Max($folds,1), 3))"
Write-Host "[run-stage0] sweepOverlaps=$overlaps  sweepSkipped=$skipped"
Write-Host "[run-stage0] artifact: $artifactFile"
