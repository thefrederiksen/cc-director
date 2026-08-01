# Assert that every worker branch already merged into the mission branch is STILL an ancestor of it.
#
# Why this exists. Worker branches are merged into the mission branch one at a time as each clears
# independent review. A branch cut from an older point of another worker's branch carries an OLD COPY of
# that worker's files, so merging it can silently revert work that was already in - and git will not
# complain, because a merge that resolves in favour of the older side is a perfectly ordinary merge.
# An independent review caught exactly that shape on the statistics-to-Postgres mission: a read-port
# branch whose diff showed it deleting two refusal reasons, eight model defaults, containment guards
# and their tests, none of which its author had touched.
#
# The check is one line of git per branch and it fails loudly. It does not prevent a bad merge; it
# refuses to let one go unnoticed, which is the property that was missing.
#
# Both parameters are required on purpose. An earlier version defaulted the mission branch to the one
# mission it was written for; that name outlived the branch, so the default would have quietly checked
# a branch that no longer exists.
param(
    [Parameter(Mandatory = $true)]
    [string] $MissionBranch,
    [Parameter(Mandatory = $true)]
    [string[]] $MergedBranches
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
        $failed += "$branch (head $head) is NOT contained in $MissionBranch"
    }
    else {
        Write-Output "OK   $branch is still contained in $MissionBranch"
    }
}

if ($failed.Count -gt 0) {
    Write-Output ""
    Write-Output "FAIL: a branch you named as merged is NOT contained in $MissionBranch."
    foreach ($f in $failed) { Write-Output "  - $f" }
    Write-Output ""
    # State the FACT, not a cause. This check knows only that the head is absent; it does NOT know
    # whether it was merged and later dropped, or was never merged at all, or has had commits added
    # since the merge. An earlier version of this message asserted "a later merge has reverted an
    # earlier one" - which reads as a diagnosis, and would have been FALSE for a branch that was
    # simply never merged. A guard that names a cause its evidence cannot establish sends the reader
    # somewhere the fault is not, which is the whole reason this mission exists.
    Write-Output "That means one of these, and this check cannot tell which - go and look:"
    Write-Output "  - the branch was merged and a later merge dropped it (the case this guards);"
    Write-Output "  - the branch was never actually merged, and the caller listed it by mistake;"
    Write-Output "  - the branch has ADVANCED since it was merged, so its new head is legitimately"
    Write-Output "    absent while the merged commits are all present - check the merged commit, not"
    Write-Output "    the branch head, if that is the case."
    Write-Output ""
    Write-Output "Only pass branches you have actually merged. If work really was dropped, rebase the"
    Write-Output "offending branch onto the mission head rather than resolving in favour of the older side."
    exit 1
}

Write-Output ""
Write-Output "PASS: every merged branch is still contained in $MissionBranch."
