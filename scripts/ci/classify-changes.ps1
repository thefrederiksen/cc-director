[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [AllowEmptyCollection()]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$normalizedPaths = @(
    $Path |
        ForEach-Object {
            $normalizedPath = $_.Replace("\", "/")
            if ($normalizedPath.StartsWith("./", [System.StringComparison]::Ordinal)) {
                $normalizedPath.Substring(2)
            } else {
                $normalizedPath
            }
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)

if ($normalizedPaths.Count -eq 0) {
    [pscustomobject]@{
        run_dotnet = $true
        run_web    = $true
        path_count = 0
        reason     = "No changed paths were available, so every build is required."
    } | ConvertTo-Json -Compress
    exit 0
}

$documentationPrefixes = @(
    ".claude/",
    ".github/ISSUE_TEMPLATE/",
    "docs/",
    "mission/",
    "research/"
)
$documentationFiles = @(
    "AGENTS.md",
    "CLAUDE.md",
    "CONTRIBUTING.md",
    "LICENSE",
    "LICENSE.md",
    "README.md",
    "SECURITY.md"
)
$webPrefixes = @("apps/", "packages/")
$webFiles = @("eslint.config.js", "package-lock.json", "package.json")
$buildControlPrefixes = @(".github/workflows/", "scripts/ci/")

$runDotNet = $false
$runWeb = $false
$categories = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

foreach ($changedPath in $normalizedPaths) {
    $isBuildControl = $false
    foreach ($prefix in $buildControlPrefixes) {
        if ($changedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $isBuildControl = $true
            break
        }
    }

    if ($isBuildControl) {
        $runDotNet = $true
        $runWeb = $true
        [void] $categories.Add("build-control change")
        continue
    }

    $isDocumentation = $documentationFiles -contains $changedPath
    if (-not $isDocumentation) {
        foreach ($prefix in $documentationPrefixes) {
            if ($changedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isDocumentation = $true
                break
            }
        }
    }

    if ($isDocumentation) {
        [void] $categories.Add("documentation or agent-instruction change")
        continue
    }

    $isWeb = $webFiles -contains $changedPath
    if (-not $isWeb) {
        foreach ($prefix in $webPrefixes) {
            if ($changedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isWeb = $true
                break
            }
        }
    }

    if ($isWeb) {
        $runWeb = $true
        [void] $categories.Add("web change")
        continue
    }

    $runDotNet = $true
    [void] $categories.Add("product or unknown change")
}

[pscustomobject]@{
    run_dotnet = $runDotNet
    run_web    = $runWeb
    path_count = $normalizedPaths.Count
    reason     = (@($categories) | Sort-Object) -join ", "
} | ConvertTo-Json -Compress
