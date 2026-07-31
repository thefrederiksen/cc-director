#Requires -Version 5.1
<#
.SYNOPSIS
    Cuts a release. The only thing you need to decide is the version number.

.DESCRIPTION
    Run it, press Enter to accept the next version, done.

    The product version lives in EXACTLY ONE file: Directory.Build.props at the
    repo root (see docs/architecture/VERSIONING.md). MSBuild stamps it into every
    .NET binary in the release (Director, Gateway, Launcher, setup wizards, setup
    CLI); all UIs read it from their assembly at runtime, so no other file needs
    to change.

    WHY THIS OPENS A PULL REQUEST INSTEAD OF PUSHING TO MAIN
    -------------------------------------------------------
    main is protected by a ruleset that requires a pull request. The previous
    version of this script ended with `git push origin main` followed by
    `git push origin <tag>`. The branch push is rejected and the tag push
    succeeds, which leaves the tag pointing at a commit that is not on main - a
    released version whose bump and notes are missing from the branch everyone
    works from. That happened on v1.9.2 and had to be repaired by hand.

    So the order is: branch, commit, pull request, MERGE, and only then tag the
    commit that is now on main. The tag can never get ahead of main.

.EXAMPLE
    .\scripts\new-release.ps1
    Current version: 1.9.2
    New version [1.9.3]:            <- press Enter to take it
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$repoSlug = "thefrederiksen/devthrottle"

function Fail($message, $howToFix) {
    Write-Host ""
    Write-Host "ERROR: $message" -ForegroundColor Red
    if ($howToFix) { Write-Host $howToFix -ForegroundColor Yellow }
    Write-Host ""
    exit 1
}

# --- Tools this needs ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "The GitHub CLI (gh) is not installed." "It is required because main only accepts changes through a pull request.`nInstall from https://cli.github.com/ then run: gh auth login"
}
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "The GitHub CLI is not signed in." "Run: gh auth login" }

# --- The single version source ---
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found at $propsPath" }

[xml]$props = Get-Content $propsPath
$currentVersion = $props.SelectSingleNode("//Version").InnerText
if (-not $currentVersion) { Fail "Could not read <Version> from $propsPath" }

# --- Suggest the next patch version, which is what we ship day to day ---
$suggested = $null
if ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
    $suggested = "{0}.{1}.{2}" -f $matches[1], $matches[2], ([int]$matches[3] + 1)
}

Write-Host ""
Write-Host "Current version: $currentVersion" -ForegroundColor Cyan
if ($suggested) {
    $answer = Read-Host "New version [$suggested]"
    if ([string]::IsNullOrWhiteSpace($answer)) { $newVersion = $suggested } else { $newVersion = $answer.Trim() }
} else {
    $newVersion = (Read-Host "New version (X.Y.Z or X.Y.Z-rcN)").Trim()
}

if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Fail "Invalid version format: '$newVersion'." "Expected X.Y.Z or X.Y.Z-rcN"
}
if ($newVersion -eq $currentVersion) { Fail "New version is the same as the current one ($currentVersion)." }

$tagName = "v$newVersion"
$branch  = "release/$tagName"

if (git -C $repoRoot tag -l $tagName)                  { Fail "Tag $tagName already exists locally." }
if (git -C $repoRoot ls-remote --tags origin $tagName) { Fail "Tag $tagName already exists on the remote." "That version has been released. Pick the next one." }

# --- Release notes must exist. The workflow publishes this file verbatim as the
#     release page and fails without it. Catching that here costs nothing; a
#     pushed tag cannot be un-pushed. The file may be uncommitted - this script
#     commits it along with the version bump. ---
$notesRel  = "docs/public/release-notes/$tagName.md"   # forward slashes: matched against git status output
$notesPath = Join-Path $repoRoot "docs\public\release-notes\$tagName.md"
if (-not (Test-Path $notesPath)) {
    Fail "No written release notes for $tagName." "Expected: $notesPath`n`nWrite them first. The workflow publishes that file as the release page and refuses`nto invent a substitute - a list of internal pull request titles looks like release`nnotes and therefore ships unread."
}
$notesChars = ((Get-Content $notesPath -Raw) -replace '\s', '').Length
if ($notesChars -lt 200) {
    Fail "$notesRel has only $notesChars non-whitespace characters." "That is a placeholder, not release notes. The workflow applies the same floor."
}

# --- Guard: no .csproj may carry its own <Version> (it silently overrides the props file) ---
$stray = Get-ChildItem $repoRoot -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\archived\\' } |
    Where-Object { (Get-Content $_.FullName -Raw) -match '<Version>' }
if ($stray) {
    $list = ($stray | ForEach-Object { "  - $($_.FullName)" }) -join "`n"
    Fail "These .csproj files declare their own <Version>, overriding Directory.Build.props:`n$list" "Remove the <Version> elements; the props file is the single source of truth."
}

