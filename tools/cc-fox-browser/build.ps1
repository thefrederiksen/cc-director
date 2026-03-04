# Build script for cc-fox-browser (Node.js tool)
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "Building cc-fox-browser..." -ForegroundColor Cyan

# Check for Node.js
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Host "ERROR: Node.js not found. Please install Node.js." -ForegroundColor Red
    exit 1
}

$nodeVersion = node --version
Write-Host "Node.js version: $nodeVersion" -ForegroundColor Yellow

# Check for npm
$npm = Get-Command npm -ErrorAction SilentlyContinue
if (-not $npm) {
    Write-Host "ERROR: npm not found. Please install Node.js with npm." -ForegroundColor Red
    exit 1
}

# Install dependencies
Write-Host "Installing npm dependencies..." -ForegroundColor Yellow
npm install --silent
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: npm install failed" -ForegroundColor Red
    exit 1
}

# Create dist directory
$distDir = "dist"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# Copy files to dist
Copy-Item -Path "package.json" -Destination "$distDir\" -Force
if (-not (Test-Path "$distDir\src")) {
    New-Item -ItemType Directory -Path "$distDir\src" | Out-Null
}
Copy-Item -Path "src\*.mjs" -Destination "$distDir\src\" -Force

# Copy node_modules
if (Test-Path "$distDir\node_modules") {
    Remove-Item -Path "$distDir\node_modules" -Recurse -Force
}
Copy-Item -Path "node_modules" -Destination "$distDir\node_modules" -Recurse -Force

# Create launcher script in dist
$launcherContent = '@echo off
node "%~dp0src\cli.mjs" %*'
Set-Content -Path "$distDir\cc-fox-browser.cmd" -Value $launcherContent -NoNewline

Write-Host "SUCCESS: cc-fox-browser built to dist\" -ForegroundColor Green
Write-Host "  - dist\cc-fox-browser.cmd (launcher)" -ForegroundColor Green
Write-Host "  - dist\src\ (source files)" -ForegroundColor Green
Write-Host "  - dist\node_modules\ (dependencies)" -ForegroundColor Green
