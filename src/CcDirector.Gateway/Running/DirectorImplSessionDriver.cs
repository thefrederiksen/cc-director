using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="IImplSessionDriver"/> over the Director tunnel
/// (issue #274). It uses the SAME session-create + seed-prompt
/// path the Cockpit/Director already use (ASSUMPTION confirmed: <see cref="NewSessionRequest.PrePrompt"/>
/// is the seed channel - the Director dispatches it once the agent reaches Idle), and reads the
/// session transcript with the session buffer. No new Director surface is introduced.
///
/// Gateway Cleanup mission (post-cut): both operations (create + buffer) route through the tunnel-only
/// <see cref="SessionVerbClient"/> - the resolved Director is reached DOWN its stream. The driver holds
/// the Director id (for the tunnel) and binds it to one <see cref="SessionVerbClient"/> via
/// <see cref="SessionVerbClient.ForDirector"/>.
/// </summary>
public sealed class DirectorImplSessionDriver : IImplSessionDriver
{
    private readonly SessionVerbClient _verb;

    /// <param name="directorId">The target Director's id (the tunnel leg).</param>
    /// <param name="repoPath">Absolute repo path the seeded implementation session opens in.</param>
    /// <param name="sendCommand">The send-a-command-down-the-stream hook.</param>
    internal DirectorImplSessionDriver(string directorId, string repoPath,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("repo path is required", nameof(repoPath));
        _verb = SessionVerbClient.ForDirector(directorId, sendCommand);
        _repoPath = repoPath;
    }

    private readonly string _repoPath;

    public async Task<(string? sessionId, string? error)> StartImplementationSessionAsync(
        string itemId, string seedPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seedPrompt))
            throw new ArgumentException("seed prompt is required", nameof(seedPrompt));

        FileLog.Write($"[DirectorImplSessionDriver] start: director={_verb.Director.DirectorId}, item={itemId}, seed={seedPrompt}");

        var req = new NewSessionRequest
        {
            RepoPath = _repoPath,
            Agent = "ClaudeCode",
            // The seed: built per source by the item's ISourceAdapter (issue #300). The
            // implementation-loop skill drives the whole DEV->QA loop for this item in its source
            // mode and prints the IMPL-LOOP-TERMINAL sentinel when it terminates.
            PrePrompt = seedPrompt,
            // Session origin (devthrottle_internal issue #982). A work-list item opening its own
            // session is automation, like cron: no person asked for this one and no agent session made
            // the call - the runner did, working its way down a list. Certain on this path, which runs
            // for nothing else.
            Origin = SessionOriginKinds.Schedule,
            OriginSurface = SessionOriginSurfaces.Workflow,
        };

        var (ok, body, error) = await _verb.CreateSessionAsync(req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[DirectorImplSessionDriver] start FAILED: item={itemId}, error={error}");
            return (null, error ?? "director did not return a session id");
        }

        FileLog.Write($"[DirectorImplSessionDriver] started: item={itemId}, sid={body.SessionId}");
        return (body.SessionId, null);
    }

    public async Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
    {
        var buffer = await _verb.GetBufferAsync(sessionId, lines: null, raw: false, since: null, ct);
        return buffer?.Text;
    }
}
