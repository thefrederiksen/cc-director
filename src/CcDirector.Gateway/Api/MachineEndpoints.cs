using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway relay routes for cross-machine Director lifecycle management via cc-launcher.
///
/// Issue #331: the cc-launcher process on each machine registers with the Gateway
/// (POST /launchers/register) and heartbeats so the Gateway knows it is live. The
/// Gateway then exposes relay routes that forward lifecycle verbs to the target
/// machine's launcher loopback REST API:
///
///   POST /launchers/register                        launcher self-registers
///   POST /launchers/{machine}/heartbeat             launcher heartbeat
///   DELETE /launchers/{machine}                     graceful launcher unregister
///   GET  /launchers                                 list registered launchers
///
///   POST /machines/{machine}/director/restart       relay -> launcher POST /director/restart
///   POST /machines/{machine}/director/start         relay -> launcher POST /director/start
///   POST /machines/{machine}/director/stop          relay -> launcher POST /director/stop
///   POST /machines/{machine}/launch                 relay -> launcher POST /launch
///   GET  /machines/{machine}/apps                   relay -> launcher GET /apps
///   GET  /machines/{machine}/files                  relay -> launcher GET /files
///
/// Relay calls are token-gated (Gateway Bearer) and audit-logged. A slot guard in the
/// relay refuses restart/stop targeting the main Director build or slots 1-4 unless the
/// request carries <c>"confirmProtected": true</c>.
///
/// Cross-machine relay: when the launcher on a remote machine registers, it supplies a
/// <c>networkAddress</c> (tailnet hostname or IP) in its registration payload.  The relay
/// uses that address (plus the registered port) to build the outbound HTTP URL so it can
/// reach launchers on other machines over Tailscale:
///   - Same-machine launcher (networkAddress empty): dials http://127.0.0.1:<port>/
///   - Remote launcher (networkAddress set):         dials http://<networkAddress>:<port>/
/// This enables the Gateway on MACHINE_A to relay lifecycle verbs to the launcher on
/// EXAMPLE-PC when EXAMPLE-PC's launcher registered with its tailnet hostname.
///
/// TENANT-SCOPED ON HOSTED, NOT DENIED (issue #1917 closed the leak by DENY; this replaces that deny with
/// the authorization it was always a placeholder for). A tenant has FULL control of every device registered
/// to its Gateway - hosted or self-hosted, no difference - including starting and stopping sessions and the
/// launcher on any of them, and including when an agent does it on the owner's behalf. Cross-machine control
/// IS the product, not a feature of it - a fleet you cannot start work on is not a fleet. (The principle is
/// recorded in full in the PRIVATE architecture record. It is restated here rather than linked, because the
/// comment this replaced was itself an authoritative-sounding rationale that outlived the code it described,
/// and a link out of the file is exactly how that survives a second time.)
///
/// WHAT THE DENY WAS FOR, AND WHY IT IS NO LONGER THE ANSWER. This family used to be TENANT-BLIND BY
/// CONSTRUCTION - no tenant dimension anywhere in the path - so on hosted any authenticated device key could
/// enumerate every tenant's machines through GET /launchers and then drive the machine routes AGAINST
/// ANOTHER TENANT'S MACHINE: cross-machine CODE EXECUTION via POST /machines/{machine}/launch, and
/// OUTBOUND-REQUEST FORGERY via POST /launchers/register, which overwrites a machine's stored token, port and
/// network address and so re-points the relay at an arbitrary host. That hole was real and it had to close.
/// The deny closed it by removing the capability from every hosted tenant on their OWN machines, which is the
/// one thing the product must never do. The correct close - stated in the deny's own un-deny instruction and
/// now implemented - is to bind machine identity to a tenant at REGISTRATION and authorize every call against
/// the CALLING tenant.
///
/// THE TENANT IS NEVER TAKEN FROM THE REQUEST. Every route resolves the calling tenant from the AUTHENTICATED
/// DEVICE KEY (<see cref="GatewayEndpoints.ResolveReadTenant"/> over
/// <see cref="Tenancy.HostedTenantBoundary"/>), never from the machine name in the path or from anything in
/// the body. A machine name is client-supplied and is unique only WITHIN a tenant; the owning tenant is
/// proven, not claimed. Deny-by-default: a hosted key that resolves to no tenant gets 403 and reaches
/// nothing - it never falls back to the Local or System partition.
///
/// SO THE KEY IS COMPOSITE, EVERYWHERE. <see cref="LauncherRegistry"/> keys on (tenant, machine) and
/// <see cref="Streaming.LauncherConnectionRegistry"/> keys on (tenant, machine), so one tenant naming a
/// machine can only ever reach its OWN entry - another tenant's launcher registered under the same bare name
/// is a DIFFERENT row and is not consulted, not listed, not overwritten and not removable.
/// <see cref="Streaming.LauncherHub"/>.Hello - the /launcher-stream half, which is not an HTTP route at all -
/// binds its connection to the tenant of its own authenticated device key and ABORTS when that key resolves
/// to none, exactly as <see cref="Streaming.DirectorHub"/>.Hello does. There is no writer left that can
/// deposit a tenant-blind row.
///
/// ALL THREE DISPATCH ARMS ARE SCOPED - INCLUDING THE FALLBACK, WHICH IS THE ONE THAT MATTERED. The old deny
/// noted that only the Director-tunnel arm was gated, because that protection lived in the TRANSPORT
/// (<c>SendCommandAsync</c> refuses to resolve a tunnel connection with no tenant in scope) rather than in
/// the route - and that the launcher stream arm and the launcher REST relay were both ungated, the REST relay
/// being the FALLBACK taken when the stream arm returns null. An ungated failure path is a gate that opens
/// exactly when something is already going wrong. All three now scope on the calling tenant:
///   * the Director tunnel - the session spawn enters the caller's tenant scope, so the transport's own
///     fail-closed resolves the caller's Director and no one else's;
///   * the launcher stream and the launcher REST relay - both behind
///     <see cref="LauncherLifecycleRelay"/>, which takes the tenant as an ARGUMENT and resolves the
///     connection, the address, the port and the bearer token as (tenant, machine).
///
/// THE PURGE THE UN-DENY INSTRUCTION REQUIRED IS DISCHARGED BY CONSTRUCTION. It asked for the launcher and
/// launcher-connection registries to be purged of rows accumulated behind the deny, deny-closed on the safe
/// side until write coverage was proven complete. Write coverage is now complete (above), and both registries
/// are process-lifetime IN-MEMORY dictionaries: <c>GatewayHost.Launchers</c> is a plain <c>new()</c>, there is
/// no launcher entity in the database, no snapshot, and nothing reloads either registry at startup. A Gateway
/// restart is therefore a full purge, and shipping this performs one. Nothing survives to be re-served.
///
/// SELF-HOST IS UNCHANGED AND IS STILL THE CONTROL. There the single tenant is Local, every authenticated
/// caller resolves to it, and the composite key degenerates to the machine name it always was - the same
/// routes, the same relay, the same behaviour, byte for byte.
///
/// WHAT THIS DELIBERATELY DOES NOT DO: reach across tenants, ever. The capability restored here is a tenant's
/// control of ITS OWN registered devices. There is no route, argument or credential in this file by which one
/// subscriber can see, start, stop or enumerate another subscriber's machines.
/// </summary>
internal static class MachineEndpoints
{
    /// <summary>
    /// Slots 1-4 and the main slot (0) are protected from unconfirmed restart/stop.
    /// Agents run on slots >= 5 (issue #331 spec: "slots 5+" relay freely; the slot-guard
    /// refuses main + 1-4 without explicit confirm).
    /// </summary>
    private static readonly Regex SlotFromPath =
        new(@"cc-director(\d*)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The launcher self-registration prefix this family owns.</summary>
    internal const string LauncherPrefix = "/launchers";

    /// <summary>The machine relay prefix this family owns.</summary>
    internal const string MachinePrefix = "/machines";

    /// <summary>
    /// Maps both surfaces - <see cref="LauncherPrefix"/> and <see cref="MachinePrefix"/> - IDENTICALLY on
    /// hosted and on self-host. There is no hosted branch here any more: the difference between the two
    /// deployments is not which routes exist, it is which TENANT the calling device key resolves to, and that
    /// is decided per request inside the handlers rather than per route at startup.
    ///
    /// This replaced a pair of <c>HostedRouteDeny.ExclusiveGroup</c> claims that refused both prefixes
    /// outright on hosted. The refusal was a placeholder for the authorization that is now in place; keeping
    /// it would keep hosted tenants locked out of their own machines, which the tenant-device-control
    /// principle forbids.
    /// </summary>
    public static void Map(IEndpointRouteBuilder outer, LauncherRegistry launchers,
        MachineSessionSpawner spawner,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand = null,
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store. When non-null, a
        // mission-scoped spawn (req.MissionId set) is validated against it here - the Gateway is the source
        // of truth - and the resolved mission NAME is stamped onto the create request so the Director stamps
        // the attachment without any local lookup. Null (old callers, tests) leaves MissionId to flow through
        // to the Director's transitional local-store bridge unchanged.
        Core.Sessions.MissionStore? missions = null,
        // Workflows mission (phase 5b, issue #1771): the workflow-run store. When non-null, a spawn is
        // SEATED on a run: an explicit req.WorkflowRunId is validated here and the run's workflow id +
        // pinned version are stamped onto the create request (the MissionName pattern); a mission-scoped
        // spawn with no explicit run auto-seats onto the mission's run. After a successful spawn the new
        // session is recorded as a run PARTICIPANT - the persisted run-to-session membership governance
        // reads. Null (old callers, tests) seats nothing and changes nothing.
        Workflows.WorkflowRunStore? workflowRuns = null,
        // The tenant boundary. Every launcher-registry read/write and every relay is scoped to the CALLING
        // tenant, resolved from the authenticated device key (never the machine name in the path or body).
        // Null on self-host, where the single tenant is Local and every authenticated caller resolves to it.
        HostedTenantBoundary? boundary = null)
    {
        if (spawner is null) throw new ArgumentNullException(nameof(spawner));

        FileLog.Write($"[MachineEndpoints] mapping {LauncherPrefix} + {MachinePrefix}; hosted={GatewayHostedMode.IsHosted} - every route authorizes against the CALLING tenant, resolved from the authenticated device key");

        MapLauncherRoutes(outer.MapGroup(LauncherPrefix), launchers, boundary);
        MapMachineRoutes(outer.MapGroup(MachinePrefix), launchers, spawner, sendLauncherCommand, missions, workflowRuns, boundary);
    }

    /// <summary>The calling tenant, resolved from the authenticated device key. Null means no tenant is
    /// bound (hosted with an unbound key) - the caller must refuse with 403.</summary>
    private static TenantId? ReqTenant(HttpContext ctx, HostedTenantBoundary? boundary)
        => GatewayEndpoints.ResolveReadTenant(ctx, boundary);

    private static IResult NoTenant() =>
        Results.Json(new { error = "no tenant is bound to this request" }, statusCode: 403);

    /// <summary>
    /// Session origin (devthrottle_internal issue #982), stamped GATEWAY-AUTHORITATIVELY on the spawn
    /// relay - the same rule <c>PromptRequest.Surface</c> already follows for turns.
    ///
    /// This route is the one spawn path a caller outside the owner's machines can reach, so what a
    /// client CLAIMS about its own origin cannot be the record. When the verified per-device key says
    /// the caller is a signed-in phone or browser, we know two things by construction - a person is
    /// holding it, and which surface it is - and both are OVERWRITTEN here, along with any parent
    /// session the client named (a phone is nobody's child).
    ///
    /// Every OTHER caller is left exactly as it arrived, and that is the important half. A remote spawn
    /// from an agent reaches this route relayed by its own Director over the tunnel, carrying that
    /// Director's key rather than a device key; the Director already stamped the truth on the loopback
    /// floor, and overwriting it here would erase the agent lineage on precisely the cross-machine
    /// spawns - one session driving work on another computer - that make the lineage worth having.
    /// </summary>
    private static void StampOriginFromDeviceKey(NewSessionRequest req, HttpContext ctx)
    {
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        var surface = Core.Sessions.SessionOriginSurfaces.FromDeviceType(deviceType);
        if (surface == Core.Sessions.SessionOriginSurfaces.Unknown)
            return; // not a person's device - keep what the relaying Director stated

        req.Origin = Core.Sessions.SessionOriginKinds.Human;
        req.OriginSurface = surface;
        req.ParentSessionId = null;
        FileLog.Write($"[MachineEndpoints] spawn origin stamped from the verified device key: human/{surface} (deviceType={deviceType})");
    }

    /// <summary>
    /// The four launcher self-registration routes, mapped relative to <see cref="LauncherPrefix"/> so the full
    /// paths are <c>/launchers</c> and <c>/launchers/{machine}/...</c>. All three
    /// <see cref="LauncherRegistry"/> writers - Upsert (register), Heartbeat, Remove (unregister) - live here,
    /// and every one of them takes the CALLING tenant, so there is no way to write a row into a partition the
    /// caller does not own.
    /// </summary>
    private static void MapLauncherRoutes(IEndpointRouteBuilder app, LauncherRegistry launchers, HostedTenantBoundary? boundary)
    {
        // ===== Launcher self-registration surface =====
        // The launcher's own machine name is client-supplied; the OWNING TENANT is not - it is resolved from
        // the launcher's authenticated device key, so a launcher registers only into its own tenant partition.

        // POST /launchers/register - the launcher POSTs this on startup and after reconnects.
        app.MapPost("/register", (LauncherRegistrationRequest req, HttpContext ctx) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.MachineName))
                return Results.BadRequest(new { error = "machineName is required" });
            if (req.Port <= 0)
                return Results.BadRequest(new { error = "port must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "token is required" });
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            FileLog.Write($"[MachineEndpoints] POST /launchers/register: tenant={tenant.Value}, machine={req.MachineName}, port={req.Port}, pid={req.Pid}, version={req.Version}");
            var dto = launchers.Upsert(tenant, req);
            return Results.Json(dto, statusCode: 201);
        });

        // POST /launchers/{machine}/heartbeat - keep-alive from the launcher every 30 s.
        app.MapPost("/{machine}/heartbeat", (string machine, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var ok = launchers.Heartbeat(tenant, machine);
            if (!ok)
            {
                FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: unknown -> 410");
                return Results.StatusCode(410);
            }
            FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: ok");
            return Results.Json(new { ok = true });
        });

        // DELETE /launchers/{machine} - graceful unregister on launcher shutdown.
        app.MapDelete("/{machine}", (string machine, HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            launchers.Remove(tenant, machine);
            FileLog.Write($"[MachineEndpoints] DELETE /launchers/{machine}: removed");
            return Results.Json(new { ok = true });
        });

        // GET /launchers - list the CALLING TENANT's registered launchers (never another tenant's).
        app.MapGet("", (HttpContext ctx) =>
        {
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();
            var list = launchers.ListLaunchers(tenant);
            FileLog.Write($"[MachineEndpoints] GET /launchers: tenant={tenant.Value}, count={list.Count}");
            return Results.Json(list);
        });
    }

