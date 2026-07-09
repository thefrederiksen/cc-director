using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The SINGLE resolve-then-create path for starting a session on a target MACHINE ("start a session
/// on another computer"). Resolves the machine to a runnable Director via
/// <see cref="IDirectorTargetResolver"/> (launching one through the launcher when none is running)
/// and creates the session on it over the Gateway's existing <see cref="DirectorEndpointClient"/>.
/// Both the cron firing engine (<see cref="DirectorCronSessionStarter"/>) and the interactive
/// POST /machines/{machine}/sessions relay call this ONE method, so scheduled and on-demand spawns
/// route identically with no duplicated resolve/create logic.
///
/// Fail-fast and loud: when the machine is off / unreachable the resolver returns an Error and this
/// reports it as a failure - it NEVER falls back to a local spawn.
/// </summary>
public sealed class MachineSessionSpawner
{
    /// <summary>
    /// The create-session call. Production binds <see cref="DirectorEndpointClient.CreateSessionAsync"/>;
    /// tests inject a fake so the resolve-then-create decision is verified without a live Director.
    /// </summary>
    public delegate Task<(bool ok, SessionDto? body, string? error)> CreateSessionDelegate(
        string endpoint, NewSessionRequest req, CancellationToken ct);

    private readonly IDirectorTargetResolver _resolver;
    private readonly CreateSessionDelegate _create;

    /// <param name="client">The Gateway's Director Control API client; its
    /// <see cref="DirectorEndpointClient.CreateSessionAsync"/> is the production create call.</param>
    /// <param name="resolver">Resolves the target machine to a Director, launching one on demand.</param>
    public MachineSessionSpawner(DirectorEndpointClient client, IDirectorTargetResolver resolver)
        : this(resolver, (client ?? throw new ArgumentNullException(nameof(client))).CreateSessionAsync)
    {
    }

    /// <summary>Test seam: inject the resolver and a fake create call directly.</summary>
    internal MachineSessionSpawner(IDirectorTargetResolver resolver, CreateSessionDelegate create)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>
    /// Resolve <paramref name="machine"/> to a Director (launching one if none is running) and create the
    /// session described by <paramref name="req"/> on it. Returns <c>ok=false</c> with the resolver's or
    /// the Director's error - and NEVER a local fallback - when the machine cannot be resolved or the create
    /// fails. <c>directorId</c> is the resolved Director (for the cron run record); it is populated even on a
    /// failure the resolver could attribute to a Director.
    /// </summary>
    public async Task<(bool ok, SessionDto? dto, string? error, string? directorId)> SpawnOnMachineAsync(
        string machine, NewSessionRequest req, CancellationToken ct)
    {
        if (req is null)
            throw new ArgumentNullException(nameof(req));

        var target = await _resolver.ResolveAsync(machine, ct);
        if (string.IsNullOrEmpty(target.Endpoint))
        {
            FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync FAILED: machine={machine}, {target.Error}");
            return (false, null, target.Error ?? "could not resolve a director on the target machine", target.DirectorId);
        }

        FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync: machine={machine}, director={target.DirectorId}, endpoint={target.Endpoint}, repo={req.RepoPath}");

        var (ok, body, error) = await _create(target.Endpoint, req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync FAILED: machine={machine}, error={error}");
            return (false, null, error ?? "director did not return a session id", target.DirectorId);
        }

        FileLog.Write($"[MachineSessionSpawner] SpawnOnMachineAsync: started sid={body.SessionId}, director={target.DirectorId}");
        return (true, body, null, target.DirectorId);
    }
}
