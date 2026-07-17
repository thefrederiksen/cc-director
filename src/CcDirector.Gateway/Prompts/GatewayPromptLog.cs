using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// THE prompt log (issue #1551). Every prompt the operator sends and every reply the agent sends back,
/// for every agent, on every machine in the fleet - held here, on the Gateway.
///
/// The Gateway is the right home and the Director is not, for two reasons:
/// - The Gateway is the one place that sees the WHOLE fleet. Anyone asking for history asks here and
///   it is already present; nothing has to go hunting across machines for it.
/// - The Gateway is what moves to the server. The log moves with it, because it was never somewhere
///   else to begin with.
///
/// The Director captures and pushes (it is the only thing that sees a prompt at all, and the only thing
/// that knows whether it was typed or spoken and from which surface). It keeps NO copy - this is the
/// single copy. Same shape as the existing stats spine, where the Director observes and
/// GatewayInputStatsAggregator holds.
///
/// One JSON line per message in a daily file: base/prompt-log/conversation-yyyyMMdd.jsonl.
///
/// Retention is unbounded. The point is looking back across weeks and months, and the text is small.
/// (Contrast TurnReviewLog, which holds terminal SCREENS and expires at 7 days.) Nothing prunes this.
///
/// Fail-safe: every write is wrapped, so a logging error can never fail a Director's push.
/// </summary>
public sealed class GatewayPromptLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    /// <param name="directory">Override the log directory (tests). Defaults to the per-user location.</param>
    public GatewayPromptLog(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    /// <summary>The Gateway's prompt-log directory.</summary>
    public static string DefaultDirectory() => CcStorage.PromptLog();

    /// <summary>The daily file a message at <paramref name="utcNow"/> lands in.</summary>
    public string FileFor(DateTime utcNow)
        => Path.Combine(_directory, $"conversation-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>
    /// Append messages pushed by a Director. Returns how many were written. Never throws: a logging
    /// failure must not fail the Director's push.
    /// </summary>
    public int Append(IEnumerable<PromptRecord> records)
    {
        var written = 0;
        try
        {
            // Group by day so a batch spanning midnight (or a backfill spanning months) lands in the
            // right daily files rather than all in today's.
            foreach (var day in records.GroupBy(r => r.TsUtc.Date))
            {
                var lines = day.Select(r => JsonSerializer.Serialize(r, JsonOpts)).ToList();
                var path = FileFor(day.Key);
                lock (_gate)
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllLines(path, lines);
                }
                written += lines.Count;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayPromptLog] Append FAILED (swallowed): {ex.Message}");
        }
        return written;
    }

    /// <summary>
    /// Read every message in the inclusive UTC day range, oldest first. Skips unparseable lines rather
    /// than failing the whole read, so one bad line cannot hide a month of work.
    /// </summary>
    public IReadOnlyList<PromptRecord> Read(DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<PromptRecord>();
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
                FileLog.Write($"[GatewayPromptLog] Read FAILED for {path}: {ex.Message}");
                continue;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<PromptRecord>(line, JsonOpts);
                    if (record is not null) results.Add(record);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[GatewayPromptLog] Skipping unparseable line in {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        return results;
    }
}
