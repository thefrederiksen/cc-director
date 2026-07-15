using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 5, the two legs that make the fix STAY fixed: the Gateway never trusts a role it did not compute
/// (the ingest discard), and stamping a role down does not make the observer chase its own tail (the change
/// gate).
///
/// Neither of these is polish. The ingest discard is the difference between "the Gateway is the only thing
/// that computes the role" being TRUE and being true BY ACCIDENT - see PushedSessionStore.DiscardInboundRole.
/// The change gate is the only thing standing between the observer and an infinite echo, because the stamp
/// we send down comes straight back up on the Director's next delta.
///
/// Design: docs/new_architecture/session-state.html, defect 5.
/// </summary>
public sealed class FleetRoleObserverTests
{
    private DateTime _now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(30);

    private static SessionDto Session(string id, string state = "WaitingForInput") => new()
    {
        SessionId = id,
        ActivityState = state,
    };

    // ===================== THE INGEST DISCARD =====================

    /// <summary>
    /// A Director's pushed session carries the role the Gateway last stamped onto it (the Director echoes it
    /// back up through its one mapper). That echo must DIE at the boundary. If it survives, a Director's
    /// stale cache is an authority on a fact only the Gateway can compute - which is the defect class, not a
    /// convenience.
    /// </summary>
    [Fact]
    public void ApplyDelta_DiscardsAnyRoleTheDirectorEchoedBack()
    {
        var store = new PushedSessionStore(() => _now);
        store.RegisterConnection("dir-A", "conn-1");

        var echoed = Session("s1");
        echoed.SessionRole = SessionRoles.Worker; // what a Director sends back up after we stamped it

        Assert.True(store.ApplyDelta("dir-A", "conn-1", 1, echoed));

        var fresh = store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Null(Assert.Single(fresh!).SessionRole);
    }

    /// <summary>The same on the reconnect path - a snapshot is where a whole roster of stale echoes arrives
    /// at once, so it is the one that matters most.</summary>
    [Fact]
    public void ApplySnapshot_DiscardsAnyRoleTheDirectorEchoedBack()
    {
        var store = new PushedSessionStore(() => _now);
        store.RegisterConnection("dir-A", "conn-1");

        var a = Session("s1");
        a.SessionRole = SessionRoles.Worker;
        var b = Session("s2");
        b.SessionRole = SessionRoles.Manager;

        Assert.True(store.ApplySnapshot("dir-A", "conn-1", 1, new[] { a, b }));

        var fresh = store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.All(fresh!, s => Assert.Null(s.SessionRole));
    }

    // ===================== THE CHANGE GATE / LOOP SAFETY =====================

    private sealed class RecordingSender
    {
        public readonly List<(string DirectorId, string SessionId, string Role)> Sent = new();

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<SetResolvedRoleRequest>(
                command.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            lock (Sent) Sent.Add((directorId, command.SessionId, payload!.Role));
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
        }
    }

    /// <summary>
    /// THE LOOP. Stamping a role down makes the Director report it back up on its next delta, which lands
    /// straight back in Observe. Without the change gate that is role -> delta -> observe -> role, forever.
    /// Sweeping repeatedly with an unchanged fleet must send exactly ONCE.
    /// </summary>
    [Fact]
    public void RepeatedSweeps_WithAnUnchangedFleet_SendTheRoleOnlyOnce()
    {
        var sender = new RecordingSender();
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetRoleObserver(() => fleet, sender.SendAsync);

        // The first sweep delivers. The next four are the echo arriving back, four times over.
        for (var i = 0; i < 5; i++)
            observer.Sweep();

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("dir-A", sent.DirectorId);
        Assert.Equal("s1", sent.SessionId);
        Assert.Equal(SessionRoles.Standalone, sent.Role);
    }

    /// <summary>
    /// The gate must not be a mute button: when the role genuinely CHANGES the stamp goes down again. A gate
    /// that suppressed real changes would trade the echo for a stale desktop, which is the same defect.
    /// </summary>
    [Fact]
    public void WhenARoleActuallyChanges_TheNewRoleIsSentDown()
    {
        var sender = new RecordingSender();
        var controller = Session("ctl", "Working");
        var worker = Session("wrk");
        worker.IsControlled = true;
        worker.ControllerSessionId = "ctl";
        var fleet = new List<(string, SessionDto)> { ("dir-A", controller), ("dir-A", worker) };
        var observer = new FleetRoleObserver(() => fleet, sender.SendAsync);

        observer.Sweep();
        Assert.Equal(SessionRoles.Worker, sender.Sent.Single(x => x.SessionId == "wrk").Role);

        // The controller dies -> the worker is no longer a Worker -> its red must surface, so the DESKTOP
        // has to be told. This is the escape hatch travelling over the wire.
        controller.ActivityState = "Exited";
        observer.Sweep();

        var workerSends = sender.Sent.Where(x => x.SessionId == "wrk").ToList();
        Assert.Equal(2, workerSends.Count);
        Assert.Equal(SessionRoles.Standalone, workerSends[^1].Role);
    }

    /// <summary>
    /// A Director with no tunnel gets no stamp, and the observer must NOT record that as delivered - the
    /// role has to be re-sent the moment it reconnects. Recording a failed send as done would leave that
    /// desktop permanently wrong, and silently.
    /// </summary>
    [Fact]
    public void WhenTheDirectorHasNoStream_TheStampIsRetriedOnTheNextSweep()
    {
        var attempts = 0;
        Task<DirectorCommandResult?> NoStream(string d, DirectorCommand c, CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult<DirectorCommandResult?>(null); // null = no active stream
        }

        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetRoleObserver(() => fleet, NoStream);

        observer.Sweep();
        observer.Sweep();

        Assert.Equal(2, attempts);
    }
}
