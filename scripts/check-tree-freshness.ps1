<#
.SYNOPSIS
    Fail loud when this checkout is behind origin/main.

.DESCRIPTION
    A checkout that lags origin/main makes every file in it a lie. An agent that reads
    such a tree draws confident conclusions from code that shipped, changed, or was
    deleted weeks ago, and reports them as fact. This has caused repeated wrong calls:
    a merged feature reported as never merged, and a fixed file reported as still broken.

    This script is the guard. It fetches, compares HEAD to origin/main, and fails with a
    non-zero exit code when the gap exceeds the allowed threshold. It is wired into the
    SessionStart hook (.claude/settings.json) so every agent session is told the truth
    about its tree before it reads a single file.

    NO FALLBACK: this does not auto-pull. A shared checkout may have other sessions and
    other agents working in it, and pulling underneath them is destructive. The script
    reports the exact problem and the exact fix; a human or agent decides.

.PARAMETER MaxBehind
    How many commits behind origin/main is tolerated before failing. Default 0.

.PARAMETER Quiet
    Print nothing when the tree is current. Used by the SessionStart hook so a healthy
    tree adds no noise to the session.

.EXAMPLE
    powershell -NoProfile -File scripts\check-tree-freshness.ps1
    powershell -NoProfile -File scripts\check-tree-freshness.ps1 -MaxBehind 5 -Quiet
#>
[CmdletBinding()]
param(
    [int] $MaxBehind = 0,
    [switch] $Quiet
)

$ErrorActionPreference = "Stop"

function Write-Section {
    param([string] $Text)
    Write-Output $Text
}

# Resolve the repository root from this script's location so the check works from any
# working directory (hooks do not guarantee one).
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

Push-Location $repoRoot
try {
    $insideRepo = & git rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $insideRepo -ne "true") {
        Write-Output "STALE-CHECK: not a git working tree at $repoRoot - skipping."
        exit 0
    }

    # Fetch quietly. A failure here is NOT fatal: offline is a legitimate state, but the
    # comparison below is then made against whatever origin/main we last saw, and we say so.
    $fetchFailed = $false
    & git fetch origin --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { $fetchFailed = $true }

    & git rev-parse --verify --quiet origin/main > $null 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Output "STALE-CHECK: origin/main not found - skipping."
        exit 0
    }

    $behind = [int](& git rev-list --count HEAD..origin/main 2>$null)
    $branch = & git rev-parse --abbrev-ref HEAD 2>$null
    $head = & git rev-parse --short HEAD 2>$null

    if ($behind -le $MaxBehind) {
        if (-not $Quiet) {
            Write-Output "STALE-CHECK: OK - $branch ($head) is $behind commit(s) behind origin/main."
        }
        exit 0
    }

    # Behind the threshold. Fail loud, and say exactly what to do about it.
    Write-Section ""
    Write-Section "=============================================================================="
    Write-Section " STOP - THIS CHECKOUT IS STALE. DO NOT TRUST WHAT YOU READ IN IT."
    Write-Section "=============================================================================="
    Write-Section ""
    Write-Section "  Tree:      $repoRoot"
    Write-Section "  Branch:    $branch ($head)"
    Write-Section "  Behind:    $behind commit(s) behind origin/main"
    if ($fetchFailed) {
        Write-Section "  WARNING:   git fetch FAILED - the real gap may be larger than $behind."
    }
    Write-Section ""
    Write-Section "  Every file in this tree may be out of date. Reading it and reporting what"
    Write-Section "  you find as fact is how we have shipped wrong conclusions before: a merged"
    Write-Section "  feature reported as never merged, a fixed file reported as still broken."
    Write-Section ""
    Write-Section "  THE RULE: never read code from a stale tree. Read shipped code, or work in"
    Write-Section "  a worktree cut from origin/main."
    Write-Section ""
    Write-Section "  To READ shipped code without touching this tree:"
    Write-Section "      git fetch origin"
    Write-Section "      git show origin/main:path/to/file.cs"
    Write-Section "      git grep <pattern> origin/main"
    Write-Section ""
    Write-Section "  To WORK (build, edit, open a pull request), cut a worktree off origin/main:"
    Write-Section "      git fetch origin"
    Write-Section "      git worktree add ../<repo>-<task> -b <branch-name> origin/main"
    Write-Section ""
    Write-Section "  Do NOT git pull this tree without checking who else is working in it -"
    Write-Section "  it is shared, and pulling underneath a live session breaks that session."
    Write-Section ""
    Write-Section "=============================================================================="
    Write-Section ""

    exit 1
}
finally {
    Pop-Location
}
