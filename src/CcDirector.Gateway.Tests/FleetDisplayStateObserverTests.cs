using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The display-state push seam: the Gateway stamps each session's folded answer down to its owning Director,
/// and the two legs that make that STAY correct - the change gate (stamping a fold down does not make the
/// observer chase its own echo), and a dropped send being retried rather than recorded as delivered.
///
/// The fold itself is proved elsewhere (SessionOrderingTests); here the fold is a deterministic stub so the
/// tests target the observer's gate and payload, exactly as FleetRoleObserverTests target the role
/// observer's. Design: docs/new_architecture/session-state.html.
/// </summary>
public sealed class FleetDisplayStateObserverTests
{
    private static SessionDto Session(string id, string state = "WaitingForInput") => new()
    {
        SessionId = id,
        ActivityState = state,
    };

    /// <summary>A stub fold: colour/label/triage derived from the raw activity, so a test can change the
    /// answer by changing the session's ActivityState - the same shape the real fold produces.</summary>
    private static void StubFold(List<SessionDto> sessions)
    {
        foreach (var s in sessions)
        {
            var working = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase);
            s.EffectiveColor = working ? "blue" : "red";
            s.StateLabel = working ? "Working" : "Needs you";
            s.TriageBucket = working ? "active" : "needsYou";
        }
    }

    private sealed class RecordingSender
    {
        public readonly List<(string DirectorId, string SessionId, SetDisplayStateRequest Payload)> Sent = new();
        public Func<DirectorCommandResult?> Reply = () => DirectorCommandResult.Success();

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<SetDisplayStateRequest>(
                command.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            lock (Sent) Sent.Add((directorId, command.SessionId, payload!));
            return Task.FromResult(Reply());
        }
    }

    /// <summary>
    /// THE LOOP. Stamping a fold down makes the Director report it back up on its next delta
    /// (ControlEndpoints.Map echoes the cached values), which lands back in Observe. Without the change gate
    /// that is fold -> delta -> observe -> fold, forever. Sweeping an unchanged fleet must send exactly ONCE.
    /// </summary>
    [Fact]
    public void RepeatedSweeps_WithAnUnchangedFleet_SendTheFoldOnlyOnce()
    {
        var sender = new RecordingSender();
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        for (var i = 0; i < 5; i++)
            observer.Sweep();

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("dir-A", sent.DirectorId);
        Assert.Equal("s1", sent.SessionId);
        Assert.Equal("red", sent.Payload.EffectiveColor);
        Assert.Equal("Needs you", sent.Payload.StateLabel);
        Assert.Equal("needsYou", sent.Payload.TriageBucket);
    }

    /// <summary>The gate must not be a mute button: when the fold genuinely CHANGES, the new answer goes down
    /// again. A gate that suppressed real changes would trade the echo for a stale desktop - the same defect.</summary>
    [Fact]
    public void WhenTheFoldChanges_TheNewFoldIsSentDown()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        Assert.Equal("red", sender.Sent.Single().Payload.EffectiveColor);

        // The session starts working -> the fold flips to blue -> the desktop must be told.
        s1.ActivityState = "Working";
        observer.Sweep();

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("blue", sender.Sent[^1].Payload.EffectiveColor);
        Assert.Equal("active", sender.Sent[^1].Payload.TriageBucket);
    }

    /// <summary>A Director with no tunnel gets no stamp, and the observer must NOT record that as delivered -
    /// the fold has to be re-sent the moment it reconnects. Recording a failed send as done would leave that
    /// desktop permanently wrong, and silently.</summary>
    [Fact]
    public void WhenTheDirectorHasNoStream_TheStampIsRetriedOnTheNextSweep()
    {
        var sender = new RecordingSender { Reply = () => null };   // null = no active stream
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        observer.Sweep();

        Assert.Equal(2, sender.Sent.Count);
    }

    /// <summary>
    /// THE ROLLOUT STORM. The normal deploy order ships the Gateway BEFORE the Directors, so for a while an
    /// old Director rejects this brand-new verb with BadRequest. That rejection must be recorded like a
    /// delivery - one send per fold change - not retried every single sweep, or every session that Director
    /// owns generates a rejected command every few seconds until it upgrades.
    /// </summary>
    [Fact]
    public void WhenADirectorRejectsTheVerb_ItIsNotReSentEverySweep()
    {
        var sender = new RecordingSender
        {
            Reply = () => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "unknown verb 'set-display-state'"),
        };
        var fleet = new List<(string, SessionDto)> { ("dir-old", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        for (var i = 0; i < 5; i++)
            observer.Sweep();

        // Recorded on the first rejection, so the unchanged fold is not re-sent to the old Director.
        Assert.Single(sender.Sent);
    }

    /// <summary>A session that leaves the fleet must drop out of the change gate, so if it ever returns its
    /// fold is stamped fresh rather than suppressed by a stale gate entry.</summary>
    [Fact]
    public void ASessionThatLeavesTheFleet_IsStampedAgainIfItReturns()
    {
        var sender = new RecordingSender();
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        Assert.Single(sender.Sent);

        fleet.Clear();
        observer.Sweep();                       // s1 is gone - the gate entry must be pruned

        fleet.Add(("dir-A", Session("s1")));
        observer.Sweep();                       // ...so its return is a fresh stamp, not a suppressed echo

        Assert.Equal(2, sender.Sent.Count);
    }
}
