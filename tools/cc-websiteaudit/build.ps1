# Build script for cc-websiteaudit
# Produces dist/cc-websiteaudit.exe

$ErrorActionPreference = "Stop"

Write-Host "[*] Building cc-websiteaudit..." -ForegroundColor Cyan

# Ensure we're in the right directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Install dependencies
Write-Host "[*] Installing dependencies..."
npm install --production

# Bundle with esbuild
Write-Host "[*] Bundling with esbuild..."
npx esbuild src/cli.mjs --bundle --platform=node --format=cjs --outfile=build/cli.cjs

# Package with pkg
Write-Host "[*] Packaging with pkg..."
npx pkg build/cli.cjs --targets node18-win-x64 --output dist/cc-websiteaudit.exe

# Copy to cc-tools bin directory
$binDir = "$env:LOCALAPPDATA\cc-tools\bin"
if (Test-Path $binDir) {
    Write-Host "[*] Copying to $binDir..."
    Copy-Item dist/cc-websiteaudit.exe $binDir/
    Write-Host "[+] Installed to $binDir\cc-websiteaudit.exe"
} else {
    Write-Host "[!] Bin directory not found: $binDir" -ForegroundColor Yellow
    Write-Host "    Run manually: copy dist\cc-websiteaudit.exe to your PATH"
}

Write-Host "[+] Build complete!" -ForegroundColor Green
