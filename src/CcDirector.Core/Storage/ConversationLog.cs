using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// The durable record of what was actually said (issue #1551): every prompt the operator sent and
/// every reply the agent sent back, for every agent, with the origin of each prompt joined on.
/// This is the foundation the weekly review reads back (devthrottle_internal#358).
///
/// One JSON line per message in a daily file: base/prompt-log/conversation-yyyyMMdd.jsonl.
///
/// Source: the agent's OWN transcript, read through the agent-neutral SessionHistoryReader facade and
/// copied here by <see cref="ConversationIngestor"/>. That is ground truth - what the agent actually
/// received, after all the line editing - rather than our reconstruction of what the user probably
/// typed. Agents read on demand and nothing persists them, so if an agent compacts or cleans up its
/// transcript the history is gone; copying it here is the whole point.
///
/// Retention is deliberately unbounded. The point is looking back across weeks and months, and this
/// text is small. (Contrast <see cref="TurnReviewLog"/>, which holds terminal SCREENS and expires at
/// 7 days.) Nothing prunes this. If that changes it becomes a stated setting, not a silent sweep.
///
/// Fail-safe: every write is wrapped, so a logging error can never break a turn.
/// </summary>
public static class ConversationLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object _gate = new();

    /// <summary>The prompt-log directory, shared with <see cref="InputOriginLog"/>.</summary>
    public static string Directory() => CcStorage.Ensure(Path.Combine(CcStorage.Root(), "prompt-log"));

    /// <summary>The daily file a message at <paramref name="utcNow"/> lands in.</summary>
    public static string FileFor(DateTime utcNow)
        => Path.Combine(Directory(), $"conversation-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>Append one message. Never throws.</summary>
    public static void Write(ConversationRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOpts);
            var path = FileFor(record.TsUtc);
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConversationLog] Write FAILED (swallowed) for session={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>Append several messages in one lock. Never throws.</summary>
    public static void WriteMany(IEnumerable<ConversationRecord> records)
    {
        foreach (var record in records)
            Write(record);
    }

    /// <summary>
    /// Read every message in the inclusive UTC day range, oldest first. Skips unparseable lines rather
    /// than failing the whole read, so one bad line cannot hide a month of work.
    /// </summary>
    public static IReadOnlyList<ConversationRecord> Read(DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<ConversationRecord>();
        for (var day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1))
        {
            var path = FileFor(day);
            if (!File.Exists(path)) continue;
            string[] lines;
            try
            {
                lock (_gate) { lines = File.ReadAllLines(path); }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ConversationLog] Read FAILED for {path}: {ex.Message}");
                continue;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<ConversationRecord>(line, JsonOpts);
                    if (record is not null) results.Add(record);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[ConversationLog] Skipping unparseable line in {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        return results;
    }
}

/// <summary>
/// One message in the durable record. <see cref="Text"/> is the whole message as the agent saw it.
///
/// The origin fields (<see cref="Modality"/>, <see cref="Surface"/>) are joined from
/// <see cref="InputOriginLog"/> at ingest and are meaningful only for a user message; an assistant
/// reply has no origin and leaves them null. When a user message cannot be matched to an origin event
/// the surface is recorded as "unknown" rather than guessed - InputOrigin.cs defines Unknown for
/// exactly this, "recorded honestly, but never presented as a real surface share".
/// </summary>
public sealed record ConversationRecord
{
    [JsonPropertyName("ts")] public required DateTime TsUtc { get; init; }
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("sessionName")] public string? SessionName { get; init; }
    [JsonPropertyName("repoPath")] public string? RepoPath { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("missionName")] public string? MissionName { get; init; }
    /// <summary>"user" or "assistant".</summary>
    [JsonPropertyName("role")] public required string Role { get; init; }
    /// <summary>"typed" or "voice"; null for an assistant reply or an unmatched user message.</summary>
    [JsonPropertyName("modality")] public string? Modality { get; init; }
    /// <summary>"desktop" / "cockpit" / "phone" / "unknown"; null for an assistant reply.</summary>
    [JsonPropertyName("surface")] public string? Surface { get; init; }
    /// <summary>True when the source agent supplied a real timestamp; false when we stamped it at
    /// ingest because the agent carries none (Gemini). Keeps an inferred time from reading as measured.</summary>
    [JsonPropertyName("tsFromAgent")] public required bool TimestampFromAgent { get; init; }
    [JsonPropertyName("charCount")] public required int CharCount { get; init; }
    [JsonPropertyName("wordCount")] public required int WordCount { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}
