# Build script for cc-crawl4ai executable
# Usage: .\build.ps1
#
# NOTE: crawl4ai uses Playwright for browser automation.
# After building, you may need to run: playwright install chromium
# to ensure browsers are available.

$ErrorActionPreference = "Stop"

Write-Host "Building cc-crawl4ai executable..." -ForegroundColor Cyan

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

# Install Playwright browsers
Write-Host "Installing Playwright browsers..." -ForegroundColor Yellow
playwright install chromium

# Build with PyInstaller
Write-Host "Building executable with PyInstaller..." -ForegroundColor Yellow
pyinstaller cc-crawl4ai.spec --clean --noconfirm

# Check result
$exePath = "dist\cc-crawl4ai.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host "SUCCESS: Built $exePath ($size MB)" -ForegroundColor Green
    Write-Host ""
    Write-Host "NOTE: After deploying, run 'playwright install chromium' on the target system" -ForegroundColor Yellow
} else {
    Write-Host "ERROR: Build failed - executable not found" -ForegroundColor Red
    exit 1
}
