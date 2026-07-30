#Requires -Version 5.1
<#
.SYNOPSIS
    Bumps the product version, commits, tags, and pushes to trigger a GitHub Actions release.

.DESCRIPTION
    The product version lives in EXACTLY ONE file: Directory.Build.props at the
    repo root (see docs/architecture/VERSIONING.md). MSBuild stamps that version
    into every .NET binary in the release (Director, Gateway, Launcher, setup
    wizards, setup CLI); all UIs read it from their assembly at runtime, so no
    other file needs to change. (The Cockpit is the React app served in-process by
    the Gateway now - issue #979 - so it has no separate stamped binary.)

    This script bumps Directory.Build.props, commits, creates the vX.Y.Z git tag,
    and pushes. The GitHub Actions release workflow builds and publishes the
    release for Windows and macOS; a workflow guard fails the release if the tag
    and Directory.Build.props ever disagree.

.EXAMPLE
    .\scripts\new-release.ps1
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

# --- Check for uncommitted changes ---
$status = git -C $repoRoot status --porcelain
if ($status) {
    Write-Host ""
    Write-Host "ERROR: Working tree has uncommitted changes." -ForegroundColor Red
    Write-Host "Commit or stash your changes before running a release." -ForegroundColor Red
    Write-Host ""
    git -C $repoRoot status --short
    Write-Host ""
    exit 1
}

# --- The single version source ---
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) {
    Write-Error "Directory.Build.props not found at $propsPath"
    exit 1
}

[xml]$props = Get-Content $propsPath
$currentVersion = $props.SelectSingleNode("//Version").InnerText
if (-not $currentVersion) {
    Write-Error "Could not read <Version> from $propsPath"
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

# --- Guard: no .csproj may carry its own <Version> (it would silently override the props file) ---
$strayVersions = Get-ChildItem $repoRoot -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\archived\\' } |
    Where-Object { (Get-Content $_.FullName -Raw) -match '<Version>' }
if ($strayVersions) {
    Write-Host ""
    Write-Host "ERROR: These .csproj files declare their own <Version>, which overrides Directory.Build.props:" -ForegroundColor Red
    $strayVersions | ForEach-Object { Write-Host "  - $($_.FullName)" -ForegroundColor Red }
    Write-Host "Remove the <Version> element(s); the props file is the single source of truth." -ForegroundColor Red
    exit 1
}

# --- Guard: the written release notes MUST exist before the tag does ---
#
# The release workflow publishes docs/public/release-notes/<tag>.md verbatim and FAILS when it is
# absent - it will not generate a substitute, because a page of internal pull-request titles looks
# like release notes and therefore ships unread. This guard is the same rule applied a minute
# earlier, where it costs nothing: a tag that is already pushed cannot be un-pushed, and the
# workflow's copy of the check can only fail AFTER the whole build has run.
#
# The working tree is clean by this point, so the notes file has to be committed already.
$notesPath = Join-Path $repoRoot "docs\public\release-notes\$tagName.md"
if (-not (Test-Path $notesPath)) {
    Write-Host ""
    Write-Host "ERROR: No written release notes for $tagName." -ForegroundColor Red
    Write-Host "  Expected: $notesPath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Write the notes, commit them, then run this script again. The release workflow" -ForegroundColor Yellow
    Write-Host "publishes that file and refuses to invent a substitute." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

$notesChars = ((Get-Content $notesPath -Raw) -replace '\s', '').Length
if ($notesChars -lt 200) {
    Write-Host ""
    Write-Host "ERROR: $notesPath has only $notesChars non-whitespace characters." -ForegroundColor Red
    Write-Host "That is a placeholder, not release notes. The workflow applies the same floor." -ForegroundColor Red
    Write-Host ""
    exit 1
}
Write-Host ""
Write-Host "Release notes: $notesPath ($notesChars characters)" -ForegroundColor Gray

# --- Update the one file ---
Write-Host ""
Write-Host "Updating version to $newVersion..." -ForegroundColor Cyan
$props.SelectSingleNode("//Version").InnerText = $newVersion
$props.Save($propsPath)
Write-Host "  [+] $propsPath" -ForegroundColor Gray

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
Write-Host "File changed:" -ForegroundColor Yellow
Write-Host "  - Directory.Build.props (the single version source)"
Write-Host ""

$confirm = Read-Host "Commit, tag, and push? (Y/N)"
if ($confirm -ne 'Y' -and $confirm -ne 'y') {
    Write-Host ""
    Write-Host "Aborted. The file was updated but not committed." -ForegroundColor Yellow
    Write-Host "Run 'git checkout -- Directory.Build.props' to undo." -ForegroundColor Yellow
    exit 0
}

# --- Git operations ---
Write-Host ""
Write-Host "Committing..." -ForegroundColor Cyan
git -C $repoRoot add $propsPath
git -C $repoRoot commit -m "release: v$newVersion"

Write-Host "Tagging $tagName..." -ForegroundColor Cyan
git -C $repoRoot tag $tagName

Write-Host "Pushing to origin..." -ForegroundColor Cyan
git -C $repoRoot push origin main
git -C $repoRoot push origin $tagName

Write-Host ""
Write-Host "Done! Release $tagName pushed." -ForegroundColor Green
Write-Host "GitHub Actions: https://github.com/example-org/devthrottle/actions" -ForegroundColor Cyan
