using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// Fills the <c>session_spend</c> table (issue #1771, spine item 3) at each turn-end. The Director reads the
/// driver's cumulative token usage locally and stamps it on the session; that snapshot rides the roster push
/// up to the Gateway as <see cref="SessionDto.TokenTotals"/>. This emitter takes one located session snapshot
/// at turn-end and upserts its cumulative spend through <see cref="SessionSpendStore"/> - the totals are
/// running sums, so an overwrite is an idempotent upsert and calling it every turn-end is safe.
///
/// It keeps the honest split (never fabricates a dollar figure):
///  - Raw tokens are recorded when the driver reports additive usage; a driver that reports only a context
///    gauge (<see cref="SessionDto.TokenTotals"/> null) is recorded as <c>TokensCaptured=false</c> - UNKNOWN
///    coverage, never a real zero.
///  - The billing-mode label says which bucket the tokens fall in. A coding agent on the owner's Claude
///    subscription is <see cref="SessionBillingMode.SubscriptionIncluded"/> (which is WHY it carries no
///    per-session dollar); any other agent's mode is not determinable from the roster snapshot yet, so it is
///    honestly <see cref="SessionBillingMode.Unknown"/> - never a guessed "metered".
/// </summary>
public sealed class SessionSpendEmitter
{
    private readonly SessionSpendStore _store;

    public SessionSpendEmitter(SessionSpendStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Record one session's cumulative spend from a turn-end roster snapshot. A snapshot with no
    /// <see cref="SessionDto.TokenTotals"/> is still recorded, with <c>TokensCaptured=false</c>, so the
    /// coverage disclosure counts it as an unmeasured session rather than dropping it silently.
    /// </summary>
    public void Emit(SessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var totals = session.TokenTotals;
        var tokensCaptured = totals is not null;
        var agentKind = ResolveAgentKind(session.Agent);

        var request = new RecordSessionSpendRequest
        {
            SessionId = session.SessionId,
            AgentKind = agentKind,
            Model = session.CurrentModel,
            RepoPath = string.IsNullOrWhiteSpace(session.RepoPath) ? null : session.RepoPath,
            TokensCaptured = tokensCaptured,
            InputTokens = totals?.InputTokens ?? 0,
            OutputTokens = totals?.OutputTokens ?? 0,
            CacheReadTokens = totals?.CacheReadTokens ?? 0,
            CacheCreationTokens = totals?.CacheCreationTokens ?? 0,
            BillingMode = ResolveBillingMode(agentKind),
        };

        _store.Record(request);
        FileLog.Write($"[SessionSpendEmitter] Emit: session={session.SessionId}, agent={agentKind}, " +
                      $"tokensCaptured={tokensCaptured}, billing={request.BillingMode}");
    }

    /// <summary>
    /// Normalise the roster's agent label (<c>ClaudeCode</c>, <c>Codex</c>, ...) to a lowercase agent family
    /// so a report groups a session by its agent consistently. An unrecognised or empty value is passed
    /// through lowercased (never dropped), so a new agent kind still records rather than vanishing.
    /// </summary>
    public static string ResolveAgentKind(string? agent)
    {
        var a = (agent ?? "").Trim().ToLowerInvariant();
        return a switch
        {
            "" => "unknown",
            "claudecode" or "claude" => "claude",
            _ => a,
        };
    }

    /// <summary>
    /// The honest billing-mode label. A Claude coding session runs on the owner's subscription, so it is
    /// subscription-included (no marginal dollar cost, which is WHY there is no per-session dollar figure) -
    /// never a fabricated "metered". Any other agent's mode is not yet determinable from the roster snapshot,
    /// so it is "unknown" rather than a guess. A precise subscription-versus-API-key signal is a follow-on
    /// that needs a Director-side flag on the pushed session.
    /// </summary>
    public static string ResolveBillingMode(string agentKind) =>
        agentKind == "claude" ? SessionBillingMode.SubscriptionIncluded : SessionBillingMode.Unknown;
}
