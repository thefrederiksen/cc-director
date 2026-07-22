using System.Text.Json;

namespace CcDirector.Gateway.Transcription;

/// <summary>Reads the bounded, local history behind Transcription Health.</summary>
public sealed class TranscriptionHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _directory;

    public TranscriptionHistoryReader(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? TranscriptionHistoryLog.DefaultDirectory()
            : directory;
    }

    public IReadOnlyList<TranscriptionHistoryRecord> Load(DateTime? sinceUtc = null, int? limit = null)
    {
        var records = new List<TranscriptionHistoryRecord>();
        if (!Directory.Exists(_directory))
            return records;

        foreach (var file in Directory.EnumerateFiles(_directory, "transcription-*.jsonl"))
        {
            foreach (var line in SafeReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TranscriptionHistoryRecord? record = null;
                try { record = JsonSerializer.Deserialize<TranscriptionHistoryRecord>(line, JsonOptions); }
                catch (JsonException) { }
                if (record is null || sinceUtc is { } since && record.TimestampUtc < since) continue;
                records.Add(record);
            }
        }

        records.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        if (limit is { } n && n >= 0 && records.Count > n)
            records = records.GetRange(0, n);
        return records;
    }

    public TranscriptionStats ComputeStats(DateTime? sinceUtc = null)
    {
        var all = Load(sinceUtc);
        var ok = all.Where(r => r.Outcome == "ok").ToList();
        var correctedOk = ok.Where(r => r.Corrected).ToList();

        return new TranscriptionStats
        {
            TotalTurns = all.Count,
            SuccessfulTurns = ok.Count,
            ByOutcome = all.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            FirstTurnUtc = all.Count > 0 ? all[^1].TimestampUtc : null,
            LastTurnUtc = all.Count > 0 ? all[0].TimestampUtc : null,
            TranscriptionMs = Percentiles.From(ok.Select(r => r.TranscriptionMs).ToArray()),
            CleanupMs = Percentiles.From(ok.Where(r => r.Corrected).Select(r => r.CleanupMs).ToArray()),
            CleanupAppliedTurns = correctedOk.Count(r => r.CleanupApplied),
            CorrectedTurns = correctedOk.Count,
            TotalWords = ok.Sum(r => (long)r.WordCount),
            TotalCharacters = ok.Sum(r => (long)r.CharCount),
        };
    }

    public IReadOnlyList<TermFrequency> TopCorrections(int top, DateTime? sinceUtc = null)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var record in Load(sinceUtc))
        {
            if (record.Changes is null) continue;
            foreach (var change in record.Changes)
            {
                var key = (change.Find, change.Replace);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts.OrderByDescending(pair => pair.Value)
            .Take(Math.Max(0, top))
            .Select(pair => new TermFrequency
            {
                Find = pair.Key.Item1,
                Replace = pair.Key.Item2,
                Count = pair.Value,
            })
            .ToList();
    }

    /// <summary>Deletes the owner's retained transcription-health records.</summary>
    public int Clear()
    {
        if (!Directory.Exists(_directory))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "transcription-*.jsonl"))
        {
            File.Delete(file);
            removed++;
        }
        return removed;
    }

    private static IEnumerable<string> SafeReadLines(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var line in lines) yield return line;
    }
}

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
}

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
        if (samples.Length == 0) return new Percentiles();
        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        return new Percentiles
        {
            Count = sorted.Length,
            Min = sorted[0],
            Max = sorted[^1],
            Avg = Math.Round(samples.Average(), 1),
            P50 = At(sorted, .50),
            P90 = At(sorted, .90),
            P95 = At(sorted, .95),
            P99 = At(sorted, .99),
        };
    }

    private static long At(long[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

public sealed record TermFrequency
{
    public required string Find { get; init; }
    public required string Replace { get; init; }
    public required int Count { get; init; }
}
