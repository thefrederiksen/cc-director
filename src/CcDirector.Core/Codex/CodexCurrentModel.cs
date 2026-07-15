using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Codex;

/// <summary>
/// Reads the model a Codex session is currently using from its rollout JSONL. Codex writes a
/// <c>turn_context</c> event at every turn whose payload carries the model in effect:
///
///   {"timestamp":"...","type":"turn_context","payload":{"model":"gpt-5.5", ...}}
///
/// The LAST turn_context wins, so a mid-session model switch is reflected. Null until the first
/// turn_context exists. The rollout for a repo is located by <see cref="CodexRolloutLocator"/>
/// (verified against codex-cli 0.143.0, issue #1637).
/// </summary>
public static class CodexCurrentModel
{
    /// <summary>The current model of the newest Codex rollout matching <paramref name="repoPath"/>,
    /// or null when no rollout matches or none of its turn_context events carries a model yet.</summary>
    public static string? ReadForRepo(string repoPath)
    {
        var rollout = CodexRolloutLocator.ResolveByRepo(repoPath);
        if (rollout is null)
            return null;
        return ReadFromFile(rollout);
    }

    /// <summary>The current model from one rollout file. Reads with FileShare.ReadWrite so the live
    /// Codex session can keep writing. Null when the file is missing or has no turn_context model.</summary>
    public static string? ReadFromFile(string rolloutPath)
    {
        if (!File.Exists(rolloutPath))
            return null;
        using var fs = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw rollout lines. Returns the LAST turn_context model.</summary>
    public static string? Compute(IEnumerable<string> rolloutLines)
    {
        ArgumentNullException.ThrowIfNull(rolloutLines);
        string? latest = null;

        foreach (var line in rolloutLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while codex writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.GetString() == "turn_context")) continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;

                if (payload.TryGetProperty("model", out var model)
                    && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                {
                    latest = model.GetString();
                }
            }
        }

        if (latest is not null)
            FileLog.Write($"[CodexCurrentModel] model={latest}");
        return latest;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
