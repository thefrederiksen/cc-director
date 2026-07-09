using System.Text.Json;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the local transcription telemetry log: it writes one JSON line per turn into a daily
/// file, can omit the transcript text, and never throws on a bad directory.
/// </summary>
public sealed class TranscriptionTelemetryLogTests : IDisposable
{
    private readonly string _dir;

    public TranscriptionTelemetryLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-telemetry-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TranscriptionTelemetryRecord SampleRecord(DateTime ts) => new()
    {
        TimestampUtc = ts,
        TurnId = "turn123",
        Outcome = "ok",
        Mode = "devthrottle",
        TranscriptionModel = "gpt-4o-transcribe",
        CleanupModel = "o4-mini",
        AudioBytes = 123456,
        TranscriptionMs = 210,
        CleanupMs = 1,
        Corrected = true,
        CleanupApplied = true,
        ChangedWordCount = 1,
        Changes = new[] { new TelemetryEdit { Find = "Akmeflow", Replace = "acmeflow" } },
        CharCount = 40,
        WordCount = 7,
        RawText = "check the Akmeflow dashboard for me now",
        CleanedText = "check the acmeflow dashboard for me now",
    };

    [Fact]
    public void Record_WritesOneJsonLine_WithAllFields()
    {
        var log = new TranscriptionTelemetryLog(_dir);
        var ts = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        log.Record(SampleRecord(ts));

        var file = log.FileFor(ts);
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("turn123", root.GetProperty("turnId").GetString());
        Assert.Equal("ok", root.GetProperty("outcome").GetString());
        Assert.Equal("gpt-4o-transcribe", root.GetProperty("transcriptionModel").GetString());
        Assert.Equal(210, root.GetProperty("transcriptionMs").GetInt64());
        Assert.Equal(7, root.GetProperty("wordCount").GetInt32());
        Assert.Equal("check the Akmeflow dashboard for me now", root.GetProperty("rawText").GetString());
        Assert.Equal("acmeflow", root.GetProperty("changes")[0].GetProperty("replace").GetString());
    }

    [Fact]
    public void Record_AppendsMultipleTurns_ToSameDailyFile()
    {
        var log = new TranscriptionTelemetryLog(_dir);
        var ts = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        log.Record(SampleRecord(ts));
        log.Record(SampleRecord(ts));
        Assert.Equal(2, File.ReadAllLines(log.FileFor(ts)).Length);
    }

    [Fact]
    public void Record_TextDisabled_OmitsTranscriptButKeepsTiming()
    {
        var log = new TranscriptionTelemetryLog(_dir) { TextEnabled = false };
        var ts = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        log.Record(SampleRecord(ts));

        using var doc = JsonDocument.Parse(File.ReadAllLines(log.FileFor(ts))[0]);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("rawText", out _));
        Assert.False(root.TryGetProperty("cleanedText", out _));
        Assert.Equal(210, root.GetProperty("transcriptionMs").GetInt64()); // timing still present
    }

    [Fact]
    public void Record_NeverThrows_OnUnwritableDirectory()
    {
        // A path under an existing FILE cannot be created as a directory; Record must swallow it.
        var filePath = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(filePath, "x");
        var log = new TranscriptionTelemetryLog(Path.Combine(filePath, "sub"));
        var ex = Record.Exception(() => log.Record(SampleRecord(DateTime.UtcNow)));
        Assert.Null(ex);
    }
}
