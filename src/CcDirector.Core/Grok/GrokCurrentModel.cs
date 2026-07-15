using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Grok;

/// <summary>
/// Reads the model a Grok CLI session is currently using from its conversation file
/// (<c>~/.grok/sessions/&lt;percent-encoded-cwd&gt;/&lt;session-id&gt;/chat_history.jsonl</c>).
/// Assistant lines carry the model that produced them:
///
///   {"type":"assistant","content":"...","model_id":"grok-4.5","model_fingerprint":"...", ...}
///
/// The LAST assistant line wins, so a mid-session model switch is reflected. The file for a repo
/// is located by <see cref="GrokSessionLocator.Scan"/> - the newest session under the per-cwd
/// directory (verified against grok 0.2.93, issue #1637).
/// </summary>
public static class GrokCurrentModel
{
    /// <summary>The current model of the newest Grok session matching <paramref name="repoPath"/>,
    /// or null when none matches or it has no assistant model line yet.</summary>
    public static string? ReadForRepo(string repoPath)
    {
        var file = GrokSessionLocator.Scan(repoPath, SessionsDirectory());
        if (file is null)
            return null;
        return ReadFromFile(file);
    }

    /// <summary>The current model from one chat_history.jsonl. Reads with FileShare.ReadWrite so
    /// the live Grok session can keep writing. Null when the file is missing or has no model line.</summary>
    public static string? ReadFromFile(string chatHistoryPath)
    {
        if (!File.Exists(chatHistoryPath))
            return null;
        using var fs = new FileStream(chatHistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw chat lines. Returns the LAST assistant <c>model_id</c>.</summary>
    public static string? Compute(IEnumerable<string> chatLines)
    {
        ArgumentNullException.ThrowIfNull(chatLines);
        string? latest = null;

        foreach (var line in chatLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while grok writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.GetString() == "assistant")) continue;

                if (root.TryGetProperty("model_id", out var model)
                    && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    latest = model.GetString();
                }
            }
        }

        if (latest is not null)
            FileLog.Write($"[GrokCurrentModel] model={latest}");
        return latest;
    }

    private static string SessionsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
