using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Sends captured prompts and replies to the Gateway's prompt log (issue #1551) - the Director half of
/// "the Director captures, the Gateway stores".
///
/// Reuses the <see cref="GatewayClient"/>'s already-authenticated connection rather than opening a
/// second one, so the push travels the same configured, tokened path as everything else the Director
/// reports.
///
/// Honest about failure: <see cref="PushAsync"/> returns false when the Gateway did not confirm the
/// write. This sink retains nothing, so the caller must not mark those messages done - they stay
/// unrecorded and are retried at the next turn end, where they are still readable from the agent's
/// transcript. This is why the push is acknowledged with a count instead of being
/// fire-and-forget.
/// </summary>
public sealed class GatewayPromptSink : IPromptSink
{
    private readonly Func<GatewayClient?> _gateway;

    /// <param name="gateway">Resolves the Director's CURRENT Gateway connection, or null when this
    /// Director is local-only (no Gateway configured) - in which case nothing is recorded and it says
    /// so. Resolved per push, not captured: the Director rebuilds its client when the Gateway is
    /// reconfigured, and a captured instance would go stale and silently stop recording.</param>
    public GatewayPromptSink(Func<GatewayClient?> gateway) => _gateway = gateway;

    public async Task<bool> PushAsync(IReadOnlyList<PromptRecord> records)
    {
        if (records.Count == 0) return true;

        var gateway = _gateway();
        if (gateway is null)
        {
            // No Gateway means no log. Say so once per push rather than silently dropping the record
            // or pretending it was stored.
            FileLog.Write($"[GatewayPromptSink] No Gateway configured - {records.Count} message(s) NOT recorded");
            return false;
        }

        try
        {
            var written = await gateway.PushPromptsAsync(records).ConfigureAwait(false);
            if (written is null) return false;

            if (written != records.Count)
                FileLog.Write($"[GatewayPromptSink] Gateway wrote {written} of {records.Count} message(s); NOT marking them done");

            // Anything less than all of them is a failure. The Gateway's Append swallows per-record
            // write failures and reports a truthful count of what it stored, so a short count means
            // that record exists nowhere: the Director does not retain the pushed conversation. Saying true here would have
            // ConversationIngestor mark them done and the next ingest skip them - silent, permanent
            // loss. The false path re-sends at the next turn end, and a duplicate on failure recovery
            // is a far better trade than a hole in the only record.
            return written == records.Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayPromptSink] Push FAILED: {ex.Message}");
            return false;
        }
    }
}
