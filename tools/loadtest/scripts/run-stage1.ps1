# Run Stage 1 (roster polling load) against a running LoadRig (devthrottle_internal issue #1173).
# Wraps k6 with the rig's key file, resets the Gateway's metrics window at the start, and scrapes
# /diag/loadmetrics into a JSONL beside the k6 summary for the whole run.
#
# Usage:
#   powershell -NoProfile -File tools/loadtest/scripts/run-stage1.ps1 `
#       -GatewayUrl http://127.0.0.1:7891 -OutDir .\loadtest-out [-MaxVus 10000] [-ScrapeSeconds 10]
#
# Requires: k6 on PATH (winget install k6.k6), and the LoadRig running with its keys in -OutDir.
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$MaxVus = 10000,
    [int]$ScrapeSeconds = 10,
    # Optional custom climb, e.g. "60s:10,60s:25,60s:50,120s:100" - zoom in on the knee, or repeat an
    # identical shape after a fix for comparison.
    [string]$Stages = ""
)
$ErrorActionPreference = "Stop"

if (-not (Get-Command k6 -ErrorAction SilentlyContinue)) {
    throw "k6 is not on PATH. Install it with: winget install k6.k6 (then open a fresh shell)."
}
$viewersFile = Join-Path $OutDir "viewers.json"
if (-not (Test-Path $viewersFile)) {
    throw "No viewers.json in $OutDir. Start the LoadRig first (tools/loadtest/README.md) - it writes the key files there."
}

# The same production guard the tools carry, applied before anything is sent.
$uri = [Uri]$GatewayUrl
$hostName = $uri.Host.ToLowerInvariant()
if ($hostName.EndsWith("azurewebsites.net") -or $hostName.Contains("devthrottle")) {
    throw "REFUSED: $GatewayUrl matches the production deny list. The harness never runs against production."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$summaryFile = Join-Path $OutDir "stage1-$stamp-summary.json"
$scrapeFile = Join-Path $OutDir "stage1-$stamp-loadmetrics.jsonl"

# One viewer key authenticates the metrics scrapes.
$viewerKey = (Get-Content $viewersFile -Raw | ConvertFrom-Json)[0].deviceKey
$headers = @{ Authorization = "Bearer $viewerKey" }

# Fresh metrics window for this run.
Invoke-RestMethod -Uri "$GatewayUrl/diag/loadmetrics?reset=true" -Headers $headers | Out-Null
Write-Host "[run-stage1] metrics window reset; scraping every $ScrapeSeconds s into $scrapeFile"

# Background scraper: one JSON line per interval for the whole run.
$scraper = Start-Job -ScriptBlock {
    param($url, $key, $file, $interval)
    $h = @{ Authorization = "Bearer $key" }
    while ($true) {
        try {
            $m = Invoke-RestMethod -Uri "$url/diag/loadmetrics" -Headers $h
            ($m | ConvertTo-Json -Compress -Depth 10) | Add-Content -Path $file -Encoding utf8
        } catch {
            "{`"scrapeError`":`"$($_.Exception.Message -replace '\"','')`"}" | Add-Content -Path $file -Encoding utf8
        }
        Start-Sleep -Seconds $interval
    }
} -ArgumentList $GatewayUrl, $viewerKey, $scrapeFile, $ScrapeSeconds

try {
    $env:GATEWAY_URL = $GatewayUrl
    $env:KEYS_FILE = $viewersFile
    $env:MAX_VUS = "$MaxVus"
    if ($Stages) { $env:STAGES = $Stages } else { Remove-Item Env:STAGES -ErrorAction SilentlyContinue }
    $env:SUMMARY_FILE = $summaryFile
    $scriptPath = Join-Path $PSScriptRoot "..\stage1-roster.js"
    k6 run $scriptPath
    $k6Exit = $LASTEXITCODE
} finally {
    Stop-Job $scraper | Out-Null
    Remove-Job $scraper -Force | Out-Null
}

Write-Host "[run-stage1] done (k6 exit $k6Exit). Summary: $summaryFile  Metrics: $scrapeFile"
exit $k6Exit
