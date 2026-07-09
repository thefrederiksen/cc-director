using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Reads and aggregates the local transcription telemetry that <see cref="TranscriptionTelemetryLog"/>
/// writes. This is the query side of the log: it loads the JSON-Lines daily files and produces summary
/// statistics, recent turns, and frequency rollups. It is what the transcription-analysis API and, in
/// turn, any agent uses to answer "how fast / how good is transcription" - all from local data.
///
/// Read-only and side-effect free. Missing directory or a malformed line is tolerated (skipped), never
/// fatal - diagnostics must never throw.
/// </summary>
public sealed class TranscriptionTelemetryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string _directory;

    public TranscriptionTelemetryReader(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? TranscriptionTelemetryLog.DefaultDirectory() : directory;
    }

    /// <summary>Load every recorded turn on or after <paramref name="sinceUtc"/>, newest first. When
    /// <paramref name="limit"/> is set, only the newest N are returned.</summary>
    public IReadOnlyList<TranscriptionTelemetryRecord> Load(DateTime? sinceUtc = null, int? limit = null)
    {
        var records = new List<TranscriptionTelemetryRecord>();
        if (!Directory.Exists(_directory))
            return records;

        foreach (var file in Directory.EnumerateFiles(_directory, "transcription-*.jsonl"))
        {
            foreach (var line in SafeReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TranscriptionTelemetryRecord? rec = null;
                try { rec = JsonSerializer.Deserialize<TranscriptionTelemetryRecord>(line, JsonOptions); }
                catch (JsonException) { /* skip a malformed line, never fail the whole read */ }
                if (rec is null) continue;
                if (sinceUtc is { } since && rec.TimestampUtc < since) continue;
                records.Add(rec);
            }
        }

        records.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc)); // newest first
        if (limit is { } n && n >= 0 && records.Count > n)
            records = records.GetRange(0, n);
        return records;
    }

    /// <summary>Aggregate summary over the window: counts by outcome, latency percentiles for the
    /// successful turns, cleanup-applied rate, and word/character totals.</summary>
    public TranscriptionStats ComputeStats(DateTime? sinceUtc = null)
    {
        var all = Load(sinceUtc);
        var ok = all.Where(r => r.Outcome == "ok").ToList();

        var byOutcome = all.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
        var transcribeMs = ok.Select(r => r.TranscriptionMs).ToArray();
        var cleanupMs = ok.Where(r => r.Corrected).Select(r => r.CleanupMs).ToArray();
        var correctedOk = ok.Where(r => r.Corrected).ToList();

        return new TranscriptionStats
        {
            TotalTurns = all.Count,
            SuccessfulTurns = ok.Count,
            ByOutcome = byOutcome,
            FirstTurnUtc = all.Count > 0 ? all[^1].TimestampUtc : null,
            LastTurnUtc = all.Count > 0 ? all[0].TimestampUtc : null,
            TranscriptionMs = Percentiles.From(transcribeMs),
            CleanupMs = Percentiles.From(cleanupMs),
            CleanupAppliedTurns = correctedOk.Count(r => r.CleanupApplied),
            CorrectedTurns = correctedOk.Count,
            TotalWords = ok.Sum(r => (long)r.WordCount),
            TotalCharacters = ok.Sum(r => (long)r.CharCount),
            TotalAudioBytes = all.Sum(r => r.AudioBytes),
        };
    }

    /// <summary>The most frequent dictionary corrections (find -> replace) applied in the window.</summary>
    public IReadOnlyList<TermFrequency> TopCorrections(int top, DateTime? sinceUtc = null)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var r in Load(sinceUtc))
        {
            if (r.Changes is null) continue;
            foreach (var c in r.Changes)
            {
                var key = (c.Find, c.Replace);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return counts.OrderByDescending(kv => kv.Value)
            .Take(Math.Max(0, top))
            .Select(kv => new TermFrequency { Find = kv.Key.Item1, Replace = kv.Key.Item2, Count = kv.Value })
            .ToList();
    }

    /// <summary>The most frequent words spoken in the window (from cleaned text, falling back to raw).
    /// Lets an agent answer word-level questions - vocabulary, filler, profanity - locally.</summary>
    public IReadOnlyList<WordFrequency> TopWords(int top, DateTime? sinceUtc = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Load(sinceUtc))
        {
            var text = r.CleanedText ?? r.RawText;
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match m in Regex.Matches(text, @"[\p{L}\p{Nd}][\p{L}\p{Nd}'\-]*",
                         RegexOptions.CultureInvariant, RegexTimeout))
            {
                var w = m.Value.ToLowerInvariant();
                counts[w] = counts.GetValueOrDefault(w) + 1;
            }
        }
        return counts.OrderByDescending(kv => kv.Value)
            .Take(Math.Max(0, top))
            .Select(kv => new WordFrequency { Word = kv.Key, Count = kv.Value })
            .ToList();
    }

    private static IEnumerable<string> SafeReadLines(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var l in lines) yield return l;
    }
}

/// <summary>Aggregate transcription statistics over a time window. Milliseconds throughout.</summary>
public sealed record TranscriptionStats
{
    public required int TotalTurns { get; init; }
    public required int SuccessfulTurns { get; init; }
    public required IReadOnlyDictionary<string, int> ByOutcome { get; init; }
    public DateTime? FirstTurnUtc { get; init; }
    public DateTime? LastTurnUtc { get; init; }
    public required Percentiles TranscriptionMs { get; init; }
    public required Percentiles CleanupMs { get; init; }
    public int CorrectedTurns { get; init; }
    public int CleanupAppliedTurns { get; init; }
    public long TotalWords { get; init; }
    public long TotalCharacters { get; init; }
    public long TotalAudioBytes { get; init; }
}

/// <summary>Latency distribution summary for a set of samples. Zeroed when there are no samples.</summary>
public sealed record Percentiles
{
    public int Count { get; init; }
    public long Min { get; init; }
    public long Max { get; init; }
    public double Avg { get; init; }
    public long P50 { get; init; }
    public long P90 { get; init; }
    public long P95 { get; init; }
    public long P99 { get; init; }

    public static Percentiles From(long[] samples)
    {
        if (samples.Length == 0)
            return new Percentiles { Count = 0 };
        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        return new Percentiles
        {
            Count = sorted.Length,
            Min = sorted[0],
            Max = sorted[^1],
            Avg = Math.Round(samples.Average(), 1),
            P50 = At(sorted, 0.50),
            P90 = At(sorted, 0.90),
            P95 = At(sorted, 0.95),
            P99 = At(sorted, 0.99),
        };
    }

    private static long At(long[] sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Length) idx = sorted.Length - 1;
        return sorted[idx];
    }
}

/// <summary>How often one find -> replace correction was applied.</summary>
public sealed record TermFrequency
{
    public required string Find { get; init; }
    public required string Replace { get; init; }
    public required int Count { get; init; }
}

/// <summary>How often one word appeared in the transcribed text.</summary>
public sealed record WordFrequency
{
    public required string Word { get; init; }
    public required int Count { get; init; }
}
