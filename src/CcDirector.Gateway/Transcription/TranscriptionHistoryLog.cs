using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Dictation;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Local, append-only history used by the owner's Transcription Health feature. Records are never sent
/// anywhere, never include transcript text or provider error bodies, and expire after
/// <see cref="Retention"/>.
/// </summary>
public sealed class TranscriptionHistoryLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    public TranscriptionHistoryLog(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    public static string DefaultDirectory() => CcStorage.TranscriptionHistory();

    public string FileFor(DateTime utcNow)
        => Path.Combine(_directory, $"transcription-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>Appends one minimized history record and prunes expired daily files. Never fails a turn.</summary>
    public void Record(TranscriptionHistoryRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(FileFor(record.TimestampUtc), line + Environment.NewLine);
                PruneExpired(record.TimestampUtc);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionHistoryLog] Record FAILED (swallowed): {ex.Message}");
        }
    }

    private void PruneExpired(DateTime utcNow)
    {
        var oldestDay = DateOnly.FromDateTime(utcNow.Subtract(Retention));
        foreach (var path in Directory.EnumerateFiles(_directory, "transcription-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != "transcription-yyyyMMdd".Length
                || !DateOnly.TryParseExact(name["transcription-".Length..], "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day >= oldestDay)
                continue;
            File.Delete(path);
        }
    }
}

/// <summary>One minimized transcription-health record. Timing values are milliseconds.</summary>
public sealed record TranscriptionHistoryRecord
{
    [JsonPropertyName("ts")] public required DateTime TimestampUtc { get; init; }
    [JsonPropertyName("turnId")] public required string TurnId { get; init; }
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("transcriptionMs")] public required long TranscriptionMs { get; init; }
    [JsonPropertyName("cleanupMs")] public required long CleanupMs { get; init; }
    [JsonPropertyName("corrected")] public required bool Corrected { get; init; }
    [JsonPropertyName("cleanupApplied")] public bool CleanupApplied { get; init; }
    [JsonPropertyName("changedWordCount")] public int ChangedWordCount { get; init; }
    [JsonPropertyName("changes")] public IReadOnlyList<TranscriptionHistoryEdit>? Changes { get; init; }
    [JsonPropertyName("charCount")] public int CharCount { get; init; }
    [JsonPropertyName("wordCount")] public int WordCount { get; init; }
}

/// <summary>A dictionary correction retained for the local “most corrected terms” feature.</summary>
public sealed record TranscriptionHistoryEdit
{
    [JsonPropertyName("find")] public required string Find { get; init; }
    [JsonPropertyName("replace")] public required string Replace { get; init; }

    public static TranscriptionHistoryEdit From(TranscriptEdit edit) =>
        new() { Find = edit.Find, Replace = edit.Replace };
}
