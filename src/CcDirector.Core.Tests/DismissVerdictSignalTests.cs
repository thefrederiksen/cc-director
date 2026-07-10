using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DismissVerdictSignal"/> (issue #1200): parsing the CC-DISMISS verdict block
/// an auto-dismiss run prints as its final message. The conservative rule is central - no complete block,
/// or an unrecognized verdict, yields null so a session is NEVER auto-closed without an explicit "done".
/// </summary>
public sealed class DismissVerdictSignalTests
{
    [Fact]
    public void ParseLatest_DoneBlock_ParsesVerdictAndReason()
    {
        var text = "Here is the digest. Nothing needs you today.\n\nCC-DISMISS\nverdict: done\nreason: quiet day, 0 to act\n";

        var sig = DismissVerdictSignal.ParseLatest(text);

        Assert.NotNull(sig);
        Assert.Equal(DismissVerdict.Done, sig!.Verdict);
        Assert.Equal("done", sig.Wire);
        Assert.Equal("quiet day, 0 to act", sig.Reason);
    }

    [Fact]
    public void ParseLatest_NeedsHumanBlock_ParsesVerdict()
    {
        var sig = DismissVerdictSignal.ParseLatest("CC-DISMISS\nverdict: needs-human\nreason: one PR needs a reply\n");

        Assert.NotNull(sig);
        Assert.Equal(DismissVerdict.NeedsHuman, sig!.Verdict);
        Assert.Equal("needs-human", sig.Wire);
    }

    [Fact]
    public void ParseLatest_NoBlock_ReturnsNull()
    {
        Assert.Null(DismissVerdictSignal.ParseLatest("just a normal message, no sentinel here"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseLatest_EmptyText_ReturnsNull(string? text)
    {
        Assert.Null(DismissVerdictSignal.ParseLatest(text));
    }

    [Fact]
    public void ParseLatest_UnrecognizedVerdict_ReturnsNull()
    {
        // A block with a bad verdict is incomplete -> null (never guess a close).
        Assert.Null(DismissVerdictSignal.ParseLatest("CC-DISMISS\nverdict: maybe\nreason: unsure\n"));
    }

    [Fact]
    public void ParseLatest_MarkerWithoutVerdict_ReturnsNull()
    {
        Assert.Null(DismissVerdictSignal.ParseLatest("CC-DISMISS\nreason: forgot the verdict\n"));
    }

    [Fact]
    public void ParseLatest_LastBlockWins_NeedsHumanSupersedesEarlierDone()
    {
        // A later turn's verdict must win, so a done that a subsequent turn overrides never closes the session.
        var text =
            "CC-DISMISS\nverdict: done\nreason: first pass\n" +
            "...later the run found something...\n" +
            "CC-DISMISS\nverdict: needs-human\nreason: found a report to file\n";

        var sig = DismissVerdictSignal.ParseLatest(text);

        Assert.NotNull(sig);
        Assert.Equal(DismissVerdict.NeedsHuman, sig!.Verdict);
    }

    [Fact]
    public void ParseLatest_FencedBlock_IsAccepted()
    {
        // The agent may wrap the block in a markdown code fence; the marker and fields tolerate backticks.
        var text = "```\nCC-DISMISS\nverdict: done\nreason: fenced\n```";

        var sig = DismissVerdictSignal.ParseLatest(text);

        Assert.NotNull(sig);
        Assert.Equal(DismissVerdict.Done, sig!.Verdict);
    }

    [Fact]
    public void ParseLatest_CaseInsensitiveVerdictValue()
    {
        var sig = DismissVerdictSignal.ParseLatest("CC-DISMISS\nverdict: DONE\n");

        Assert.NotNull(sig);
        Assert.Equal(DismissVerdict.Done, sig!.Verdict);
    }
}
