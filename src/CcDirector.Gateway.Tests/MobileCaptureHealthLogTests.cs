using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one shared mobile capture-health record builder (issue #863) feeds BOTH the Voice-mode complete
/// path and the durable Terminal/Chat Send complete path, so these guard its two behaviours: it emits
/// nothing when the client did not measure (recordedMs null), and when it did it maps the recorded
/// wall-clock, decoded duration, source tag, and transcribed byte count onto the record faithfully.
/// </summary>
public sealed class MobileCaptureHealthLogTests
{
    [Fact]
    public void BuildRecord_NoClientRecordedMs_ReturnsNull()
    {
        // The client did not opt in (or its on-device decode failed): there is nothing to persist, and a
        // fabricated deficit would be worse than no record at all.
        var record = MobileCaptureHealthLog.BuildRecord(
            uploadId: "u1", source: "mobile-send", recordedMs: null, decodedSeconds: 12.0,
            sourceBytes: 4096, audioBytes: 8192, cleaned: "hello");

        Assert.Null(record);
    }

    [Fact]
    public void BuildRecord_WithMeasurements_MapsWallClockDecodedSourceAndBytes()
    {
        var record = MobileCaptureHealthLog.BuildRecord(
            uploadId: "upload-123", source: "mobile-send", recordedMs: 120_000, decodedSeconds: 108.5,
            sourceBytes: 1_500_000, audioBytes: 3_840_000, cleaned: "the whole sentence");

        Assert.NotNull(record);
        Assert.Equal("mobile-send", record!.Source);
        Assert.Equal(120_000, record.RecordedWallMs);
        Assert.Equal(108.5, record.DecodedAudioSeconds);
        Assert.Equal(120_000, record.RecordingDurationMs);
        Assert.Equal(3_840_000, record.AudioBytesReceived);
        Assert.Equal("upload-123", record.SessionId);
        Assert.Equal("the whole sentence", record.CleanedTranscript);
    }

    [Fact]
    public void BuildRecord_MissingDecodedSeconds_DefaultsToZeroNotNegative()
    {
        // A recorded clip whose decode duration did not come through reads as zero decoded audio (a total
        // deficit), never a negative or fabricated value.
        var record = MobileCaptureHealthLog.BuildRecord(
            uploadId: "u2", source: "mobile", recordedMs: 5_000, decodedSeconds: null,
            sourceBytes: null, audioBytes: 1024, cleaned: null);

        Assert.NotNull(record);
        Assert.Equal(0, record!.DecodedAudioSeconds);
        Assert.Equal("", record.CleanedTranscript);
    }
}
