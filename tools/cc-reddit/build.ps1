# Build script for cc-reddit
# Creates standalone executable using PyInstaller

$ErrorActionPreference = "Stop"

Write-Host "Building cc-reddit..." -ForegroundColor Cyan

# Ensure we're in the right directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Create/activate virtual environment
if (-not (Test-Path "venv")) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    python -m venv venv
}

# Activate venv
. .\venv\Scripts\Activate.ps1

# Install dependencies
Write-Host "Installing dependencies..." -ForegroundColor Yellow
pip install -r requirements.txt
pip install pyinstaller

# Build executable
Write-Host "Building executable..." -ForegroundColor Yellow
pyinstaller cc-reddit.spec --clean

# Check result
if (Test-Path "dist\cc-reddit.exe") {
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Executable: dist\cc-reddit.exe" -ForegroundColor Green
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
