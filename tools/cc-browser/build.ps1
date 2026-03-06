# CC Browser v2 Build Script
# Deploys v2 source files to %LOCALAPPDATA%\cc-director\bin\_cc-browser\

$ErrorActionPreference = "Stop"

$binDir = Join-Path $env:LOCALAPPDATA "cc-director\bin"
$deployDir = Join-Path $binDir "_cc-browser"

Write-Host "[cc-browser] Deploying v2 to $deployDir"

# Kill cc-browser daemon and native-host node processes before deploying
# (these lock files in _cc-browser/ and cause build failures)
# Note: node.exe Path is always nodejs install dir, so check CommandLine instead
Write-Host "[cc-browser] Stopping running cc-browser processes..."
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq "node.exe" -and $_.CommandLine -match "cc-browser" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# Clean old deployment
if (Test-Path $deployDir) {
    Write-Host "[cc-browser] Removing old deployment..."
    Remove-Item -Recurse -Force $deployDir
}

# Create directory structure
New-Item -ItemType Directory -Force -Path "$deployDir\src" | Out-Null
New-Item -ItemType Directory -Force -Path "$deployDir\extension" | Out-Null
New-Item -ItemType Directory -Force -Path "$deployDir\native-host" | Out-Null

# Copy source files
$toolDir = $PSScriptRoot
Copy-Item "$toolDir\package.json" "$deployDir\"
Copy-Item "$toolDir\src\*.mjs" "$deployDir\src\"
Copy-Item "$toolDir\extension\*" "$deployDir\extension\"
Copy-Item "$toolDir\native-host\*.mjs" "$deployDir\native-host\"
Copy-Item "$toolDir\native-host\*.json" "$deployDir\native-host\"

# Copy managed skills
Copy-Item -Recurse "$toolDir\skills" "$deployDir\skills"

# Copy managed skills to shared location for runtime resolution
$managedSkillsDir = Join-Path $env:LOCALAPPDATA "cc-director\skills\managed"
if (!(Test-Path $managedSkillsDir)) {
    New-Item -ItemType Directory -Force -Path $managedSkillsDir | Out-Null
}
Copy-Item "$toolDir\skills\*.skill.md" "$managedSkillsDir\"
Copy-Item "$toolDir\skills\manifest.json" "$managedSkillsDir\"
Write-Host "[cc-browser] Managed skills deployed to $managedSkillsDir"

# Install production dependencies (ws for WebSocket)
Push-Location $deployDir
npm install --production --silent 2>$null
Pop-Location

# Ensure cc-browser.cmd launcher exists
$cmdFile = Join-Path $binDir "cc-browser.cmd"
Set-Content -Path $cmdFile -Value '@node "%~dp0_cc-browser\src\cli.mjs" %*'

# Install native messaging host (registry key + manifest)
Write-Host "[cc-browser] Installing native messaging host..."
node "$deployDir\native-host\install.mjs" --extension-dir "$deployDir\extension" --native-host-dir "$deployDir\native-host"

# Clear service worker caches so Chrome/Brave loads updated extension code
$connectionsDir = Join-Path $env:LOCALAPPDATA "cc-director\connections"
if (Test-Path $connectionsDir) {
    Get-ChildItem $connectionsDir -Directory | ForEach-Object {
        $swDir = Join-Path $_.FullName "Default\Service Worker"
        if (Test-Path $swDir) {
            Write-Host "[cc-browser] Clearing service worker cache: $($_.Name)"
            Remove-Item -Recurse -Force $swDir -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "[cc-browser] v2 deployed successfully"
Write-Host "[cc-browser] CLI: $cmdFile"
Write-Host "[cc-browser] Source: $deployDir\src\"
