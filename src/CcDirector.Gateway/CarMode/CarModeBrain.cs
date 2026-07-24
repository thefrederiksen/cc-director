using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The Car Mode fleet-manager brain (Car Mode mission, New build A): a hand-rolled C# tool-calling loop
/// against the hosted model (decision 10 - not an agent framework, not the OpenAI Agents SDK). It sends
/// the owner's transcript plus the tool catalog, executes any tool the model calls in-process against the
/// fleet, feeds the results back, and repeats until the model returns a final spoken message. Conversation
/// context is kept server-side per device so multi-turn references ("show me the latest one") resolve.
///
/// Phase 2 wires the READ tools only (list + activity). Phase 3 adds the act tools (start / message /
/// approve) and the confirmed-destructive tools (delete), extending the tool catalog, the system prompt,
/// and the dispatch below - the loop itself does not change.
/// </summary>
public sealed class CarModeBrain
{
    /// <summary>Hard cap on model round trips per turn so a model that keeps calling tools without
    ///  answering fails loud (no silent infinite loop). Each round may carry several tool calls.</summary>
    private const int MaxRounds = 6;

    private static readonly JsonSerializerOptions ToolResultJson = new() { WriteIndented = false };

    private readonly ICarModeChat _chat;
    private readonly Func<string, ICarModeFleet> _fleetForCaller;
    private readonly CarModeConversationStore _conversations;
    private readonly CarModePendingStore _pending;
    private readonly CarModeSubjectStore _subjects;
    private readonly Action<string> _log;
    private readonly string _systemPrompt;

