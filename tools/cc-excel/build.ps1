# Build script for cc-excel executable
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "Building cc-excel executable..." -ForegroundColor Cyan

# Check for Python
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host "ERROR: Python not found. Please install Python 3.11 or later." -ForegroundColor Red
    exit 1
}

# Create virtual environment if it doesn't exist
if (-not (Test-Path "venv")) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    python -m venv venv
}

# Activate virtual environment
Write-Host "Activating virtual environment..." -ForegroundColor Yellow
& .\venv\Scripts\Activate.ps1

# Install dependencies
Write-Host "Installing dependencies..." -ForegroundColor Yellow
pip install -e ".[dev]"

# Build with PyInstaller
Write-Host "Building executable with PyInstaller..." -ForegroundColor Yellow
pyinstaller cc-excel.spec --clean --noconfirm

# Check result
$exePath = "dist\cc-excel.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "SUCCESS: Built $exePath ($size MB)" -ForegroundColor Green

    # Copy to cc-tools bin directory
    $binDir = "$env:LOCALAPPDATA\cc-tools\bin"
    if (Test-Path $binDir) {
        Copy-Item $exePath "$binDir\cc-excel.exe" -Force
        Write-Host "Deployed to $binDir\cc-excel.exe" -ForegroundColor Green
    }
} else {
    Write-Host "ERROR: Build failed - executable not found" -ForegroundColor Red
    exit 1
}
