using CcDirector.Core.Tenancy;
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

    private static long _screenGridPulls;

    /// <summary>How many <c>screen-grid</c> reads this Gateway process has sent down a tunnel since it
    /// started. Process-wide and monotonic, so a proof reads it before and after and states the DIFFERENCE.
    /// The number that matters is a difference of zero across a voice turn, with a control run showing the
    /// same counter DOES move when the store cannot answer - a counter that never moves proves nothing.</summary>
    public static long ScreenGridPulls => Interlocked.Read(ref _screenGridPulls);

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
        string sid, TenantId tenant, DirectorRegistry registry,
        PushedSessionStore? pushedSessions, TimeSpan streamStale, SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        // Hosted Multi-Tenancy: SessionVerbClient is the tunnel-only RELAY caller (voice/verb sends). The
        // caller resolves the tenant - the request tenant on the wingman voice surface, the owning tenant in
        // background loops - and passes it here, so a session is only ever located inside its own partition.
        var (director, session) = await GatewayEndpoints.LocateSessionAsync(
            registry, sid, pushedSessions, streamStale, tenant, owners);
        if (director is null || session is null)
            return null;
        return new SessionVerbClient(director, sendCommand);
    }

    /// <summary>Read the session's turn widgets. Tunnel-only ("turns" verb -> <see cref="TurnsResponse"/>);
    /// a failed or absent tunnel result maps to null (owning Director not connected = unreachable).</summary>
    public async Task<TurnsResponse?> GetTurnsAsync(string sid, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "turns", sid, null, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<TurnsResponse>(result) : null;
    }

    /// <summary>Read the session terminal buffer. Tunnel-only ("buffer" verb, the query arguments in a
    /// <see cref="BufferRequest"/> payload -> <see cref="BufferResponse"/>).</summary>
    public async Task<BufferResponse?> GetBufferAsync(string sid, int? lines, bool raw, long? since, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = raw, Since = since }, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(result) : null;
    }

    /// <summary>Read the session's RESOLVED live screen grid (issue #1777). Tunnel-only ("screen-grid" verb
    /// -> <see cref="ScreenGridResponse"/>); a failed or absent tunnel result maps to null (owning Director not
    /// connected = unreachable, which the caller treats as an unreadable screen and fails closed). This is the
    /// alternate-screen-correct read the menu detector uses - it sees a full-screen picker the scrollback-based
    /// <see cref="GetBufferAsync"/> cannot.</summary>
    public async Task<ScreenGridResponse?> GetScreenGridAsync(string sid, CancellationToken ct = default)
    {
        // Terminal Rules (issue #2644): counted HERE, on the tunnel send itself, and not on any caller.
        // The claim phase 0 has to prove is "this turn cost no tunnel screen read", and a counter kept on a
        // caller only ever measures that caller - a second caller added later would not show up in it, and
        // the count would go on reading zero while round trips were being made. Counting the thing rather
        // than a proxy for it is the difference between evidence and a number that cannot fall.
        Interlocked.Increment(ref _screenGridPulls);
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "screen-grid", sid, null, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<ScreenGridResponse>(result) : null;
    }

    /// <summary>Send a prompt into the session. Tunnel-only ("prompt" verb, the <see cref="PromptRequest"/>
    /// as payload -> <see cref="PromptResponse"/>); a failed or absent tunnel result maps to the same
    /// (false, null, error) tuple. This is the UserInput send the voice cluster needs.</summary>
    public async Task<(bool ok, PromptResponse? body, string? error)> PostPromptAsync(
        string sid, PromptRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "prompt", sid, req, ct,
            machineName: _director.MachineName);
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
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "create", "", req, ct,
            machineName: _director.MachineName);
        if (result is null)
            return (false, null, "owning Director is not connected to the tunnel");
        return result.Ok
            ? (true, DirectorCommandRouter.ReadBody<SessionDto>(result), null)
            : (false, null, DirectorCommandRouter.DescribeFailure(result));
    }
}
