using System;
using System.Collections.Generic;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The durable server-owned dictation complete handler (issue #1006) clears the orange
/// "Transcribing..." mark on EVERY terminal outcome, not just the happy paths (issue #1048). The
/// clear is driven by <see cref="DictationOutcome.IsIncomplete"/>: the wrapper clears the mark unless
/// the outcome is an incomplete upload (more chunks still coming). These tests lock that contract, the
/// exact thing that was broken - previously an error return (no key, empty audio, a transcription
/// failure, out-of-credits, the session gone, or a submit refused because the session was parked on a
/// modal) left the mark set and leaned on the 20-minute MaxAge backstop, wedging the session orange.
/// </summary>
public sealed class GatewayDictationEndpointTests
{
    [Fact]
    public void Submitted_ClearsTheMark()
    {
        // A submitted turn is terminal and must clear the mark.
        var outcome = DictationOutcome.Submitted(submitted: true, movedOn: false, transcript: "hello");

        Assert.False(outcome.IsIncomplete, "a submitted turn must clear the transcribing mark");
    }

    [Fact]
    public void MovedOn_ClearsTheMark()
    {
        var outcome = DictationOutcome.Submitted(submitted: false, movedOn: true, transcript: "stale");

        Assert.False(outcome.IsIncomplete, "a dropped stale clip must clear the transcribing mark");
    }

    [Fact]
    public void Error_ClearsTheMark()
    {
        // The regression: an error outcome (e.g. the submit was refused because the session is parked on
        // a modal like "Approve this batch?") must ALSO clear the mark, not leave it stuck for 20 minutes.
        var outcome = DictationOutcome.Error(502, "submit to session failed");

        Assert.False(outcome.IsIncomplete, "an error outcome must clear the transcribing mark");
    }

    [Fact]
    public void OutOfCredits_ClearsTheMark()
    {
        var outcome = DictationOutcome.OutOfCredits(default(HostedAiState));

        Assert.False(outcome.IsIncomplete, "an out-of-credits outcome must clear the transcribing mark");
    }

    [Fact]
    public void Incomplete_KeepsTheMark()
    {
        // The ONLY outcome that keeps the mark: chunks are still arriving and the client completes again
        // on the same upload id, so the session is genuinely still transcribing.
        var outcome = DictationOutcome.Incomplete(new List<int> { 2, 5 });

        Assert.True(outcome.IsIncomplete, "an incomplete upload must keep the transcribing mark");
    }
}
