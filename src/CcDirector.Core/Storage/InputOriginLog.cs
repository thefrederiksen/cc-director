using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// The provenance half of the prompt record (issue #1551): where each submitted prompt came from -
/// typed or spoken, desktop or cockpit or phone.
///
/// This exists because the two halves are knowable in two different places and NOWHERE else:
/// - WHAT was said is the agent's own business, read back from its transcript
///   (<see cref="ConversationLog"/> ingests it via SessionHistoryReader).
/// - WHERE IT CAME FROM is ONLY ever known here, at the Session choke points. By the time a prompt
///   reaches a transcript, one spoken on the phone and one typed at the terminal are identical.
///
/// So this log deliberately holds NO text - just a small event per submission that the conversation
/// ingest joins to a message by session + nearest timestamp. One JSON line per submission in a daily
/// file: base/prompt-log/origin-yyyyMMdd.jsonl.
///
/// Fail-safe: every write is wrapped, so a disk error can never break a turn.
/// </summary>
public static class InputOriginLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object _gate = new();

    /// <summary>The prompt-log directory, shared with <see cref="ConversationLog"/>.</summary>
    public static string Directory() => CcStorage.Ensure(Path.Combine(CcStorage.Root(), "prompt-log"));

    /// <summary>The daily file an event at <paramref name="utcNow"/> lands in.</summary>
    public static string FileFor(DateTime utcNow)
        => Path.Combine(Directory(), $"origin-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>Append one origin event. Never throws.</summary>
    public static void Write(InputOriginRecord record)
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
            FileLog.Write($"[InputOriginLog] Write FAILED (swallowed) for session={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Read every origin event in the inclusive UTC day range, oldest first. Skips unparseable lines
    /// rather than failing the whole read.
    /// </summary>
    public static IReadOnlyList<InputOriginRecord> Read(DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<InputOriginRecord>();
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
                FileLog.Write($"[InputOriginLog] Read FAILED for {path}: {ex.Message}");
                continue;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<InputOriginRecord>(line, JsonOpts);
                    if (record is not null) results.Add(record);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[InputOriginLog] Skipping unparseable line in {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        return results;
    }
}

/// <summary>
/// One submission crossing a Session choke point: when, which session, and how the operator produced
/// it. Deliberately carries no prompt text - the conversation log holds the text, and this is joined
/// to it by session + nearest timestamp.
/// </summary>
public sealed record InputOriginRecord
{
    [JsonPropertyName("ts")] public required DateTime TsUtc { get; init; }
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    /// <summary>"typed" or "voice".</summary>
    [JsonPropertyName("modality")] public required string Modality { get; init; }
    /// <summary>"desktop", "cockpit", "phone", or "unknown".</summary>
    [JsonPropertyName("surface")] public required string Surface { get; init; }
    /// <summary>Characters submitted. A size hint only - the authoritative text is in the conversation log.</summary>
    [JsonPropertyName("charCount")] public required int CharCount { get; init; }
}
