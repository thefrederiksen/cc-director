using System.Text;
using System.Text.Json;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #1177 (Phase 1, increment 6): the Director-LOCAL services some command verbs must fire as a side
/// effect (a cache warm-up), so the stream path runs them exactly as the REST path does. Additive: verbs
/// that need no service ignore it, and a null field simply skips that side effect (as the REST endpoints
/// already do when the service is absent). Both call sites - <c>ControlEndpoints.Map</c> and
/// <c>ControlApiHost.BuildStreamClient</c> - have these in scope and pass the same instances.
/// </summary>
internal sealed class SessionCommandServices
{
    /// <summary>The auto-explain / background-briefing service (mobile/voice/wingman toggles warm it).</summary>
    public ProactiveExplainService? ProactiveExplain { get; init; }

    /// <summary>The turn-summary + goal-assessment cache (setting a wingman goal kicks an assessment).</summary>
    public TurnSummaryCache? TurnSummaryCache { get; init; }

    /// <summary>The Mission record store (attaching a session to a Mission, and honoring a create-time
    /// MissionId, resolve the Mission's display name through it). Null skips Mission resolution.</summary>
    public MissionStore? MissionStore { get; init; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (wave 3): this Director's build version string, so a director-level
    /// read that stamps it into its response (the <c>facts</c> and <c>handover</c> verbs) can serve the same
    /// value over the tunnel that the REST route stamped from <c>ControlApiHost._version</c>. The producing
    /// Director always stamps its own version, so the value is identical on both paths.
    /// </summary>
    public string? DirectorVersion { get; init; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (wave 3): the live per-host repository registry, so the <c>repos-list</c>
    /// verb reads the same instance the REST route read at Map time. Null lists nothing (as the REST route
    /// returned when no registry was wired).
    /// </summary>
    public RepositoryRegistry? Repositories { get; init; }
}

/// <summary>
/// Issue #1177 (Phase 1): the single command core shared by the Director's REST endpoints and its
/// Gateway stream down-channel. Each verb reproduces the exact guards and underlying
/// <see cref="Session"/>/<see cref="SessionManager"/> calls the REST lambda made before this refactor,
/// returning a <see cref="DirectorCommandResult"/> so both callers execute identical logic and cannot
/// drift (the same reason Phase 1a extracted the shared <c>ControlEndpoints.Map</c> session mapper).
///
/// The REST layer maps the returned <see cref="DirectorCommandStatus"/> back to <c>Results.*</c>; the
/// stream layer ships the result down the wire verbatim. These methods are NOT boundaries, so they never
/// catch - they validate and fail explicitly, and let real faults bubble to the calling boundary (the
/// endpoint lambda or the SignalR handler), per the coding standard.
/// </summary>
internal static class SessionCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (spine): every command AREA, pre-created ONCE. The verb-to-area map
    /// below is built from these. A worker fills its own area class; this list changes only when a whole new
    /// area is introduced (never for adding a verb), so it is not a merge chokepoint.
    /// </summary>
    private static readonly ISessionCommandArea[] Areas =
    {
        new SessionReadExecutor(),
        new CatalogReadExecutor(),
        new SessionWriteExecutor(),
        new QueueGitExecutor(),
        new SessionByteExecutor(),
    };

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (spine): the single verb-to-area dictionary, the one source of truth
    /// for which area owns which verb. Built ONCE at type initialization from the areas' declared verb lists;
    /// a duplicate verb across two areas throws immediately (fail loud, no silent shadow).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ISessionCommandArea> VerbMap = BuildVerbMap(Areas);

    internal static IReadOnlyDictionary<string, ISessionCommandArea> BuildVerbMap(IReadOnlyList<ISessionCommandArea> areas)
    {
        var map = new Dictionary<string, ISessionCommandArea>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            foreach (var verb in area.Verbs)
            {
                if (map.TryGetValue(verb, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate command verb '{verb}' declared by both {existing.GetType().Name} and {area.GetType().Name}. Each verb must be owned by exactly one command area.");
                map[verb] = area;
            }
        }
        return map;
    }

    /// <summary>
    /// Execute a command by verb. Looks the verb up in the single verb-to-area map and routes it to the
    /// owning area, which resolves the payload and target session with the shared guards and executes it.
    /// An unknown verb is a fail-loud <see cref="DirectorCommandStatus.BadRequest"/> naming the verb. The
    /// four connection-bound stream verbs are NOT dispatched here - they branch earlier, in the connection
    /// layer (Architect ruling A) - so this map is exactly the unary read and write surface.
    /// </summary>
    public static async Task<DirectorCommandResult> DispatchAsync(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services = null, SendSource source = SendSource.UserInput, CancellationToken cancellationToken = default)
    {
        if (sessionManager is null) throw new ArgumentNullException(nameof(sessionManager));
        if (command is null) throw new ArgumentNullException(nameof(command));

        FileLog.Write($"[SessionCommandExecutor] DispatchAsync: verb={command.Verb}, sid={command.SessionId}, cmdId={command.CommandId}, source={source}, director={directorId}");

        if (!VerbMap.TryGetValue(command.Verb, out var area))
        {
            FileLog.Write($"[SessionCommandExecutor] DispatchAsync: unknown verb '{command.Verb}'");
            return new DirectorCommandResult
            {
                CommandId = command.CommandId,
                Status = DirectorCommandStatus.BadRequest,
                Error = $"unknown verb '{command.Verb}'",
            };
        }

        var context = new SessionCommandContext(sessionManager, directorId, services, source);
        var result = await area.ExecuteAsync(context, command, cancellationToken);

        result.CommandId = command.CommandId;
        FileLog.Write($"[SessionCommandExecutor] DispatchAsync result: verb={command.Verb}, sid={command.SessionId}, status={result.Status}");
        return result;
    }

    /// <summary>
    /// The <c>prompt</c> verb: send text (with or without Enter) to a session. Mirrors the Director's
    /// <c>POST /sessions/{sid}/prompt</c> lambda exactly - invalid id -&gt; BadRequest, empty text -&gt;
    /// BadRequest, missing session -&gt; NotFound, Exited/Failed -&gt; Conflict - and returns a serialized
    /// <see cref="PromptResponse"/> on success.
    /// </summary>
    internal static async Task<DirectorCommandResult> PromptAsync(SessionManager sessionManager, DirectorCommand command, SendSource source = SendSource.UserInput)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<PromptRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.Text))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "text is required");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Gateway Cleanup mission, Phase 2: a dictation delivery marks itself in the request DTO
        // (DeliveryUploadId), so the tunnel prompt verb carries the Delivery source with no HTTP header.
        // The REST path still sets `source` from the X-Dictation-Delivery header (back-compat); either
        // signal makes this a Delivery.
        var effectiveSource = !string.IsNullOrWhiteSpace(request.DeliveryUploadId) ? SendSource.Delivery : source;

        return await SendPromptAsync(session, request, effectiveSource);
    }

    /// <summary>
    /// The prompt core, past the id/session guards: reject an exited session, capture the pre-send
    /// buffer cursor, then deliver the text. Shared by the verb handler and directly testable against
    /// a session. <paramref name="source"/> names who is sending - <see cref="SendSource.UserInput"/>
    /// (the default), <see cref="SendSource.Delivery"/> (a dictation's own arrival) or
    /// <see cref="SendSource.Internal"/> - for diagnostics only; no source is ever refused. The old
    /// dictation-lock refusal was removed deliberately (single-operator tool; the operator may inject
    /// into their own sessions whenever they like).
    /// </summary>
    internal static async Task<DirectorCommandResult> SendPromptAsync(Session session, PromptRequest request, SendSource source = SendSource.UserInput)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");

        var bufferCursor = session.Buffer?.TotalBytesWritten ?? 0;

        // DevThrottle Stats: build the origin for the Director's choke-point tally. Modality is voice for a
        // dictation delivery (SendSource.Delivery, set from the X-Dictation-Delivery marker) and typed
        // otherwise. Surface comes from the Gateway-authoritative request.Surface. Count when this is an
        // operator prompt - marked by a NON-NULL request.Surface, which the operator front doors (the
        // Gateway prompt handler and the dictation delivery) always set, stamping "unknown" when the device
        // key did not resolve. A phone/cockpit key maps to that surface; "unknown" maps to the Unknown
        // bucket the dashboard shows (decision 9: excluded volume is surfaced, never silently dropped).
        // A framework send (SendSource.Internal) and machine-to-machine traffic (fanout/broadcast, which
        // never sets Surface, so it is null) are correctly NOT counted.
        InputOrigin? origin = (source != SendSource.Internal && request.Surface is not null)
            ? new InputOrigin(
                source == SendSource.Delivery ? InputModality.Voice : InputModality.Typed,
                InputOrigin.RemoteSurfaceFromDeviceType(request.Surface))
            : null;

        if (request.AppendEnter)
            await session.SendTextAsync(request.Text, source, origin);
        else
            session.SendInput(Encoding.UTF8.GetBytes(request.Text), origin);

        var response = new PromptResponse
        {
            Accepted = true,
            SentAt = DateTime.UtcNow,
            BufferCursor = bufferCursor,
            ActivityState = session.ActivityState.ToString(),
        };
        return DirectorCommandResult.Success(Serialize(response));
    }

    /// <summary>
    /// The <c>interrupt</c> verb: hard-interrupt a session's current turn (Ctrl+C for Claude). Mirrors the
    /// Director's <c>POST /sessions/{sid}/interrupt</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound - and a driver that refuses (e.g. pi, whose double-Ctrl+C quits it) -&gt; Conflict.
    /// </summary>
    internal static async Task<DirectorCommandResult> InterruptAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return await InterruptCoreAsync(session);
    }

    /// <summary>The interrupt core, past the guards. A driver's <see cref="NotSupportedException"/> is the
    /// expected "this CLI has no safe hard interrupt" signal, surfaced as a typed Conflict result.</summary>
    internal static async Task<DirectorCommandResult> InterruptCoreAsync(Session session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            await session.InterruptAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>escape</c> verb: soft-stop a session's current turn (Esc). Mirrors the Director's
    /// <c>POST /sessions/{sid}/escape</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and a driver that refuses -&gt; Conflict.
    /// </summary>
    internal static async Task<DirectorCommandResult> EscapeAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return await EscapeCoreAsync(session);
    }

    /// <summary>The escape core, past the guards. A driver's <see cref="NotSupportedException"/> is the
    /// expected "this CLI has no soft cancel" signal, surfaced as a typed Conflict result.</summary>
    internal static async Task<DirectorCommandResult> EscapeCoreAsync(Session session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            await session.CancelTurnAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>hold</c> verb: park / un-park a session in the FIFO voice queue. Mirrors the Director's
    /// <c>POST /sessions/{sid}/hold</c> lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound -
    /// and sets <see cref="Session.OnHold"/>, returning the resulting state. An empty/absent payload
    /// defaults to <c>OnHold = true</c> (the common "hold this one" case), same as the REST endpoint.
    /// </summary>
    internal static DirectorCommandResult Hold(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<HoldRequest>(command.PayloadJson);
        var onHold = request?.OnHold ?? true;

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.OnHold = onHold;
        FileLog.Write($"[SessionCommandExecutor] hold: session={guid} onHold={onHold}");
        return DirectorCommandResult.Success(Serialize(new HoldResponse { OnHold = session.OnHold }));
    }

    /// <summary>
    /// The <c>kill</c> verb: kill then remove a session. Mirrors the Director's <c>DELETE /sessions/{sid}</c>
    /// lambda exactly: a missing session -&gt; NotFound; otherwise the kill is BEST-EFFORT (a process that
    /// already died must not leave a zombie row), so any non-KeyNotFound kill fault is logged and removal
    /// proceeds anyway. Returns <c>{ killed, removed }</c>. This deliberate best-effort catch is the shared
    /// kill semantics (issue #212 L3), which is exactly why it lives here rather than being duplicated.
    /// </summary>
    internal static async Task<DirectorCommandResult> KillAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        try
        {
            // Faster STOP: this is the FLEET/remote stop path (Gateway DELETE / stream "kill" verb), so it
            // escalates to force after the shorter FleetKillGraceMs window instead of the full desktop
            // GracefulShutdownTimeoutSeconds. Graceful-first is preserved (Ctrl+C then wait), just quicker.
            // When FleetKillGraceMs is disabled (null/non-positive) this resolves to the standard window.
            await sessionManager.KillSessionAsync(guid, sessionManager.FleetKillGraceMs);
        }
        catch (KeyNotFoundException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");
        }
        catch (Exception killEx)
        {
            // The process may have already exited; that is not a reason to leave a zombie row, so log and
            // fall through to removal. DELETE always means gone (matches the desktop close flow).
            FileLog.Write($"[SessionCommandExecutor] kill: session={guid} kill raised (process likely already gone): {killEx.Message}");
        }

        sessionManager.RemoveSession(guid);
        FileLog.Write($"[SessionCommandExecutor] kill: session={guid} killed+removed");
        return DirectorCommandResult.Success(Serialize(new { killed = true, removed = true }));
    }

    /// <summary>
    /// The <c>patch</c> verb: rename a session (the only PATCH field today). Mirrors the Director's
    /// <c>PATCH /sessions/{sid}</c> lambda - invalid id -&gt; BadRequest, unknown session -&gt; NotFound -
    /// and returns the updated session mapped through the SAME <see cref="ControlEndpoints.Map"/> the
    /// stream snapshot uses (the Gateway stamps machine/user/tailnet identity onto pushed rows during
    /// aggregation, exactly as it does for every other streamed row). The Director's own REST endpoint
    /// re-maps with its identity-stamped mapper for its local response, keeping that path byte-identical.
    /// </summary>
    internal static DirectorCommandResult Patch(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SessionUpdateRequest>(command.PayloadJson);

        if (!sessionManager.RenameSession(guid, request?.Name))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        FileLog.Write($"[SessionCommandExecutor] patch: session={guid} name=\"{request?.Name}\"");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>create</c> verb (director-level: no target session id): create a new session. This is the
    /// heaviest verb and keeps ALL the inline pre-work the REST <c>POST /sessions</c> lambda did, so REST
    /// and stream create identically: agent-kind parse, RawCli command validation, agent construction
    /// (<see cref="RawCliAgent"/> vs <see cref="AgentPluginRegistry.CreateAgent"/>), default-args
    /// resolution (issue #1017, <see cref="AgentLaunchDefaults.ResolveDefaultArgs"/> when no Args given),
    /// name-at-birth validation (issue #800), the controlled-sub-agent controller id (issue #815), the
    /// <see cref="SessionManager.CreateSession"/> call, the per-session Wingman opt-in, and the
    /// fire-and-forget PrePrompt dispatch that waits for the TUI to settle (issue #212). Returns the new
    /// session mapped through the plain <see cref="ControlEndpoints.Map"/> (the Director's own REST
    /// endpoint re-maps with its identity-stamped mapper for its local 201 response).
    /// </summary>
    internal static DirectorCommandResult Create(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services = null)
    {
        var req = Deserialize<NewSessionRequest>(command.PayloadJson);

        if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath is required");

        if (!Directory.Exists(req.RepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"repoPath does not exist: {req.RepoPath}");

        if (!Enum.TryParse<AgentKind>(req.Agent, ignoreCase: true, out var kind))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown agent: {req.Agent}. Valid: ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli");

        // Automatic session roles (chunk 2.5): reject an unknown explicit role BEFORE creating the session,
        // so a mistyped --role never silently drops (the exact --type situation we removed).
        if (req.Role is not null && !SessionRoles.IsValid(req.Role))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown role '{req.Role}'. Valid: {string.Join(", ", SessionRoles.All)}");

        // Mission attach at spawn. Missions are a FLEET-level concept and now live at the Gateway (source of
        // truth), so the mission name that binds the session into a pod arrives ON the create request:
        //   * GATEWAY path (MissionId AND MissionName both set): the Gateway already resolved+validated the
        //     mission against its OWN store, so the Director stamps the attachment DIRECTLY - no local-store
        //     lookup, no local validation. This is the end state.
        //   * TRANSITIONAL BRIDGE (MissionId set, MissionName blank): an old caller hitting the Director's
        //     POST /sessions directly for a Director-store mission. The Director resolves the name from its
        //     own MissionStore exactly as before, rejecting an unknown mission. This local-lookup bridge is
        //     TEMPORARY: it is REMOVED when the Gateway Cleanup Phase 1 drops the Director MissionStore, after
        //     which the Director never resolves a mission name locally and only stamps what create carries.
        //   * No MissionId: no attach.
        // Resolved BEFORE creating the session (mirroring the explicit-role check) so an unknown mission never
        // silently drops. attachMissionId / attachMissionName below carry the values stamped after creation.
        Guid? attachMissionId = null;
        string? attachMissionName = null;
        if (req.MissionId is Guid createMissionId)
        {
            if (!string.IsNullOrWhiteSpace(req.MissionName))
            {
                // Gateway path: trust the Gateway's already-validated mission id + name; stamp directly.
                attachMissionId = createMissionId;
                attachMissionName = req.MissionName;
            }
            else
            {
                // Transitional bridge: resolve the name from the local Director MissionStore. Removed with
                // the Director MissionStore in Phase 1.
                var mission = services?.MissionStore?.Get(createMissionId);
                if (mission is null)
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                        $"unknown mission '{createMissionId}'. Create it first with POST /missions.");
                attachMissionId = mission.MissionId;
                attachMissionName = mission.MissionName;
            }
        }

        // RawCli requires a Command; validate before constructing the agent.
        if (kind == AgentKind.RawCli && string.IsNullOrWhiteSpace(req.Command))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "command is required when agent is RawCli");

        IAgent agent;
        if (kind == AgentKind.RawCli)
        {
            // Non-null here: the RawCli guard above already rejected a blank command.
            var rawCommand = req.Command ?? throw new InvalidOperationException("RawCli command missing after validation");
            agent = new RawCliAgent(rawCommand, req.CommandArgs);
        }
        else
        {
            agent = AgentPluginRegistry.CreateAgent(kind, sessionManager.Options);
        }

        // Issue #1017: with no explicit Args, inherit the configured default launch line for this kind
        // (most importantly the permission-mode preset), exactly as the desktop New Session dialog does.
        // An explicitly supplied Args (even empty) is honored verbatim; RawCli carries its whole command
        // line, so it has nothing to inherit. Empty normalizes back to null so Claude's legacy
        // DefaultClaudeArgs fallback still applies.
        string? effectiveArgs = req.Args;
        if (req.Args is null && kind != AgentKind.RawCli)
        {
            var resolvedDefault = AgentLaunchDefaults.ResolveDefaultArgs(kind, sessionManager.Options);
            effectiveArgs = string.IsNullOrWhiteSpace(resolvedDefault) ? null : resolvedDefault;
            FileLog.Write($"[SessionCommandExecutor] create: no args supplied; applied default agent settings for {kind}: \"{effectiveArgs ?? "(empty)"}\"");
        }

        // Issue #800: enforce a meaningful name at birth. An EXPLICIT name that is blank or equal to the
        // bare repository folder name is rejected; an ABSENT name is auto-composed by the name factory.
        var repoFolderName = SessionName.FolderName(req.RepoPath);
        if (req.Name is not null && SessionName.IsWeakExplicitName(req.Name, repoFolderName))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"Provide a meaningful session name or a purpose: a blank name or the bare repository folder name (\"{repoFolderName}\") is not allowed.");

        var explicitName = req.Name;
        var purpose = req.Purpose;

        // Issue #815: a controlled "Supporting" sub-agent carries the spawning session's id (set only at
        // birth). An absent/unparseable value leaves it a normal (uncontrolled) session.
        Guid? controllerSessionId = null;
        if (!string.IsNullOrWhiteSpace(req.ControllerSessionId)
            && Guid.TryParse(req.ControllerSessionId, out var parsedControllerId))
            controllerSessionId = parsedControllerId;

        Session session;
        try
        {
            session = sessionManager.CreateSession(
                req.RepoPath,
                agent,
                effectiveArgs,
                SessionBackendType.ConPty,
                resumeSessionId: string.IsNullOrWhiteSpace(req.ResumeSessionId) ? null : req.ResumeSessionId,
                // Automatic session roles (chunk 3): a controlled-at-birth session is a Worker, so its
                // auto-composed name is task-flavored; others get the repo default.
                nameFactory: id => SessionName.Compose(
                    repoFolderName, explicitName, purpose, SessionName.Disambiguator(id),
                    isWorker: controllerSessionId is not null),
                controllerSessionId: controllerSessionId);
        }
        catch (Exception ex)
        {
            // Creation genuinely can fail (spawn error, bad path); the documented contract is to surface
            // the message as an error the caller maps to 500 - the same behaviour the REST lambda had.
            FileLog.Write($"[SessionCommandExecutor] create FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
        }

        // Apply the per-session Wingman opt-in (contract default true, matching Session.WingmanEnabled).
        session.WingmanEnabled = req.WingmanEnabled;
        FileLog.Write($"[SessionCommandExecutor] create: sid={session.Id} wingmanEnabled={session.WingmanEnabled}");

        // Scheduled-run auto-dismiss (issue #1200): a cron seed marks the session auto-dismiss so it closes
        // itself when it finishes with nothing needing a human. Only then attach the verdict watcher, which
        // parses the agent's CC-DISMISS sentinel off the transcript at each turn-end and stamps the verdict
        // (which flows up to the Gateway's auto-dismiss sweep). A human-started session leaves this false and
        // is never watched or auto-closed.
        session.AutoDismiss = req.AutoDismiss;
        if (session.AutoDismiss)
        {
            new AutoDismissVerdictWatcher().Attach(session);
            FileLog.Write($"[SessionCommandExecutor] create: sid={session.Id} autoDismiss=true (verdict watcher attached)");
        }

        // Automatic session roles (chunk 3): the name was AUTO-composed unless the caller gave an explicit
        // --name. Marking it lets a later explicit rename win and never be re-auto-named.
        session.IsAutoNamed = string.IsNullOrWhiteSpace(explicitName);

        // Automatic session roles (chunk 2.5): a spawn-time explicit role is sticky and WINS over the
        // Gateway's auto-derivation (the only way to declare an Architect). Already validated above.
        var explicitRole = SessionRoles.Normalize(req.Role);
        if (explicitRole is not null)
            session.SetExplicitRole(explicitRole);

        // Mission attach at spawn: stamp the session's MissionId and cache the display name (resolved above,
        // either carried by the Gateway or resolved via the transitional local-store bridge). This is the
        // attachment that binds the new session into a pod.
        if (attachMissionId is Guid stampMissionId)
            session.AttachToMission(stampMissionId, attachMissionName);

        // Issue #212: dispatch a supplied PrePrompt once the agent is actually READY, fire-and-forget so
        // create returns immediately. Readiness = a substantial startup burst followed by a quiet poll;
        // ActivityState alone is not a gate (a fresh session reads WaitingForInput from t=0, and seeding
        // into a still-booting agent drops the Enter keypresses).
        var seedText = req.PrePrompt;
        if (!string.IsNullOrWhiteSpace(seedText))
        {
            var prePrompt = seedText;
            var waitMs = Math.Max(1000, req.PrePromptWaitMs);
            var capturedSession = session;
            _ = Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                    long lastBytes = -1;
                    while (DateTime.UtcNow < deadline)
                    {
                        var st = capturedSession.ActivityState;
                        if (st is ActivityState.Exited) { FileLog.Write($"[SessionCommandExecutor] PrePrompt: session exited before ready, sid={capturedSession.Id}"); return; }
                        var bytes = capturedSession.Buffer?.TotalBytesWritten ?? 0;
                        var settled = bytes > 1500 && bytes == lastBytes
                            && st is ActivityState.Idle or ActivityState.WaitingForInput;
                        if (settled)
                        {
                            FileLog.Write($"[SessionCommandExecutor] PrePrompt: agent ready (TUI rendered {bytes} bytes, then settled), sid={capturedSession.Id}");
                            break;
                        }
                        lastBytes = bytes;
                        await Task.Delay(750);
                    }
                    FileLog.Write($"[SessionCommandExecutor] PrePrompt: dispatching to sid={capturedSession.Id}, len={prePrompt.Length}");
                    // Framework pre-prompt (not a human racing the dictation): exempt (issue #1181, Task 3b).
                    await capturedSession.SendTextAsync(prePrompt, SendSource.Internal);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[SessionCommandExecutor] PrePrompt FAILED: {ex.Message}");
                }
            });
        }

        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>wingman-goal</c> verb: set (or clear) the session's wingman goal. Mirrors the Director's
    /// <c>POST /sessions/{sid}/wingman/goal</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - sets <c>Session.WingmanGoal</c>, and (as a side effect, when a non-blank goal is set and
    /// the cache is available) kicks an immediate goal assessment so the verdict is warm. Returns the
    /// resulting goal / goalSetAt / goalState.
    /// </summary>
    internal static DirectorCommandResult WingmanGoal(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<WingmanGoalRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.SetWingmanGoal(request?.Goal);
        FileLog.Write($"[SessionCommandExecutor] wingman-goal: session={guid} goal=\"{request?.Goal}\"");

        // Side effect (identical to the REST endpoint): warm the goal assessment now. Fire-and-forget so
        // the command returns immediately; skipped when no goal is set or the cache is absent.
        if (!string.IsNullOrWhiteSpace(request?.Goal) && services?.TurnSummaryCache is not null)
            _ = services.TurnSummaryCache.AssessGoalNowAsync(guid);

        return DirectorCommandResult.Success(Serialize(new
        {
            goal = session.WingmanGoal,
            goalSetAt = session.WingmanGoalSetAt,
            goalState = session.WingmanGoalState,
        }));
    }

    /// <summary>
    /// The <c>set-role</c> verb: (re)declare a session's sticky explicit role after birth (automatic session
    /// roles). Invalid id -&gt; BadRequest, missing session -&gt; NotFound, unknown role -&gt; BadRequest. A
    /// blank/absent role CLEARS the explicit role (reverting to auto-derivation). Returns the updated session
    /// mapped through the SAME <see cref="ControlEndpoints.Map"/> the stream snapshot uses.
    /// </summary>
    internal static DirectorCommandResult SetRole(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SetRoleRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        string? normalized;
        if (string.IsNullOrWhiteSpace(request?.Role))
        {
            normalized = null; // clear -> revert to auto-derivation
        }
        else
        {
            normalized = SessionRoles.Normalize(request.Role);
            if (normalized is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"unknown role '{request.Role}'. Valid: {string.Join(", ", SessionRoles.All)}");
        }

        session.SetExplicitRole(normalized);
        FileLog.Write($"[SessionCommandExecutor] set-role: session={guid} role={normalized ?? "(cleared)"}");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>
    /// The <c>attach-mission</c> verb: attach a session to a Mission (or DETACH it on a blank/absent
    /// MissionId). Invalid id -&gt; BadRequest, missing session -&gt; NotFound, unknown Mission -&gt;
    /// BadRequest. The Mission's display name is resolved through the Mission store and cached onto the
    /// session. Returns the updated session mapped through the SAME <see cref="ControlEndpoints.Map"/> the
    /// stream snapshot uses. Mirrors <see cref="SetRole"/>.
    /// </summary>
    internal static DirectorCommandResult AttachMission(SessionManager sessionManager, string directorId, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = Deserialize<SetMissionRequest>(command.PayloadJson);

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        if (request?.MissionId is not Guid missionId)
        {
            // Blank/absent -> detach (mirrors set-role clearing the explicit role).
            session.AttachToMission(null, null);
            FileLog.Write($"[SessionCommandExecutor] attach-mission: session={guid} detached");
            return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
        }

        var mission = services?.MissionStore?.Get(missionId);
        if (mission is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"unknown mission '{missionId}'. Create it first with POST /missions.");

        session.AttachToMission(mission.MissionId, mission.MissionName);
        FileLog.Write($"[SessionCommandExecutor] attach-mission: session={guid} mission={mission.MissionId}");
        return DirectorCommandResult.Success(Serialize(ControlEndpoints.Map(session, directorId)));
    }

    /// <summary>Serialize a verb response DTO for <see cref="DirectorCommandResult.BodyJson"/>.</summary>
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Deserialize a verb request DTO from <see cref="DirectorCommand.PayloadJson"/> ("" =&gt; null).</summary>
    internal static T? Deserialize<T>(string? payloadJson) where T : class =>
        string.IsNullOrEmpty(payloadJson) ? null : JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
}
