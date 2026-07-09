using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the read/aggregate side of the transcription telemetry log: stats, latency percentiles,
/// correction and word frequencies, time-window filtering, and tolerance of missing/garbled data.
/// </summary>
public sealed class TranscriptionTelemetryReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly TranscriptionTelemetryLog _log;
    private readonly TranscriptionTelemetryReader _reader;

    public TranscriptionTelemetryReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-telemetry-read-" + Guid.NewGuid().ToString("N"));
        _log = new TranscriptionTelemetryLog(_dir);
        _reader = new TranscriptionTelemetryReader(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Write(DateTime tsUtc, string outcome, long transcribeMs, long cleanupMs,
        bool applied, string? cleaned, params (string, string)[] changes)
    {
        _log.Record(new TranscriptionTelemetryRecord
        {
            TimestampUtc = tsUtc,
            TurnId = Guid.NewGuid().ToString("N"),
            Outcome = outcome,
            Mode = "devthrottle",
            TranscriptionModel = "gpt-4o-transcribe",
            CleanupModel = "o4-mini",
            AudioBytes = 1000,
            TranscriptionMs = transcribeMs,
            CleanupMs = cleanupMs,
            Corrected = true,
            CleanupApplied = applied,
            ChangedWordCount = changes.Length,
            Changes = changes.Length > 0 ? changes.Select(c => new TelemetryEdit { Find = c.Item1, Replace = c.Item2 }).ToList() : null,
            CharCount = cleaned?.Length ?? 0,
            WordCount = cleaned?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
            RawText = cleaned,
            CleanedText = applied ? cleaned : null,
        });
    }

    [Fact]
    public void ComputeStats_AggregatesOutcomesLatencyAndWords()
    {
        var t = new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc);
        Write(t, "ok", 100, 1, applied: true, "check the acmeflow dashboard now", ("Akmeflow", "acmeflow"));
        Write(t, "ok", 300, 2, applied: false, "just some words here today");
        Write(t, "provider_error", 500, 0, applied: false, null);

        var stats = _reader.ComputeStats();
        Assert.Equal(3, stats.TotalTurns);
        Assert.Equal(2, stats.SuccessfulTurns);
        Assert.Equal(1, stats.ByOutcome["provider_error"]);
        Assert.Equal(2, stats.TranscriptionMs.Count);         // only ok turns
        Assert.Equal(100, stats.TranscriptionMs.Min);
        Assert.Equal(300, stats.TranscriptionMs.Max);
        Assert.Equal(1, stats.CleanupAppliedTurns);
        Assert.Equal(10, stats.TotalWords);                    // two ok turns, 5 words each
    }

    [Fact]
    public void TopCorrections_CountsAcrossTurns()
    {
        var t = new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc);
        Write(t, "ok", 100, 1, true, "a", ("Akmeflow", "acmeflow"));
        Write(t, "ok", 100, 1, true, "b", ("Akmeflow", "acmeflow"), ("Contui", "ConPTY"));
        var terms = _reader.TopCorrections(10);
        Assert.Equal(("Akmeflow", "acmeflow"), (terms[0].Find, terms[0].Replace));
        Assert.Equal(2, terms[0].Count);
        Assert.Equal(1, terms.Single(x => x.Replace == "ConPTY").Count);
    }

    [Fact]
    public void TopWords_CountsCaseInsensitively()
    {
        var t = new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc);
        Write(t, "ok", 100, 1, true, "Ship the ship today ship");
        var words = _reader.TopWords(10);
        Assert.Equal("ship", words[0].Word);
        Assert.Equal(3, words[0].Count);
    }

    [Fact]
    public void Load_FiltersBySinceWindow()
    {
        Write(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), "ok", 100, 1, true, "old");
        Write(new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc), "ok", 100, 1, true, "new");
        var recent = _reader.Load(sinceUtc: new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(recent);
        Assert.Equal("new", recent[0].RawText);
    }

    [Fact]
    public void MissingDirectory_ReturnsEmptyStats()
    {
        var reader = new TranscriptionTelemetryReader(Path.Combine(_dir, "does-not-exist"));
        var stats = reader.ComputeStats();
        Assert.Equal(0, stats.TotalTurns);
        Assert.Equal(0, stats.TranscriptionMs.Count);
    }

    [Fact]
    public void Percentiles_ComputesOrderStatistics()
    {
        var p = Percentiles.From(new long[] { 10, 20, 30, 40, 100 });
        Assert.Equal(10, p.Min);
        Assert.Equal(100, p.Max);
        Assert.Equal(30, p.P50);
        Assert.Equal(100, p.P95);
    }
}
