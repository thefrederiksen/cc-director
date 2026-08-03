# The never-production target guard, for the PowerShell side of the harness.
#
# The same three rules as Shared/LoadTargetGuard.cs and the copy inside stage1-roster.js, in the same
# order: the production deny list has no override; loopback is free; any other host must be named exactly
# in LOADTEST_ALLOW_HOST. It lives in its own file because two scripts need it and a security guard kept
# in duplicate is a guard that will eventually be tightened in one copy only.
#
# Dot-source it and call the function:
#   . (Join-Path $PSScriptRoot "loadtarget-guard.ps1")
#   Assert-LoadTargetAllowed -GatewayUrl $GatewayUrl

function Assert-LoadTargetAllowed {
    param([Parameter(Mandatory = $true)][string]$GatewayUrl)

    $uri = [Uri]$GatewayUrl
    # Uri.Host keeps IPv6 in brackets, and Windows PowerShell 5.1 EXPANDS [::1] to the full zero-padded
    # form - so loopback is ruled by PARSING the address (IPAddress.IsLoopback covers 127.0.0.0/8 and
    # ::1 in every spelling), never by string-matching one spelling of it. Trailing dots are trimmed
    # first so the absolute-DNS spelling cannot slip past.
    $hostName = $uri.Host.ToLowerInvariant().TrimEnd('.').Trim('[', ']')
    if ($hostName.EndsWith("azurewebsites.net") -or $hostName.Contains("devthrottle")) {
        throw "REFUSED: $GatewayUrl matches the production deny list. The harness never runs against production; there is no override."
    }
    $parsedIp = $null
    $isLoopback = [System.Net.IPAddress]::TryParse($hostName, [ref]$parsedIp) -and [System.Net.IPAddress]::IsLoopback($parsedIp)
    if (-not ($isLoopback -or $hostName -eq 'localhost' -or $hostName -eq 'host.docker.internal')) {
        $allowedHost = $env:LOADTEST_ALLOW_HOST
        if ($null -ne $allowedHost) { $allowedHost = $allowedHost.Trim().ToLowerInvariant().TrimEnd('.') }
        if ($allowedHost -ne $hostName) {
            throw "REFUSED: non-local host '$hostName'. If this is a dedicated staging rig, set LOADTEST_ALLOW_HOST=$hostName. Production is refused regardless."
        }
    }
}
