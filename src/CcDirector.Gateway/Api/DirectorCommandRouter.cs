using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Issue #1177 (Phase 1): the ONE place the Gateway sends a command DOWN the Director's stream (the tunnel).
/// Every command endpoint routes through <see cref="TrySendAsync"/> so the send decision - and the
/// request-DTO serialization - is uniform across verbs and cannot diverge.
///
/// The send itself lives in the injected <paramref name="sendCommand"/> delegate
/// (<c>GatewayHost.SendCommandAsync</c>). Gateway Cleanup (the cut) made the tunnel MANDATORY and deleted
/// DirectorEndpointClient, so there is NO HTTP fallback: a null return here means the Director is not
/// tunnel-connected and the command is UNROUTABLE - the endpoint surfaces that as a 502, never an HTTP dial.
/// A non-null result - success OR a typed failure - is authoritative.
/// </summary>
internal static class DirectorCommandRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The signature of the "send a command down a Director's stream" hook.</summary>
    public delegate Task<DirectorCommandResult?> SendDirectorCommandAsync(string directorId, DirectorCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the stream. Returns the stream result, or null when the Director is not
    /// tunnel-connected (post-cut there is no HTTP fallback, so the caller surfaces null as a 502).
    /// </summary>
    public static async Task<DirectorCommandResult?> TrySendAsync(
        SendDirectorCommandAsync? sendCommand, string directorId, string verb, string sessionId, object? payload, CancellationToken ct)
    {
        if (sendCommand is null)
            return null;

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = verb,
            SessionId = sessionId,
            PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, JsonOptions),
        };

        var result = await sendCommand(directorId, command, ct);
        FileLog.Write($"[DirectorCommandRouter] {verb} sid={sessionId} director={directorId}: {(result is null ? "director not tunnel-connected (unroutable)" : $"stream status={result.Status}")}");
        return result;
    }

    /// <summary>Deserialize a verb response DTO carried in <see cref="DirectorCommandResult.BodyJson"/>.</summary>
    public static T? ReadBody<T>(DirectorCommandResult result) where T : class =>
        string.IsNullOrEmpty(result.BodyJson) ? null : JsonSerializer.Deserialize<T>(result.BodyJson, JsonOptions);

    /// <summary>Render a failed stream result as the "director returned N: msg" error string the HTTP client path uses.</summary>
    public static string DescribeFailure(DirectorCommandResult result) =>
        $"director returned {result.Status}: {result.Error}";
}
