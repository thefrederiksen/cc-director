<#
.SYNOPSIS
    Declares this working tree to be part of a mutation proof, so every test run in it is checked against
    the head the proof pinned.

.DESCRIPTION
    We prove a security guard is real by mutating the one primitive it depends on, running the whole suite
    again, and reconciling the arithmetic: passed plus failed under the mutation must equal passed under the
    restored run.

    That reconciliation has one failure it cannot see. If the BASELINE is taken on a tree where a guard block
    has already been silently deleted and left uncommitted - which is what a KILLED run leaves behind, because
    a killed process skips its cleanup - the tree still compiles, the baseline still looks green and complete,
    and the two runs reconcile PERFECTLY. They agree with each other rather than measuring anything. The
    cheapest detector we own passes while measuring nothing.

    This script writes the declaration that arms the guard. From the moment a pin is set until it is released,
    EVERY test run in this working tree is checked: the head must be the pinned head, and the working tree must
    carry exactly the declared changes - no more (a contaminated tree) and no less (an "arm" with no mutation
    in it, which is a second baseline in disguise and reconciles perfectly against the first).

    A run with no pin is never touched. A worker mid-rework is legitimately dirty and must be able to run
    whatever it likes; only a baseline and an arm carry a meaning that depends on the tree being exact.

    The pin, and the ledger of what each proof run verified, live under the per-user local application data
    directory - OUTSIDE every working tree. That is deliberate: removing a worktree after merging is correct
    hygiene, and it is also what destroyed the only evidence for four already-merged proofs, which can now
    never be shown to have been taken on clean trees.

.EXAMPLE
    # Before the baseline run:
    ./scripts/mutation-proof-pin.ps1 set -Phase baseline -Note "tenant isolation guard, issue 1901"

.EXAMPLE
    # Before the mutation arm, after applying the mutation:
    ./scripts/mutation-proof-pin.ps1 set -Phase arm -Mutates src/CcDirector.Gateway/Api/GatewayEndpoints.cs

.EXAMPLE
    # When the proof is finished:
    ./scripts/mutation-proof-pin.ps1 release
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('set', 'status', 'release', 'ledger')]
    [string] $Verb,

    [ValidateSet('baseline', 'arm')]
    [string] $Phase,

    [string[]] $Mutates = @(),

    [string] $Note = ''
)

$ErrorActionPreference = 'Stop'

function Get-WorkingTreeRoot {
    $root = (git rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
        throw "Not inside a git working tree. Run this from the tree the proof will be run in."
    }
    return ($root -replace '\\', '/').TrimEnd('/')
}

function Get-LedgerDirectory {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($local)) {
        throw "Cannot locate the per-user local application data directory, so the ledger has no home."
    }
    return (Join-Path (Join-Path $local 'cc-director') 'mutation-proof-pins')
}

# The pin file's name. This ONE string is all the script and the guard have to agree on, and the test
# TheScriptAndTheGuardAgreeOnThePinFileName reads this very line and compares it against the guard's
# constant - so a change to either side reddens a test instead of silently disarming the guard.
$script:PinFileName = 'cc-director-mutation-proof.pin'

function Get-PinPath([string] $root) {
    # ASK GIT, DO NOT DERIVE. The first version of this script computed the pin's location from the working
    # tree path with a hash and a per-user directory, and the guard computed it again in C#. A reviewer
    # found the two copies did not agree on non-Windows platforms: this script printed "PINNED" while the
    # guard looked elsewhere, found nothing, and admitted contaminated baselines. Armed by its own output,
    # inert in fact - and continuous integration runs on Linux.
    #
    # Aligning the two derivations would have fixed that instance and kept the class. There is now no
    # derivation to diverge: one question to git, one answer, both sides. In a worktree this returns
    # .git/worktrees/<name>, so trees are separated with nothing to key on; the git directory is outside
    # "git status" so the pin cannot dirty the tree it guards; and "git clean -xdf" does not reach it.
    $gitDir = (git -C $root --no-optional-locks rev-parse --absolute-git-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitDir)) {
        throw "git could not name its own directory for '$root', so the pin has nowhere to live."
    }
    return (Join-Path $gitDir.Trim() $script:PinFileName)
}

function Read-Pin([string] $path) {
    if (-not (Test-Path $path)) { return $null }
    $fields = @{}
    foreach ($line in (Get-Content $path)) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $split = $trimmed.IndexOf('=')
        if ($split -le 0) { continue }
        $key = $trimmed.Substring(0, $split).Trim()
        $value = $trimmed.Substring($split + 1).Trim()
        if ($key -eq 'mutates') {
            if (-not $fields.ContainsKey('mutates')) { $fields['mutates'] = @() }
            $fields['mutates'] += $value
        }
        else { $fields[$key] = $value }
    }
    return $fields
}

