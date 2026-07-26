using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The recorder's contract (issue #2194): the write throttle (first sight writes immediately, an
/// unchanged re-push does not, a material change does), and the ending rulings for per-session
/// removes, snapshot reconciliation, and the Director farewell. Over the real store and a throwaway
/// SQLite file - the throttle decisions are what is under test, so the row's LastSeenUtc is the
/// observable.
/// </summary>
public sealed class SessionHistoryRecorderTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (SessionHistoryRecorder Recorder, SessionHistoryStore Store) New()
    {
        var store = new SessionHistoryStore(_harness.Open());
        return (new SessionHistoryRecorder(store), store);
    }

    private static SessionDto Session(string id = "s1", string? name = "Build the thing",
        string activityState = "Working", string status = "Running", bool crashed = false,
        string? dismissVerdict = null) => new()
    {
        SessionId = id,
        Name = name,
        Number = 200,
        RepoPath = @"D:\repos\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = DateTime.UtcNow.AddHours(-2),
        ActivityState = activityState,
        Status = status,
        Crashed = crashed,
        DismissVerdict = dismissVerdict,
    };

    private WorkHistorySessionDto Row(SessionHistoryStore store, string id = "s1")
        => store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))
            .Single(r => r.SessionId == id);

    [Fact]
    public void First_sight_writes_immediately_and_an_unchanged_repush_does_not()
    {
        var (recorder, store) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session());
        var firstSeen = Row(store).LastSeenUtc;

        // The 10-second re-push: same facts, well inside the freshness interval.
        recorder.Observe(TenantId.Local, "dir-1", Session());
        Assert.Equal(firstSeen, Row(store).LastSeenUtc); // no write happened
    }

    [Fact]
    public void A_material_change_writes_through_the_throttle()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(name: "Old name"));

        recorder.Observe(TenantId.Local, "dir-1", Session(name: "Renamed"));

        Assert.Equal("Renamed", Row(store).SessionName);
    }

    [Fact]
    public void An_activity_flip_alone_is_not_material_and_does_not_write()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Working"));
        var firstSeen = Row(store).LastSeenUtc;

        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Idle"));
        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Working"));

        Assert.Equal(firstSeen, Row(store).LastSeenUtc);
    }

    [Fact]
    public void Removal_of_a_running_session_is_ruled_closed()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Working"));

        recorder.ObserveRemoval(TenantId.Local, "dir-1", "s1");

        var row = Row(store);
        Assert.Equal(SessionHistoryEndings.Closed, row.EndingKind);
        Assert.Equal("Closed", row.EndingLabel);
    }

    [Fact]
    public void Removal_after_the_agent_exited_is_ruled_finished()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Exited", status: "Exited"));

        recorder.ObserveRemoval(TenantId.Local, "dir-1", "s1");

        var row = Row(store);
        Assert.Equal(SessionHistoryEndings.Finished, row.EndingKind);
        Assert.Equal("Finished", row.EndingLabel);
    }

    [Fact]
    public void Removal_of_a_crashed_session_keeps_the_finished_kind_with_the_crash_wording()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(activityState: "Exited", status: "Failed", crashed: true));

        recorder.ObserveRemoval(TenantId.Local, "dir-1", "s1");

        var row = Row(store);
        Assert.Equal(SessionHistoryEndings.Finished, row.EndingKind);
        Assert.Equal("Agent exited unexpectedly", row.EndingLabel);
    }

    [Fact]
    public void An_auto_dismissed_done_session_is_ruled_finished()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(dismissVerdict: "done"));

        recorder.ObserveRemoval(TenantId.Local, "dir-1", "s1");

        Assert.Equal(SessionHistoryEndings.Finished, Row(store).EndingKind);
    }

    [Fact]
    public void A_session_missing_from_the_next_snapshot_is_ruled_closed()
    {
        var (recorder, store) = New();
        recorder.ObserveSnapshot(TenantId.Local, "dir-1", new[] { Session(id: "a"), Session(id: "b") });

        recorder.ObserveSnapshot(TenantId.Local, "dir-1", new[] { Session(id: "a") });

        Assert.Null(Row(store, "a").EndingKind);
        Assert.Equal(SessionHistoryEndings.Closed, Row(store, "b").EndingKind);
    }

    [Fact]
    public void The_director_farewell_rules_director_stopped_for_its_open_rows()
    {
        var (recorder, store) = New();
        recorder.ObserveSnapshot(TenantId.Local, "dir-1", new[] { Session(id: "a"), Session(id: "b") });
        recorder.ObserveRemoval(TenantId.Local, "dir-1", "a"); // removed individually first

        recorder.ObserveDirectorStopping(TenantId.Local, "dir-1");

        Assert.Equal(SessionHistoryEndings.Closed, Row(store, "a").EndingKind); // kept its own ruling
        Assert.Equal(SessionHistoryEndings.DirectorStopped, Row(store, "b").EndingKind);
    }

    [Fact]
    public void The_first_user_prompt_becomes_the_description_source()
    {
        var (recorder, store) = New();
        recorder.Observe(TenantId.Local, "dir-1", Session(name: null));

        recorder.ObservePrompts(TenantId.Local, new[]
        {
            new PromptRecord
            {
                TsUtc = DateTime.UtcNow, SessionId = "s1", Role = "user", TimestampFromAgent = true,
                CharCount = 10, WordCount = 3, Text = "  Please   build\nthe History page  ",
            },
        });

        // Whitespace folded to one line, and later prompts never overwrite (store contract).
        Assert.Equal("Please build the History page", Row(store).DescriptionLine);
    }
}
