using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Data;

/// <summary>
/// The RUNTIME half of the no-SQLite-on-hosted rule: the refusal a hosted-reachable SQLite site calls before
/// it opens a connection, so that on a hosted Gateway the open throws with the offending path named instead
/// of quietly succeeding against a file on a network share.
///
/// WHY THIS EXISTS. On 2026-07-30 the hosted Gateway answered HTTP 500 to every client for 32 minutes. It
/// keeps a SQLite database on an Azure Files network share; a deploy ran two containers against that one
/// share and the file was corrupted. A hosted Gateway has a real database (PostgreSQL) and must not keep a
/// second, file-backed one beside it. The remediation removes the SQLite store; this guard is what stops it
/// coming back, so the next hand that adds an innocent-looking SQLite-backed store to the Gateway is stopped
/// by a mechanism rather than by somebody remembering the incident.
///
/// HOSTED IS IDENTIFIED EXACTLY AS THE GATEWAY ALREADY IDENTIFIES IT. <see cref="HostedMarkerEnvVar"/> is a
/// compile-time alias of <see cref="GatewayDatabase.PostgresConnectionEnvVar"/>, not a copy of its spelling:
/// the two cannot drift apart, because there is only one string. The PREDICATE matches too - set (including
/// set-but-blank) means hosted - which is the same test <see cref="GatewayDatabase"/> uses to select
/// PostgreSQL. Set-but-blank counting as hosted is deliberate and fails CLOSED: the operator meant to point
/// at PostgreSQL and left the value empty, which is a misconfiguration, and a misconfiguration must never be
/// the thing that hands a hosted Gateway a SQLite file.
///
/// SCOPE. Self-host keeps SQLite and that is CORRECT - the rule is no SQLite on the HOSTED Gateway. On an
/// unset variable this guard returns and changes nothing, so the desktop install is untouched.
///
/// This is NOT a fallback and there is no substitute store behind it: it refuses, loudly, with the path in
/// the message. Choosing what a caller does about statistics being unavailable is the caller's failure-domain
/// decision, not this guard's.
///
/// This runtime refusal only binds the sites that CALL it. The rule is made TOTAL - including over the call
/// site nobody has written yet - by the static allowlist in
/// <c>CcDirector.Gateway.Tests.Architecture.NoSqliteOnHostedArchitectureTests</c>, which fails the build when
/// any type in the hosted Gateway assemblies touches SQLite without being named there.
/// </summary>
public static class HostedSqliteGuard
{
    /// <summary>
    /// The variable whose presence means "this Gateway is hosted". A compile-time alias of
    /// <see cref="GatewayDatabase.PostgresConnectionEnvVar"/> so the guard's notion of hosted is the SAME
    /// string as the Gateway's provider selection and cannot be edited out of agreement with it.
    /// </summary>
    public const string HostedMarkerEnvVar = GatewayDatabase.PostgresConnectionEnvVar;

    /// <summary>
    /// True when this Gateway is hosted - <see cref="HostedMarkerEnvVar"/> is SET, blank or not. Read live on
    /// every call rather than cached at first use: a cached verdict would be a stale measurement taken at
    /// whatever moment the first caller happened to arrive.
    /// </summary>
    public static bool IsHosted => Environment.GetEnvironmentVariable(HostedMarkerEnvVar) is not null;

    /// <summary>
    /// Refuse, loudly, if this Gateway is hosted. Call this immediately before opening a SQLite connection on
    /// any path a hosted Gateway can reach. On self-host it returns and does nothing.
    /// </summary>
    /// <param name="sqlitePath">The database being opened, and enough of the caller to find it in the code -
    /// this string is what the thrown message and the log line NAME, so it is the whole diagnostic value of
    /// the guard. Blank is rejected rather than tolerated: a refusal that cannot say what it refused sends
    /// the next incident hunting.</param>
    /// <exception cref="ArgumentException"><paramref name="sqlitePath"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">This Gateway is hosted.</exception>
    public static void EnsureNotHosted(string sqlitePath)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
            throw new ArgumentException(
                "A non-blank description of the SQLite database being opened is required; the guard's message " +
                "names it and that name is the whole diagnostic value of the refusal.", nameof(sqlitePath));

        if (!IsHosted) return;

        FileLog.Write($"[HostedSqliteGuard] EnsureNotHosted REFUSED: hosted Gateway tried to open SQLite at {sqlitePath}");
        throw new InvalidOperationException(
            $"The hosted Gateway tried to open a SQLite database ('{sqlitePath}'). A hosted Gateway keeps NO " +
            "SQLite database: its file would live on a network share, where a deploy running two containers " +
            "corrupts it (the 2026-07-30 outage). " + HostedMarkerEnvVar + " is set, so this Gateway is hosted " +
            "and its data belongs in PostgreSQL. There is no fallback: move this store onto PostgreSQL, or make " +
            "its surface report itself unavailable with a named reason. Self-host is unaffected and keeps SQLite.");
    }
}
