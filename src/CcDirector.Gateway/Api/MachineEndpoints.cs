using System.Net.Http.Json;
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
/// DENIED IN WHOLE ON HOSTED (issue #1917). EVERY route in this file is refused on the hosted Gateway - both
/// prefixes, every verb, every existing and future sub-path alike.
///
/// THE DEFECT THIS CLOSES. This family is TENANT-BLIND BY CONSTRUCTION: there is no tenant dimension
/// anywhere in the path. <see cref="LauncherRegistry"/> keys on machine NAME alone,
/// <see cref="Streaming.LauncherConnectionRegistry"/> keys on machine NAME alone, and
/// <see cref="Streaming.LauncherHub"/>.Hello binds a connection to a machine name with NO tenant resolution
/// at all - in direct contrast to <see cref="Streaming.DirectorHub"/>.Hello, which aborts when the device key
/// resolves to no tenant. So on hosted, ANY authenticated device key could enumerate every tenant's machines
/// through GET /launchers and then drive the machine routes AGAINST ANOTHER TENANT'S MACHINE: cross-machine
/// CODE EXECUTION via POST /machines/{machine}/launch, and OUTBOUND-REQUEST FORGERY via
/// POST /launchers/register, which overwrites a machine's stored token, port and network address and so
/// re-points the REST relay at an arbitrary host.
///
/// WHY THE USUAL PROTECTION DOES NOT APPLY. Elsewhere on the hosted Gateway, bare-identifier Director routes
/// are inert cross-tenant because the command rides SendCommandAsync, which refuses to resolve a tunnel
/// connection with no tenant in scope. THAT PROTECTION LIVES IN THE TRANSPORT, NOT IN THE ROUTE. This family
/// has three dispatch arms and only the Director-tunnel arm is gated: the launcher STREAM arm resolves purely
/// on machine name, and the launcher REST RELAY - the FALLBACK taken when the stream arm returns null - dials
/// the launcher's stored address with its stored bearer token. The FAILURE path is the ungated one by design.
///
/// IT IS A DENY, NOT A PARTITION. On shared hosted infrastructure A TENANT DOES NOT OWN A MACHINE. There is
/// no correct per-tenant answer to serve here - only a leak to close. A partition would require inventing an
/// ownership relation that was never recorded, which is a half-partition: worse than an honest refusal
/// because it looks like isolation. Nothing is substituted and no empty list is served: an empty
/// GET /launchers would be a FALSE statement (a fleet with no machines) where a refusal is merely absent.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This family is denied
/// through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family on
/// this Gateway adopts (reference adoption: #1904 for /vault/keys; primitive at
/// <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>). An earlier revision of this file rolled its own
/// <c>AddEndpointFilter</c> deny before that primitive existed; it has been replaced so the release ships ONE
/// refusal boundary, not one per family. What the primitive buys here over the old ad-hoc filter:
///
///   * On hosted the family's handlers are NEVER MAPPED. In their place each prefix maps ONE verb-less
///     catch-all refusal over everything beneath it plus a root refusal at the prefix itself. There is no
///     binding step to get ahead of, no body parameter, no media-type constraint and no method constraint, so
///     EVERY request shape - a valid body, a malformed body, a wrong media type, a verb the group never
///     mapped, and a route added LATER - meets the refusal. The old request-time filter answered only the
///     shapes that reached it.
///   * The exclusivity claim is CHECKED at startup, not trusted:
///     <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/> refuses to start the Gateway if any live
///     route serves beneath <c>/launchers</c> or <c>/machines</c>, or if two refusals tie.
///   * The refusal payload is validated on CONSTRUCTION, so a blank message fails the Gateway at startup
///     rather than serving an empty refusal that reads like a working route.
///
/// TWO EXCLUSIVE PREFIXES, BECAUSE THIS FAMILY OWNS TWO. The launcher self-registration surface owns
/// <c>/launchers</c> outright and the machine relay surface owns <c>/machines</c> outright; nothing else on
/// the Gateway serves beneath either (checked at startup). Each is its own exclusive group, so each gets the
/// verb-less catch-all and the future-route coverage independently. A single empty-prefix group would NOT be
/// an exclusive claim - it would claim the whole Gateway - which is why the bespoke filter used an empty
/// prefix plus a request-time check and the primitive uses two real prefixes plus a startup-checked claim.
///
/// WHAT THIS DENY STOPS, NARROWLY: the HTTP ROUTE reads, writes and relays under both prefixes. It does NOT
/// stop every write to launcher state. Every mutating call to <see cref="LauncherRegistry"/> in the
/// codebase - Upsert (register), Heartbeat, Remove (unregister) - is an ON-ROUTE handler in THIS file, so all
/// three close with the routes. But <see cref="Streaming.LauncherHub"/>.Hello still writes a
/// machine-name-keyed connection row into <see cref="Streaming.LauncherConnectionRegistry"/> over the
/// /launcher-stream SignalR hub, which is NOT in this route group (it is the HUB half, host-gated on a
/// separate change). So tenant-blind state can still ACCUMULATE behind this deny.
///
/// SELF-HOST IS COMPLETELY UNCHANGED AND IS THE CONTROL. Off hosted the primitive maps the nine real handlers
/// on the two groups exactly as an unguarded builder would and creates no refusal at all. Cross-machine
/// launch is legitimate fleet function there: one operator, his own machines.
///
/// UN-DENY. The permanent fix is a tenant-scoped launcher design that binds machine identity to a tenant at
/// REGISTRATION and authorizes every launch, lifecycle and relay call against the calling tenant - including
/// on the REST fallback arm. That unit is NOT enough on its own: because the /launcher-stream hub keeps
/// writing behind this deny, the un-deny is the tenant-key unit PLUS A PURGE of the launcher and
/// launcher-connection registries. Deny-closed on the safe side: assume the purge is required until
/// write-coverage is proven complete.
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

    /// <summary>
    /// The exact refusal every route in this family serves on hosted. One string, shared by BOTH denied
    /// groups and asserted verbatim by the tests, so the refusal is identifiable as ITSELF - a bare 404 would
    /// be indistinguishable from a route that does not exist.
    /// </summary>
    internal const string HostedRefusal =
        "machine and launcher control is not available on the hosted gateway";

    /// <summary>The launcher self-registration prefix this family owns outright on hosted.</summary>
    internal const string LauncherPrefix = "/launchers";

    /// <summary>The machine relay prefix this family owns outright on hosted.</summary>
    internal const string MachinePrefix = "/machines";

    /// <summary>
    /// The hosted refusal payload for the launcher self-registration surface (issue #1917). Validated on
    /// construction, so a blank field fails the Gateway at startup rather than serving a refusal a caller
    /// cannot act on. 404 rather than 403: on hosted a tenant-owned machine does not exist as a concept, so
    /// "not here" is the truthful answer; 403 would imply some credential could reach it, and none can. The
    /// hosted decision is read inside the primitive from <see cref="GatewayHostedMode.IsHosted"/> directly -
    /// the INDEPENDENT signal - never from an argument a caller could forget and so fail OPEN.
    /// </summary>
    private static HostedDenial LauncherDenial() => new(
        family: "launcher-registration",
        message: HostedRefusal,
        reason: "the launcher registry keys on machine name alone with no tenant in the file, the store or the " +
                "routes, and the host-wide auth gate admits any enrolled device key from any account - so one " +
                "subscriber could enumerate, overwrite or delete every other subscriber's registered machines",
        unDenyInstruction: "do NOT simply remove this deny: bind machine identity to a tenant at registration and " +
                "authorize every call against the calling tenant, THEN purge the launcher and launcher-connection " +
                "registries - the /launcher-stream hub keeps writing machine-name-keyed rows behind this deny",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The hosted refusal payload for the machine relay surface - the SAME message and status as the launcher
    /// group, so a caller sees one consistent refusal across the family, with a family name and reason tuned to
    /// the relay's cross-machine-code-execution risk.
    /// </summary>
    private static HostedDenial MachineDenial() => new(
        family: "machine-control",
        message: HostedRefusal,
        reason: "the machine relay resolves purely on machine name with no tenant scope; its REST fallback arm " +
                "dials a machine's stored address with its stored token, so one subscriber could run code on and " +
                "relay lifecycle verbs to every other subscriber's machine",
        unDenyInstruction: "do NOT simply remove this deny: bind machine identity to a tenant at registration and " +
                "authorize every launch, lifecycle and relay call against the calling tenant - including the REST " +
                "fallback arm - THEN purge the launcher and launcher-connection registries",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the launcher and machine groups and RETURNS both denied handles, so the refusal can be proved to
    /// cover routes that do not exist yet: a test maps a NEW probe route onto a returned group and finds it
    /// already refused on hosted, without anyone having written a deny for it. Returning the groups is the only
    /// way to state that future-route property from outside this file - and it is the ONLY property that
    /// distinguishes an exclusive-prefix deny from a guard repeated per route, because the two behave
    /// identically on every route that exists today.
    ///
    /// Each surface is its OWN exclusive group. <see cref="LauncherPrefix"/> and <see cref="MachinePrefix"/>
    /// are owned outright - nothing else on the Gateway serves beneath either, which
    /// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/> checks at startup - so each maps ONE verb-less
    /// catch-all refusal on hosted (covering every verb, every sub-path, and every route added later) and its
    /// real handlers off hosted. The routes are mapped through the returned GROUP HANDLES, never through
    /// <paramref name="outer"/>: a route mapped around the refusal is not expressible without changing the
    /// signatures of <see cref="MapLauncherRoutes"/> / <see cref="MapMachineRoutes"/>, which take only the
    /// handle - the bypass count is reduced by design, not by care.
    /// </summary>
    public static (HostedDenyGroup Launchers, HostedDenyGroup Machines) Map(IEndpointRouteBuilder outer, LauncherRegistry launchers,
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
        // The tenant boundary. Every launcher-registry read/write is now scoped to the CALLING tenant,
        // resolved from the authenticated device key (never the machine name in the path/body). Null on
        // self-host, where the single tenant is Local. The launcher/machine families stay denied on hosted
        // for now; this makes the registries tenant-correct by construction so a future un-deny is safe.
        HostedTenantBoundary? boundary = null)
    {
        if (spawner is null) throw new ArgumentNullException(nameof(spawner));

        FileLog.Write($"[MachineEndpoints] mapping {LauncherPrefix} + {MachinePrefix}; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in both groups is refused via the shared refusal primitive (issue #1917)");

        var launcherGroup = HostedRouteDeny.ExclusiveGroup(outer, LauncherPrefix, LauncherDenial());
        MapLauncherRoutes(launcherGroup, launchers, boundary);

        var machineGroup = HostedRouteDeny.ExclusiveGroup(outer, MachinePrefix, MachineDenial());
        MapMachineRoutes(machineGroup, launchers, spawner, sendLauncherCommand, missions, workflowRuns, boundary);

        return (launcherGroup, machineGroup);
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
    /// paths are <c>/launchers</c> and <c>/launchers/{machine}/...</c> exactly as before. Takes the denied
    /// GROUP HANDLE and nothing else: the ungrouped route builder is deliberately out of scope so no route can
    /// be mapped around the hosted refusal. All three <see cref="LauncherRegistry"/> writers - Upsert
    /// (register), Heartbeat, Remove (unregister) - live here, ON-ROUTE, so every one of them closes with the
    /// group; there is no off-route launcher-registry writer to host-gate separately.
    /// </summary>
    private static void MapLauncherRoutes(HostedDenyGroup app, LauncherRegistry launchers, HostedTenantBoundary? boundary)
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
    /// <c>/machines/{machine}/...</c> exactly as before. Takes the denied GROUP HANDLE and the relay
    /// dependencies, and nothing else that could map a route around the hosted refusal.
    /// </summary>
    private static void MapMachineRoutes(HostedDenyGroup app, LauncherRegistry launchers,
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

            // launcher-persistent-join: prefer the persistent stream. When stream mode is on and the launcher
            // is joined, push the launch DOWN the open connection instead of dialing its REST API. A null
            // result means no stream (flag off, or launcher offline), so fall through to the REST relay below.
            var launchStreamCmd = new LauncherCommand
            {
                Verb = "launch",
                Path = body?.Path,
                Args = body?.Args,
                Cwd = body?.Cwd,
                Headless = body?.Headless ?? false,
            };
            var launchStreamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, tenant, machine, launchStreamCmd, ct);
            if (launchStreamResult is not null)
                return MapLauncherStreamResult(machine, "launch", launchStreamResult);

            var (launcher, token, networkAddress, err) = ResolveLauncher(tenant, machine, launchers);
            if (err is not null)
            {
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch: {err.Value.log}");
                return err.Value.result;
            }

            using var http = BuildLauncherClient(launcher!.Port, token!, networkAddress!);
            IResult result;
            try
            {
                var response = await http.PostAsJsonAsync("/launch", body, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch relay: status={response.StatusCode}");
                result = Results.Json(new RelayResult
                {
                    Machine = machine,
                    Verb = "launch",
                    RelayStatus = (int)response.StatusCode,
                    Payload = payload,
                }, statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch relay FAILED: {ex.Message}");
                result = Results.Json(new { error = $"launcher unreachable on {machine}:{launcher!.Port}", detail = ex.Message }, statusCode: 502);
            }
            return result;
        });
    }

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

        // launcher-persistent-join: prefer the persistent stream. When stream mode is on and the launcher is
        // joined, push the lifecycle verb DOWN the open connection instead of dialing its REST API. The slot
        // guard above has already run, so a protected-slot action is still gated identically. A null result
        // means no stream (flag off, or launcher offline), so fall through to the REST relay below unchanged.
        var streamCmd = new LauncherCommand
        {
            Verb = $"director/{verb}",
            Path = exePathFromBody,
            ConfirmProtected = confirmProtectedFromBody == true,
        };
        var streamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, tenant, machine, streamCmd, ct);
        if (streamResult is not null)
            return MapLauncherStreamResult(machine, verb, streamResult);

        var (launcher, token, networkAddress, err) = ResolveLauncher(tenant, machine, launchers);
        if (err is not null)
        {
            FileLog.Write($"[MachineEndpoints] /machines/{machine}/director/{verb}: {err.Value.log}");
            return err.Value.result;
        }

        var dialHost = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;
        using var http = BuildLauncherClient(launcher!.Port, token!, networkAddress!);
        try
        {
            var response = await http.PostAsync($"/director/{verb}", null, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            FileLog.Write($"[MachineEndpoints] relay /director/{verb} machine={machine} host={dialHost} -> status={response.StatusCode}");
            return Results.Json(new RelayResult
            {
                Machine = machine,
                Verb = verb,
                RelayStatus = (int)response.StatusCode,
                Payload = payload,
            }, statusCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MachineEndpoints] relay /director/{verb} machine={machine} host={dialHost} FAILED: {ex.Message}");
            return Results.Json(new
            {
                error = $"launcher unreachable on {dialHost}:{launcher!.Port}",
                detail = ex.Message,
            }, statusCode: 502);
        }
    }

    /// <summary>
    /// Resolve the launcher entry for a machine. Returns (dto, token, networkAddress, null) on
    /// success or (null, null, null, (log, result)) on failure.
    /// </summary>
    private static (LauncherDto? launcher, string? token, string? networkAddress, (string log, IResult result)? err)
        ResolveLauncher(TenantId tenant, string machine, LauncherRegistry launchers)
    {
        var launcher = launchers.Get(tenant, machine);
        if (launcher is null)
        {
            return (null, null, null, ($"launcher not registered for tenant={tenant.Value}, machine={machine}",
                Results.Json(new { error = $"no launcher registered for machine '{machine}'", machine }, statusCode: 404)));
        }

        var token = launchers.GetToken(tenant, machine);
        if (string.IsNullOrEmpty(token))
        {
            return (null, null, null, ($"launcher token missing for tenant={tenant.Value}, machine={machine}",
                Results.Json(new { error = "launcher token not available" }, statusCode: 500)));
        }

        var networkAddress = launchers.GetNetworkAddress(tenant, machine) ?? "";
        return (launcher, token, networkAddress, null);
    }

    /// <summary>
    /// Build a short-lived HttpClient pointed at the launcher's REST API.
    ///
    /// When <paramref name="networkAddress"/> is non-empty the launcher is on a REMOTE
    /// machine: dial http://&lt;networkAddress&gt;:&lt;port&gt;/ over the tailnet.
    /// When <paramref name="networkAddress"/> is empty the launcher is co-located with the
    /// Gateway: dial http://127.0.0.1:&lt;port&gt;/ on loopback.
    /// </summary>
    private static HttpClient BuildLauncherClient(int port, string token, string networkAddress)
    {
        var host = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return http;
    }

    /// <summary>
    /// launcher-persistent-join: render a <see cref="LauncherCommandResult"/> from the stream path as the
    /// same <see cref="RelayResult"/> envelope the REST relay returns, so a stream-served call is
    /// indistinguishable to the caller. Ok -> 200; BadRequest -> 400; Error -> 502 (launcher-side failure,
    /// mirroring the REST relay's "launcher unreachable" 502).
    /// </summary>
    private static IResult MapLauncherStreamResult(string machine, string verb, LauncherCommandResult result)
    {
        var status = result.Status switch
        {
            LauncherCommandStatus.Ok => 200,
            LauncherCommandStatus.BadRequest => 400,
            _ => 502,
        };
        var payload = result.IsOk
            ? JsonSerializer.Serialize(new { ok = true, via = "stream" })
            : JsonSerializer.Serialize(new { error = result.Error, via = "stream" });
        FileLog.Write($"[MachineEndpoints] relay /director/{verb} machine={machine} via=stream -> status={status}");
        return Results.Json(new RelayResult
        {
            Machine = machine,
            Verb = verb,
            RelayStatus = status,
            Payload = payload,
        }, statusCode: status);
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
