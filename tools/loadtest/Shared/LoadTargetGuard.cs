namespace CcDirector.LoadTest.Shared;

/// <summary>
/// The one hard safety rule of the load-test plan (devthrottle_internal issue #1173): NEVER point the
/// harness at production. Every tool in tools/loadtest calls this before touching its target, and the k6
/// script carries the same rule in JavaScript. The rule, in order:
///
///  1. A production-looking host is REFUSED with no override: anything under azurewebsites.net, and any
///     host containing "devthrottle". There is deliberately no environment variable that unlocks these.
///  2. Loopback targets (localhost, 127.0.0.1, [::1], host.docker.internal) are allowed - that is the
///     local hosted-mode rig.
///  3. Any other host (a staging rig) is allowed ONLY when LOADTEST_ALLOW_HOST is set to exactly that
///     host, so pointing at a non-local machine is always a deliberate, named act.
/// </summary>
public static class LoadTargetGuard
{
    public const string AllowHostVariable = "LOADTEST_ALLOW_HOST";

    /// <summary>Validate a Gateway base URL. Throws with the exact reason and fix on refusal.</summary>
    public static void AssertUrlAllowed(string gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new InvalidOperationException("GATEWAY_URL is required (e.g. http://127.0.0.1:7891).");
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"GATEWAY_URL is not a valid absolute URL: {gatewayUrl}");
        AssertHostAllowed(uri.Host, $"the Gateway URL {gatewayUrl}");
    }

    /// <summary>Validate a bare host name (used for the database host). Throws on refusal.</summary>
    public static void AssertHostAllowed(string host, string what)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException($"No host could be read from {what}.");

        var normalized = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();

        // Rule 1: production shapes are refused outright, before any allow rule is consulted.
        if (normalized.EndsWith("azurewebsites.net", StringComparison.Ordinal)
            || normalized.Contains("devthrottle", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"REFUSED: {what} points at '{host}', which matches the production deny list " +
                "(azurewebsites.net / devthrottle). The load-test harness NEVER runs against production, " +
                "and there is no override for this rule. Use the local rig (tools/loadtest/README.md) or a " +
                "dedicated staging host.");

        // Rule 2: the local rig. Loopback is ruled by PARSING (127.0.0.0/8 and ::1 in every
        // spelling - some hosts expand ::1 to the full zero-padded form), never by string-matching
        // one spelling of it.
        if (normalized is "localhost" or "host.docker.internal")
            return;
        if (System.Net.IPAddress.TryParse(normalized, out var parsed) && System.Net.IPAddress.IsLoopback(parsed))
            return;

        // Rule 3: a named, deliberate staging target.
        var allowed = Environment.GetEnvironmentVariable(AllowHostVariable);
        if (string.Equals(allowed?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            $"REFUSED: {what} points at non-local host '{host}'. If this is a dedicated staging rig, set " +
            $"{AllowHostVariable}={normalized} to name it deliberately. Production is refused regardless.");
    }
}
