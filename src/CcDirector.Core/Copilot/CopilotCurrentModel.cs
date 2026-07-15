using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Copilot;

/// <summary>
/// Reads the model a GitHub Copilot CLI session is currently using from its per-session event log
/// (<c>~/.copilot/session-state/&lt;session-id&gt;/events.jsonl</c>). Assistant events carry the
/// model that served the turn:
///
///   {"type":"assistant.turn_start","data":{"turnId":"0","model":"claude-haiku-4.5", ...}, ...}
///   {"type":"assistant.message","data":{"model":"claude-haiku-4.5", ...}, ...}
///
/// The LAST event with a model wins, so a mid-session model switch is reflected. The Director
/// preassigns the Copilot session id at launch (<c>--session-id</c>), so the directory is directly
/// addressable - no repo scan needed. Copilot's SQLite session store has NO model column, so this
/// event log is the only model source (verified against GitHub Copilot CLI 1.0.70, issue #1637).
/// </summary>
public static class CopilotCurrentModel
{
    /// <summary>The current model of the Copilot session <paramref name="agentSessionId"/>, or null
    /// when its event log does not exist or carries no model-bearing event yet.</summary>
    public static string? ReadForSession(string agentSessionId)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
            return null;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state",
            agentSessionId,
            "events.jsonl");
        return ReadFromFile(path);
    }

    /// <summary>The current model from one events.jsonl. Reads with FileShare.ReadWrite so the live
    /// Copilot session can keep writing. Null when the file is missing or has no model event.</summary>
    public static string? ReadFromFile(string eventsPath)
    {
        if (!File.Exists(eventsPath))
            return null;
        using var fs = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw event lines. Returns the LAST event's <c>data.model</c>.</summary>
    public static string? Compute(IEnumerable<string> eventLines)
    {
        ArgumentNullException.ThrowIfNull(eventLines);
        string? latest = null;

        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while copilot writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

                if (data.TryGetProperty("model", out var model)
                    && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    latest = model.GetString();
                }
            }
        }

        if (latest is not null)
            FileLog.Write($"[CopilotCurrentModel] model={latest}");
        return latest;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
