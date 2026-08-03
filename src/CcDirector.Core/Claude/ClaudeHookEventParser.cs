using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// Parses a Claude SessionStart hook event into a <see cref="ClaudeHookRequest"/>.
///
/// Remove-the-network-port mission, phase 3: this used to parse the body of
/// <c>POST /sessions/{id}/claude-hook</c>. That route is gone - the hook now writes the event to a
/// file the Director watches (<see cref="Sessions.SessionPointerWatcher"/>) - so this parses the file's
/// contents instead. The parsing itself is unchanged, which is why it moved rather than being rewritten.
///
/// Two shapes are accepted:
/// - Claude Code's RAW hook event JSON (<c>session_id</c>, <c>transcript_path</c>,
///   <c>hook_event_name</c>, <c>source</c>). Both hook scripts now write this verbatim: neither shell
///   nor PowerShell has to understand it, so neither can get it wrong.
/// - The mapped camelCase shape (<c>claudeSessionId</c>, <c>transcriptPath</c>, <c>hookEvent</c>,
///   <c>source</c>) the Windows script used to build. Still accepted because it costs one line and
///   the mapping is what the tests around it pin.
/// </summary>
internal static class ClaudeHookEventParser
{
    /// <summary>Parse either accepted shape. Returns null when the text is not valid JSON.</summary>
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
