using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
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

    public static void Map(IEndpointRouteBuilder app, LauncherRegistry launchers,
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
        Workflows.WorkflowRunStore? workflowRuns = null)
    {
        if (spawner is null) throw new ArgumentNullException(nameof(spawner));

        // ===== Launcher self-registration surface =====

        // POST /launchers/register - the launcher POSTs this on startup and after reconnects.
        app.MapPost("/launchers/register", (LauncherRegistrationRequest req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.MachineName))
                return Results.BadRequest(new { error = "machineName is required" });
            if (req.Port <= 0)
                return Results.BadRequest(new { error = "port must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "token is required" });

            FileLog.Write($"[MachineEndpoints] POST /launchers/register: machine={req.MachineName}, port={req.Port}, pid={req.Pid}, version={req.Version}");
            var dto = launchers.Upsert(req);
            return Results.Json(dto, statusCode: 201);
        });

        // POST /launchers/{machine}/heartbeat - keep-alive from the launcher every 30 s.
        app.MapPost("/launchers/{machine}/heartbeat", (string machine) =>
        {
            var ok = launchers.Heartbeat(machine);
            if (!ok)
            {
                FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: unknown -> 410");
                return Results.StatusCode(410);
            }
            FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: ok");
            return Results.Json(new { ok = true });
        });

        // DELETE /launchers/{machine} - graceful unregister on launcher shutdown.
        app.MapDelete("/launchers/{machine}", (string machine) =>
        {
            launchers.Remove(machine);
            FileLog.Write($"[MachineEndpoints] DELETE /launchers/{machine}: removed");
            return Results.Json(new { ok = true });
        });

        // GET /launchers - list all registered launchers (machine name, port, last-seen).
        app.MapGet("/launchers", () =>
        {
            var list = launchers.ListLaunchers();
            FileLog.Write($"[MachineEndpoints] GET /launchers: count={list.Count}");
            return Results.Json(list);
        });

        // ===== Machine relay surface =====

        // POST /machines/{machine}/director/restart
        app.MapPost("/machines/{machine}/director/restart", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/restart: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "restart", ctx, launchers, sendLauncherCommand, ct);
        });

        // POST /machines/{machine}/director/start
        app.MapPost("/machines/{machine}/director/start", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/start: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "start", ctx, launchers, sendLauncherCommand, ct);
        });

        // POST /machines/{machine}/sessions - "start a session on another computer". Resolve the machine
        // to a Director (auto-launching one via the launcher if none is running) and create the session
        // there through the SAME resolve-then-create path the cron firing engine uses. Fail loud: an
        // off/unreachable machine or a create failure returns 502 with the error - NEVER a local spawn.
        app.MapPost("/machines/{machine}/sessions", async (string machine, NewSessionRequest req, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/sessions: repo={req?.RepoPath}, agent={req?.Agent}");
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

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
        app.MapPost("/machines/{machine}/director/stop", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/stop: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "stop", ctx, launchers, sendLauncherCommand, ct);
        });

        // POST /machines/{machine}/launch - relay a generic launch request to the launcher.
        app.MapPost("/machines/{machine}/launch", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/launch: caller={ctx.Connection.RemoteIpAddress}");

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
            var launchStreamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, machine, launchStreamCmd, ct);
            if (launchStreamResult is not null)
                return MapLauncherStreamResult(machine, "launch", launchStreamResult);

            var (launcher, token, networkAddress, err) = ResolveLauncher(machine, launchers);
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
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand, CancellationToken ct)
    {
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
        var streamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, machine, streamCmd, ct);
        if (streamResult is not null)
            return MapLauncherStreamResult(machine, verb, streamResult);

        var (launcher, token, networkAddress, err) = ResolveLauncher(machine, launchers);
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
        ResolveLauncher(string machine, LauncherRegistry launchers)
    {
        var launcher = launchers.Get(machine);
        if (launcher is null)
        {
            return (null, null, null, ($"launcher not registered for machine={machine}",
                Results.Json(new { error = $"no launcher registered for machine '{machine}'", machine }, statusCode: 404)));
        }

        var token = launchers.GetToken(machine);
        if (string.IsNullOrEmpty(token))
        {
            return (null, null, null, ($"launcher token missing for machine={machine}",
                Results.Json(new { error = "launcher token not available" }, statusCode: 500)));
        }

        var networkAddress = launchers.GetNetworkAddress(machine) ?? "";
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
