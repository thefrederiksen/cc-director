using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Issue #1177 (Phase 1): the ONE place the Gateway decides "send this command DOWN the Director's stream,
/// or fall back to HTTP". Every command endpoint routes through <see cref="TrySendAsync"/> so the decision
/// - and the request-DTO serialization - is uniform across verbs and cannot diverge.
///
/// The decision itself lives in the injected <paramref name="sendCommand"/> delegate (the Gateway host
/// passes <c>GatewayHost.SendCommandAsync</c> when stream mode is ON, and null when it is off). That
/// delegate returns null when the Director has no active stream connection, so a null return here means
/// "no stream - use HTTP" for BOTH the flag-off and the flag-on-but-offline cases. A non-null result -
/// success OR a typed failure - is authoritative and the endpoint must not also call HTTP.
/// </summary>
internal static class DirectorCommandRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The signature of the "send a command down a Director's stream" hook, non-null only when stream mode is on.</summary>
    public delegate Task<DirectorCommandResult?> SendDirectorCommandAsync(string directorId, DirectorCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the stream. Returns the stream result, or null to signal the caller to
    /// fall back to its existing HTTP path (stream mode off, or the Director is not stream-connected).
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
        FileLog.Write($"[DirectorCommandRouter] {verb} sid={sessionId} director={directorId}: {(result is null ? "no stream -> HTTP fallback" : $"stream status={result.Status}")}");
        return result;
    }

    /// <summary>Deserialize a verb response DTO carried in <see cref="DirectorCommandResult.BodyJson"/>.</summary>
    public static T? ReadBody<T>(DirectorCommandResult result) where T : class =>
        string.IsNullOrEmpty(result.BodyJson) ? null : JsonSerializer.Deserialize<T>(result.BodyJson, JsonOptions);

    /// <summary>Render a failed stream result as the "director returned N: msg" error string the HTTP client path uses.</summary>
    public static string DescribeFailure(DirectorCommandResult result) =>
        $"director returned {result.Status}: {result.Error}";
}
