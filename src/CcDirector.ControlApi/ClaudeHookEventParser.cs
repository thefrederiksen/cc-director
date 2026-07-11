using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Parses the body of POST /sessions/{id}/claude-hook into a <see cref="ClaudeHookRequest"/>.
///
/// Two body shapes are accepted:
/// - The mapped camelCase shape the Windows PowerShell hook script builds
///   (<c>claudeSessionId</c>, <c>transcriptPath</c>, <c>hookEvent</c>, <c>source</c>).
/// - Claude Code's RAW hook event JSON (<c>session_id</c>, <c>transcript_path</c>,
///   <c>hook_event_name</c>, <c>source</c>), forwarded verbatim by the macOS/Linux shell
///   hook script. Shell cannot parse JSON with tools guaranteed to exist on a stock
///   machine, so the mapping happens here instead - in testable C#.
/// </summary>
internal static class ClaudeHookEventParser
{
    /// <summary>Parse either accepted body shape. Returns null when the body is not valid JSON.</summary>
    public static ClaudeHookRequest? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return new ClaudeHookRequest(
                ClaudeSessionId: ReadString(root, "claudeSessionId", "session_id"),
                TranscriptPath: ReadString(root, "transcriptPath", "transcript_path"),
                HookEvent: ReadString(root, "hookEvent", "hook_event_name"),
                Source: ReadString(root, "source", "source"));
        }
    }

    private static string? ReadString(JsonElement root, string mappedName, string rawName)
    {
        if (root.TryGetProperty(mappedName, out var mapped) && mapped.ValueKind == JsonValueKind.String)
            return mapped.GetString();
        if (root.TryGetProperty(rawName, out var raw) && raw.ValueKind == JsonValueKind.String)
            return raw.GetString();
        return null;
    }
}
