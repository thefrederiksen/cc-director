$ErrorActionPreference = "Stop"

$project = "$PSScriptRoot\CcDirectorSetup.csproj"

Write-Host "Building cc-director-setup..." -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED" -ForegroundColor Red
    exit 1
}

$exe = "$PSScriptRoot\bin\Release\net10.0-windows\win-x64\publish\cc-director-setup.exe"
if (Test-Path $exe) {
    $size = (Get-Item $exe).Length / 1MB
    Write-Host "SUCCESS: cc-director-setup.exe ($([math]::Round($size, 1)) MB)" -ForegroundColor Green

    # Copy to dist/ for consistency with other tools
    New-Item -ItemType Directory -Path "$PSScriptRoot\dist" -Force | Out-Null
    Copy-Item $exe "$PSScriptRoot\dist\cc-director-setup.exe" -Force
} else {
    Write-Host "Build FAILED: output exe not found" -ForegroundColor Red
    exit 1
}
