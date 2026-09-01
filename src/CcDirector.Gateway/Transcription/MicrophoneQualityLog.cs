using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The per-tenant record of how good each dictation SOUNDED, one line per dictation, so the Cockpit
/// can say which microphone is letting a user down.
///
/// WHY THIS IS SEPARATE FROM <see cref="TranscriptionHistoryLog"/>. That log is written by the Gateway
/// at transcription time and records how the TRANSCRIBER behaved - latency, outcome, corrections. The
/// measurement here is made by the CLIENT, from the audio, and only exists after the clip has been
/// decoded in the browser. It therefore cannot be a column on a record the Gateway has already
/// written. The two share a shape and a retention window on purpose, so an operator learns one layout
/// rather than two, and they are joined by nothing - the quality picture is an aggregate, not a
/// per-turn lookup.
///
/// WHAT IS NOT IN HERE: audio and transcript text. This is a handful of numbers per dictation plus the
/// microphone's name. It exists to answer "which of my microphones is bad", which needs no recording
/// of what was said.
///
/// RETENTION: 30 days, matching the Transcription Health history. (It no longer matches the
/// transcript-text window: the owner moved dictation transcripts to 90 days on 2026-09-01 to sit on
/// the same clock as session history. These quality MEASUREMENTS carry no text of what was said, and
/// nothing argued for holding them longer, so they keep the shorter window.)
/// </summary>
public sealed class MicrophoneQualityLog
{
    /// <summary>How long a daily file is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    public static string DefaultDirectory() => CcStorage.MicrophoneQuality();

    /// <summary>
    /// This tenant's partition. The tenant is in the PATH, so no code path can write one account's
    /// measurements into another's folder. The single Local tenant keeps the flat directory, matching
    /// how the transcription history lays itself out, so a self-host install has one obvious place.
    /// </summary>
    public static string DirectoryFor(Core.Tenancy.TenantId tenant)
    {
        if (tenant == Core.Tenancy.TenantId.Local) return DefaultDirectory();
        var chars = tenant.Value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return Path.Combine(DefaultDirectory(), new string(chars));
    }

    public static MicrophoneQualityLog ForTenant(Core.Tenancy.TenantId tenant) => new(DirectoryFor(tenant));

    public MicrophoneQualityLog(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
    }

    public string Directory => _directory;

    private string FileFor(DateTime utcNow) => Path.Combine(_directory, $"microphone-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>
    /// Append one measurement and prune expired days. NEVER throws: a dictation that already
    /// succeeded must not be turned into an error because our bookkeeping failed.
    /// </summary>
    public void Record(MicrophoneQualityRecord record)
    {
        if (record is null) return;
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(FileFor(DateTime.UtcNow), JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
                PruneExpired();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MicrophoneQualityLog] Record FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>Every measurement inside the window, newest first. One unreadable line is skipped
    /// rather than taking the whole history down with it.</summary>
    public IReadOnlyList<MicrophoneQualityRecord> Load(DateTime? sinceUtc = null)
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_directory)) return Array.Empty<MicrophoneQualityRecord>();
            var records = new List<MicrophoneQualityRecord>();
            foreach (var file in new DirectoryInfo(_directory).GetFiles("microphone-*.jsonl"))
            {
                foreach (var line in ReadLines(file.FullName))
                {
                    try
                    {
                        var rec = JsonSerializer.Deserialize<MicrophoneQualityRecord>(line, JsonOptions);
                        if (rec is not null && (sinceUtc is null || rec.TimestampUtc >= sinceUtc.Value)) records.Add(rec);
                    }
                    catch (JsonException)
                    {
                        // A torn final line from a crash mid-append. Skip it; the rest is still good.
                    }
                }
            }
            return records.OrderByDescending(r => r.TimestampUtc).ToList();
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l));
        }
        catch (IOException ex)
        {
            FileLog.Write($"[MicrophoneQualityLog] could not read {Path.GetFileName(path)}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Delete every measurement for this tenant. Returns how many files were removed.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(_directory)) return 0;
            var removed = 0;
            foreach (var file in new DirectoryInfo(_directory).GetFiles("microphone-*.jsonl"))
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException ex)
                {
                    FileLog.Write($"[MicrophoneQualityLog] could not delete {file.Name}: {ex.Message}");
                }
            }
            return removed;
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var file in new DirectoryInfo(_directory).GetFiles("microphone-*.jsonl"))
        {
            if (file.LastWriteTimeUtc >= cutoff) continue;
            try
            {
                file.Delete();
            }
            catch (IOException ex)
            {
                FileLog.Write($"[MicrophoneQualityLog] prune could not delete {file.Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>One dictation's acoustic measurement. No audio, no transcript - only how it sounded.</summary>
public sealed record MicrophoneQualityRecord
{
    [JsonPropertyName("ts")] public DateTime TimestampUtc { get; init; }
    /// <summary>The microphone's name as the operating system reported it, or empty when unknown.</summary>
    [JsonPropertyName("device")] public string Device { get; init; } = "";
    /// <summary>The microphone's stable per-origin identifier - the GROUPING key. The name is display
    /// metadata (a driver update or an operating system language change renames it); this is what
    /// keeps one microphone's history in one row. Empty on records written before it existed, or when
    /// the browser withheld it - those group by name as before.</summary>
    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = "";
    /// <summary>What kind of machine captured it: "mobile", "mac", "windows" or "unknown". Empty on
    /// records written before it existed (folded as unknown).</summary>
    [JsonPropertyName("platform")] public string Platform { get; init; } = "";
    /// <summary>The raw evidence behind the platform bucket, kept so a wrong bucket can be diagnosed
    /// later without guessing.</summary>
    [JsonPropertyName("platformRaw")] public string PlatformRaw { get; init; } = "";
    /// <summary>Which surface produced it ("dictation-dialog", "dictation-send").</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("sampleRate")] public int SampleRate { get; init; }
    [JsonPropertyName("speechLevelDb")] public double SpeechLevelDb { get; init; }
    [JsonPropertyName("noiseFloorDb")] public double NoiseFloorDb { get; init; }
    [JsonPropertyName("signalToNoiseDb")] public double SignalToNoiseDb { get; init; }
    [JsonPropertyName("clippedFraction")] public double ClippedFraction { get; init; }
    [JsonPropertyName("highBandRatioDb")] public double HighBandRatioDb { get; init; }
    [JsonPropertyName("narrowband")] public bool Narrowband { get; init; }
    /// <summary>"good", "fair" or "poor" - folded by the same rules the Test microphone screen shows.</summary>
    [JsonPropertyName("rating")] public string Rating { get; init; } = "";
    /// <summary>Issue identifiers joined by "+", empty when the microphone is good.</summary>
    [JsonPropertyName("issues")] public string Issues { get; init; } = "";
}
