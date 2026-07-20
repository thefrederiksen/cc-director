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

function Get-PinsDirectory {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($local)) {
        throw "Cannot locate the per-user local application data directory, so the pin has no environment-independent home."
    }
    return (Join-Path (Join-Path $local 'cc-director') 'mutation-proof-pins')
}

function Get-PinPath([string] $root) {
    # Must match MutationProofPinGuard.ComputePinFilePath EXACTLY, or the script writes a pin the guard
    # never reads and the whole mechanism is inert while appearing to work. The C# side of this derivation
    # is frozen by a golden value in MutationProofPinGuardTests
    # (ThePinFileNameIsAGoldenValue_BecauseAScriptDerivesTheSameNameSeparately) - if you change either
    # side, run that test and reconcile both, because a silent divergence here disarms the guard.
    #
    # SHA256::Create rather than SHA256::HashData: HashData does not exist on Windows PowerShell 5.1, which
    # is the shell this repository's scripts are driven from.
    $normalized = $root.ToLowerInvariant()
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sha = $hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally {
        $hasher.Dispose()
    }
    $digest = ([System.BitConverter]::ToString($sha) -replace '-', '').ToLowerInvariant()

    $leafSource = Split-Path -Leaf $normalized
    $leaf = -join ($leafSource.ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_) -or $_ -eq '-' -or $_ -eq '_') { $_ } else { '-' }
    })
    if ($leaf.Length -gt 40) { $leaf = $leaf.Substring(0, 40) }

    return (Join-Path (Get-PinsDirectory) ("$leaf-" + $digest.Substring(0, 16) + '.pin'))
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

        $head = (git -C $root rev-parse HEAD).Trim().ToLowerInvariant()
        $changes = Get-WorkingTreeChanges $root

        if ($Phase -eq 'baseline') {
            if ($Mutates.Count -gt 0) {
                throw "A baseline declares no mutations. A run that carries a mutation is an arm."
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
        }

        New-Item -ItemType Directory -Force -Path (Get-PinsDirectory) | Out-Null

        $lines = @(
            "# Written by scripts/mutation-proof-pin.ps1. Read MutationProofPinGuard.cs before editing by hand.",
            "phase=$Phase",
            "pinnedHead=$head",
            # Parenthesised deliberately: in PowerShell the comma binds tighter than "+", so without these
            # brackets this element parses as "pinnedUtc=" + (<timestamp>, "tree=...") and the timestamp
            # lands on a line of its own. The guard then refuses the whole proof as a malformed pin, which
            # is the correct behaviour and is exactly how this defect was found - but it is not what was
            # meant, and it would have stopped the first proof anybody tried to run.
            ("pinnedUtc=" + [DateTime]::UtcNow.ToString('o')),
            "tree=$root"
        )
        foreach ($m in $Mutates) { $lines += ("mutates=" + ($m -replace '\\', '/').Trim()) }
        if (-not [string]::IsNullOrWhiteSpace($Note)) { $lines += "note=$Note" }

        Set-Content -Path $pinPath -Value $lines -Encoding utf8

        Write-Host "PINNED. Every test run in this working tree is now checked until the pin is released." -ForegroundColor Green
        Write-Host "    tree:        $root"
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
        Write-Host ("    " + (Join-Path (Get-PinsDirectory) 'mutation-proof-ledger.log'))
    }

    'ledger' {
        $ledger = Join-Path (Get-PinsDirectory) 'mutation-proof-ledger.log'
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
