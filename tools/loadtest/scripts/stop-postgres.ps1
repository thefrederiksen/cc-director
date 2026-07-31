# Tear down the throwaway load-test Postgres COMPLETELY (devthrottle_internal issue #1173).
# Removing the container removes the database and with it every synthetic tenant and device key -
# the plan's "no load-test tenant survives into a real database" rule, enforced by destruction.
#
# Usage:  powershell -NoProfile -File tools/loadtest/scripts/stop-postgres.ps1
$ErrorActionPreference = "Stop"
$container = "dt-loadtest-pg"

$existing = docker ps -a --filter "name=^/$container$" --format "{{.Names}}"
if ($existing -ne $container) {
    Write-Host "[stop-postgres] container '$container' does not exist; nothing to tear down."
    exit 0
}

docker rm -f -v $container | Out-Null
Write-Host "[stop-postgres] container '$container' removed with its data. All synthetic tenants are gone."
