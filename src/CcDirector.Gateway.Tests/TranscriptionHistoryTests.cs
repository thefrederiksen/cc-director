using System.Text.Json;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class TranscriptionHistoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cc-transcription-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }

    private static TranscriptionHistoryRecord Record(DateTime timestamp, string outcome = "ok") => new()
    {
        TimestampUtc = timestamp,
        TurnId = Guid.NewGuid().ToString("N"),
        Outcome = outcome,
        TranscriptionMs = 250,
        CleanupMs = 20,
        Corrected = true,
        CleanupApplied = true,
        ChangedWordCount = 1,
        Changes = [new TranscriptionHistoryEdit { Find = "Akme", Replace = "Acme" }],
        CharCount = 20,
        WordCount = 4,
    };

    [Fact]
    public void Record_WritesOnlyTheMinimizedFeatureFields()
    {
        var log = new TranscriptionHistoryLog(_directory);
        var now = DateTime.UtcNow;

        log.Record(Record(now));

        var json = JsonDocument.Parse(File.ReadAllText(log.FileFor(now)).Trim()).RootElement;
        Assert.False(json.TryGetProperty("rawText", out _));
        Assert.False(json.TryGetProperty("cleanedText", out _));
        Assert.False(json.TryGetProperty("error", out _));
        Assert.False(json.TryGetProperty("cleanupModel", out _));
        Assert.False(json.TryGetProperty("mode", out _));
        Assert.False(json.TryGetProperty("transcriptionModel", out _));
        Assert.False(json.TryGetProperty("audioBytes", out _));
    }

    [Fact]
    public void Record_PrunesDailyFilesOlderThanThirtyDays()
    {
        var log = new TranscriptionHistoryLog(_directory);
        var now = DateTime.UtcNow.Date;
        Directory.CreateDirectory(_directory);
        File.WriteAllText(log.FileFor(now.AddDays(-31)), "{}");

        log.Record(Record(now));

        Assert.False(File.Exists(log.FileFor(now.AddDays(-31))));
        Assert.True(File.Exists(log.FileFor(now)));
    }

    [Fact]
    public void Reader_ComputesStatsTermsAndSupportsOwnerClear()
    {
        var log = new TranscriptionHistoryLog(_directory);
        var now = DateTime.UtcNow;
        log.Record(Record(now.AddMinutes(-1)));
        log.Record(Record(now, "provider_error"));
        var reader = new TranscriptionHistoryReader(_directory);

        var stats = reader.ComputeStats();
        var terms = reader.TopCorrections(10);

        Assert.Equal(2, stats.TotalTurns);
        Assert.Equal(1, stats.SuccessfulTurns);
        Assert.Equal(2, terms.Single().Count);
        Assert.Equal(1, reader.Clear());
        Assert.Empty(reader.Load());
    }
}