    /// <param name="fleetForCaller">Resolves the fleet view for one authenticated caller credential
    ///  (issue #2129, hosted tenant isolation): every fleet tool call must run AS the calling device, so
    ///  the Gateway's own endpoints resolve the caller's tenant exactly as they do for any client. The
    ///  factory is invoked once per turn with the same authenticated credential that keys the conversation;
    ///  tests pass a constant fake fleet.</param>
    /// <param name="surface">Which surface this brain instance speaks to. Car (the default) keeps the
    ///  hands-free one-or-two-sentence style; Desk (the cockpit Assistant screen) appends the desk-surface
    ///  overrides. Everything else - loop, tools, stores, model - is identical.</param>
    public CarModeBrain(ICarModeChat chat, Func<string, ICarModeFleet> fleetForCaller, CarModeConversationStore conversations, CarModePendingStore pending, CarModeSubjectStore subjects, Action<string>? log = null, CarModeSurface surface = CarModeSurface.Car)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _fleetForCaller = fleetForCaller ?? throw new ArgumentNullException(nameof(fleetForCaller));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _log = log ?? FileLog.Write;
        _systemPrompt = surface == CarModeSurface.Desk ? SystemPrompt + DeskAddendum : SystemPrompt;
    }

    /// <summary>
    /// Run one turn: send the owner's command plus prior context to the model, drive the tool-calling
    /// loop, and return the final spoken reply and any actions taken. Throws
    /// <see cref="CarModeUnavailableException"/> on a money refusal (the endpoint maps it to a 402) and a
    /// specific exception on any other model/fleet failure, so the browser speaks a loud, specific failure
    /// - never a silent stall or a guess.
    /// </summary>
    public async Task<CarModeTurnResponse> RunTurnAsync(TenantId tenant, string deviceKey, string userText, CancellationToken ct)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("Car Mode requires an explicit tenant.", nameof(tenant));
        // Time the WHOLE turn from here, and collect per-model-call and per-fleet-read timings inside the
        // core loop, so the endpoint returns a real per-stage breakdown to the browser (performance round).
        var timer = new TurnTimer();
        var response = await RunTurnCoreAsync(tenant, deviceKey, userText, timer, ct);
        timer.Total.Stop();
        var timing = timer.ToTiming();
        _log($"[CarModeBrain] turn timing: total={timing.TotalMs:F0}ms, models={timing.ModelCallCount} ({timing.ModelMsTotal:F0}ms), "
            + $"fleetReads={timing.FleetReadCount} ({timing.FleetReadMsTotal:F0}ms), rounds={timing.Rounds}");
        return response with { Timing = timing };
    }

    /// <summary>Collects the per-stage server timing for one turn: the whole-turn stopwatch, each hosted-model
    ///  round trip, and each fleet/roster read. Mutated in-place by the core loop, then frozen to a
    ///  <see cref="CarModeTurnTiming"/>.</summary>
    private sealed class TurnTimer
    {
        public readonly Stopwatch Total = Stopwatch.StartNew();
        public readonly List<double> ModelMs = new();
        public int FleetReadCount;
        public double FleetReadMs;
        public int Rounds;

        /// <summary>Time one hosted-model round trip.</summary>
        public async Task<T> TimeModelAsync<T>(Func<Task<T>> call)
        {
            var sw = Stopwatch.StartNew();
            try { return await call(); }
            finally { sw.Stop(); ModelMs.Add(sw.Elapsed.TotalMilliseconds); }
        }

        /// <summary>Time one fleet/roster read (or directors/repos read).</summary>
        public async Task<T> TimeFleetAsync<T>(Func<Task<T>> call)
        {
            var sw = Stopwatch.StartNew();
            try { return await call(); }
            finally { sw.Stop(); FleetReadCount++; FleetReadMs += sw.Elapsed.TotalMilliseconds; }
        }

        /// <summary>Time one fleet act (start/message/approve/delete) as a fleet read for accounting.</summary>
        public async Task TimeFleetAsync(Func<Task> call)
        {
            var sw = Stopwatch.StartNew();
            try { await call(); }
            finally { sw.Stop(); FleetReadCount++; FleetReadMs += sw.Elapsed.TotalMilliseconds; }
        }

        public CarModeTurnTiming ToTiming() => new()
        {
            TotalMs = Total.Elapsed.TotalMilliseconds,
            ModelCallCount = ModelMs.Count,
            ModelMsTotal = ModelMs.Sum(),
            ModelMs = ModelMs.ToArray(),
            FleetReadCount = FleetReadCount,
            FleetReadMsTotal = FleetReadMs,
            Rounds = Rounds,
        };
    }

    private async Task<CarModeTurnResponse> RunTurnCoreAsync(TenantId tenant, string deviceKey, string userText, TurnTimer timer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("The command text is required.", nameof(userText));

        _log($"[CarModeBrain] turn: len={userText.Length}");

        // The fleet view for THIS caller: every tool call this turn runs as the calling device, so on the
        // hosted Gateway the loopback requests resolve to the caller's own tenant (issue #2129).
        var fleet = _fleetForCaller(deviceKey);

        // The confirmation gate (decision 3): if a destructive action is armed for this device, THIS turn
        // is the spoken confirmation. A clear affirmative executes it; a cancel drops it; anything else
        // disarms it (safe default - the delete never runs) and is processed as a fresh command. This is
        // deterministic C#, not left to the model, so a destructive action can never run without a clear
        // spoken "confirm".
        var armed = _pending.Get(deviceKey);
        if (armed is not null)
        {
            _pending.Clear(deviceKey);
            if (CarModeConfirm.IsAffirmative(userText))
            {
                var (spokenDone, action) = await ExecuteConfirmedAsync(fleet, armed, timer, ct);
                _conversations.Append(deviceKey, userText, spokenDone);
                _log($"[CarModeBrain] confirmed {armed.Tool} for {armed.TargetName}");
                return new CarModeTurnResponse { Spoken = spokenDone, Actions = new[] { action } };
            }
            if (CarModeConfirm.IsNegative(userText))
            {
                var spokenCancel = $"Okay, I left {armed.TargetName} alone.";
                _conversations.Append(deviceKey, userText, spokenCancel);
                _log($"[CarModeBrain] cancelled {armed.Tool} for {armed.TargetName}");
                return new CarModeTurnResponse { Spoken = spokenCancel };
            }
            // Neither confirm nor cancel: the armed delete is dropped (safe) and this utterance is treated
            // as a new command below.
            _log($"[CarModeBrain] pending {armed.Tool} disarmed by an unrelated command");
        }

        var messages = new List<object>();
        messages.Add(new { role = "system", content = _systemPrompt });
        foreach (var m in _conversations.GetHistory(deviceKey))
            messages.Add(new { role = m.Role, content = m.Content });
        messages.Add(new { role = "user", content = userText });

        var actions = new List<CarModeActionRecord>();
        var armedThisTurn = false;

        for (var round = 0; round < MaxRounds; round++)
        {
            timer.Rounds = round + 1;
            var messagesJson = JsonSerializer.Serialize(messages);
            var turn = await timer.TimeModelAsync(() => _chat.CompleteAsync(tenant, messagesJson, ToolCatalogJson, ct));

            // Structural guard against the Car Mode hallucination gotcha (a known, recurring failure of the
            // fast model): under tool_choice=required the model MUST speak by calling speak_answer and act by
            // calling an action tool. A bare-content reply is a protocol violation - and, critically, it is how
            // the model tries to CLAIM an action ("I've snoozed it") WITHOUT calling the tool, which would make
            // Car Mode narrate something it never did (the banned fire-and-forget defect). So bare content is
            // NEVER returned as the answer: push it back and require a real tool call. The prompt hard rule is
            // the belt; this is the suspenders. The round cap still bounds it, so a model that refuses to comply
            // ends in a loud failure below - never a false success.
            if (turn.ToolCalls.Count == 0)
            {
                _log($"[CarModeBrain] bare content with no tool call (round {round + 1}); re-prompting for a real tool call");
                messages.Add(new { role = "assistant", content = turn.Content ?? "" });
                messages.Add(new
                {
                    role = "user",
                    content = "You answered in plain text without calling a tool. That does not count and I did not "
                        + "hear it. If you performed or intend an action (snooze, switch to voice mode, message, "
                        + "approve, start, delete), you MUST call that action's tool now - saying it is not doing it. "
                        + "If you are only speaking to me, call speak_answer with the exact words. Do not reply in "
                        + "plain text again.",
                });
                continue;
            }

            // The model wants tools this round: echo its assistant message (with the tool_calls) back,
            // then run each tool and append its result, so the next round sees the outcomes.
            messages.Add(new
            {
                role = "assistant",
                content = turn.Content,
                tool_calls = turn.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.ArgumentsJson },
                }).ToList(),
            });

            string? finalSpoken = null;
            foreach (var call in turn.ToolCalls)
            {
                if (string.Equals(call.Name, "speak_answer", StringComparison.Ordinal))
                {
                    // This is how the model says its final words under tool_choice=required.
                    var words = (ReadStringArg(call.ArgumentsJson, "text") ?? "").Trim();
                    if (words.Length > 0)
                    {
                        finalSpoken = words;
                        messages.Add(new { role = "tool", tool_call_id = call.Id, content = "{\"status\":\"spoken\"}" });
                    }
                    else
                    {
                        // A model occasionally calls speak_answer with no words (a glitch under
                        // tool_choice=required). Do NOT fail the whole turn - hand it back a nudge so it
                        // speaks proper words on the next round; the round cap still bounds it.
                        _log("[CarModeBrain] speak_answer called with no words; asking the model to retry");
                        messages.Add(new { role = "tool", tool_call_id = call.Id, content = "{\"error\":\"speak_answer needs the exact words to say. Call it again with the words.\"}" });
                    }
                    continue;
                }
                if (string.Equals(call.Name, "get_help", StringComparison.Ordinal))
                {
                    // Help Mode (issue #1441): the model classified this turn as a "help" / "what can you do"
                    // request and called get_help. The spoken answer is the ONE curated script from
                    // CarModeHelp - returned VERBATIM, never the model's own words - so the spoken help is
                    // identical to what the Help button plays (GET /carmode/help), reliably complete, and it
                    // teaches the command-vs-relay addressing model. The model's job was only to classify the
                    // intent; the content is server-owned. This is terminal, like speak_answer.
                    finalSpoken = CarModeHelp.Script;
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = "{\"status\":\"spoken\"}" });
                    continue;
                }
                var outcome = await ExecuteToolAsync(fleet, deviceKey, call, timer, ct);
                if (outcome.Action is not null) actions.Add(outcome.Action);
                if (outcome.ArmedConfirmation) armedThisTurn = true;
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = outcome.ResultJson });
            }

            if (finalSpoken is not null)
            {
                _conversations.Append(deviceKey, userText, finalSpoken);
                _log($"[CarModeBrain] turn done in {round + 1} round(s) via speak_answer: actions={actions.Count}, pending={armedThisTurn}");
                return new CarModeTurnResponse { Spoken = finalSpoken, Actions = actions, PendingConfirmation = armedThisTurn };
            }
        }

        // The model never settled on an answer within the round cap: a loud, specific failure, not a guess.
        _log("[CarModeBrain] round cap reached without a final answer");
        var giveUp = "I'm having trouble answering that right now. Please try again.";
        _conversations.Append(deviceKey, userText, giveUp);
        return new CarModeTurnResponse { Spoken = giveUp, Actions = actions, PendingConfirmation = armedThisTurn };
    }

    /// <summary>The outcome of one tool call: the JSON handed back to the model, an optional action record
    ///  (for the ordinary acts; reads produce none), and whether it ARMED a destructive action awaiting a
    ///  spoken confirmation (which was NOT executed).</summary>
    private sealed record ToolOutcome(string ResultJson, CarModeActionRecord? Action, bool ArmedConfirmation);

    private static ToolOutcome Result(object payload) => new(JsonSerializer.Serialize(payload, ToolResultJson), null, false);
    private static ToolOutcome Acted(object payload, CarModeActionRecord action) => new(JsonSerializer.Serialize(payload, ToolResultJson), action, false);

    /// <summary>Run one tool the model called. An unknown tool or bad arguments is returned to the model as
    ///  a tool error (the model can recover next round); a genuine fleet failure throws (a loud, specific,
    ///  spoken failure). Ordinary acts (start / message / approve) run immediately; the destructive act
    ///  (delete) is NOT run - it arms a confirmation the owner must speak next turn (decision 3).</summary>
    private async Task<ToolOutcome> ExecuteToolAsync(ICarModeFleet fleet, string deviceKey, CarModeToolCall call, TurnTimer timer, CancellationToken ct)
    {
        // Log the raw arguments too: model tool arguments are the boundary where a mis-resolve (the wrong
        // session) originates, so the exact reference the model produced must be visible when diagnosing.
        _log($"[CarModeBrain] tool: {call.Name} args={call.ArgumentsJson}");
        try
        {
            return await ExecuteToolCoreAsync(fleet, deviceKey, call, timer, ct);
        }
        catch (CarModeToolUnavailableException ex)
        {
            // A tool that is KNOWINGLY unavailable on this deployment (issue #2129) is a fact for the model
            // to relay in plain words, not a turn-killing failure - the owner hears the truth, and the rest
            // of the conversation keeps working.
            _log($"[CarModeBrain] tool {call.Name} unavailable: {ex.Message}");
            return Result(new { error = ex.Message });
        }
    }

    private async Task<ToolOutcome> ExecuteToolCoreAsync(ICarModeFleet fleet, string deviceKey, CarModeToolCall call, TurnTimer timer, CancellationToken ct)
    {
        switch (call.Name)
        {
            case "list_sessions":
            {
                var sessions = await timer.TimeFleetAsync(() => fleet.ListSessionsAsync(ct));
                return Result(new
                {
                    needsYouCount = sessions.Count(s => s.NeedsYou),
                    total = sessions.Count,
                    sessions = sessions.Select(s => new
                    {
                        s.Name, s.Number, s.Repo, s.MachineName, mission = s.MissionName,
                        s.State, s.NeedsYou, s.WaitingMinutes, s.Summary, s.AgeHours, s.IdleMinutes,
                    }),
                });
            }
            case "get_session_activity":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                if (string.IsNullOrWhiteSpace(reference))
                    return Result(new { error = "Provide a session name, repo, or number." });
                var activity = await timer.TimeFleetAsync(() => fleet.GetSessionActivityAsync(reference, ct));
                if (activity is null)
                    return Result(new { error = $"No session matched \"{reference}\"." });
                // Reading a session makes it the current subject, so a follow-up "read the wingman" / "answer
                // it" / "snooze it" resolves without the owner naming it again.
                _subjects.Set(deviceKey, new CarModeSubject(activity.SessionId, activity.Name, activity.Repo));
                return Result(new { activity.Name, activity.Repo, activity.State, activity.NeedsYou, activity.Summary });
            }
            case "focus_next_needs_me":
            {
                // "The next one that needs me": focus the OLDEST-waiting needs-you session (longest wait first)
                // as the current subject, so follow-ups ("read the wingman", "answer it", "snooze it") resolve.
                var sessions = await timer.TimeFleetAsync(() => fleet.ListSessionsAsync(ct));
                var next = sessions.Where(s => s.NeedsYou).OrderByDescending(s => s.WaitingMinutes).FirstOrDefault();
                if (next is null)
                    return Result(new { status = "none", message = "No session is waiting on the owner right now." });
                _subjects.Set(deviceKey, new CarModeSubject(next.SessionId, next.Name, next.Repo));
                return Result(new { session = next.Name, repo = next.Repo, next.WaitingMinutes, next.Summary });
            }
            case "read_wingman":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "read the wingman for", timer, ct);
                if (target is null) return Result(new { error });
                var explain = await timer.TimeFleetAsync(() => fleet.ExplainSessionAsync(target.SessionId, ct));
                // The REAL, current narration - the brain reads this aloud, never a canned "it is waiting".
                return Result(new { session = target.Name, repo = target.Repo, narration = explain.Spoken, explain.NothingYet });
            }
            case "start_session":
            {
                var repo = ReadStringArg(call.ArgumentsJson, "repo");
                if (string.IsNullOrWhiteSpace(repo))
                    return Result(new { error = "Provide the repository name to start a session in." });
                var summary = await timer.TimeFleetAsync(() => fleet.StartSessionAsync(repo, ct));
                return Acted(new { status = "started", summary }, new CarModeActionRecord("start_session", summary));
            }
            case "message_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var message = ReadStringArg(call.ArgumentsJson, "message");
                if (string.IsNullOrWhiteSpace(message))
                    return Result(new { error = "Provide the message to send into the session." });
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "send that to", timer, ct);
                if (target is null) return Result(new { error });
                await timer.TimeFleetAsync(() => fleet.MessageSessionAsync(target.SessionId, message, ct));
                var summary = $"Messaged {target.Name}.";
                return Acted(new { status = "sent", session = target.Name }, new CarModeActionRecord("message_session", summary));
            }
            case "approve_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "approve", timer, ct);
                if (target is null) return Result(new { error });
                await timer.TimeFleetAsync(() => fleet.ApproveSessionAsync(target.SessionId, ct));
                var summary = $"Approved {target.Name}.";
                return Acted(new { status = "approved", session = target.Name }, new CarModeActionRecord("approve_session", summary));
            }
            case "switch_to_voice_mode":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "switch to voice mode", timer, ct);
                if (target is null) return Result(new { error });
                await timer.TimeFleetAsync(() => fleet.SwitchVoiceModeAsync(target.SessionId, true, ct));
                var summary = $"Switched {target.Name} to voice mode.";
                return Acted(new { status = "voice_on", session = target.Name }, new CarModeActionRecord("switch_to_voice_mode", summary));
            }
            case "snooze_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "snooze", timer, ct);
                if (target is null) return Result(new { error });
                await timer.TimeFleetAsync(() => fleet.SnoozeSessionAsync(target.SessionId, ct));
                var summary = $"Snoozed {target.Name}.";
                return Acted(new { status = "snoozed", session = target.Name }, new CarModeActionRecord("snooze_session", summary));
            }
            case "delete_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var (target, error) = await ResolveActTargetAsync(fleet, deviceKey, reference, "delete", timer, ct);
                if (target is null) return Result(new { error });
                // Destructive: do NOT delete. Arm a confirmation the owner must speak next turn. The
                // confirmation ALWAYS names the resolved session and repo out loud, so even a stale current
                // subject is heard before it acts - the omit-session convenience never makes delete less safe.
                _pending.Arm(deviceKey, new CarModePendingAction("delete", target.SessionId, target.Name));
                return new ToolOutcome(
                    JsonSerializer.Serialize(new
                    {
                        status = "confirmation_required",
                        session = target.Name,
                        repo = target.Repo,
                        message = $"Deleting {target.Name} in the {target.Repo} repo is permanent. Tell the owner exactly which session and repo you are about to delete, and ask him to say \"confirm\" to proceed, or \"cancel\" to stop. Do not call any more tools.",
                    }, ToolResultJson),
                    Action: null,
                    ArmedConfirmation: true);
            }
            case "get_credits":
            {
                var credits = await timer.TimeFleetAsync(() => fleet.GetCreditsAsync(ct));
                if (!credits.SignedIn)
                    return Result(new { signedIn = false, message = "The Gateway is not signed in to a DevThrottle account, so there is no balance to read." });
                // The fleet guarantees a signed-in read carries a balance (it throws on a malformed
                // payload); this throw is the same guarantee restated here so a fake fleet in a test -
                // or a future second implementation - can never turn "no number" into zero dollars.
                var balanceMicros = credits.BalanceMicros
                    ?? throw new InvalidOperationException("get_credits: signed in but no balanceMicros - the fleet must fail loud on a malformed credits payload, never hand the brain a null balance.");
                return Result(new
                {
                    signedIn = true,
                    balanceDollars = Math.Round(balanceMicros / 1_000_000.0, 2),
                    lastActionCostDollars = credits.LastDebitMicros is { } debit ? Math.Round(debit / 1_000_000.0, 4) : (double?)null,
                });
            }
            case "list_machines":
            {
                var machines = await timer.TimeFleetAsync(() => fleet.ListMachinesAsync(ct));
                return Result(new
                {
                    count = machines.Count,
                    machines = machines.Select(m => new
                    {
                        machine = m.MachineName, m.Version, m.LastSeenMinutesAgo, m.SessionCount,
                    }),
                });
            }
            case "list_schedules":
            {
                var schedules = await timer.TimeFleetAsync(() => fleet.ListSchedulesAsync(ct));
                return Result(new
                {
                    count = schedules.Count,
                    schedules = schedules.Select(s => new
                    {
                        s.Name, s.Enabled, s.Schedule, s.Machine, action = s.ActionSummary,
                        s.NextRunUtc, s.LastFiredUtc, s.LastStatus,
                    }),
                });
            }
            case "get_spend":
            {
                // Codex review finding 6: seven days is the default for an OMITTED argument only. An
                // explicitly supplied but invalid days value goes back to the model as a tool error it can
                // correct - silently substituting 7 would answer a question the owner did not ask.
                var daysText = ReadStringArg(call.ArgumentsJson, "days");
                int days;
                if (string.IsNullOrWhiteSpace(daysText))
                    days = 7;
                else if (!int.TryParse(daysText, out days) || days < 1 || days > 90)
                    return Result(new { error = "days must be a whole number from 1 to 90, or omitted for the default 7." });
                var spend = await timer.TimeFleetAsync(() => fleet.GetSpendAsync(days, ct));
                return Result(new
                {
                    days,
                    totalDollars = Math.Round(spend.TotalMicros / 1_000_000.0, 2),
                    hostedActionCount = spend.DebitCount,
                });
            }
            default:
                return Result(new { error = $"Unknown tool \"{call.Name}\"." });
        }
    }

    /// <summary>
    /// Resolve the session an act tool will operate on, honoring the current-subject convenience (design B).
    /// When the model supplies a <paramref name="reference"/>, resolve it live and, on success, make it the
    /// current subject so later "it" references work. When the model OMITS the reference (the owner said
    /// "answer it" / "snooze it" / "read the wingman"), fall back to the device's current subject. Returns the
    /// target and a null error on success, or a null target and a specific, speakable error the model can
    /// relay - a different message for "which one do you mean" (nothing named, no subject) versus "no session
    /// matched X" (named something that does not exist). The subject NEVER makes delete less safe: delete's
    /// confirmation still names the resolved session and repo out loud.
    /// </summary>
    private async Task<(CarModeSubject? Target, string? Error)> ResolveActTargetAsync(
        ICarModeFleet fleet, string deviceKey, string? reference, string actionPhrase, TurnTimer timer, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var resolved = await timer.TimeFleetAsync(() => fleet.ResolveSessionAsync(reference, ct));
            if (resolved is null)
                return (null, $"No session matched \"{reference}\".");
            var subject = new CarModeSubject(resolved.SessionId, resolved.Name, resolved.Repo);
            _subjects.Set(deviceKey, subject);
            return (subject, null);
        }
        var current = _subjects.Get(deviceKey);
        if (current is null)
            return (null, $"Which session should I {actionPhrase}? Name it, or first say which one you mean.");
        return (current, null);
    }

    /// <summary>Execute a destructive action the owner has just confirmed out loud, returning the spoken
    ///  acknowledgement and the action record. A fleet failure throws (a loud, specific, spoken failure).</summary>
    private async Task<(string Spoken, CarModeActionRecord Action)> ExecuteConfirmedAsync(ICarModeFleet fleet, CarModePendingAction pending, TurnTimer timer, CancellationToken ct)
    {
        switch (pending.Tool)
        {
            case "delete":
                await timer.TimeFleetAsync(() => fleet.DeleteSessionAsync(pending.SessionId, ct));
                return ($"Done. I deleted {pending.TargetName}.", new CarModeActionRecord("delete_session", $"Deleted {pending.TargetName}."));
            default:
                throw new InvalidOperationException($"Unknown pending action \"{pending.Tool}\".");
        }
    }

    /// <summary>
    /// Read one string argument from a tool call's JSON arguments. Model output is a BOUNDARY, so this is
    /// defensively tolerant of the malformed shapes a model occasionally emits under tool_choice=required:
    /// an empty object, an argument that is a number/bool rather than a string, or the whole arguments body
    /// wrapped in a one-element ARRAY (observed intermittently). It never throws - anything it cannot read
    /// as the named string returns null, so the tool returns a "provide X" result the model can recover from
    /// on the next round rather than crashing the turn.
    /// </summary>
    internal static string? ReadStringArg(string argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            // Some models wrap the arguments object in a one-element array; unwrap to the first object.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) { root = item; break; }
                }
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty(name, out var el)) return null;
            // Accept a string; coerce a number/bool to its text so a stray non-string value is still usable.
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }
        catch (Exception)
        {
            // Any parse/type surprise from the model boundary degrades to null (no-crash), never propagates.
            return null;
        }
    }

    // The system prompt: a competent, concise development manager the owner talks to hands-free. Spoken
    // output, human names never numbers, real facts from tools, ask when unsure (mission decisions 5 + 3).
    private const string SystemPrompt =
        "You are the voice of DevThrottle Car Mode. The owner is driving and talks to you hands-free to run "
        + "his fleet of coding-agent sessions. Behave like a competent, calm development manager on a phone "
        + "call.\n\n"
        + "Rules:\n"
        + "- Answer OUT LOUD in one or two short spoken sentences. No lists, no markdown, no headings, no "
        + "emoji. It will be read aloud, so write it the way you would say it.\n"
        + "- Always refer to a session by its human NAME and its repository, never by its number "
        + "(for example: \"Local Files Manager, in the devthrottle repo\"). Only use a number if the owner "
        + "used one.\n"
        + "- When a question is ABOUT THE FLEET - how many sessions there are, which need you, what a session "
        + "is doing, the latest one, or an action on a session - use the tools to get REAL facts first. Never "
        + "guess a count, a name, or a state.\n"
        + "- Account and infrastructure questions have their own read tools: get_credits for the credit "
        + "balance, get_spend for recent hosted AI spending, list_machines for which machines are online, and "
        + "list_schedules for the scheduled jobs. Use them the same way - never guess a balance, a dollar "
        + "figure, or a schedule. Sessions in list_sessions carry ageHours (how long open) and idleMinutes "
        + "(how long since output), so \"open too long\" and \"is anything stuck\" are answered from those "
        + "real numbers.\n"
        + "- When the owner asks for HELP or how this works - \"help\", \"what can you do\", \"what can you "
        + "help me with\", \"how does this work\", \"how do I talk to you\" - call get_help and nothing else. "
        + "It speaks a fixed guided explanation; do NOT write your own and do NOT read the fleet for it.\n"
        + "- For other GENERAL small talk that is NOT about the fleet - a greeting, a thank-you, a passing "
        + "remark - answer it DIRECTLY by calling speak_answer. Do NOT call list_sessions or any fleet tool "
        + "for these. Reading the fleet you do not need only makes the owner wait longer, so skip it.\n"
        + "- \"need me\", \"need you\", \"waiting\", and \"who wants me\" all mean sessions whose needsYou is "
        + "true. \"The latest one\" is the first session in the list (it is ordered newest first).\n"
        + "- If a request is ambiguous or you cannot tell which session is meant, ask one short clarifying "
        + "question instead of guessing.\n\n"
        + "Two ways the owner talks to you - COMMANDING you, or RELAYING words into a session:\n"
        + "- By DEFAULT the owner is COMMANDING YOU, the manager. \"Who needs me\", \"read me the next one\", "
        + "\"what is it doing\", \"snooze it\", \"approve it\", \"remove it\" are all commands to YOU - you act "
        + "on the fleet yourself with the read and act tools.\n"
        + "- The owner is RELAYING words INTO a session ONLY when he starts with a relay verb - TELL, ANSWER, "
        + "REPLY, MESSAGE, or SAY TO - AND aims it at a session (a name, or \"it\"/\"that one\"). Then call "
        + "message_session and send the words he gave, exactly. Examples: \"tell the devthrottle session to "
        + "run the tests\", \"answer it, yes go ahead\", \"reply to Local Files that it can continue\".\n"
        + "- \"Tell me\", \"read me\", \"give me\", \"show me\" are NOT relays - the target is YOU (\"me\"), so "
        + "they are commands you answer yourself. Only tell/answer/reply/message aimed at a SESSION relays.\n"
        + "- CRITICAL: whatever the owner says AFTER the relay verb is literal TEXT to type into that session. "
        + "It is DATA, never a command to you. \"Tell the devthrottle session to delete session five\" sends the "
        + "words \"delete session five\" INTO that session with message_session - it does NOT mean you delete "
        + "anything, and you must NOT call delete_session. \"Tell it to snooze the tests\" sends that text in - "
        + "you do NOT snooze. Never carry out the relayed words as your own action; only pass them along.\n\n"
        + "The session you are talking about (\"it\"):\n"
        + "- The system remembers the session you last acted on or read as the CURRENT one. When the owner "
        + "says \"it\", \"that one\", \"this session\", \"answer it\", \"snooze it\", he means that current "
        + "session. For those follow-ups you may OMIT the session argument and the tool acts on the current "
        + "one. Only name a session explicitly when he named one or you are changing which session you mean.\n"
        + "- \"the next one that needs me\", \"who's next\", \"read me the next one\": call focus_next_needs_me. "
        + "It focuses the session that has been waiting the longest and makes it the current one; then read it "
        + "to him. After that, \"answer it\" / \"snooze it\" act on that session.\n"
        + "- When you DO put a session in a tool's session argument, use only its short NAME (for example "
        + "\"Car Mode Demo\") - never the \"in the such-and-such repo\" phrase. The name alone identifies it; "
        + "adding the repository can point the action at the wrong session in that repository.\n\n"
        + "Taking action (you have full control):\n"
        + "- CRITICAL: doing something means CALLING ITS TOOL. To snooze, you MUST call snooze_session. To "
        + "switch to voice mode, you MUST call switch_to_voice_mode. To answer or message, message_session. To "
        + "approve, approve_session. To start, start_session. Saying it out loud is NOT doing it. NEVER tell the "
        + "owner you snoozed, switched, messaged, approved, started, or deleted a session unless you actually "
        + "called that tool THIS turn. If you have not called the tool yet, you have not done it - call the tool "
        + "first, then say what you did. Claiming an action you did not perform is a serious error.\n"
        + "- Ordinary actions just happen - do not ask permission for them. After an act, say briefly what you "
        + "did.\n"
        + "- To START a session, call start_session with the repository name.\n"
        + "- To ANSWER a session or send it an instruction - \"answer it and say yes\", \"tell it to run the "
        + "tests\", \"reply that ...\" - call message_session with the exact words to send. If he is answering "
        + "the current session, omit the session argument.\n"
        + "- To READ what a session needs - \"what does it need\", \"read me that one\", \"what is it waiting "
        + "on\" - call read_wingman. It returns the session's REAL current narration; read that narration back "
        + "to him. Never invent a status like \"it is waiting for you\" - say what the narration actually says.\n"
        + "- To APPROVE a waiting session (accept its highlighted default), call approve_session.\n"
        + "- To SWITCH a session into voice mode - \"put it in voice mode\", \"switch it to voice\" - call "
        + "switch_to_voice_mode.\n"
        + "- To SNOOZE a session - \"snooze it\", \"hold it\", \"silence it for a bit\", \"let it wait\" - call "
        + "snooze_session. It comes back on its own after the snooze time.\n"
        + "- Deleting, killing, or removing a session is DESTRUCTIVE and always needs the owner's spoken "
        + "confirmation. When he asks to delete, kill, or remove a session, call delete_session; you will get a "
        + "confirmation_required result. Then tell him exactly which session and repo will be deleted and ask "
        + "him to say \"confirm\" to proceed or \"cancel\" to stop, and call no more tools. The system handles "
        + "the actual deletion once he confirms out loud - you never delete without that confirmation.\n"
        + "\n\nHow you speak:\n"
        + "- To SAY anything to the owner - an answer, a clarifying question, or an acknowledgement of what you "
        + "did - you MUST call the speak_answer tool with the exact words to say out loud. Do not put your reply "
        + "in the message content; only speak_answer is heard.\n"
        + "- A normal turn is: call the tools you need to get facts or act, then call speak_answer once with the "
        + "final spoken sentence. Keep it to one or two short spoken sentences.";

    // The desk-surface overlay (the cockpit Assistant screen): the SAME brain, tools, and rules, with only
    // the speech-style constraints relaxed - the owner is at his computer, the reply is shown as text and
    // may also be read aloud, so a fleet overview may run a few sentences. Appended AFTER the car prompt so
    // every behavioural rule above still binds; only the style rules are overridden, explicitly.
    private const string DeskAddendum =
        "\n\nDESK SURFACE OVERRIDES - this conversation comes from the cockpit Assistant screen, not the car:\n"
        + "- The owner is at his computer in the cockpit, typing or talking. Your reply is shown on screen as "
        + "text and may also be read aloud.\n"
        + "- The one-or-two-sentence limit is relaxed: use up to four or five plain sentences when the "
        + "question genuinely needs them - a fleet overview, a recommendation with its reasons. Short is "
        + "still better whenever short answers it.\n"
        + "- Keep it plain spoken text all the same: no markdown, no bullet lists, no headings, no emoji, "
        + "because the same words may be read aloud.\n"
        + "- Include concrete numbers (counts, hours, dollars) when you have them from the tools.\n"
        + "- Everything else is unchanged: every answer still goes through speak_answer, actions still mean "
        + "calling the tool, and destructive actions still need the owner's confirmation.";

    // The tool catalog. Standard chat-completions function tools: reads, ordinary acts, and the
    // destructive delete (which the loop holds for a spoken confirmation - the model just requests it).
    private const string ToolCatalogJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "list_sessions",
              "description": "List the whole fleet of sessions with their human name, repository, state, whether each needs the owner, how long it has been waiting, and a short summary of what it is doing. Use this to answer how many need the owner, to list sessions, or to find the latest one.",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_session_activity",
              "description": "Get what one specific session is doing right now (its short one-line status). Resolve a fuzzy reference (a name, a repository, or a number) to one session. This makes that session the current one.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to read: a human name, a repository name, or a number." }
                },
                "required": ["session"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "focus_next_needs_me",
              "description": "Focus the session that has been waiting on the owner the LONGEST (the next one that needs him) and make it the current one. Use for \"the next one that needs me\", \"who's next\", \"read me the next one\". Returns its name, repository, how long it has waited, and a short summary; if nothing is waiting it returns none.",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_wingman",
              "description": "Read a session's REAL current narration - what it actually needs or last said - so you can say it back out loud. Use for \"what does it need\", \"read me that one\", \"what is it waiting on\". Omit session to read the current session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to read: a name, a repository, or a number. Omit to read the current session." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "start_session",
              "description": "Start a brand-new agent session in a repository the owner names. Give the repository's short name (its folder leaf), for example \"devthrottle\".",
              "parameters": {
                "type": "object",
                "properties": {
                  "repo": { "type": "string", "description": "The repository to start the session in, by its short name." }
                },
                "required": ["repo"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "message_session",
              "description": "Send a message (a prompt) into a running session, the same as typing to it. Use this to answer a session, or to tell it to do something (for example run the tests). Omit session to send it to the current session (\"answer it\", \"reply ...\").",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to send to: a name, a repository, or a number. Omit to send to the current session." },
                  "message": { "type": "string", "description": "The exact words to send into the session." }
                },
                "required": ["message"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "approve_session",
              "description": "Approve a session that is waiting for the owner by accepting its highlighted default (presses Enter). Use when the owner says to approve, allow, or continue a waiting session. Omit session to approve the current session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to approve: a name, a repository, or a number. Omit to approve the current session." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "switch_to_voice_mode",
              "description": "Switch a session INTO voice mode (so the assistant reads its turns aloud). Use for \"put it in voice mode\", \"switch it to voice\". Omit session to switch the current session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to switch: a name, a repository, or a number. Omit for the current session." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "snooze_session",
              "description": "Snooze a session so it stops asking for now and comes back on its own after the snooze time. Use for \"snooze it\", \"hold it\", \"silence it for a bit\", \"let it wait\". Omit session to snooze the current session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to snooze: a name, a repository, or a number. Omit for the current session." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "delete_session",
              "description": "Request to delete (kill and remove) a session. This is DESTRUCTIVE: it does not delete immediately - it returns confirmation_required, and the owner must confirm out loud before it happens. Omit session to target the current session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to delete: a name, a repository, or a number. Omit to target the current session." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_help",
              "description": "Explain to the owner what Car Mode can do and how to talk to it. Call this - and NOTHING else - when the owner asks for help or how this works: \"help\", \"what can you do\", \"what can you help me with\", \"how does this work\", \"how do I talk to you\". It speaks a fixed guided explanation of the two ways to talk to Car Mode; you do not write the words and you do not read the fleet for it.",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_credits",
              "description": "Read the account's current credit balance in dollars, and what the last hosted action cost. Use for questions about credits, balance, subscription money, or \"are we running low\". Never guess a balance.",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_spend",
              "description": "Read the total hosted AI spend in dollars over the trailing window, and how many hosted actions it covers. Use for \"what have we spent\", \"how fast are we burning credits\", or pace questions. Defaults to the last 7 days.",
              "parameters": {
                "type": "object",
                "properties": {
                  "days": { "type": "string", "description": "The trailing window in days, 1 to 90. Omit for 7." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "list_machines",
              "description": "List the machines running Directors right now: each machine's name, how many minutes since the Gateway last heard from it, and how many sessions it is running. Use for \"which machines are online\" or \"where is everything running\".",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "list_schedules",
              "description": "List the scheduled jobs: each job's name, whether it is enabled, when it runs, on which machine, what it does, the next due time, and how its last run went. Use for questions about schedules, cron jobs, nightly runs, or \"what runs automatically\".",
              "parameters": { "type": "object", "properties": {}, "required": [] }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "speak_answer",
              "description": "Say words out loud to the owner. Call this for EVERY answer, clarifying question, or acknowledgement - it is the only thing the owner hears. Call it once, last, with the final spoken sentence.",
              "parameters": {
                "type": "object",
                "properties": {
                  "text": { "type": "string", "description": "The exact words to say out loud, in one or two short spoken sentences." }
                },
                "required": ["text"]
              }
            }
          }
        ]
        """;
}
