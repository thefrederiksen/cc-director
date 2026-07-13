using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B): the tunnel-first caller for the session verbs the voice and
/// dictation cluster needs (turns / buffer / prompt). It replaces the raw
/// <c>DirectorEndpointClient.Xxx(endpoint, sid)</c> HTTP dials those endpoints used, so a resolved owning
/// Director is reached DOWN its stream when stream mode is on and only over HTTP as the byte-identical
/// fallback (stream mode off, or the Director has no active stream).
///
/// One instance binds a resolved owning <see cref="DirectorDto"/> - its <see cref="DirectorDto.DirectorId"/>
/// for the tunnel and its <see cref="DirectorDto.ControlEndpoint"/> for the fallback - to the shared
/// <see cref="DirectorEndpointClient"/> and the send-command hook (non-null only under stream mode, exactly
/// like every other tunnel caller). Every method routes through <see cref="DirectorCommandRouter"/>: a
/// non-null <see cref="DirectorCommandResult"/> is authoritative (its Ok/typed-failure maps to the SAME
/// shape the HTTP dial produced - a null body / null return on failure), and only a null result (no stream)
/// falls through to the existing HTTP dial. This keeps the tunnel-vs-HTTP decision in ONE place instead of
/// scattering an <c>if (streamMode)</c> across the many call sites in the cluster.
/// </summary>
internal sealed class SessionVerbClient
{
    private readonly DirectorEndpointClient _client;
    private readonly DirectorDto _director;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;

    public SessionVerbClient(DirectorEndpointClient client, DirectorDto director,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _sendCommand = sendCommand;
    }

    /// <summary>The resolved owning Director (so a caller can log or thread its id).</summary>
    public DirectorDto Director => _director;

    /// <summary>
    /// Resolve the Director that owns <paramref name="sid"/> (push-store first, then the HTTP-pull fallback -
    /// the SAME resolution the session REST endpoints use via <see cref="GatewayEndpoints.LocateSessionAsync"/>)
    /// and bind it to a tunnel-first caller. Returns null when no Director owns the session. The
    /// <paramref name="sendCommand"/> hook is non-null only under stream mode, exactly like every other
    /// tunnel caller; when null the returned client always uses the HTTP fallback.
    /// </summary>
    public static async Task<SessionVerbClient?> ResolveAsync(
        string sid, DirectorRegistry registry, DirectorEndpointClient client,
        PushedSessionStore? pushedSessions, TimeSpan streamStale, SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        var (director, session) = await GatewayEndpoints.LocateSessionAsync(
            registry, client, sid, pushedSessions, streamStale, owners);
        if (director is null || session is null)
            return null;
        return new SessionVerbClient(client, director, sendCommand);
    }

    /// <summary>The HTTP fallback base URL for this Director - the ControlEndpoint, then the
    /// TailnetEndpoint - trimmed. Empty when the Director advertises neither (a push-only,
    /// remotely-unreachable Director); the tunnel is the only path in that case.</summary>
    private string Endpoint => (!string.IsNullOrWhiteSpace(_director.ControlEndpoint)
        ? _director.ControlEndpoint
        : _director.TailnetEndpoint ?? "").TrimEnd('/');

    /// <summary>Read the session's turn widgets. Tunnel-first ("turns" verb -> <see cref="TurnsResponse"/>);
    /// a failed tunnel result maps to null exactly as the HTTP dial returns null on a non-200.</summary>
    public async Task<TurnsResponse?> GetTurnsAsync(string sid, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "turns", sid, null, ct);
        if (result is not null)
            return result.Ok ? DirectorCommandRouter.ReadBody<TurnsResponse>(result) : null;
        return await _client.GetTurnsAsync(Endpoint, sid, ct);
    }

    /// <summary>Read the session terminal buffer. Tunnel-first ("buffer" verb, the query arguments in a
    /// <see cref="BufferRequest"/> payload -> <see cref="BufferResponse"/>).</summary>
    public async Task<BufferResponse?> GetBufferAsync(string sid, int? lines, bool raw, long? since, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = raw, Since = since }, ct);
        if (result is not null)
            return result.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(result) : null;
        return await _client.GetBufferAsync(Endpoint, sid, lines, raw, since, ct);
    }

    /// <summary>Send a prompt into the session. Tunnel-first ("prompt" verb, the
    /// <see cref="PromptRequest"/> as payload -> <see cref="PromptResponse"/>); a failed tunnel result maps
    /// to the same (false, null, error) tuple the HTTP dial produces. This is the UserInput send the voice
    /// cluster needs; the dictation Delivery marker (SendSource.Delivery, carried over HTTP as the
    /// X-Dictation-Delivery header) is a separate mechanism handled where dictation is re-pointed, not
    /// here.</summary>
    public async Task<(bool ok, PromptResponse? body, string? error)> PostPromptAsync(
        string sid, PromptRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "prompt", sid, req, ct);
        if (result is not null)
            return result.Ok
                ? (true, DirectorCommandRouter.ReadBody<PromptResponse>(result), null)
                : (false, null, DirectorCommandRouter.DescribeFailure(result));
        return await _client.PostPromptAsync(Endpoint, sid, req, ct);
    }
}
