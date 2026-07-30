# Assert that every worker branch already merged into the mission branch is STILL an ancestor of it.
#
# Why this exists. Worker branches are merged into the mission branch one at a time as each clears
# independent review. A branch cut from an older point of another worker's branch carries an OLD COPY of
# that worker's files, so merging it can silently revert work that was already in - and git will not
# complain, because a merge that resolves in favour of the older side is a perfectly ordinary merge.
# An independent review caught exactly that shape on this mission: a read-port branch whose diff showed
# it deleting two refusal reasons, eight model defaults, containment guards and their tests, none of
# which its author had touched.
#
# The check is one line of git per branch and it fails loudly. It does not prevent a bad merge; it
# refuses to let one go unnoticed, which is the property that was missing.
param(
    [string] $MissionBranch = "nosqlite-stats",
    [string[]] $MergedBranches = @()
)

$ErrorActionPreference = "Stop"
git fetch origin --quiet

$missionRef = "origin/$MissionBranch"
$failed = @()

foreach ($branch in $MergedBranches) {
    $branchRef = "origin/$branch"
    git merge-base --is-ancestor $branchRef $missionRef 2>$null
    if ($LASTEXITCODE -ne 0) {
        $head = (git rev-parse --short $branchRef 2>$null)
        $failed += "$branch (head $head) is NOT an ancestor of $MissionBranch"
    }
    else {
        Write-Output "OK   $branch is still contained in $MissionBranch"
    }
}

if ($failed.Count -gt 0) {
    Write-Output ""
    Write-Output "FAIL: work that was merged into $MissionBranch is no longer contained in it."
    foreach ($f in $failed) { Write-Output "  - $f" }
    Write-Output ""
    Write-Output "A later merge has reverted an earlier one. Do NOT merge anything else until this is"
    Write-Output "resolved: find the merge that dropped it, and rebase the offending branch onto the"
    Write-Output "mission branch head rather than resolving the conflict in favour of the older side."
    exit 1
}

Write-Output ""
Write-Output "PASS: every merged branch is still contained in $MissionBranch."
