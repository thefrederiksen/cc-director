using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The morning report's assembly (issue #2119). The claims under test are the ones an EMAIL depends on:
///
///  - THE HONESTY RULE. A stat whose backing store holds nothing for this tenant is ABSENT, not zero. A
///    zero-filled stat would put a measurement the Gateway never made into a person's inbox. The pair of
///    tests <c>*_is_absent_when_the_store_is_empty</c> / <c>*_is_zero_when_the_store_has_rows_outside_the_window</c>
///    is the whole point: absent and zero are different answers and both must be reachable.
///  - CEIL ROUNDING on money: a report about real dollars never claims less was spent than was.
///  - THE WINDOW BOUNDS: inclusive start, EXCLUSIVE end, so a day's last event is not counted twice.
///  - TENANT ISOLATION: one account's report can never contain another account's rows.
/// </summary>
public sealed class MorningReportBuilderTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");

    /// <summary>Noon UTC on the day the report is built - safely after the reported window closes.</summary>
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The reported day: 23 July 2026 in Toronto = 2026-07-23T04:00Z .. 2026-07-24T04:00Z.</summary>
    private static MorningReportWindow Window() => MorningReportWindow.Resolve("2026-07-23", "America/Toronto");

    public void Dispose() => _h.Dispose();

    /// <summary>A database whose AMBIENT tenant is <paramref name="tenant"/> - what the seeding stores write as.</summary>
    private GatewayDatabase DbAs(TenantId tenant) => _h.Open(new FixedTenantContext(tenant));

    private MorningReportBuilder NewBuilder(GatewayDatabase db, PushedSessionStore? sessions = null) =>
        new(db, sessions, TimeSpan.FromMinutes(5), () => Now);

    // ---- seeding ---------------------------------------------------------------------------------------

    private static void SeedSessionEvent(GatewayDatabase db, TenantId tenant, string sessionId, string state, DateTime occurredUtc)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = state,
            OccurredUtc = occurredUtc,
            RecordedUtc = occurredUtc,
        });
        ctx.SaveChanges();
    }

    private static void SeedRun(GatewayDatabase db, TenantId tenant, string acceptance, DateTime? completedUtc)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.WorkflowRuns.Add(new WorkflowRunEntity
        {
            TenantId = tenant.Value,
            WorkflowId = "mission",
            Name = "a run",
            Status = completedUtc is null ? WorkflowRunStatus.Active : WorkflowRunStatus.Succeeded,
            AcceptanceStatus = acceptance,
            CreatedUtc = completedUtc ?? Now,
            CompletedUtc = completedUtc,
        });
        ctx.SaveChanges();
    }

    private static void SeedSpend(GatewayDatabase db, TenantId tenant, long micros, DateTime txUtc)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.AccountHostedAiSpend.Add(new AccountHostedAiSpendEntity
        {
            TenantId = tenant.Value,
            AmountMicros = micros,
            Kind = "debit",
            TransactionCreatedUtc = txUtc,
            ObservedUtc = txUtc,
        });
        ctx.SaveChanges();
    }

    // ---- the honesty rule: absent is not zero ----------------------------------------------------------

    [Fact]
    public void Every_stat_is_absent_when_this_tenant_has_no_data_at_all()
    {
        var db = DbAs(Alice);
        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // Not 0 - ABSENT. The Gateway has measured nothing, and an email must be able to say so.
        Assert.Null(report.Stats.SessionsRan);
        Assert.Null(report.Stats.WorkDelivered);
        Assert.Null(report.Stats.HostedAiSpendUsd);

        // The attention list is always present, possibly empty: "nothing is waiting on you" IS knowledge.
        Assert.NotNull(report.Attention);
        Assert.Empty(report.Attention);

        // And the coordinates always ride along.
        Assert.Equal("2026-07-23", report.Window.Date);
        Assert.Equal("America/Toronto", report.Window.Tz);
        Assert.Equal(new DateTime(2026, 7, 23, 4, 0, 0, DateTimeKind.Utc), report.Window.StartUtc);
        Assert.Equal(new DateTime(2026, 7, 24, 4, 0, 0, DateTimeKind.Utc), report.Window.EndUtc);
        Assert.Equal("alice@example.com", report.Account);
    }

    [Fact]
    public void A_stat_is_a_measured_zero_when_the_store_has_rows_but_none_in_the_window()
    {
        var db = DbAs(Alice);
        // Rows exist for this tenant, but all of them fall a week before the reported day.
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));
        SeedRun(db, Alice, WorkflowRunAcceptance.Accepted, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));
        SeedSpend(db, Alice, 500_000, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // Present AND zero: the Gateway looked, and the answer is nothing happened that day.
        Assert.Equal(0, report.Stats.SessionsRan);
        Assert.Equal(0, report.Stats.WorkDelivered);
        Assert.Equal(0m, report.Stats.HostedAiSpendUsd);
    }

    // ---- the three headline numbers --------------------------------------------------------------------

    [Fact]
    public void SessionsRan_counts_DISTINCT_sessions_inside_the_window()
    {
        var db = DbAs(Alice);
        var inWindow = new DateTime(2026, 7, 23, 14, 0, 0, DateTimeKind.Utc);

        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Idle, inWindow.AddHours(1));   // same session again
        SeedSessionEvent(db, Alice, "s2", GovernanceEventState.Active, inWindow.AddHours(2));
        SeedSessionEvent(db, Alice, "s3", GovernanceEventState.Active, Window().EndUtc);      // the NEXT day

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // s1 and s2 - s1 counted once despite two transitions, s3 excluded by the exclusive end bound.
        Assert.Equal(2, report.Stats.SessionsRan);
    }

    [Fact]
    public void The_window_start_is_inclusive_and_the_end_is_exclusive()
    {
        var db = DbAs(Alice);
        var w = Window();
        SeedSessionEvent(db, Alice, "at-the-start", GovernanceEventState.Active, w.StartUtc);
        SeedSessionEvent(db, Alice, "at-the-end", GovernanceEventState.Active, w.EndUtc);

        var report = NewBuilder(db).Build("alice@example.com", Alice, w);

        // A day is [start, end). The session at exactly the end instant belongs to the NEXT day's report -
        // counting it in both would double-count every midnight.
        Assert.Equal(1, report.Stats.SessionsRan);
    }

    [Fact]
    public void WorkDelivered_counts_only_ACCEPTED_runs_completed_in_the_window()
    {
        var db = DbAs(Alice);
        var inWindow = new DateTime(2026, 7, 23, 15, 0, 0, DateTimeKind.Utc);

        SeedRun(db, Alice, WorkflowRunAcceptance.Accepted, inWindow);
        SeedRun(db, Alice, WorkflowRunAcceptance.Accepted, inWindow.AddHours(1));
        SeedRun(db, Alice, WorkflowRunAcceptance.Pending, inWindow);    // succeeded but unaccepted
        SeedRun(db, Alice, WorkflowRunAcceptance.Rejected, inWindow);
        SeedRun(db, Alice, WorkflowRunAcceptance.Accepted, null);       // never completed

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        Assert.Equal(2, report.Stats.WorkDelivered);
    }

    [Theory]
    // A fraction of a cent becomes a whole cent - the report never undercounts real money.
    [InlineData(1L, 0.01)]
    [InlineData(9_999L, 0.01)]
    [InlineData(10_000L, 0.01)]
    [InlineData(10_001L, 0.02)]
    [InlineData(1_000_000L, 1.00)]
    [InlineData(1_234_567L, 1.24)]     // 1.234567 -> 1.24, not 1.23
    [InlineData(0L, 0.00)]
    public void Hosted_AI_spend_is_ceil_rounded_to_the_cent(long micros, double expectedUsd)
    {
        Assert.Equal((decimal)expectedUsd, MorningReportBuilder.CeilMicrosToUsd(micros));
    }

    [Fact]
    public void Hosted_AI_spend_sums_the_window_and_rounds_the_TOTAL_up()
    {
        var db = DbAs(Alice);
        var inWindow = new DateTime(2026, 7, 23, 16, 0, 0, DateTimeKind.Utc);
        SeedSpend(db, Alice, 1_234_567, inWindow);
        SeedSpend(db, Alice, 2_345, inWindow.AddMinutes(1));
        SeedSpend(db, Alice, 9_000_000, Window().EndUtc); // next day - excluded

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // 1,236,912 micros = $1.236912 -> $1.24
        Assert.Equal(1.24m, report.Stats.HostedAiSpendUsd);
    }

    // ---- the waiting-session attention rows -------------------------------------------------------------

    [Fact]
    public void A_session_whose_LAST_recorded_state_is_waiting_is_reported_with_a_real_age()
    {
        var db = DbAs(Alice);
        var waitingSince = Now.AddHours(-6);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, waitingSince.AddHours(-1));
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, waitingSince);

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal(MorningAttentionTypes.WaitingSession, item.Type);
        Assert.Equal(waitingSince, item.WaitingSinceUtc);
        Assert.Equal(6.0, item.AgeHours);
        // With no live roster to name it, the row still identifies itself - by session id, never blank.
        Assert.Equal("s1", item.Session);
        Assert.Null(item.Repo);
    }

    [Fact]
    public void A_session_that_came_BACK_is_not_reported_as_waiting()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-6));
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, Now.AddHours(-2));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // The ledger's LAST word wins. A report that read any waiting event ever recorded would nag the
        // owner every morning about work they finished weeks ago.
        Assert.Empty(report.Attention);
    }

    [Fact]
    public void Waiting_rows_are_ordered_longest_wait_first()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "recent", GovernanceEventState.WaitingOnHuman, Now.AddHours(-2));
        SeedSessionEvent(db, Alice, "oldest", GovernanceEventState.WaitingOnHuman, Now.AddHours(-30));
        SeedSessionEvent(db, Alice, "middle", GovernanceEventState.WaitingOnPermission, Now.AddHours(-9));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        var names = report.Attention.Cast<WaitingSessionAttentionDto>().Select(i => i.Session).ToList();
        Assert.Equal(new[] { "oldest", "middle", "recent" }, names);
    }

    [Fact]
    public void A_live_roster_supplies_the_friendly_name_and_the_repository_path()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "Morning report - Developer", RepoPath = "D:/ReposFred/devthrottle", ActivityState = "WaitingForInput" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal("Morning report - Developer", item.Session);
        Assert.Equal("D:/ReposFred/devthrottle", item.Repo);
        // The AGE still comes from the durable ledger, never from the live roster.
        Assert.Equal(3.0, item.AgeHours);
    }

    [Fact]
    public void A_session_the_owner_has_SNOOZED_is_not_this_mornings_problem()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "parked", HoldState = HoldStates.Held },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        // The owner deliberately parked it. Putting it in the 7am email defeats the snooze.
        Assert.Empty(report.Attention);
    }

    [Fact]
    public void A_session_that_has_EXITED_is_not_waiting_on_anybody()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "gone", ActivityState = "Exited" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        Assert.Empty(report.Attention);
    }

    [Fact]
    public void A_session_older_than_the_lookback_horizon_is_not_reported()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "ancient", GovernanceEventState.WaitingOnHuman,
            Now.AddDays(-(MorningReportBuilder.WaitingLookbackDays + 1)));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // The Gateway will not assert the CURRENT state of something it has not heard about in a month.
        Assert.Empty(report.Attention);
    }

    // ---- the hygiene sections are absent until the repo-state feed exists -------------------------------

    [Fact]
    public void No_hygiene_items_are_emitted_while_there_is_no_repo_state_store()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // This is what makes this slice mergeable and shippable BEFORE the snapshot feed lands: no
        // repo-state data means no stale-worktree / unmerged-branch rows at all - not empty ones.
        Assert.DoesNotContain(report.Attention, i => i.Type == MorningAttentionTypes.StaleWorktrees);
        Assert.DoesNotContain(report.Attention, i => i.Type == MorningAttentionTypes.UnmergedBranches);
    }

    // ---- tenant isolation ------------------------------------------------------------------------------

    [Fact]
    public void One_accounts_report_never_contains_another_accounts_rows()
    {
        // Two tenants, ONE database file - exactly the hosted shape.
        var aliceDb = DbAs(Alice);
        var bobDb = DbAs(Bob);
        var inWindow = new DateTime(2026, 7, 23, 15, 0, 0, DateTimeKind.Utc);

        SeedSessionEvent(aliceDb, Alice, "alice-1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(aliceDb, Alice, "alice-2", GovernanceEventState.WaitingOnHuman, Now.AddHours(-4));
        SeedRun(aliceDb, Alice, WorkflowRunAcceptance.Accepted, inWindow);
        SeedSpend(aliceDb, Alice, 1_000_000, inWindow);

        SeedSessionEvent(bobDb, Bob, "bob-1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(bobDb, Bob, "bob-2", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(bobDb, Bob, "bob-3", GovernanceEventState.WaitingOnHuman, Now.AddHours(-40));
        SeedRun(bobDb, Bob, WorkflowRunAcceptance.Accepted, inWindow);
        SeedRun(bobDb, Bob, WorkflowRunAcceptance.Accepted, inWindow);
        SeedSpend(bobDb, Bob, 9_000_000, inWindow);

        // Build BOTH reports through the SAME builder instance, to prove the tenant argument - not some
        // remembered ambient state - is what scopes the read.
        var builder = NewBuilder(aliceDb);
        var aliceReport = builder.Build("alice@example.com", Alice, Window());
        var bobReport = builder.Build("bob@example.com", Bob, Window());

        // Only alice-1 transitioned INSIDE the reported day; alice-2 has been waiting since after it closed,
        // so it is an attention row without being a "ran yesterday" session. The two numbers answer different
        // questions and are deliberately allowed to disagree.
        Assert.Equal(1, aliceReport.Stats.SessionsRan);
        Assert.Equal(1, aliceReport.Stats.WorkDelivered);
        Assert.Equal(1.00m, aliceReport.Stats.HostedAiSpendUsd);
        var aliceWaiting = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(aliceReport.Attention));
        Assert.Equal("alice-2", aliceWaiting.Session);

        Assert.Equal(2, bobReport.Stats.SessionsRan);     // bob-1 + bob-2; bob-3's wait began before the day
        Assert.Equal(2, bobReport.Stats.WorkDelivered);
        Assert.Equal(9.00m, bobReport.Stats.HostedAiSpendUsd);
        var bobWaiting = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(bobReport.Attention));
        Assert.Equal("bob-3", bobWaiting.Session);
    }

    [Fact]
    public void A_tenant_with_no_rows_of_its_own_reports_ABSENT_even_when_another_tenant_has_plenty()
    {
        var aliceDb = DbAs(Alice);
        var inWindow = new DateTime(2026, 7, 23, 15, 0, 0, DateTimeKind.Utc);
        SeedSessionEvent(aliceDb, Alice, "alice-1", GovernanceEventState.Active, inWindow);
        SeedRun(aliceDb, Alice, WorkflowRunAcceptance.Accepted, inWindow);
        SeedSpend(aliceDb, Alice, 1_000_000, inWindow);

        var bobReport = NewBuilder(aliceDb).Build("bob@example.com", Bob, Window());

        // The "does this tenant have any data" probe must itself be tenant-scoped. If it were not, Bob would
        // get a ZERO for every stat - a measurement made entirely out of Alice's rows.
        Assert.Null(bobReport.Stats.SessionsRan);
        Assert.Null(bobReport.Stats.WorkDelivered);
        Assert.Null(bobReport.Stats.HostedAiSpendUsd);
        Assert.Empty(bobReport.Attention);
    }

    [Fact]
    public void A_live_roster_from_another_tenant_never_labels_this_tenants_row()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "shared-id", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        // Bob happens to run a session with the SAME raw id - a collision the tenant partition must absorb.
        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Bob, "dir-bob", "conn-bob");
        Assert.True(sessions.ApplySnapshot(Bob, "dir-bob", "conn-bob", 0, new List<SessionDto>
        {
            new() { SessionId = "shared-id", Name = "BOB'S SECRET PROJECT", RepoPath = "D:/bob/private" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal("shared-id", item.Session);   // the id, NOT Bob's session name
        Assert.Null(item.Repo);                    // and certainly not Bob's repository path
    }

    // ---- argument discipline ---------------------------------------------------------------------------

    [Fact]
    public void An_invalid_tenant_is_refused_rather_than_read_as_something()
    {
        var db = DbAs(Alice);
        Assert.Throws<ArgumentException>(() => NewBuilder(db).Build("a@b.c", default, Window()));
    }
}
