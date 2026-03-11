using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Appends usage snapshots to a JSONL file for historical trend tracking.
/// Retains up to 30 days of data. File: config/director/usage-history.jsonl
/// </summary>
public class UsageHistoryStore
{
    private readonly string _filePath;
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public UsageHistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(CcStorage.ToolConfig("director"), "usage-history.jsonl");
    }

    /// <summary>
    /// Append a usage snapshot to the JSONL file.
    /// </summary>
    public void Append(ClaudeUsageInfo info)
    {
        FileLog.Write($"[UsageHistoryStore] Append: account={info.AccountId}, 5h={info.FiveHourUtilization}");

        var entry = new UsageHistoryEntry
        {
            Timestamp = info.FetchedAt,
            AccountId = info.AccountId,
            FiveHourUtilization = info.FiveHourUtilization,
            SevenDayUtilization = info.SevenDayUtilization,
            OpusUtilization = info.OpusUtilization,
            ExtraUsageSpent = info.ExtraUsageSpent,
        };

        var line = JsonSerializer.Serialize(entry, JsonOptions);

        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.AppendAllText(_filePath, line + Environment.NewLine);
        FileLog.Write($"[UsageHistoryStore] Append: written to {_filePath}");
    }

    /// <summary>
    /// Load all entries from the JSONL file, optionally filtering by time range.
    /// </summary>
    public List<UsageHistoryEntry> LoadAll(TimeSpan? maxAge = null)
    {
        FileLog.Write($"[UsageHistoryStore] LoadAll: path={_filePath}, maxAge={maxAge}");

        var results = new List<UsageHistoryEntry>();

        if (!File.Exists(_filePath))
        {
            FileLog.Write("[UsageHistoryStore] LoadAll: file does not exist, returning empty");
            return results;
        }

        var cutoff = maxAge.HasValue
            ? DateTimeOffset.UtcNow - maxAge.Value
            : DateTimeOffset.MinValue;

        var lines = File.ReadAllLines(_filePath);
        FileLog.Write($"[UsageHistoryStore] LoadAll: read {lines.Length} lines");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = JsonSerializer.Deserialize<UsageHistoryEntry>(line, JsonOptions);
            if (entry == null)
                continue;

            if (entry.Timestamp >= cutoff)
                results.Add(entry);
        }

        FileLog.Write($"[UsageHistoryStore] LoadAll: returning {results.Count} entries after filter");
        return results;
    }

    /// <summary>
    /// Prune entries older than 30 days by rewriting the file.
    /// </summary>
    public void Prune()
    {
        FileLog.Write("[UsageHistoryStore] Prune: removing entries older than 30 days");

        if (!File.Exists(_filePath))
        {
            FileLog.Write("[UsageHistoryStore] Prune: file does not exist, nothing to prune");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - DefaultMaxAge;
        var lines = File.ReadAllLines(_filePath);
        var kept = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = JsonSerializer.Deserialize<UsageHistoryEntry>(line, JsonOptions);
            if (entry != null && entry.Timestamp >= cutoff)
                kept.Add(line);
        }

        FileLog.Write($"[UsageHistoryStore] Prune: kept {kept.Count} of {lines.Length} lines");
        File.WriteAllLines(_filePath, kept);
    }
}

public sealed class UsageHistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string AccountId { get; init; } = "";
    public double FiveHourUtilization { get; init; }
    public double SevenDayUtilization { get; init; }
    public double? OpusUtilization { get; init; }
    public double? ExtraUsageSpent { get; init; }
}
