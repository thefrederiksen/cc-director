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
    private readonly Action<string> _log;

    public CarModeBrain(ICarModeChat chat, ICarModeFleet fleet, CarModeConversationStore conversations, Action<string>? log = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
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

        var messages = new List<object>();
        messages.Add(new { role = "system", content = SystemPrompt });
        foreach (var m in _conversations.GetHistory(deviceKey))
            messages.Add(new { role = m.Role, content = m.Content });
        messages.Add(new { role = "user", content = userText });

        var actions = new List<CarModeActionRecord>();

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
                _log($"[CarModeBrain] turn done in {round + 1} round(s): actions={actions.Count}");
                return new CarModeTurnResponse { Spoken = spoken, Actions = actions };
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
                var (resultJson, action) = await ExecuteToolAsync(call, ct);
                if (action is not null) actions.Add(action);
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = resultJson });
            }
        }

        // The model never settled on an answer within the round cap: a loud, specific failure, not a guess.
        _log("[CarModeBrain] round cap reached without a final answer");
        var giveUp = "I'm having trouble answering that right now. Please try again.";
        _conversations.Append(deviceKey, userText, giveUp);
        return new CarModeTurnResponse { Spoken = giveUp, Actions = actions };
    }

    /// <summary>Run one tool the model called and return its result JSON (for the tool message) and an
    ///  optional action record (Phase 3 acts; reads have none). An unknown tool or bad arguments is
    ///  returned to the model as a tool error, not thrown - the model can recover on the next round.</summary>
    private async Task<(string ResultJson, CarModeActionRecord? Action)> ExecuteToolAsync(CarModeToolCall call, CancellationToken ct)
    {
        _log($"[CarModeBrain] tool: {call.Name}");
        switch (call.Name)
        {
            case "list_sessions":
            {
                var sessions = await _fleet.ListSessionsAsync(ct);
                var payload = new
                {
                    needsYouCount = sessions.Count(s => s.NeedsYou),
                    total = sessions.Count,
                    sessions = sessions.Select(s => new
                    {
                        s.Name,
                        s.Number,
                        s.Repo,
                        s.MachineName,
                        mission = s.MissionName,
                        s.State,
                        s.NeedsYou,
                        s.WaitingMinutes,
                        s.Summary,
                    }),
                };
                return (JsonSerializer.Serialize(payload, ToolResultJson), null);
            }
            case "get_session_activity":
            {
                var reference = ReadStringArg(call.ArgumentsJson, "session");
                if (string.IsNullOrWhiteSpace(reference))
                    return (JsonSerializer.Serialize(new { error = "Provide a session name, repo, or number." }, ToolResultJson), null);
                var activity = await _fleet.GetSessionActivityAsync(reference, ct);
                if (activity is null)
                    return (JsonSerializer.Serialize(new { error = $"No session matched \"{reference}\"." }, ToolResultJson), null);
                var payload = new
                {
                    activity.Name,
                    activity.Repo,
                    activity.State,
                    activity.NeedsYou,
                    activity.Summary,
                };
                return (JsonSerializer.Serialize(payload, ToolResultJson), null);
            }
            default:
                return (JsonSerializer.Serialize(new { error = $"Unknown tool \"{call.Name}\"." }, ToolResultJson), null);
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
        + "question instead of guessing.\n"
        + "- When you have the answer, reply with the final spoken sentence and call no more tools.";

    // The tool catalog (Phase 2: read-only). Standard chat-completions function tools.
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
          }
        ]
        """;
}
