using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Transcription;

/// <summary>
/// The transient/permanent split (issue #1130) decides whether the durable dictation retry loop keeps
/// auto-retrying a clip or parks it. Getting it wrong either hammers a doomed request or gives up on a
/// recoverable blip - the exact 504 that lost the user's audio - so it is pinned here.
/// </summary>
public sealed class TranscriptionFailedExceptionTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)] // the DevThrottle proxy upstream_timeout that started issue #1130
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(425)]
    public void IsTransient_ServerAndThrottleStatuses_AreRetryable(int status)
    {
        var ex = new TranscriptionFailedException(status, $"Transcription returned {status}");
        Assert.True(ex.IsTransient, $"status {status} should be retryable");
        Assert.Equal(status, ex.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)] // payload too large - retrying the identical body fails identically
    [InlineData(422)]
    public void IsTransient_ClientRequestErrors_AreNotRetryable(int status)
    {
        var ex = new TranscriptionFailedException(status, $"Transcription returned {status}");
        Assert.False(ex.IsTransient, $"status {status} should not be retried");
    }

    [Fact]
    public void TranscriptionFailedException_IsAnInvalidOperationException_SoExistingCatchesStillWork()
    {
        // Callers written before the typed exception catch InvalidOperationException; the new type must
        // still be caught by them.
        var ex = new TranscriptionFailedException(504, "Transcription returned 504");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
