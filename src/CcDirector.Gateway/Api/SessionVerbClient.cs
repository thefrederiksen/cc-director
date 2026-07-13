using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission: the tunnel-only caller for the session verbs the voice and dictation cluster
/// needs (turns / buffer / prompt / create). Each method routes through <see cref="DirectorCommandRouter"/>:
/// a non-null <see cref="DirectorCommandResult"/> is authoritative (its Ok/typed-failure maps to a null body
/// / null return on failure), and a null result means the owning Director is not connected to the tunnel =
/// unreachable, which maps to the same not-found/failure the old HTTP path produced when the Director was down.
///
/// One instance binds a resolved owning <see cref="DirectorDto"/> - its <see cref="DirectorDto.DirectorId"/>
/// for the tunnel - to the send-command hook. This keeps the routing decision in ONE place instead of
/// scattering it across the many call sites in the cluster.
/// </summary>
internal sealed class SessionVerbClient
{
    private readonly DirectorDto _director;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;

    public SessionVerbClient(DirectorDto director,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _sendCommand = sendCommand;
    }

    /// <summary>The resolved owning Director (so a caller can log or thread its id).</summary>
    public DirectorDto Director => _director;

    /// <summary>
    /// Gateway Cleanup mission: bind a tunnel-only caller to a Director known only by its
    /// <paramref name="directorId"/> - the shape the machine spawner and the work-list drain driver hold.
    /// These are director-level callers (they create a session and read its buffer on a resolved MACHINE,
    /// not a pre-located session), so there is no owning-session resolve to do; a minimal
    /// <see cref="DirectorDto"/> carrying just the id is enough for the tunnel.
    /// </summary>
    public static SessionVerbClient ForDirector(string directorId,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        var director = new DirectorDto { DirectorId = directorId ?? "" };
        return new SessionVerbClient(director, sendCommand);
    }

    /// <summary>
    /// Resolve the Director that owns <paramref name="sid"/> (from the pushed store - the SAME resolution the
    /// session REST endpoints use via <see cref="GatewayEndpoints.LocateSessionAsync"/>) and bind it to a
    /// tunnel-only caller. Returns null when no Director owns the session.
    /// </summary>
    public static async Task<SessionVerbClient?> ResolveAsync(
        string sid, DirectorRegistry registry,
        PushedSessionStore? pushedSessions, TimeSpan streamStale, SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        var (director, session) = await GatewayEndpoints.LocateSessionAsync(
            registry, sid, pushedSessions, streamStale, owners);
        if (director is null || session is null)
            return null;
        return new SessionVerbClient(director, sendCommand);
    }

    /// <summary>Read the session's turn widgets. Tunnel-only ("turns" verb -> <see cref="TurnsResponse"/>);
    /// a failed or absent tunnel result maps to null (owning Director not connected = unreachable).</summary>
    public async Task<TurnsResponse?> GetTurnsAsync(string sid, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "turns", sid, null, ct);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<TurnsResponse>(result) : null;
    }

    /// <summary>Read the session terminal buffer. Tunnel-only ("buffer" verb, the query arguments in a
    /// <see cref="BufferRequest"/> payload -> <see cref="BufferResponse"/>).</summary>
    public async Task<BufferResponse?> GetBufferAsync(string sid, int? lines, bool raw, long? since, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = raw, Since = since }, ct);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(result) : null;
    }

    /// <summary>Send a prompt into the session. Tunnel-only ("prompt" verb, the <see cref="PromptRequest"/>
    /// as payload -> <see cref="PromptResponse"/>); a failed or absent tunnel result maps to the same
    /// (false, null, error) tuple. This is the UserInput send the voice cluster needs.</summary>
    public async Task<(bool ok, PromptResponse? body, string? error)> PostPromptAsync(
        string sid, PromptRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "prompt", sid, req, ct);
        if (result is null)
            return (false, null, "owning Director is not connected to the tunnel");
        return result.Ok
            ? (true, DirectorCommandRouter.ReadBody<PromptResponse>(result), null)
            : (false, null, DirectorCommandRouter.DescribeFailure(result));
    }

    /// <summary>
    /// Create a new session on this Director. Tunnel-only ("create" verb - director-level, so the command
    /// carries an EMPTY session id exactly like the /directors surface reads do; the
    /// <see cref="NewSessionRequest"/> is the payload -&gt; <see cref="SessionDto"/>). A failed or absent
    /// tunnel result maps to the same (false, null, error) tuple.
    /// </summary>
    public async Task<(bool ok, SessionDto? body, string? error)> CreateSessionAsync(
        NewSessionRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "create", "", req, ct);
        if (result is null)
            return (false, null, "owning Director is not connected to the tunnel");
        return result.Ok
            ? (true, DirectorCommandRouter.ReadBody<SessionDto>(result), null)
            : (false, null, DirectorCommandRouter.DescribeFailure(result));
    }
}