function Get-WorkingTreeChanges([string] $root) {
    $status = (git -C $root --no-optional-locks status --porcelain=v1 --untracked-files=normal)
    if ([string]::IsNullOrWhiteSpace($status)) { return @() }
    return @($status -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
}

$root = Get-WorkingTreeRoot
$pinPath = Get-PinPath $root

switch ($Verb) {

    'set' {
        if ([string]::IsNullOrWhiteSpace($Phase)) {
            throw "Specify -Phase baseline (the tree must be unmodified) or -Phase arm (the tree must carry exactly the declared mutation)."
        }

        $currentHead = (git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
        $changes = Get-WorkingTreeChanges $root
        $existing = Read-Pin $pinPath

        # ------------------------------------------------------------------------------------------------
        # THE PINNED HEAD IS WRITTEN ONCE, WHEN THE BASELINE IS PINNED, AND IS NEVER RECOMPUTED AFTERWARDS.
        #
        # This is the defect a reviewer found in the first version, and it defeated the guard rather than
        # weakening it. Every "set" recomputed "git rev-parse HEAD" and overwrote the pinned head - so the
        # documented workflow, which is "set -Phase baseline, run, set -Phase arm, run", silently re-pinned
        # to the new head whenever HEAD had moved between the two runs. The guard then compared the arm
        # against the NEW head, found an exact match, and admitted it. The baseline and the arm had measured
        # two different programs, which is precisely the condition this guard exists to refuse, and the
        # supported happy path walked straight into it.
        #
        # A proof's pinned head is its identity. Changing it is not a correction, it is a different proof.
        # So the arm transition CARRIES the baseline's head forward and refuses if the tree has moved off
        # it; re-pinning requires an explicit release, which is loud and throws the old numbers away.
        # ------------------------------------------------------------------------------------------------

        if ($Phase -eq 'baseline') {
            if ($Mutates.Count -gt 0) {
                throw "A baseline declares no mutations. A run that carries a mutation is an arm."
            }
            if ($null -ne $existing) {
                Write-Host "CANNOT PIN A BASELINE: this working tree already has an active pin." -ForegroundColor Red
                Write-Host ("    proof:       " + $existing['proofId'])
                Write-Host ("    phase:       " + $existing['phase'])
                Write-Host ("    pinned head: " + $existing['pinnedHead'])
                Write-Host ""
                Write-Host "Overwriting it would start a second proof at a possibly different commit while the numbers"
                Write-Host "already collected still claim to belong to this one. If that proof is finished or abandoned,"
                Write-Host "say so explicitly and the old identity is retired:"
                Write-Host "    ./scripts/mutation-proof-pin.ps1 release"
                exit 1
            }
            if ($changes.Count -gt 0) {
                # Refused HERE as well as at run time, because pinning a contaminated tree would record a
                # false starting point and the run-time refusal would then look like a mystery.
                Write-Host "CANNOT PIN A BASELINE: this working tree already carries uncommitted changes." -ForegroundColor Red
                $changes | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
                Write-Host ""
                Write-Host "A baseline taken over uncommitted changes measures those changes as if they were the committed program."
                Write-Host "If a security guard has been deleted here and left behind - which is what a killed mutation run leaves -"
                Write-Host "the baseline is green, complete, and reconciles perfectly against its own arm while proving nothing."
                Write-Host ""
                Write-Host "Commit these changes, or revert them, then pin again."
                exit 1
            }
        }
        else {
            if ($Mutates.Count -eq 0) {
                throw "An arm must declare which paths its mutation touches (-Mutates src/...). An arm that declares nothing would admit any tree at all."
            }

            if ($null -eq $existing) {
                # An arm with no baseline before it has nothing to be compared against. Minting a fresh pin
                # here would create a proof whose "baseline" was never taken - and it would look identical
                # to a correct one.
                Write-Host "CANNOT SET AN ARM: this working tree has no active baseline pin." -ForegroundColor Red
                Write-Host ""
                Write-Host "A mutation arm only means something next to a baseline taken at the same commit. Pin the"
                Write-Host "baseline first and run it, then set the arm:"
                Write-Host "    ./scripts/mutation-proof-pin.ps1 set -Phase baseline"
                exit 1
            }

            if ($existing['pinnedHead'] -ne $currentHead) {
                Write-Host "CANNOT SET AN ARM: the head has moved since this proof's baseline was pinned." -ForegroundColor Red
                Write-Host ("    proof:        " + $existing['proofId'])
                Write-Host ("    pinned head:  " + $existing['pinnedHead'] + "   (what the baseline measured)")
                Write-Host ("    current head: " + $currentHead + "   (what an arm would measure now)")
                Write-Host ""
                Write-Host "The pinned head is NOT being updated to the current one. That is the whole point: a baseline"
                Write-Host "and its arm must be taken at the same commit, or the reconciliation compares two different"
                Write-Host "programs and the arithmetic means nothing while still adding up."
                Write-Host ""
                Write-Host "Either return the tree to the pinned commit and set the arm again, or abandon this proof"
                Write-Host "and start over:  ./scripts/mutation-proof-pin.ps1 release"
                exit 1
            }
        }

        # Carried forward, never recomputed, for anything other than a brand-new baseline.
        $proofId = if ($null -ne $existing) { $existing['proofId'] } else { [Guid]::NewGuid().ToString('N') }
        $head = if ($null -ne $existing) { $existing['pinnedHead'] } else { $currentHead }
        $pinnedUtc = if ($null -ne $existing) { $existing['pinnedUtc'] } else { [DateTime]::UtcNow.ToString('o') }

        New-Item -ItemType Directory -Force -Path (Get-LedgerDirectory) | Out-Null

        $lines = @(
            "# Written by scripts/mutation-proof-pin.ps1. Read MutationProofPinGuard.cs before editing by hand.",
            "proofId=$proofId",
            "phase=$Phase",
            "pinnedHead=$head",
            # Parenthesised deliberately: in PowerShell the comma binds tighter than "+", so without these
            # brackets this element parses as "pinnedUtc=" + (<timestamp>, "tree=...") and the timestamp
            # lands on a line of its own. The guard then refuses the whole proof as a malformed pin, which
            # is the correct behaviour and is exactly how this defect was found - but it is not what was
            # meant, and it would have stopped the first proof anybody tried to run.
            ("pinnedUtc=" + $pinnedUtc),
            "tree=$root"
        )
        foreach ($m in $Mutates) { $lines += ("mutates=" + ($m -replace '\\', '/').Trim()) }
        if (-not [string]::IsNullOrWhiteSpace($Note)) { $lines += "note=$Note" }

        Set-Content -Path $pinPath -Value $lines -Encoding utf8

        Write-Host "PINNED. Every test run in this working tree is now checked until the pin is released." -ForegroundColor Green
        Write-Host "    tree:        $root"
        Write-Host "    proof:       $proofId"
        Write-Host "    phase:       $Phase"
        Write-Host "    pinned head: $head"
        if ($Mutates.Count -gt 0) { Write-Host ("    mutates:     " + ($Mutates -join ', ')) }
        Write-Host "    pin file:    $pinPath"
        Write-Host ""
        Write-Host "CITE THAT PINNED HEAD IN THE PROOF WRITE-UP. It is the commit the numbers belong to."
        Write-Host "Release the pin when the proof is finished: ./scripts/mutation-proof-pin.ps1 release"
    }

    'status' {
        Write-Host "tree:     $root"
        Write-Host "pin file: $pinPath"
        if (-not (Test-Path $pinPath)) {
            Write-Host "NO PIN. Test runs in this tree are not checked, and are not this guard's business." -ForegroundColor Yellow
            exit 0
        }

        Write-Host "PIN ACTIVE:" -ForegroundColor Green
        Get-Content $pinPath | ForEach-Object { Write-Host "    $_" }
        Write-Host ""
        Write-Host "working tree now:"
        $changes = Get-WorkingTreeChanges $root
        if ($changes.Count -eq 0) { Write-Host "    (clean)" }
        else { $changes | ForEach-Object { Write-Host "    $_" } }
        Write-Host ("head now: " + (git -C $root rev-parse HEAD).Trim())
    }

    'release' {
        if (-not (Test-Path $pinPath)) {
            Write-Host "No pin was set for this working tree; nothing to release."
            exit 0
        }
        Remove-Item $pinPath -Force
        Write-Host "Pin released. Test runs in this tree are no longer checked." -ForegroundColor Green
        Write-Host "The ledger of what each proof run verified is kept, and outlives this working tree:"
        Write-Host ("    " + (Join-Path (Get-LedgerDirectory) 'mutation-proof-ledger.log'))
    }

    'ledger' {
        $ledger = Join-Path (Get-LedgerDirectory) 'mutation-proof-ledger.log'
        if (-not (Test-Path $ledger)) {
            Write-Host "No proof runs have been recorded on this machine yet: $ledger"
            exit 0
        }
        Write-Host "Every declared proof run recorded on this machine, and whether its tree was verified:"
        Write-Host "    $ledger"
        Write-Host ""
        Get-Content $ledger
    }
}
