# Non-destructive UI test of the installer's Workstation path (skip Sign-in).
# Launches the built cc-director-setup.exe, selects the "I already have a gateway" (Workstation) card,
# and asserts the step rail contains only the current four-step flow - Welcome, Prerequisites,
# Install, Complete - with the retired Sign-in step gone.
#
# It stops at Prerequisites and NEVER clicks past it, so nothing is installed. That matters: with the
# Skills screen gone (issue 995 - the installer places no skills at all), the step straight after
# Prerequisites is Install, and reaching it starts a REAL install on this machine.
# It then closes the installer it launched.
$ErrorActionPreference = "Stop"

$exe    = "C:\repos\devthrottle\tools\cc-director-setup\bin\Release\net10.0-windows\win-x64\publish\cc-director-setup.exe"
$shotDir = Join-Path $env:TEMP "devthrottle-setup-wstest"
$capture = "C:\repos\devthrottle\scripts\capture-window.ps1"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-Window([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    for ($i = 0; $i -lt 40; $i++) {
        $w = $AE::RootElement.FindFirst($TS::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}
function ByName($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $root.FindFirst($TS::Descendants, $cond)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-El($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Shot([int]$procId, [string]$name) {
    & $capture -TargetPid $procId -OutPath (Join-Path $shotDir $name) | Out-Null
    Write-Host "  shot: $name"
}
function VisibleSteps($win) {
    # Read the rail circle numbers that are present (collapsed rows are absent from the UIA tree).
    $nums = @()
    foreach ($n in @(2, 6, 7, 8)) {
        $el = ById $win "Step$($n)Num"
        if ($el) { $nums += $el.Current.Name }
    }
    return ,$nums
}
function StepLabels($win) {
    $labels = @()
    foreach ($n in @(1, 2, 6, 7, 8)) {
        $el = ById $win "Step$($n)Label"
        if ($el) { $labels += $el.Current.Name }
    }
    return ,$labels
}

# Force fresh-install mode so the role picker appears (this machine already has a real install).
# The override changes ONLY the wizard's install DETECTION, pointed at an empty scratch root; it never
# redirects an actual install, and this test never reaches the Install step anyway.
$freshRoot = Join-Path $env:TEMP "devthrottle-setup-wstest-emptyroot"
New-Item -ItemType Directory -Force -Path $freshRoot | Out-Null
$env:CC_DIRECTOR_SETUP_INSTALL_ROOT = $freshRoot

$proc = Start-Process -FilePath $exe -PassThru
$procId = $proc.Id
Write-Host "Launched installer pid=$procId"
$win = Get-Window $procId
if (-not $win) { Write-Host "RESULT: FAIL - no installer window"; Stop-Process -Id $procId -Force; exit 1 }
Start-Sleep -Milliseconds 900

$fail = @()

# --- Welcome, default (Gateway pre-selected): rail should INCLUDE Sign in ---
Shot $procId "01-welcome-default-gateway.png"
$labelsDefault = StepLabels $win
Write-Host "Welcome (default) rail labels: $($labelsDefault -join ' | ')"
if ($labelsDefault -notcontains "Sign in") { $fail += "Default Gateway rail is missing the 'Sign in' step." }

# --- Select Workstation: 'I already have a gateway' ---
$ws = ById $win "HaveGatewayRadio"
if (-not $ws) { Write-Host "RESULT: FAIL - HaveGatewayRadio not found"; Stop-Process -Id $procId -Force; exit 1 }
Select-El $ws
Start-Sleep -Milliseconds 700
Shot $procId "02-welcome-workstation.png"
$labelsWs = StepLabels $win
$numsWs   = VisibleSteps $win
Write-Host "Welcome (workstation) rail labels: $($labelsWs -join ' | ')"
Write-Host "Welcome (workstation) circle numbers (steps 2..n): $($numsWs -join ' ')"
if ($labelsWs -contains "Sign in") { $fail += "Workstation rail STILL shows the 'Sign in' step." }
# The current flow is Welcome, Prerequisites, Install, Complete.
if (($numsWs -join ' ') -ne "2 3 4") { $fail += "Workstation rail numbers not renumbered cleanly (got '$($numsWs -join ' ')', expected '2 3 4')." }
if (($labelsWs -join ' | ') -ne "Welcome | Prerequisites | Install | Complete") {
    $fail += "Workstation rail does not match the four-step flow (got '$($labelsWs -join ' | ')')."
}

# --- Welcome -> Prerequisites ---
Invoke-El (ById $win "NextButton")
Start-Sleep -Milliseconds 1500
Shot $procId "03-prerequisites.png"
$onPrereq = (ByName $win "Sign in to DevThrottle") -eq $null
if (-not $onPrereq) { $fail += "Landed on the Sign-in step right after Welcome on the Workstation path." }

# --- Stop here ---
# The step after Prerequisites is Install, which starts a REAL install the moment it is shown, so this
# script does not click Next again. The rail assertions above already prove the Workstation flow: no
# Sign-in step, no Skills step, four steps in the right order.
$next = ById $win "NextButton"
Write-Host "Prerequisites Next enabled: $($next.Current.IsEnabled) (not clicked - the next step installs for real)"

# --- Close the installer we launched (this is cc-director-setup.exe, NOT cc-director.exe) ---
Stop-Process -Id $procId -Force
Write-Host ""
if ($fail.Count -eq 0) {
    Write-Host "RESULT: PASS - Workstation install skips the Sign-in step."
    Write-Host "Screenshots: $shotDir"
    exit 0
} else {
    Write-Host "RESULT: FAIL"
    $fail | ForEach-Object { Write-Host "  - $_" }
    Write-Host "Screenshots: $shotDir"
    exit 1
}
