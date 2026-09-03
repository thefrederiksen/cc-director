using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Pi;

/// <summary>
/// Reads the model a pi session is currently using from its session JSONL. Every assistant message
/// line carries the model that produced it:
///
///   {"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.5", ...}}
///
/// The LAST assistant message wins, so a mid-session model switch is reflected. Null until the
/// first assistant message exists. The session file is the one named by the session's id
/// (<see cref="PiSessionLocator"/>; format verified against pi 0.79.4, issue #1637).
/// </summary>
public static class PiCurrentModel
{
    /// <summary>The current model of the pi session with this id, or null when pi has not written its
    /// file yet or it has no assistant message yet.</summary>
    public static string? ReadForSession(string agentSessionId)
    {
        var file = PiSessionLocator.Resolve(agentSessionId);
        if (file is null)
            return null;
        return ReadFromFile(file);
    }

    /// <summary>The current model from one pi session file. Reads with FileShare.ReadWrite so the
    /// live pi session can keep writing. Null when the file is missing or has no assistant model.</summary>
    public static string? ReadFromFile(string sessionPath)
    {
        if (!File.Exists(sessionPath))
            return null;
        using var fs = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw session lines. Returns the LAST assistant model.</summary>
    public static string? Compute(IEnumerable<string> sessionLines)
    {
        ArgumentNullException.ThrowIfNull(sessionLines);
        string? latest = null;

        foreach (var line in sessionLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while pi writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.GetString() == "message")) continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!(msg.TryGetProperty("role", out var role) && role.GetString() == "assistant")) continue;

                if (msg.TryGetProperty("model", out var model)
                    && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    latest = model.GetString();
                }
            }
        }

        if (latest is not null)
            FileLog.Write($"[PiCurrentModel] model={latest}");
        return latest;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
