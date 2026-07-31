# Start the THROWAWAY Postgres the load-test rig runs against (devthrottle_internal issue #1173).
# One dedicated container, its own name and port, no volume mounts from the host - so teardown
# (stop-postgres.ps1) removes the database and every synthetic tenant with it.
#
# Usage:  powershell -NoProfile -File tools/loadtest/scripts/start-postgres.ps1 [-Port 55442]
param(
    [int]$Port = 55442
)
$ErrorActionPreference = "Stop"
$container = "dt-loadtest-pg"

$existing = docker ps -a --filter "name=^/$container$" --format "{{.Names}}"
if ($existing -eq $container) {
    Write-Host "[start-postgres] container '$container' already exists; starting it (stop-postgres.ps1 removes it)."
    docker start $container | Out-Null
} else {
    Write-Host "[start-postgres] creating container '$container' on port $Port"
    docker run -d --name $container `
        -e POSTGRES_USER=loadtest `
        -e POSTGRES_PASSWORD=loadtest `
        -e POSTGRES_DB=gateway_loadtest `
        -p "127.0.0.1:${Port}:5432" `
        postgres:16 | Out-Null
}

# Wait until it answers.
$deadline = (Get-Date).AddSeconds(60)
while ($true) {
    $ready = docker exec $container pg_isready -U loadtest -d gateway_loadtest 2>&1
    if ($LASTEXITCODE -eq 0) { break }
    if ((Get-Date) -gt $deadline) { throw "Postgres in '$container' did not become ready within 60 seconds. Last output: $ready" }
    Start-Sleep -Milliseconds 500
}

$conn = "Host=127.0.0.1;Port=$Port;Database=gateway_loadtest;Username=loadtest;Password=loadtest"
Write-Host "[start-postgres] READY."
Write-Host "[start-postgres] connection string (set this before starting the LoadRig):"
Write-Host ""
Write-Host "  `$env:CC_GATEWAY_DB_CONNECTION = `"$conn`""
Write-Host ""
Write-Host "[start-postgres] teardown after the run: powershell -NoProfile -File tools/loadtest/scripts/stop-postgres.ps1"
