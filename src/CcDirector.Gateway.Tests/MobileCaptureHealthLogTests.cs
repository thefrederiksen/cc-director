using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The shared mobile capture-health hook (issue #863) feeds BOTH the Voice-mode complete path and the durable
/// Terminal/Chat Send complete path. Issue #509 retired the host-global <c>dictation/sessions/*.jsonl</c> flat
/// file it used to append to (the transcript is now stored per-tenant by the transcription service), so this
/// hook is now a log-only diagnostic. These guard its two remaining behaviours at the unit level, without
/// touching the storage root: it is a no-op when the client did not measure (recordedMs null), and it never
/// throws when it did. The "the flat file is never written" retirement regression lives in
/// <see cref="MobileCaptureHealthLogHostedTests"/> (which owns the storage-root flipping).
/// </summary>
public sealed class MobileCaptureHealthLogTests
{
    [Fact]
    public void Persist_NoClientRecordedMs_IsANoOpAndDoesNotThrow()
    {
        // The client did not opt in (or its on-device decode failed): there is nothing to record, and a
        // fabricated deficit would be worse than none. It must return quietly.
        var ex = Record.Exception(() => MobileCaptureHealthLog.Persist(
            uploadId: "u1", source: "mobile-send", recordedMs: null, decodedSeconds: 12.0,
            sourceBytes: 4096, audioBytes: 8192, cleaned: "hello"));

        Assert.Null(ex);
    }

    [Fact]
    public void Persist_WithMeasurements_DoesNotThrow()
    {
        // With measurements the hook logs the deficit and returns. It never fails a turn - a capture-health
        // problem must not surface as a transcription error.
        var ex = Record.Exception(() => MobileCaptureHealthLog.Persist(
            uploadId: "upload-123", source: "mobile-send", recordedMs: 120_000, decodedSeconds: 108.5,
            sourceBytes: 1_500_000, audioBytes: 3_840_000, cleaned: "the whole sentence"));

        Assert.Null(ex);
    }

    // ---- SurfaceOr: which shell the measurement is filed under -----------------------------------
    // Every browser dictation used to be logged as "mobile" / "mobile-send" no matter which shell
    // recorded it, so a Cockpit dictation on a desktop was indistinguishable from a phone dictation
    // and per-surface audio loss simply could not be read out of the log. These pin that a reported
    // surface is honoured, that an absent one falls back to the literal the path always wrote (so an
    // older client and an already-queued clip keep working), and that the value - which arrives from
    // a browser and is written into a log line - can never forge a log entry.

    [Fact]
    public void SurfaceOr_UsesTheReportedSurface()
    {
        Assert.Equal("cockpit-send", MobileCaptureHealthLog.SurfaceOr("cockpit-send", "mobile-send"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SurfaceOr_FallsBackWhenTheClientReportedNothing(string? reported)
    {
        // An older client, or a clip queued on device before the tag existed. It must keep its
        // historical tag rather than becoming unlabelled, so the existing history stays comparable.
        Assert.Equal("mobile-send", MobileCaptureHealthLog.SurfaceOr(reported, "mobile-send"));
    }

    [Fact]
    public void SurfaceOr_StripsAnythingThatCouldForgeALogEntry()
    {
        // Newlines would append a second, fabricated line to the log; spaces would forge a field break.
        var cleaned = MobileCaptureHealthLog.SurfaceOr("cockpit\n[MobileCaptureHealth] fake 0: recordedMs=0", "mobile");

        Assert.DoesNotContain("\n", cleaned);
        Assert.DoesNotContain(" ", cleaned);
        Assert.StartsWith("cockpit", cleaned);
    }

    [Fact]
    public void SurfaceOr_BoundsTheLength()
    {
        Assert.True(MobileCaptureHealthLog.SurfaceOr(new string('x', 500), "mobile").Length <= 40);
    }

    [Fact]
    public void SurfaceOr_FallsBackWhenNothingUsableSurvivesCleaning()
    {
        // A value made entirely of rejected characters is not a surface name; it must not become "".
        Assert.Equal("mobile", MobileCaptureHealthLog.SurfaceOr("!!! @@@ ###", "mobile"));
    }

    [Fact]
    public void Persist_MissingDecodedSeconds_DoesNotThrow()
    {
        // A recorded clip whose decode duration did not come through still logs (as a total deficit) and never
        // throws or fabricates a negative value.
        var ex = Record.Exception(() => MobileCaptureHealthLog.Persist(
            uploadId: "u2", source: "mobile", recordedMs: 5_000, decodedSeconds: null,
            sourceBytes: null, audioBytes: 1024, cleaned: null));

        Assert.Null(ex);
    }
}
