# build.ps1 - Build cc_director_service executable
# Run from: D:\ReposFred\cc_director\scheduler\

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building cc_director_service" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check Python version
$pythonVersion = python --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Python not found" -ForegroundColor Red
    exit 1
}
Write-Host "Python: $pythonVersion"

# Check if venv exists, create if not
$venvPath = Join-Path $scriptDir "venv"
if (-not (Test-Path $venvPath)) {
    Write-Host "Creating virtual environment..." -ForegroundColor Yellow
    python -m venv venv
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to create venv" -ForegroundColor Red
        exit 1
    }
}

# Activate venv
$activateScript = Join-Path $venvPath "Scripts\Activate.ps1"
Write-Host "Activating virtual environment..."
. $activateScript

# Install/upgrade pip
Write-Host "Upgrading pip..."
python -m pip install --upgrade pip --quiet

# Install dependencies
Write-Host "Installing dependencies..."
pip install -r requirements.txt --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to install dependencies" -ForegroundColor Red
    exit 1
}

# Install the package in editable mode
Write-Host "Installing cc_director package..."
pip install -e . --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to install package" -ForegroundColor Red
    exit 1
}

# Install PyInstaller
Write-Host "Installing/verifying PyInstaller..."
pip install pyinstaller --quiet --upgrade

# Clean previous build
$distDir = Join-Path $scriptDir "dist"
$buildDir = Join-Path $scriptDir "build"
if (Test-Path $distDir) {
    Write-Host "Cleaning dist directory..."
    Remove-Item -Recurse -Force $distDir
}
if (Test-Path $buildDir) {
    Write-Host "Cleaning build directory..."
    Remove-Item -Recurse -Force $buildDir
}

# Run PyInstaller
Write-Host ""
Write-Host "Running PyInstaller..." -ForegroundColor Yellow
pyinstaller cc_director_service.spec --clean --noconfirm

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: PyInstaller failed" -ForegroundColor Red
    exit 1
}

# Verify output
$exePath = Join-Path $distDir "cc_director_service.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    $sizeMB = [math]::Round($fileInfo.Length / 1MB, 1)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Output: $exePath"
    Write-Host "Size: $sizeMB MB"
    Write-Host ""
    Write-Host "To deploy, run:" -ForegroundColor Cyan
    Write-Host "  .\platform\windows\deploy.bat"
} else {
    Write-Host "ERROR: Expected output not found: $exePath" -ForegroundColor Red
    exit 1
}