    /// <summary>
    /// The five machine relay routes, mapped relative to <see cref="MachinePrefix"/> so the full paths are
    /// <c>/machines/{machine}/...</c>. Every one of them resolves the calling tenant first and refuses with
    /// 403 when none is bound.
    /// </summary>
    private static void MapMachineRoutes(IEndpointRouteBuilder app, LauncherRegistry launchers,
        MachineSessionSpawner spawner,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        Core.Sessions.MissionStore? missions,
        Workflows.WorkflowRunStore? workflowRuns,
        HostedTenantBoundary? boundary)
    {
        // ===== Machine relay surface =====
        // The target machine name is in the path; the caller's TENANT comes from the authenticated key, and
        // the launcher/connection is resolved as (callerTenant, machine) - so a caller can only ever reach a
        // launcher its OWN tenant registered, never another tenant's machine of the same bare name.

        // POST /machines/{machine}/director/restart
        app.MapPost("/{machine}/director/restart", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/restart: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "restart", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/director/start
        app.MapPost("/{machine}/director/start", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/start: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "start", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/sessions - "start a session on another computer". Resolve the machine
        // to a Director (auto-launching one via the launcher if none is running) and create the session
        // there through the SAME resolve-then-create path the cron firing engine uses. Fail loud: an
        // off/unreachable machine or a create failure returns 502 with the error - NEVER a local spawn.
        app.MapPost("/{machine}/sessions", async (string machine, NewSessionRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: repo={req?.RepoPath}, agent={req?.Agent}");
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            StampOriginFromDeviceKey(req, ctx);

            // Gateway Cleanup mission (Wave 4b): a mission-scoped spawn is validated against the Gateway's OWN
            // mission store (the source of truth) and the resolved NAME is stamped onto the create request, so
            // the Director stamps the attachment directly with no local-store lookup. Reject an unknown mission
            // here rather than forwarding it to a Director that no longer owns mission validation.
            if (req.MissionId is Guid spawnMissionId && missions is not null)
            {
                var mission = missions.Get(spawnMissionId);
                if (mission is null)
                {
                    FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: unknown mission {spawnMissionId}");
                    return Results.BadRequest(new { error = $"unknown mission '{spawnMissionId}'. Create it first with POST /missions." });
                }
                req.MissionName = mission.MissionName;
            }

            // Workflows mission (phase 5b): resolve the seat. An EXPLICIT run id must exist; a
            // mission-scoped spawn with no explicit run auto-seats onto the mission's newest run (the
            // one POST /missions opened). The run's workflow id + pinned version ride the create
            // request so the Director stamps the seat with no lookup of its own - and the seated
            // session's conduct is pinned to the run's version, never a moving head.
            Contracts.WorkflowRunDto? seatRun = null;
            if (workflowRuns is not null)
            {
                if (req.WorkflowRunId is Guid explicitRunId)
                {
                    seatRun = workflowRuns.Get(explicitRunId);
                    if (seatRun is null)
                    {
                        FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: unknown workflow run {explicitRunId}");
                        return Results.BadRequest(new { error = $"unknown workflow run '{explicitRunId}'." });
                    }
                }
                else if (req.MissionId is Guid seatMissionId)
                {
                    seatRun = workflowRuns.List(missionId: seatMissionId, limit: 1).FirstOrDefault();
                }

                if (seatRun is not null && !seatRun.WorkflowEnabled)
                {
                    // The owner turned this workflow OFF: no new seats. The spawn proceeds
                    // unseated - the owner's switch, honestly applied and loudly logged.
                    FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: workflow " +
                                  $"'{seatRun.WorkflowId}' is OFF - spawning UNSEATED");
                    seatRun = null;
                }
                if (seatRun is not null)
                {
                    req.WorkflowRunId = seatRun.Id;
                    req.WorkflowId = seatRun.WorkflowId;
                    req.WorkflowVersion = seatRun.WorkflowVersion;
                }
            }

            // ENTER THE CALLER'S TENANT SCOPE FOR THE WHOLE SPAWN, AND HOLD IT ACROSS THE AWAIT. Everything
            // downstream of here resolves the tenant AMBIENTLY rather than by argument: the target resolver
            // lists the machine's Directors within the current tenant, the auto-launch reaches only this
            // tenant's launcher, and the create rides GatewayHost.SendCommandAsync, which resolves the
            // Director's tunnel connection in the current tenant and DROPS the command when there is no scope.
            //
            // That last one is why the scope is not optional and why its absence was invisible. An HTTP
            // request does NOT enter a tenant scope on this Gateway - only the endpoints that need one enter
            // it, individually - so before this line the spawn ran unscoped: on hosted the resolver would
            // throw its deny-by-default, and had it not, the create would have been silently discarded and
            // reported to the caller as an unreachable Director. This is the ONLY route in the family that
            // resolves its tenant ambiently instead of by argument, because it is the only one that hands off
            // to machinery (the resolver and the tunnel) whose tenant seam is the ambient scope.
            using var tenantScope = boundary is null ? null : boundary.EnterScope(tenant);

            var (ok, dto, error, _) = await spawner.SpawnOnMachineAsync(machine, req, ct);
            if (!ok || dto is null)
            {
                FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions FAILED: {error}");
                return Results.Json(new { error = error ?? $"could not start a session on '{machine}'", machine }, statusCode: 502);
            }

            // Record the new session as a run participant - the persisted run-to-session membership
            // (#1771). The session id is the canonical fleet GUID governance joins effort on. Two
            // guards, both from inspection findings:
            //  - Record ONLY when the Director's reply proves the seat actually landed. An older
            //    Director (rolling upgrade) ignores the seat fields and returns a DTO without them;
            //    recording membership for a session whose agent never received its conduct would be
            //    a governance lie.
            //  - The spawn has already SUCCEEDED; a participant-write failure is reported loudly in
            //    the log, never converted into an HTTP failure the caller would retry into a second
            //    session.
            if (seatRun is not null && workflowRuns is not null && !string.IsNullOrWhiteSpace(dto.SessionId))
            {
                if (dto.WorkflowRunId != seatRun.Id)
                {
                    FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: Director did NOT " +
                                  $"stamp the seat (returned run={dto.WorkflowRunId?.ToString() ?? "none"}; it " +
                                  "likely predates seated sessions). Session started UNSEATED; no participant recorded.");
                }
                else
                {
                    try
                    {
                        workflowRuns.Patch(seatRun.Id, new Contracts.PatchWorkflowRunRequest
                        {
                            AddParticipants = new List<Contracts.WorkflowRunParticipantDto>
                            {
                                new()
                                {
                                    SessionId = dto.SessionId,
                                    AgentKind = req.Agent,
                                    Role = req.Role ?? "",
                                    Machine = machine,
                                },
                            },
                        });
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: run-participant " +
                                      $"record FAILED for session {dto.SessionId} on run {seatRun.Id}: {ex.Message}. " +
                                      "The session is seated and running; governance is missing this membership row.");
                    }
                }
            }

            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: started sid={dto.SessionId}" +
                          (seatRun is null ? "" : $", seated on run {seatRun.Id} ({seatRun.WorkflowId} v{seatRun.WorkflowVersion})"));
            return Results.Json(dto, statusCode: 201);
        });

        // POST /machines/{machine}/director/stop
        app.MapPost("/{machine}/director/stop", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/stop: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "stop", ctx, launchers, sendLauncherCommand, boundary, ct);
        });

        // POST /machines/{machine}/launch - relay a generic launch request to the launcher.
        app.MapPost("/{machine}/launch", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/launch: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            // Forward the original request body verbatim to the launcher.
            LaunchRelayBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<LaunchRelayBody>(ct); }
            catch { /* treat as null -> launcher will 400 */ }

            var outcome = await LauncherLifecycleRelay.SendLaunchAsync(
                tenant, machine, body, launchers, sendLauncherCommand, ct);
            return ToResult(machine, "launch", outcome);
        });

        // GET /machines/{machine}/apps - what is installed on that machine, so a caller can find something to
        // start without knowing any of its paths.
        app.MapGet("/{machine}/apps", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] GET /machines/{machine}/apps: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            var query = ctx.Request.Query["q"].ToString();
            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);

            var outcome = await LauncherLifecycleRelay.SendQueryAsync(
                tenant, machine, "apps", query, limit, timeoutMilliseconds: 0, launchers, sendLauncherCommand, ct);
            return ToQueryResult(machine, "apps", outcome);
        });

        // GET /machines/{machine}/files - a filename search across that machine's drives.
        app.MapGet("/{machine}/files", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] GET /machines/{machine}/files: caller={ctx.Connection.RemoteIpAddress}");
            if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

            var query = ctx.Request.Query["q"].ToString();
            if (string.IsNullOrWhiteSpace(query))
                return Results.Json(new { error = "q is required for a file search", machine }, statusCode: 400);

            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            _ = int.TryParse(ctx.Request.Query["timeoutMilliseconds"].ToString(), out var timeout);

            var outcome = await LauncherLifecycleRelay.SendQueryAsync(
                tenant, machine, "files", query, limit, timeout, launchers, sendLauncherCommand, ct);
            return ToQueryResult(machine, "files", outcome);
        });
    }

    /// <summary>
    /// Render a QUERY outcome as the HTTP answer.
    ///
    /// It differs from <see cref="ToResult"/> in one way that matters to every caller: a successful query
    /// returns the launcher's own document AS the response body, rather than wrapping it in a relay envelope
    /// with the answer buried in a string field. A caller asking a machine what it has installed should get
    /// the catalogue, not a description of a relay hop that happens to contain one.
    ///
    /// Every failure shape is left identical to the action verbs, including the deliberate merging of
    /// "no such machine" with "that machine belongs to another tenant".
    /// </summary>
    private static IResult ToQueryResult(string machine, string verb, LauncherLifecycleRelay.LauncherRelayOutcome outcome)
    {
        if (outcome.Kind != LauncherLifecycleRelay.RelayOutcomeKind.Relayed)
            return ToResult(machine, verb, outcome);

        // The launcher's body is already JavaScript Object Notation - its own answer on success, its own error
        // document on failure - so it is passed through untouched at the status the launcher chose. An empty
        // body would mean the launcher answered with nothing at all, which is a relay fault rather than an
        // answer, and is reported as one instead of being served as an empty document.
        if (string.IsNullOrWhiteSpace(outcome.Payload))
        {
            FileLog.Write($"[MachineEndpoints] {verb} on {machine}: relayed {outcome.RelayStatus} with an EMPTY body");
            return Results.Json(new { error = $"the launcher on '{machine}' returned no answer for '{verb}'", machine },
                statusCode: 502);
        }

        return Results.Content(outcome.Payload, "application/json; charset=utf-8", statusCode: outcome.RelayStatus);
    }

    /// <summary>
    /// Render a relay outcome as the HTTP answer. One mapping for both relay routes and both dispatch arms,
    /// so a stream-served call is indistinguishable to the caller from a REST-relayed one.
    ///
    /// The not-registered case is 404 and says so in the caller's terms. It is reached both when nobody has
    /// registered that machine and when ANOTHER TENANT has registered a machine of that name - those are the
    /// same answer on purpose, because a distinguishable "exists, but not yours" would let one subscriber
    /// enumerate another's machines one name at a time.
    /// </summary>
    private static IResult ToResult(string machine, string verb, LauncherLifecycleRelay.LauncherRelayOutcome outcome)
        => outcome.Kind switch
        {
            LauncherLifecycleRelay.RelayOutcomeKind.Relayed => Results.Json(new RelayResult
            {
                Machine = machine,
                Verb = verb,
                RelayStatus = outcome.RelayStatus,
                Payload = outcome.Payload ?? "",
            }, statusCode: outcome.RelayStatus),

            LauncherLifecycleRelay.RelayOutcomeKind.NoLauncher => Results.Json(
                new { error = $"no launcher registered for machine '{machine}'", machine }, statusCode: 404),

            LauncherLifecycleRelay.RelayOutcomeKind.NoToken => Results.Json(
                new { error = "launcher token not available" }, statusCode: 500),

            _ => Results.Json(
                new { error = $"launcher unreachable on {outcome.DialHost}:{outcome.Port}", detail = outcome.Detail },
                statusCode: 502),
        };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Relay a director lifecycle verb (restart/start/stop) to the target machine's launcher.
    /// Enforces the slot guard for restart/stop verbs.
    /// </summary>
    private static async Task<IResult> RelayDirectorLifecycleAsync(
        string machine, string verb, HttpContext ctx, LauncherRegistry launchers,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand, HostedTenantBoundary? boundary, CancellationToken ct)
    {
        if (ReqTenant(ctx, boundary) is not { } tenant) return NoTenant();

        // Parse optional body for confirmProtected flag and target exe path.
        // Use JsonDocument to avoid internal-class reflection issues with System.Text.Json.
        // Do NOT gate on ContentLength - transfer-encoded bodies may have no explicit length.
        string? exePathFromBody = null;
        bool? confirmProtectedFromBody = null;
        try
        {
            ctx.Request.EnableBuffering();
            if (ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                || ctx.Request.ContentLength is > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("exePath", out var ep))
                    exePathFromBody = ep.GetString();
                if (doc.RootElement.TryGetProperty("confirmProtected", out var cp) && cp.ValueKind == JsonValueKind.True)
                    confirmProtectedFromBody = true;
            }
        }
        catch { /* body is optional */ }

        // Slot guard: refuse restart/stop targeting the main build or slots 1-4 without confirm.
        if ((verb == "restart" || verb == "stop") && exePathFromBody is { } exePath)
        {
            var (isProtected, slotDesc) = IsProtectedSlot(exePath);
            if (isProtected && confirmProtectedFromBody != true)
            {
                var reason = $"slot guard: refusing {verb} of protected Director ({slotDesc}) without confirmProtected=true";
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb={verb} reason={reason}");
                return Results.Json(new
                {
                    error = "slot_guard",
                    detail = reason,
                    machine,
                    verb,
                    exePath,
                    hint = "Set confirmProtected=true to override (human-confirmed action only).",
                }, statusCode: 403);
            }
        }

        // Both dispatch arms - the persistent launcher stream, then the REST relay it falls back to - live in
        // LauncherLifecycleRelay and BOTH resolve on (tenant, machine). The slot guard above has already run,
        // so a protected-slot action is gated identically whichever arm carries it.
        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(
            tenant, machine, verb, exePathFromBody, confirmProtectedFromBody == true,
            launchers, sendLauncherCommand, ct);
        return ToResult(machine, verb, outcome);
    }

    /// <summary>
    /// Returns (isProtected=true, description) when the exe path refers to the main build
    /// or a protected slot (1-4). Agent slots (5+) are NOT protected.
    /// </summary>
    private static (bool isProtected, string description) IsProtectedSlot(string exePath)
    {
        var m = SlotFromPath.Match(Path.GetFileName(exePath));
        if (!m.Success)
        {
            // Path does not match cc-director*.exe pattern - not a slot we can classify.
            return (false, "unknown");
        }

        var slotStr = m.Groups[1].Value;
        if (string.IsNullOrEmpty(slotStr))
        {
            // cc-director.exe - the main production build.
            return (true, "main build (cc-director.exe)");
        }

        if (int.TryParse(slotStr, out var slot) && slot >= 1 && slot <= 4)
        {
            return (true, $"protected slot cc-director{slot}.exe");
        }

        // Slot 5+ - not protected.
        return (false, $"agent slot {slotStr}");
    }
}

/// <summary>Request body forwarded verbatim to the launcher POST /launch endpoint.</summary>
internal sealed class LaunchRelayBody
{
    public string? Path { get; init; }

    /// <summary>
    /// An application display name from that machine's catalogue, used instead of <see cref="Path"/>. This is
    /// the form a remote caller can actually use: it has no way to know where a program lives on a machine it
    /// cannot see, and the launcher resolves the name locally where the catalogue is.
    /// </summary>
    public string? App { get; init; }

    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }
}

/// <summary>Response body returned by the Gateway for relay calls.</summary>
internal sealed class RelayResult
{
    public required string Machine { get; init; }
    public required string Verb { get; init; }
    public required int RelayStatus { get; init; }
    public required string Payload { get; init; }
}