# --- Working tree must be clean apart from the two files this script owns ---
$notesPattern = [regex]::Escape($notesRel)
$dirty = git -C $repoRoot status --porcelain | Where-Object {
    $_ -notmatch 'Directory\.Build\.props$' -and $_ -notmatch "$notesPattern$"
}
if ($dirty) {
    Fail "The working tree has changes that are not part of this release:`n$($dirty -join "`n")" "Commit, stash or discard them first. A release must be cut from a known state."
}

# --- Cut from an up-to-date main ---
Write-Host "Fetching origin..." -ForegroundColor Gray
git -C $repoRoot fetch --quiet origin main
$currentBranch = git -C $repoRoot rev-parse --abbrev-ref HEAD
if ($currentBranch -ne "main") { Fail "You are on '$currentBranch', not main." "Run: git checkout main" }
$behind = git -C $repoRoot rev-list --count HEAD..origin/main
if ([int]$behind -gt 0) { Fail "Local main is $behind commit(s) behind origin/main." "Run: git pull" }

$isPreRelease = $newVersion -match '-rc\d+$'

Write-Host ""
Write-Host "=== Release summary ===" -ForegroundColor Yellow
Write-Host "  Version : $currentVersion -> $newVersion"
Write-Host "  Tag     : $tagName  (created only AFTER the pull request merges)"
Write-Host "  Branch  : $branch"
Write-Host "  Notes   : $notesRel ($notesChars characters)"
if ($isPreRelease) { Write-Host "  Type    : Pre-release" -ForegroundColor Yellow }
else               { Write-Host "  Type    : Stable release" -ForegroundColor Green }
Write-Host ""
Write-Host "This will bump the version, open a pull request, merge it, then tag main." -ForegroundColor Gray
Write-Host ""

$confirm = Read-Host "Go? (Y/N)"
if ($confirm -ne 'Y' -and $confirm -ne 'y') { Write-Host "Aborted. Nothing was changed." -ForegroundColor Yellow; exit 0 }

# --- 1. Branch, bump, commit, push ---
Write-Host ""
Write-Host "Creating $branch..." -ForegroundColor Cyan
git -C $repoRoot checkout -q -b $branch

$props.SelectSingleNode("//Version").InnerText = $newVersion
$props.Save($propsPath)

git -C $repoRoot add -- $propsPath $notesPath
git -C $repoRoot commit -q -m "release: $tagName"
git -C $repoRoot push -q -u origin $branch
if ($LASTEXITCODE -ne 0) { Fail "Could not push $branch." }

# --- 2. Pull request, then merge. main requires this; see the header. ---
Write-Host "Opening the pull request..." -ForegroundColor Cyan
$prBody = "Version bump and release notes for $tagName.`n`nThe tag is created only after this merges, so it can never point at a commit that is not on main.`n`nSee $notesRel for what is in this release."
gh pr create --repo $repoSlug --base main --head $branch --title "release: $tagName" --body $prBody | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Could not open the pull request." "The branch is pushed. Open and merge it by hand, then run:`n  git checkout main; git pull; git tag $tagName; git push origin $tagName" }

Write-Host "Merging..." -ForegroundColor Cyan
gh pr merge $branch --repo $repoSlug --squash --delete-branch
if ($LASTEXITCODE -ne 0) {
    Fail "The pull request did not merge." "It is open and the branch is pushed. Merge it in the browser, then run:`n  git checkout main; git pull; git tag $tagName; git push origin $tagName"
}

# --- 3. Only now: tag the commit that is actually on main ---
Write-Host "Returning to main..." -ForegroundColor Cyan
git -C $repoRoot checkout -q main
git -C $repoRoot pull -q

# Prove the merge landed before tagging it.
[xml]$check = Get-Content $propsPath
$onMain = $check.SelectSingleNode("//Version").InnerText
if ($onMain -ne $newVersion) {
    Fail "main still reports version '$onMain', not '$newVersion'." "The merge did not land as expected. Nothing has been tagged, so nothing has been`nreleased. Check the pull request, then tag by hand once main is correct."
}

Write-Host "Tagging $tagName on main..." -ForegroundColor Cyan
git -C $repoRoot tag $tagName
git -C $repoRoot push origin $tagName
if ($LASTEXITCODE -ne 0) { Fail "Could not push the tag." "main is correct; only the tag is missing. Run: git push origin $tagName" }

Write-Host ""
Write-Host "Released $tagName." -ForegroundColor Green
Write-Host "  Build    : https://github.com/$repoSlug/actions/workflows/release.yml" -ForegroundColor Cyan
Write-Host "  Release  : https://github.com/$repoSlug/releases/tag/$tagName" -ForegroundColor Cyan
Write-Host "  Download : https://stdevthrottledl.blob.core.windows.net/download/latest/devthrottle-setup-win-x64.exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "The workflow builds, signs, publishes, mirrors the public downloads, and then" -ForegroundColor Gray
Write-Host "fetches the installer back from that address to check its hash. If it goes red," -ForegroundColor Gray
Write-Host "the release is not usable - read the failing step before announcing anything." -ForegroundColor Gray
Write-Host ""
