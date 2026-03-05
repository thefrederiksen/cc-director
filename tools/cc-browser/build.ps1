# CC Browser v2 Build Script
# Deploys v2 source files to %LOCALAPPDATA%\cc-director\bin\_cc-browser\

$ErrorActionPreference = "Stop"

$binDir = Join-Path $env:LOCALAPPDATA "cc-director\bin"
$deployDir = Join-Path $binDir "_cc-browser"

Write-Host "[cc-browser] Deploying v2 to $deployDir"

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

Write-Host "[cc-browser] v2 deployed successfully"
Write-Host "[cc-browser] CLI: $cmdFile"
Write-Host "[cc-browser] Source: $deployDir\src\"
