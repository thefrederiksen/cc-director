using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Local, append-only telemetry for every transcription the Gateway performs. Now that all
/// transcription flows through the Gateway (issue #839) this is the one place that sees every turn, so
/// it is the right place to record what happened - purely on THIS machine, never sent to any server.
///
/// Each turn is one JSON line in a daily file under
/// <c>%LOCALAPPDATA%\cc-director\transcription-log\transcription-YYYYMMDD.jsonl</c>. JSON Lines is
/// append-friendly, human-inspectable, and trivially loadable for later diagnostics and graphs (how
/// fast is transcription really, p50/p95 latency, how often cleanup changes anything, word counts, and
/// so on). It records timing, the model that ran, the audio size, the cleanup result, AND the
/// transcribed text itself so those word-level diagnostics are possible.
///
/// Privacy: the transcript text is written to LOCAL disk only. It is diagnostic data for the user's own
/// machine and is never transmitted. (A future opt-out can gate <see cref="TextEnabled"/>.)
///
/// Fail-safe: telemetry must never break a transcription. Every write is wrapped so a disk error is
/// logged and swallowed, exactly like the fail-open contract of the cleanup pass.
/// </summary>
public sealed class TranscriptionTelemetryLog
{
    /// <summary>Process-wide shared log used by the per-request transcription service.</summary>
    public static readonly TranscriptionTelemetryLog Shared = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    /// <summary>When false, the transcript text fields are omitted (timing/metadata still recorded).</summary>
    public bool TextEnabled { get; init; } = true;

    /// <param name="directory">Override the log directory (tests). Defaults to the per-user location.</param>
    public TranscriptionTelemetryLog(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    /// <summary>The per-user transcription-log directory.</summary>
    public static string DefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "transcription-log");
    }

    /// <summary>The daily file a record written at <paramref name="utcNow"/> lands in.</summary>
    public string FileFor(DateTime utcNow)
        => Path.Combine(_directory, $"transcription-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>
    /// Append one transcription turn. Never throws: on any error it logs and returns, so telemetry
    /// can never fail a recording.
    /// </summary>
    public void Record(TranscriptionTelemetryRecord record)
    {
        try
        {
            var toWrite = TextEnabled ? record : record with { RawText = null, CleanedText = null };
            var line = JsonSerializer.Serialize(toWrite, JsonOptions);
            var path = FileFor(record.TimestampUtc);
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionTelemetryLog] Record FAILED (swallowed): {ex.Message}");
        }
    }
}

/// <summary>
/// One transcription turn, recorded locally. Fields are ordered for readable JSON. Timing is in
/// milliseconds; <see cref="CleanupMs"/> is 0 when correction was not requested.
/// </summary>
public sealed record TranscriptionTelemetryRecord
{
    [JsonPropertyName("ts")] public required DateTime TimestampUtc { get; init; }
    [JsonPropertyName("turnId")] public required string TurnId { get; init; }
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("transcriptionModel")] public string? TranscriptionModel { get; init; }
    [JsonPropertyName("cleanupModel")] public string? CleanupModel { get; init; }
    [JsonPropertyName("audioBytes")] public required long AudioBytes { get; init; }
    [JsonPropertyName("transcriptionMs")] public required long TranscriptionMs { get; init; }
    [JsonPropertyName("cleanupMs")] public required long CleanupMs { get; init; }
    [JsonPropertyName("corrected")] public required bool Corrected { get; init; }
    [JsonPropertyName("cleanupApplied")] public bool CleanupApplied { get; init; }
    [JsonPropertyName("changedWordCount")] public int ChangedWordCount { get; init; }
    [JsonPropertyName("changes")] public IReadOnlyList<TelemetryEdit>? Changes { get; init; }
    [JsonPropertyName("charCount")] public int CharCount { get; init; }
    [JsonPropertyName("wordCount")] public int WordCount { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("rawText")] public string? RawText { get; init; }
    [JsonPropertyName("cleanedText")] public string? CleanedText { get; init; }
}

/// <summary>A single dictionary correction that was applied, recorded for diagnostics.</summary>
public sealed record TelemetryEdit
{
    [JsonPropertyName("find")] public required string Find { get; init; }
    [JsonPropertyName("replace")] public required string Replace { get; init; }

    public static TelemetryEdit From(TranscriptEdit edit) => new() { Find = edit.Find, Replace = edit.Replace };
}
