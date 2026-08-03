using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="AutoDismissSweeper"/> (issue #1200): selecting which auto-dismiss sessions to
/// close (only auto-dismiss + verdict "done" + settled at a turn-end) and issuing the close over the stream
/// via the "kill" verb - never twice for the same session, and only when a stream is actually connected.
/// </summary>
public sealed class AutoDismissSweeperTests
{
    private static SessionDto Session(
        string id, bool autoDismiss = true, string? verdict = "done",
        string activity = "WaitingForInput", string status = "Running") => new()
    {
        SessionId = id,
        AutoDismiss = autoDismiss,
        DismissVerdict = verdict,
        ActivityState = activity,
        Status = status,
    };

    [Fact]
    public void SelectDismissable_PicksAutoDismissDoneSettled()
    {
        var input = new[] { ("d1", Session("s1")) };

        var picks = AutoDismissSweeper.SelectDismissable(input);

        Assert.Single(picks);
        Assert.Equal("s1", picks[0].Session.SessionId);
        Assert.Equal("d1", picks[0].DirectorId);
    }

    [Fact]
    public void SelectDismissable_SkipsNonAutoDismiss()
    {
        var input = new[] { ("d1", Session("s1", autoDismiss: false)) };
        Assert.Empty(AutoDismissSweeper.SelectDismissable(input));
    }

    [Fact]
    public void SelectDismissable_SkipsNeedsHumanAndNoVerdict()
    {
        var input = new[]
        {
            ("d1", Session("needs", verdict: "needs-human")),
            ("d1", Session("none", verdict: null)),
        };
        Assert.Empty(AutoDismissSweeper.SelectDismissable(input));
    }

    [Fact]
    public void SelectDismissable_SkipsMidTurnOrPermissionOrExited()
    {
        var input = new[]
        {
            ("d1", Session("working", activity: "Working")),
            ("d1", Session("perm", activity: "WaitingForPerm")),
            ("d1", Session("exited", status: "Exited")),
        };
        Assert.Empty(AutoDismissSweeper.SelectDismissable(input));
    }

    [Fact]
    public void SelectDismissable_HonorsAlreadyClosingFilter()
    {
        var input = new[] { ("d1", Session("s1")) };
        Assert.Empty(AutoDismissSweeper.SelectDismissable(input, alreadyClosing: sid => sid == "s1"));
    }

    [Fact]
    public async Task SweepAsync_ClosesQualifyingSession_OverStream_WithKillVerb()
    {
        var sent = new List<(string DirectorId, DirectorCommand Command)>();
        DirectorCommandResult? Ok(string dir, DirectorCommand cmd)
        {
            sent.Add((dir, cmd));
            return DirectorCommandResult.Success();
        }

        var sweeper = new AutoDismissSweeper(
            () => new[] { ("d1", Session("s1")) },
            (dir, cmd, ct) => Task.FromResult(Ok(dir, cmd)));

        var closed = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        Assert.Single(sent);
        Assert.Equal("kill", sent[0].Command.Verb);
        Assert.Equal("s1", sent[0].Command.SessionId);
        Assert.Equal("d1", sent[0].DirectorId);
    }

    [Fact]
    public async Task SweepAsync_DoesNotCloseTwice_WhenSessionLingersOneExtraSweep()
    {
        var sendCount = 0;
        var snapshot = new[] { ("d1", Session("s1")) };
        var sweeper = new AutoDismissSweeper(
            () => snapshot,
            (dir, cmd, ct) => { sendCount++; return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success()); });

        await sweeper.SweepAsync(CancellationToken.None);
        // Same session still present next sweep (its removal tombstone has not arrived yet): must NOT re-kill.
        await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task SweepAsync_NoStream_ReturnsNull_RetriesNextSweep()
    {
        var sendCount = 0;
        var sweeper = new AutoDismissSweeper(
            () => new[] { ("d1", Session("s1")) },
            (dir, cmd, ct) => { sendCount++; return Task.FromResult<DirectorCommandResult?>(null); });

        var closed1 = await sweeper.SweepAsync(CancellationToken.None);
        var closed2 = await sweeper.SweepAsync(CancellationToken.None);

        // A null result means "no active stream" - nothing closed, and the session is retried (not marked done).
        Assert.Equal(0, closed1);
        Assert.Equal(0, closed2);
        Assert.Equal(2, sendCount);
    }
}
