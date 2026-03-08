#Requires -Version 5.1
<#
.SYNOPSIS
    Bumps version, commits, tags, and pushes to trigger a GitHub Actions release.

.DESCRIPTION
    Updates the version in all 3 locations (WPF csproj, Setup csproj, Setup XAML),
    commits the changes, creates a git tag, and pushes to origin. The existing
    GitHub Actions workflow handles building and creating the GitHub Release.

.EXAMPLE
    .\scripts\new-release.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

# --- Version file paths ---
$wpfCsproj   = Join-Path $repoRoot "src\CcDirector.Wpf\CcDirector.Wpf.csproj"
$setupCsproj = Join-Path $repoRoot "tools\cc-director-setup\CcDirectorSetup.csproj"
$setupXaml   = Join-Path $repoRoot "tools\cc-director-setup\MainWindow.xaml"

# --- Read current version ---
[xml]$csproj = Get-Content $wpfCsproj
$currentVersion = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $currentVersion) {
    Write-Error "Could not read <Version> from $wpfCsproj"
    exit 1
}

Write-Host ""
Write-Host "Current version: $currentVersion" -ForegroundColor Cyan
$newVersion = Read-Host "New version (X.Y.Z or X.Y.Z-rcN)"

# --- Validate semver format ---
if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Write-Error "Invalid version format: '$newVersion'. Expected X.Y.Z or X.Y.Z-rcN"
    exit 1
}

if ($newVersion -eq $currentVersion) {
    Write-Error "New version is the same as current version ($currentVersion)"
    exit 1
}

# --- Check tag doesn't already exist ---
$tagName = "v$newVersion"
$existingTag = git -C $repoRoot tag -l $tagName
if ($existingTag) {
    Write-Error "Tag $tagName already exists"
    exit 1
}

# --- Update files ---
Write-Host ""
Write-Host "Updating version to $newVersion..." -ForegroundColor Cyan

# 1. WPF csproj
[xml]$wpfXml = Get-Content $wpfCsproj
$wpfXml.Project.PropertyGroup.Version = $newVersion
$wpfXml.Save($wpfCsproj)
Write-Host "  [+] $wpfCsproj" -ForegroundColor Gray

# 2. Setup csproj
[xml]$setupXml = Get-Content $setupCsproj
$setupXml.Project.PropertyGroup.Version = $newVersion
$setupXml.Save($setupCsproj)
Write-Host "  [+] $setupCsproj" -ForegroundColor Gray

# 3. Setup XAML (replace version text like v1.2.0)
$xamlContent = Get-Content $setupXaml -Raw
$xamlContent = $xamlContent -replace 'Text="v[0-9]+\.[0-9]+\.[0-9]+(-rc[0-9]+)?"', "Text=`"v$newVersion`""
Set-Content $setupXaml $xamlContent -NoNewline
Write-Host "  [+] $setupXaml" -ForegroundColor Gray

# --- Determine pre-release ---
$isPreRelease = $newVersion -match '-rc\d+$'

# --- Summary ---
Write-Host ""
Write-Host "=== Release Summary ===" -ForegroundColor Yellow
Write-Host "  Version : $currentVersion -> $newVersion"
Write-Host "  Tag     : $tagName"
if ($isPreRelease) {
    Write-Host "  Type    : Pre-release" -ForegroundColor Yellow
} else {
    Write-Host "  Type    : Stable release" -ForegroundColor Green
}
Write-Host ""
Write-Host "Files changed:" -ForegroundColor Yellow
Write-Host "  - src\CcDirector.Wpf\CcDirector.Wpf.csproj"
Write-Host "  - tools\cc-director-setup\CcDirectorSetup.csproj"
Write-Host "  - tools\cc-director-setup\MainWindow.xaml"
Write-Host ""

$confirm = Read-Host "Commit, tag, and push? (Y/N)"
if ($confirm -ne 'Y' -and $confirm -ne 'y') {
    Write-Host ""
    Write-Host "Aborted. Files were updated but not committed." -ForegroundColor Yellow
    Write-Host "Run 'git checkout -- .' to undo." -ForegroundColor Yellow
    exit 0
}

# --- Git operations ---
Write-Host ""
Write-Host "Committing..." -ForegroundColor Cyan
git -C $repoRoot add $wpfCsproj $setupCsproj $setupXaml
git -C $repoRoot commit -m "release: v$newVersion"

Write-Host "Tagging $tagName..." -ForegroundColor Cyan
git -C $repoRoot tag $tagName

Write-Host "Pushing to origin..." -ForegroundColor Cyan
git -C $repoRoot push origin main
git -C $repoRoot push origin $tagName

Write-Host ""
Write-Host "Done! Release $tagName pushed." -ForegroundColor Green
Write-Host "GitHub Actions: https://github.com/thefrederiksen/cc-director/actions" -ForegroundColor Cyan
