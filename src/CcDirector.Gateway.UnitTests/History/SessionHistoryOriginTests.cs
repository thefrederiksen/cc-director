using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
// CcDirector.Core.Sessions has a SessionHistoryStore of its own (the Director's workspace history,
// unrelated to the Gateway's durable record). Name the one under test outright.
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The birth facts on the durable work-history row (devthrottle_internal issue #982): who asked for a
/// session, from where, and which session asked. Runs over the real EF store on a throwaway SQLite
/// file, exactly as the Gateway runs it.
///
/// Every test here defends a way the record could be quietly wrong rather than loudly broken - a later
/// push blanking a known origin, a lineage edge lost across a Gateway restart, a birth-count that
/// double-counts the long-lived sessions.
/// </summary>
public sealed class SessionHistoryOriginTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionHistoryStore NewStore(ITenantContext? tenant = null)
        => new(_harness.Open(tenant));

    private static SessionDto Session(string id, DateTime startedAt,
        string? originKind = null, string? originSurface = null, string? parentSessionId = null) => new()
    {
        SessionId = id,
        Name = "A session",
        RepoPath = @"D:\repos\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = startedAt,
        ActivityState = "Working",
        Status = "Running",
        OriginKind = originKind,
        OriginSurface = originSurface,
        ParentSessionId = parentSessionId,
    };

    [Fact]
    public void The_birth_facts_land_on_the_row_and_reach_the_wire()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        var parent = Guid.NewGuid().ToString();

        store.UpsertLive("dir-1", Session("child", now.AddMinutes(-5),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, parent), now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionOriginKinds.Agent, row.OriginKind);
        Assert.Equal(SessionOriginSurfaces.Cli, row.OriginSurface);
        Assert.Equal(parent, row.ParentSessionId);
    }

    [Fact]
    public void A_later_push_that_lost_the_origin_never_blanks_it()
    {
        // The failure this exists for: a Director rolling back to an older build, or a second Director
        // adopting the session, pushes a DTO with no origin fields at all. The row must keep what it
        // knows. Birth facts cannot be re-derived - once blanked, the answer is gone for good, and
        // nothing about the running session would ever reveal that it used to be known.
        var store = NewStore();
        var now = DateTime.UtcNow;
        var parent = Guid.NewGuid().ToString();

        store.UpsertLive("dir-1", Session("s1", now.AddMinutes(-5),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, parent), now);
        store.UpsertLive("dir-1", Session("s1", now.AddMinutes(-5)), now.AddMinutes(1));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionOriginKinds.Agent, row.OriginKind);
        Assert.Equal(SessionOriginSurfaces.Cli, row.OriginSurface);
        Assert.Equal(parent, row.ParentSessionId);
    }

    [Fact]
    public void A_row_created_without_an_origin_is_filled_in_by_a_later_push_that_has_one()
    {
        // The mirror case, and the reason the guard is on the STORED value being empty rather than on
        // first sight: a session first seen through an old Director creates an origin-less row, and
        // the moment a current Director reports the same session the facts should land.
        var store = NewStore();
        var now = DateTime.UtcNow;

        store.UpsertLive("dir-old", Session("s1", now.AddMinutes(-5)), now);
        store.UpsertLive("dir-new", Session("s1", now.AddMinutes(-5),
            SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now.AddMinutes(1));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionOriginKinds.Human, row.OriginKind);
        Assert.Equal(SessionOriginSurfaces.Desktop, row.OriginSurface);
    }

    [Fact]
    public void A_recorded_unknown_is_a_value_and_is_not_overwritten_later()
    {
        // "unknown" is an answer, not an absence. If a later push could replace it, the recorded origin
        // would depend on which push happened to arrive - the same session reading differently between
        // two runs of the same fleet.
        var store = NewStore();
        var now = DateTime.UtcNow;

        store.UpsertLive("dir-1", Session("s1", now.AddMinutes(-5),
            SessionOriginKinds.Unknown, SessionOriginSurfaces.Unknown), now);
        store.UpsertLive("dir-1", Session("s1", now.AddMinutes(-5),
            SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now.AddMinutes(1));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionOriginKinds.Unknown, row.OriginKind);
    }

    [Fact]
    public void The_lineage_edge_survives_a_gateway_restart()
    {
        var now = DateTime.UtcNow;
        var parent = Guid.NewGuid().ToString();
        NewStore().UpsertLive("dir-1", Session("child", now.AddMinutes(-5),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, parent), now);

        // A fresh store over the same file - the Gateway coming back up.
        var reopened = NewStore();

        var row = Assert.Single(reopened.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(parent, row.ParentSessionId);
    }

    [Fact]
    public void Origin_totals_count_by_birth_and_keep_not_recorded_apart_from_unknown()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;

        store.UpsertLive("dir-1", Session("a", now.AddHours(-1),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        store.UpsertLive("dir-1", Session("b", now.AddHours(-1),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        store.UpsertLive("dir-1", Session("c", now.AddHours(-1),
            SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now);
        store.UpsertLive("dir-1", Session("d", now.AddHours(-1),
            SessionOriginKinds.Schedule, SessionOriginSurfaces.Cron), now);
        store.UpsertLive("dir-1", Session("e", now.AddHours(-1),
            SessionOriginKinds.Unknown, SessionOriginSurfaces.Unknown), now);
        // A row from before the fields existed: no origin on the wire at all.
        store.UpsertLive("dir-1", Session("f", now.AddHours(-1)), now);

        var totals = store.OriginTotals(now.AddDays(-1), now.AddDays(1));

        Assert.Equal(6, totals.Sessions);
        Assert.Equal(2, totals.ByKind[SessionOriginKinds.Agent]);
        Assert.Equal(1, totals.ByKind[SessionOriginKinds.Human]);
        Assert.Equal(1, totals.ByKind[SessionOriginKinds.Schedule]);
        // The two that look alike and are not: one was asked and said nothing, one was never asked.
        Assert.Equal(1, totals.ByKind[SessionOriginKinds.Unknown]);
        Assert.Equal(1, totals.ByKind[SessionHistoryStore.NotRecorded]);
        Assert.Equal(2, totals.BySurface[SessionOriginSurfaces.Cli]);
        Assert.Equal(2, totals.WithParent);
        // Where the RECORD begins, which is the oldest birth found and NOT the floor of the query -
        // the number a caller has to quote beside any "all time" share, because retention prunes from
        // the front and the fields only began being written the day they shipped.
        Assert.NotNull(totals.EarliestStartUtc);
        Assert.True(Math.Abs((totals.EarliestStartUtc!.Value - now.AddHours(-1)).TotalSeconds) < 1);
    }

    [Fact]
    public void Origin_totals_over_an_empty_window_report_no_record_rather_than_a_date()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;

        var totals = store.OriginTotals(now.AddDays(-1), now);

        Assert.Equal(0, totals.Sessions);
        Assert.Null(totals.EarliestStartUtc);
        Assert.Empty(totals.ByKind);
    }

    [Fact]
    public void Origin_totals_count_a_session_in_the_window_it_was_born_in_not_every_window_it_survived()
    {
        // The bias this prevents: ReadRange returns any session whose life TOUCHED the window, which is
        // right for "what was I working on Tuesday" and wrong here. Agent-started sessions tend to be
        // short and human-started ones long, so counting by overlap would credit the long human session
        // to every window it ran through and understate the agent share - by more, the wider the window.
        var store = NewStore();
        var now = DateTime.UtcNow;

        // Born ten days ago, still being observed today.
        store.UpsertLive("dir-1", Session("long-runner", now.AddDays(-10),
            SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now);
        // Born an hour ago.
        store.UpsertLive("dir-1", Session("fresh", now.AddHours(-1),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);

        var lastWeek = store.OriginTotals(now.AddDays(-7), now);

        Assert.Equal(1, lastWeek.Sessions);
        Assert.Equal(1, lastWeek.ByKind[SessionOriginKinds.Agent]);
        Assert.False(lastWeek.ByKind.ContainsKey(SessionOriginKinds.Human));
        // It is still in the record, just not in this week's births.
        Assert.Equal(2, store.OriginTotals(now.AddDays(-30), now).Sessions);
    }

    [Fact]
    public void The_cost_and_interruption_facts_land_and_only_ever_move_forward()
    {
        // The rest of what issue #982 asked for per session: what it cost, how much was said to it,
        // how many times it interrupted the owner, and how full its context got. All on the SAME
        // high-water-mark rule as the counters that were already here, for the same reason - a
        // Director restart begins its counters again at zero, and the run's record must not follow.
        var store = NewStore();
        var now = DateTime.UtcNow;
        var mission = Guid.NewGuid();

        var measured = Session("s1", now.AddHours(-1));
        measured.MissionId = mission;
        measured.WaitingStretchCount = 7;
        measured.InputStats = new InputStatsDto
        {
            Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = 3, Characters = 900 } },
            AgentDrivenTurns = 2,
            AgentDrivenCharacters = 100,
        };
        measured.TokenTotals = new TokenTotalsDto
        {
            InputTokens = 5_000,
            OutputTokens = 1_200,
            CacheReadTokens = 40_000,
            CacheCreationTokens = 900,
            ContextTokens = 88_000,
        };
        store.UpsertLive("dir-1", measured, now);

        // The Director came back: every counter starts again at zero, and the context gauge - which
        // DROPS on a compaction anyway - reads lower.
        var restarted = Session("s1", now.AddHours(-1));
        restarted.WaitingStretchCount = 1;
        restarted.InputStats = new InputStatsDto
        {
            Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = 1, Characters = 10 } },
        };
        restarted.TokenTotals = new TokenTotalsDto
        {
            InputTokens = 50, OutputTokens = 10, CacheReadTokens = 0, CacheCreationTokens = 0, ContextTokens = 12_000,
        };
        store.UpsertLive("dir-1", restarted, now.AddMinutes(1));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(mission, row.MissionId);
        Assert.Equal(7, row.WaitingStretchCount);
        Assert.Equal(1000, row.InputCharacterCount);   // 900 typed + 100 agent-driven
        Assert.Equal(5_000, row.InputTokens);
        Assert.Equal(1_200, row.OutputTokens);
        Assert.Equal(40_000, row.CacheReadTokens);
        Assert.Equal(900, row.CacheCreationTokens);
        // The gauge keeps its PEAK. Summing a gauge would produce a number with no unit; taking the
        // latest would report a compacted session as one that never filled up.
        Assert.Equal(88_000, row.PeakContextTokens);
    }

    [Fact]
    public void A_director_that_reports_none_of_the_cost_facts_leaves_them_unknown_not_zero()
    {
        // An agent whose driver reports no usage (everything but Claude today) must read "we do not
        // know what this cost", never "this cost nothing" - the second is a claim, and a wrong one.
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session("s1", now.AddHours(-1)), now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.PeakContextTokens);
        Assert.Null(row.WaitingStretchCount);
        Assert.Null(row.InputCharacterCount);
        Assert.Null(row.MissionId);
    }

    [Fact]
    public void Origin_totals_are_tenant_scoped()
    {
        var alpha = NewStore(new FixedTenantContext(new TenantId("alpha")));
        var beta = NewStore(new FixedTenantContext(new TenantId("beta")));
        var now = DateTime.UtcNow;

        alpha.UpsertLive("dir-1", Session("a", now.AddHours(-1),
            SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);

        Assert.Equal(1, alpha.OriginTotals(now.AddDays(-1), now.AddDays(1)).Sessions);
        Assert.Equal(0, beta.OriginTotals(now.AddDays(-1), now.AddDays(1)).Sessions);
    }
}
