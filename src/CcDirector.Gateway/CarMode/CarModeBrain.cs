using System.Text.Json;
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
    private readonly ICarModeFleet _fleet;
    private readonly CarModeConversationStore _conversations;
    private readonly CarModePendingStore _pending;
    private readonly Action<string> _log;

    public CarModeBrain(ICarModeChat chat, ICarModeFleet fleet, CarModeConversationStore conversations, CarModePendingStore pending, Action<string>? log = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _log = log ?? FileLog.Write;
    }

    /// <summary>
    /// Run one turn: send the owner's command plus prior context to the model, drive the tool-calling
    /// loop, and return the final spoken reply and any actions taken. Throws
    /// <see cref="CarModeUnavailableException"/> on a money refusal (the endpoint maps it to a 402) and a
    /// specific exception on any other model/fleet failure, so the browser speaks a loud, specific failure
    /// - never a silent stall or a guess.
    /// </summary>
    public async Task<CarModeTurnResponse> RunTurnAsync(string deviceKey, string userText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("The command text is required.", nameof(userText));

        _log($"[CarModeBrain] turn: len={userText.Length}");

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
                var (spokenDone, action) = await ExecuteConfirmedAsync(armed, ct);
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
        messages.Add(new { role = "system", content = SystemPrompt });
        foreach (var m in _conversations.GetHistory(deviceKey))
            messages.Add(new { role = m.Role, content = m.Content });
        messages.Add(new { role = "user", content = userText });

        var actions = new List<CarModeActionRecord>();
        var armedThisTurn = false;

        for (var round = 0; round < MaxRounds; round++)
        {
            var messagesJson = JsonSerializer.Serialize(messages);
            var turn = await _chat.CompleteAsync(messagesJson, ToolCatalogJson, ct);

            if (turn.ToolCalls.Count == 0)
            {
                var spoken = (turn.Content ?? "").Trim();
                if (spoken.Length == 0)
                    throw new InvalidOperationException("The model returned an empty spoken reply.");
                _conversations.Append(deviceKey, userText, spoken);
                _log($"[CarModeBrain] turn done in {round + 1} round(s): actions={actions.Count}, pending={armedThisTurn}");
                return new CarModeTurnResponse { Spoken = spoken, Actions = actions, PendingConfirmation = armedThisTurn };
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

            foreach (var call in turn.ToolCalls)
            {
                var outcome = await ExecuteToolAsync(deviceKey, call, ct);
                if (outcome.Action is not null) actions.Add(outcome.Action);
                if (outcome.ArmedConfirmation) armedThisTurn = true;
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = outcome.ResultJson });
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
    private async Task<ToolOutcome> ExecuteToolAsync(string deviceKey, CarModeToolCall call, CancellationToken ct)
    {
        _log($"[CarModeBrain] tool: {call.Name}");
        switch (call.Name)
        {
            case "list_sessions":
            {
                var sessions = await _fleet.ListSessionsAsync(ct);
                return Result(new
                {
                    needsYouCount = sessions.Count(s => s.NeedsYou),
                    total = sessions.Count,
                    sessions = sessions.Select(s => new
                    {
                        s.Name, s.Number, s.Repo, s.MachineName, mission = s.MissionName,
                        s.State, s.NeedsYou, s.WaitingMinutes, s.Summary,
                    }),
                });
            }
            case "get_session_activity":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                if (string.IsNullOrWhiteSpace(reference))
                    return Result(new { error = "Provide a session name, repo, or number." });
                var activity = await _fleet.GetSessionActivityAsync(reference, ct);
                if (activity is null)
                    return Result(new { error = $"No session matched \"{reference}\"." });
                return Result(new { activity.Name, activity.Repo, activity.State, activity.NeedsYou, activity.Summary });
            }
            case "start_session":
            {
                var repo = ReadStringArg(call.ArgumentsJson, "repo");
                if (string.IsNullOrWhiteSpace(repo))
                    return Result(new { error = "Provide the repository name to start a session in." });
                var summary = await _fleet.StartSessionAsync(repo, ct);
                return Acted(new { status = "started", summary }, new CarModeActionRecord("start_session", summary));
            }
            case "message_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                var message = ReadStringArg(call.ArgumentsJson, "message");
                if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(message))
                    return Result(new { error = "Provide both the session and the message to send." });
                var target = await _fleet.ResolveSessionAsync(reference, ct);
                if (target is null)
                    return Result(new { error = $"No session matched \"{reference}\"." });
                await _fleet.MessageSessionAsync(target.SessionId, message, ct);
                var summary = $"Messaged {target.Name}.";
                return Acted(new { status = "sent", session = target.Name }, new CarModeActionRecord("message_session", summary));
            }
            case "approve_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                if (string.IsNullOrWhiteSpace(reference))
                    return Result(new { error = "Provide the session to approve." });
                var target = await _fleet.ResolveSessionAsync(reference, ct);
                if (target is null)
                    return Result(new { error = $"No session matched \"{reference}\"." });
                await _fleet.ApproveSessionAsync(target.SessionId, ct);
                var summary = $"Approved {target.Name}.";
                return Acted(new { status = "approved", session = target.Name }, new CarModeActionRecord("approve_session", summary));
            }
            case "delete_session":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                if (string.IsNullOrWhiteSpace(reference))
                    return Result(new { error = "Provide the session to delete." });
                var target = await _fleet.ResolveSessionAsync(reference, ct);
                if (target is null)
                    return Result(new { error = $"No session matched \"{reference}\"." });
                // Destructive: do NOT delete. Arm a confirmation the owner must speak next turn.
                _pending.Arm(deviceKey, new CarModePendingAction("delete", target.SessionId, target.Name));
                return new ToolOutcome(
                    JsonSerializer.Serialize(new
                    {
                        status = "confirmation_required",
                        session = target.Name,
                        repo = target.Repo,
                        message = $"Deleting {target.Name} in the {target.Repo} repo is permanent. Ask the owner to say \"confirm\" to proceed, or \"cancel\" to stop. Do not call any more tools.",
                    }, ToolResultJson),
                    Action: null,
                    ArmedConfirmation: true);
            }
            default:
                return Result(new { error = $"Unknown tool \"{call.Name}\"." });
        }
    }

    /// <summary>Execute a destructive action the owner has just confirmed out loud, returning the spoken
    ///  acknowledgement and the action record. A fleet failure throws (a loud, specific, spoken failure).</summary>
    private async Task<(string Spoken, CarModeActionRecord Action)> ExecuteConfirmedAsync(CarModePendingAction pending, CancellationToken ct)
    {
        switch (pending.Tool)
        {
            case "delete":
                await _fleet.DeleteSessionAsync(pending.SessionId, ct);
                return ($"Done. I deleted {pending.TargetName}.", new CarModeActionRecord("delete_session", $"Deleted {pending.TargetName}."));
            default:
                throw new InvalidOperationException($"Unknown pending action \"{pending.Tool}\".");
        }
    }

    /// <summary>Read one string argument from a tool call's JSON arguments, tolerating an absent/garbled
    ///  body (the model occasionally emits an empty object) by returning null.</summary>
    internal static string? ReadStringArg(string argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
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
        + "- Use the tools to get REAL facts before you answer. Never guess a count, a name, or a state.\n"
        + "- \"need me\", \"need you\", \"waiting\", and \"who wants me\" all mean sessions whose needsYou is "
        + "true. \"The latest one\" is the first session in the list (it is ordered newest first).\n"
        + "- If a request is ambiguous or you cannot tell which session is meant, ask one short clarifying "
        + "question instead of guessing.\n\n"
        + "Taking action (you have full control):\n"
        + "- Ordinary actions just happen - do not ask permission for them. To start a session, call "
        + "start_session with the repository name. To message a session, call message_session. To approve "
        + "a session that is waiting, call approve_session. After an act, say briefly what you did.\n"
        + "- Deleting or killing a session is DESTRUCTIVE and always needs the owner's spoken confirmation. "
        + "When he asks to delete or kill a session, call delete_session; you will get a "
        + "confirmation_required result. Then tell him exactly what will be deleted and ask him to say "
        + "\"confirm\" to proceed or \"cancel\" to stop, and call no more tools. The system handles the "
        + "actual deletion once he confirms out loud - you never delete without that confirmation.\n"
        + "- When you have the answer or have acted, reply with the final spoken sentence and call no more tools.";

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
              "description": "Get what one specific session is doing right now. Resolve a fuzzy reference (a name, a repository, or a number) to one session.",
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
              "description": "Send a message (a prompt) into a running session, the same as typing to it. Use this to tell a session to do something, for example run the tests.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to message: a human name, a repository, or a number." },
                  "message": { "type": "string", "description": "The message to send into the session." }
                },
                "required": ["session", "message"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "approve_session",
              "description": "Approve a session that is waiting for the owner by accepting its highlighted default (presses Enter). Use this when the owner says to approve, allow, or continue a waiting session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to approve: a human name, a repository, or a number." }
                },
                "required": ["session"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "delete_session",
              "description": "Request to delete (kill and remove) a session. This is DESTRUCTIVE: it does not delete immediately - it returns confirmation_required, and the owner must confirm out loud before it happens.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "The session to delete: a human name, a repository, or a number." }
                },
                "required": ["session"]
              }
            }
          }
        ]
        """;
}
