using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The durable work-history store contract (issue #2194): a row exists from first sight and survives
/// a "restart" (a fresh database handle over the same file), endings are stamped once and ruled by
/// the Gateway, the silence rule concludes "interrupted" and presence reopens it, summaries seal or
/// generate with the sealed account winning, and reads are tenant-scoped. Runs over the real EF
/// store on a throwaway SQLite file, exactly as the Gateway runs it locally.
/// </summary>
public sealed class SessionHistoryStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionHistoryStore NewStore(ITenantContext? tenant = null)
        => new(_harness.Open(tenant));

    private static SessionDto Session(string id = "s1", string? name = "Fix the parser",
        string repoPath = @"D:\repos\devthrottle", string repoName = "thefrederiksen/devthrottle",
        DateTime? createdAt = null, string activityState = "Working", string status = "Running",
        bool crashed = false, string? mission = null, string? role = null) => new()
    {
        SessionId = id,
        Name = name,
        Number = 123,
        RepoPath = repoPath,
        RepoName = repoName,
        Agent = "ClaudeCode",
        CurrentModel = "claude-fable-5",
        MachineName = "SOREN_NORTH",
        CreatedAt = createdAt ?? DateTime.UtcNow.AddHours(-1),
        LastActivityAt = DateTime.UtcNow.AddMinutes(-1),
        ActivityState = activityState,
        Status = status,
        Crashed = crashed,
        MissionName = mission,
        ExplicitRole = role,
    };

    [Fact]
    public void Supervision_facts_land_and_unknown_never_erases_them()
    {
        // internal#625 phase 4: the agent turn count and the cumulative idle clock ride the pushed
        // session onto the history row. The cases that are silent when wrong: an older Director's
        // null must not erase a known value, and a Director restart (whose counters start again at
        // zero) must not lower the run's high-water mark.
        var store = NewStore();
        var now = DateTime.UtcNow;

        var measured = Session();
        measured.TurnCount = 14;
        measured.CumulativeIdleSeconds = 2520;
        store.UpsertLive("dir-1", measured, now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);

        // An older Director pushes the same session with no supervision facts: nothing is erased.
        var silent = Session();
        silent.TurnCount = null;
        silent.CumulativeIdleSeconds = null;
        store.UpsertLive("dir-1", silent, now.AddMinutes(6));

        row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);

        // A restarted Director reports lower counters: the high-water mark stands.
        var restarted = Session();
        restarted.TurnCount = 2;
        restarted.CumulativeIdleSeconds = 30;
        store.UpsertLive("dir-1", restarted, now.AddMinutes(12));

        row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(14, row.AgentTurnCount);
        Assert.Equal(2520, row.IdleSeconds);
    }

    [Fact]
    public void First_sight_creates_an_open_row_carrying_the_director_measured_start()
    {
        var store = NewStore();
        var created = new DateTime(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        store.UpsertLive("dir-1", Session(createdAt: created), now);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Null(row.EndingKind);
        Assert.Equal(SessionHistoryFold.ToneLive, row.EndingTone);
        // The start is the Director's measured CreatedAt, persisted - never a Gateway guess.
        Assert.Equal(created, row.StartedAtUtc);
        Assert.Equal(now, row.LastSeenUtc);
        Assert.Equal("thefrederiksen/devthrottle", row.RepoName);
        Assert.Equal("claude-fable-5", row.Model);
        // No mission and no prompt yet: the description floor is name + repository, never empty.
        Assert.Equal("Fix the parser in thefrederiksen/devthrottle", row.DescriptionLine);
    }

    [Fact]
    public void The_record_survives_a_gateway_restart()
    {
        var now = DateTime.UtcNow;
        NewStore().UpsertLive("dir-1", Session(), now);

        // A second database handle over the SAME file is the harness's restart simulation.
        var reopened = NewStore();
        var row = Assert.Single(reopened.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal("s1", row.SessionId);
        Assert.Null(row.EndingKind);
    }

    [Fact]
    public void A_farewell_ending_is_stamped_once_and_the_first_ruling_sticks()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);

        store.RecordEnding("s1", SessionHistoryEndings.Closed, crashed: false, now);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, now.AddSeconds(5));

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionHistoryEndings.Closed, row.EndingKind);
        Assert.Equal("Closed", row.EndingLabel);
        Assert.Equal("neutral", row.EndingTone);
    }

    [Fact]
    public void An_ending_for_a_session_never_seen_alive_records_nothing()
    {
        var store = NewStore();
        store.RecordEnding("ghost", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow);
        Assert.Empty(store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void Silence_past_the_threshold_concludes_interrupted_with_last_seen_as_the_end()
    {
        var store = NewStore();
        var lastSeen = DateTime.UtcNow.AddMinutes(-30);
        store.UpsertLive("dir-1", Session(id: "silent"), lastSeen);
        store.UpsertLive("dir-1", Session(id: "fresh"), DateTime.UtcNow);

        var ruled = store.ConcludeInterrupted(DateTime.UtcNow - TimeSpan.FromMinutes(15));

        Assert.Equal(1, ruled);
        var rows = store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var silent = rows.Single(r => r.SessionId == "silent");
        Assert.Equal(SessionHistoryEndings.Interrupted, silent.EndingKind);
        // The end time is the last observation - the honest stand-in, and the label says so.
        Assert.Equal(silent.LastSeenUtc, silent.EndedAtUtc);
        Assert.Contains("last seen", silent.EndingLabel);
        Assert.Equal("attention", silent.EndingTone);
        Assert.Null(rows.Single(r => r.SessionId == "fresh").EndingKind);
    }

    [Fact]
    public void A_session_that_reappears_reopens_its_interrupted_row()
    {
        var store = NewStore();
        store.UpsertLive("dir-1", Session(), DateTime.UtcNow.AddMinutes(-30));
        store.ConcludeInterrupted(DateTime.UtcNow - TimeSpan.FromMinutes(15));

        store.UpsertLive("dir-1", Session(), DateTime.UtcNow);

        var row = Assert.Single(store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.Null(row.EndingKind);
        Assert.Null(row.EndedAtUtc);
        Assert.Equal(SessionHistoryFold.ToneLive, row.EndingTone);
    }

    [Fact]
    public void Director_stopped_stamps_only_that_directors_open_rows()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(id: "a"), now);
        store.UpsertLive("dir-1", Session(id: "b"), now);
        store.UpsertLive("dir-2", Session(id: "other"), now);
        store.RecordEnding("a", SessionHistoryEndings.Finished, crashed: false, now); // already ruled

        var stamped = store.MarkDirectorStopped("dir-1", now.AddSeconds(1));

        Assert.Equal(1, stamped);
        var rows = store.ReadRange(now.AddDays(-1), now.AddDays(1));
        Assert.Equal(SessionHistoryEndings.Finished, rows.Single(r => r.SessionId == "a").EndingKind);
        Assert.Equal(SessionHistoryEndings.DirectorStopped, rows.Single(r => r.SessionId == "b").EndingKind);
        Assert.Null(rows.Single(r => r.SessionId == "other").EndingKind);
    }

    [Fact]
    public void A_session_absent_from_the_authoritative_snapshot_is_ruled_closed()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(id: "kept"), now);
        store.UpsertLive("dir-1", Session(id: "gone"), now);

        var closed = store.CloseAbsentSessions("dir-1",
            new HashSet<string>(StringComparer.Ordinal) { "kept" }, now.AddSeconds(1));

        Assert.Equal(1, closed);
        var rows = store.ReadRange(now.AddDays(-1), now.AddDays(1));
        Assert.Null(rows.Single(r => r.SessionId == "kept").EndingKind);
        Assert.Equal(SessionHistoryEndings.Closed, rows.Single(r => r.SessionId == "gone").EndingKind);
    }

    [Fact]
    public void A_sealed_summary_wins_and_is_never_overwritten_by_the_generator()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);

        var sealedOk = store.SealSummary("s1", new SealSessionSummaryRequest
        {
            Summary = "Built the History page and proved the API.",
            WhatWasBuilt = new[] { "History page" },
            LeftUnverified = new[] { "kill-survival proof" },
            Branches = new[] { "feat/2194-work-history" },
        });
        store.StoreGeneratedSummary("s1", SessionHistorySummaryKinds.Generated, isPartial: true,
            "A generated account that must not win.", null, null, null, null, null, DateTime.UtcNow);

        Assert.True(sealedOk);
        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionHistorySummaryKinds.Sealed, row.SummaryKind);
        Assert.False(row.SummaryIsPartial);
        Assert.Equal("Built the History page and proved the API.", row.SummaryText);
        Assert.Equal(new[] { "History page" }, row.WhatWasBuilt);
        Assert.Equal(new[] { "feat/2194-work-history" }, row.Branches);
    }

    [Fact]
    public void Sealing_an_unknown_session_reports_not_found()
    {
        Assert.False(NewStore().SealSummary("ghost", new SealSessionSummaryRequest { Summary = "x" }));
    }

    [Fact]
    public void Repeated_summary_failures_mark_the_summary_unavailable_at_the_cap()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);
        store.RecordEnding("s1", SessionHistoryEndings.Interrupted, crashed: false, now);

        for (var i = 0; i < SessionHistoryStore.MaxSummaryAttempts; i++)
        {
            Assert.Single(store.PendingSummaries(now.AddMinutes(5), 10));
            store.NoteSummaryFailure("s1", DateTime.UtcNow);
        }

        Assert.Empty(store.PendingSummaries(now.AddMinutes(5), 10));
        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(SessionHistorySummaryKinds.Unavailable, row.SummaryKind);
    }

    [Fact]
    public void The_range_read_includes_overlapping_sessions_and_excludes_the_rest()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        // Started well before the window but last seen inside it: overlaps.
        store.UpsertLive("dir-1", Session(id: "spanning", createdAt: now.AddDays(-10)), now.AddDays(-1));
        // Entirely before the window.
        store.UpsertLive("dir-1", Session(id: "ancient", createdAt: now.AddDays(-20)), now.AddDays(-15));

        var rows = store.ReadRange(now.AddDays(-3), now);

        Assert.Equal("spanning", Assert.Single(rows).SessionId);
    }

    [Fact]
    public void Two_tenants_never_see_each_others_history()
    {
        var alpha = NewStore(new FixedTenantContext(new TenantId("alpha")));
        var beta = NewStore(new FixedTenantContext(new TenantId("beta")));
        var now = DateTime.UtcNow;

        alpha.UpsertLive("dir-1", Session(), now);

        Assert.Single(alpha.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Empty(beta.ReadRange(now.AddDays(-1), now.AddDays(1)));
        // The same caller-supplied session id is beta's own row, not a collision with alpha's.
        beta.UpsertLive("dir-9", Session(name: "Beta's own work"), now);
        Assert.Equal("Fix the parser",
            Assert.Single(alpha.ReadRange(now.AddDays(-1), now.AddDays(1))).SessionName);
    }

    [Fact]
    public void The_first_prompt_is_set_once_and_becomes_the_description()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);

        store.SetFirstPrompt("s1", "Read the mission brief and build the History page", DateTime.UtcNow);
        store.SetFirstPrompt("s1", "A later prompt that must not overwrite", DateTime.UtcNow);

        var row = Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
        Assert.Equal("Read the mission brief and build the History page", row.DescriptionLine);
    }

    [Fact]
    public void Rollups_roundtrip_by_repo_and_day()
    {
        var store = NewStore();
        var day = DateTime.UtcNow.Date;
        store.SaveRollup("thefrederiksen/devthrottle", day, "Worked on work history.", "hash1", 0, DateTime.UtcNow, DateTime.UtcNow);
        store.SaveRollup("thefrederiksen/devthrottle", day, "Worked on work history, updated.", "hash2", 0, DateTime.UtcNow, DateTime.UtcNow);

        var rollup = Assert.Single(store.ReadRollups(day, day));
        Assert.Equal("Worked on work history, updated.", rollup.SummaryText);
        Assert.Equal("hash2", rollup.InputHash);
    }

    // ---- The prompt-delete erasure (mission work item W2) ------------------------------------------

    /// <summary>
    /// The fact the whole work item exists for. Before this, <c>DELETE /prompts</c> removed the prompt
    /// files and left the derived copy sitting in <c>session_history</c> for another ninety days, served
    /// on the History page - while the endpoint's own documentation said the delete WAS the erasure.
    ///
    /// Asserted against the raw COLUMNS rather than the folded record on purpose: the fold hides
    /// <c>FirstPromptLine</c> behind a description that falls back to the repository name, so a
    /// DTO-level assertion would pass over a row whose prompt line was still in the database.
    ///
    /// The summary here is a GENERATED one - written by the summariser out of the prompt log - because
    /// that is what makes it prompt-derived. A sealed summary is the session's own farewell and survives;
    /// that is a different fact, beside this one.
    /// </summary>
    [Fact]
    public void Erasing_clears_all_seven_prompt_derived_columns_resets_the_metadata_and_drops_the_rollups()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);
        store.SetFirstPrompt("s1", "Erase the derived copy when the member deletes their prompts", DateTime.UtcNow);
        store.StoreGeneratedSummary("s1", SessionHistorySummaryKinds.Generated, isPartial: true,
            "Built the erasure and proved it.",
            new[] { "the erasure" }, new[] { "nothing yet" }, new[] { "prompt-delete-erases" },
            new[] { "2378" }, new[] { "abc1234" }, DateTime.UtcNow);
        store.NoteSummaryFailure("s1", DateTime.UtcNow);
        store.SaveRollup("thefrederiksen/devthrottle", now.Date, "A paragraph made of those summaries.", "hash1", 0, now, DateTime.UtcNow);

        // Every one of the ten fields carries something first - an erasure test over empty columns
        // proves only that null stayed null.
        using (var ctx = db.CreateContext())
        {
            var before = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
            Assert.NotNull(before.FirstPromptLine);
            Assert.NotNull(before.SummaryText);
            Assert.NotNull(before.WhatWasBuiltJson);
            Assert.NotNull(before.LeftUnverifiedJson);
            Assert.NotNull(before.BranchesJson);
            Assert.NotNull(before.PullRequestsJson);
            Assert.NotNull(before.CommitsJson);
            Assert.NotNull(before.SummaryKind);
            Assert.Equal(1, before.SummaryAttempts);
        }

        var erased = store.ErasePromptDerived();

        Assert.Equal(1, erased.SessionRows);
        Assert.Equal(1, erased.RollupRows);
        using (var ctx = db.CreateContext())
        {
            var after = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
            Assert.Null(after.FirstPromptLine);
            Assert.Null(after.SummaryText);
            Assert.Null(after.WhatWasBuiltJson);
            Assert.Null(after.LeftUnverifiedJson);
            Assert.Null(after.BranchesJson);
            Assert.Null(after.PullRequestsJson);
            Assert.Null(after.CommitsJson);
            // Reset, not left claiming a summary that no longer exists.
            Assert.Null(after.SummaryKind);
            Assert.False(after.SummaryIsPartial);
            Assert.Equal(0, after.SummaryAttempts);
            // The row itself SURVIVES - this is an erasure of prompt-derived content, not of the
            // member's session record. The repository and the timing are not prompt material.
            Assert.Equal(@"D:\repos\devthrottle", after.RepoPath);
        }
        Assert.Empty(store.ReadRollups(now.Date.AddDays(-1), now.Date.AddDays(1)));
    }

    /// <summary>
    /// The erasure runs under the ambient tenant's query filter, so it can only reach the erasing
    /// account's rows. Without this, one member exercising their delete would quietly erase every
    /// other account's history - the worst possible way for this fix to be wrong.
    /// </summary>
    [Fact]
    public void The_erasure_reaches_only_the_erasing_tenants_rows()
    {
        var alphaDb = _harness.Open(new FixedTenantContext(new TenantId("alpha")));
        var betaDb = _harness.Open(new FixedTenantContext(new TenantId("beta")));
        var alpha = new SessionHistoryStore(alphaDb);
        var beta = new SessionHistoryStore(betaDb);
        var now = DateTime.UtcNow;

        alpha.UpsertLive("dir-1", Session(id: "alpha-1", name: null), now);
        alpha.SetFirstPrompt("alpha-1", "alpha's own words", DateTime.UtcNow);
        alpha.SaveRollup("alpha/repo", now.Date, "alpha's paragraph", "h", 0, now, DateTime.UtcNow);
        beta.UpsertLive("dir-9", Session(id: "beta-1", name: null), now);
        beta.SetFirstPrompt("beta-1", "beta's own words", DateTime.UtcNow);
        beta.SaveRollup("beta/repo", now.Date, "beta's paragraph", "h", 0, now, DateTime.UtcNow);

        var erased = alpha.ErasePromptDerived();

        // Beta's data FIRST, before the counts: the counts are the endpoint's report, and a report is
        // not the thing it reports on. An assertion order that trips on the number first would hide
        // which rows actually went, which is the only question this test exists to answer.
        using (var ctx = betaDb.CreateContext())
        {
            Assert.Equal("beta's own words",
                ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "beta-1").FirstPromptLine);
        }
        Assert.Equal("beta's paragraph",
            Assert.Single(beta.ReadRollups(now.Date, now.Date)).SummaryText);
        using (var ctx = alphaDb.CreateContext())
        {
            Assert.Null(ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "alpha-1").FirstPromptLine);
        }
        Assert.Empty(alpha.ReadRollups(now.Date, now.Date));
        Assert.Equal(1, erased.SessionRows);
        Assert.Equal(1, erased.RollupRows);
        // The erasure watermark is one account's fact too. If beta could see alpha's, beta's own
        // summariser would start refusing writes it should make - the guard failing in the quiet
        // direction, where nothing errors and content simply stops appearing.
        Assert.NotNull(alpha.PromptErasureWatermarkUtc());
        Assert.Null(beta.PromptErasureWatermarkUtc());
    }

    /// <summary>
    /// The counts describe rows that actually carried something. A count of "every row I matched"
    /// would report work on a second delete that erased nothing, and a member reading "erased 400
    /// rows" twice has been told something false about their own data.
    /// </summary>
    [Fact]
    public void A_second_erasure_honestly_reports_nothing_to_do()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);
        store.SetFirstPrompt("s1", "the only prompt", DateTime.UtcNow);

        Assert.Equal(1, store.ErasePromptDerived().SessionRows);

        var second = store.ErasePromptDerived();
        Assert.Equal(0, second.SessionRows);
        Assert.Equal(0, second.RollupRows);
        // The row is still there, and still readable as a session record.
        Assert.Single(store.ReadRange(now.AddDays(-1), now.AddDays(1)));
    }

    /// <summary>
    /// A SEALED summary is erased with everything else, and this REVERSES what this file asserted for two
    /// rounds. The exemption rested on a verification that turned out to answer the wrong question: it
    /// established that <c>SummaryKind</c> tracks which WRITER wrote the row, when what was needed was that
    /// the CONTENT is not prompt-derived. The seal route takes caller-supplied prose with no material time
    /// and no provenance of any kind, so a seal composed from the member's own prompts is accepted exactly
    /// like any other - and the exemption would then have preserved it through every later delete.
    ///
    /// The fields are seeded through the real seal route so this is the actual shape a sealed row has.
    /// </summary>
    [Fact]
    public void A_sealed_summary_is_erased_with_the_rest()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);
        store.SetFirstPrompt("s1", "the member's own prompt", now.AddMinutes(-1));
        Assert.True(store.SealSummary("s1", new SealSessionSummaryRequest
        {
            Summary = "A farewell that may well be made of the member's prompts.",
            WhatWasBuilt = new[] { "the erasure" },
            LeftUnverified = new[] { "nothing" },
            Branches = new[] { "prompt-delete-erases" },
            PullRequests = new[] { "2379" },
            Commits = new[] { "abc1234" },
        }));

        var erased = store.ErasePromptDerived();

        using var ctx = db.CreateContext();
        var after = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(after.FirstPromptLine);
        Assert.Null(after.SummaryText);
        Assert.Null(after.WhatWasBuiltJson);
        Assert.Null(after.LeftUnverifiedJson);
        Assert.Null(after.BranchesJson);
        Assert.Null(after.PullRequestsJson);
        Assert.Null(after.CommitsJson);
        Assert.Null(after.SummaryKind);
        Assert.Equal(1, erased.SessionRows);
        // The session RECORD survives - this erases prompt-derived content, not the member's history.
        Assert.Equal(@"D:\repos\devthrottle", after.RepoPath);
    }

    /// <summary>
    /// A seal is judged by the SESSION'S OWN START, and no caller supplies a time. A session that began
    /// before an erasure can never seal afterwards - its farewell would be written from the conversation
    /// the member has just erased - and a session that began after it seals normally.
    ///
    /// The previous version of this fact passed a backdated material time straight to the store. That value
    /// was cooperative: the real endpoint could only ever supply the ARRIVAL moment, which has the opposite
    /// sign, so the test passed while every seal was admitted after every delete. There is now no parameter
    /// to get wrong, and the endpoint path is proved over HTTP in TheDeletionBoundaryGuardsTests.
    /// </summary>
    [Fact]
    public void A_seal_is_refused_for_a_session_the_gateway_first_saw_before_the_erasure()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var before = DateTime.UtcNow.AddHours(-2);
        store.UpsertLive("dir-1", Session(id: "older", name: null, createdAt: before), before);

        store.ErasePromptDerived();

        Assert.False(store.SealSummary("older", new SealSessionSummaryRequest
        {
            Summary = "Composed from the conversation the member just erased.",
        }));
        using (var ctx = db.CreateContext())
            Assert.Null(ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "older").SummaryText);

        // Control: a session that started AFTER the erasure seals normally.
        store.UpsertLive("dir-1", Session(id: "newer", name: null, createdAt: DateTime.UtcNow.AddSeconds(1)),
            DateTime.UtcNow);
        Assert.True(store.SealSummary("newer", new SealSessionSummaryRequest { Summary = "After." }));
    }

    /// <summary>
    /// The seal exemption must not spare a row that merely FAILED to be summarised. Such a row has no
    /// summary kind at all, and a predicate written as "kind is not sealed" is exactly where a null column
    /// silently drops out of the match - the row would keep its counters and its partial flag while the
    /// endpoint reported it erased.
    /// </summary>
    [Fact]
    public void A_row_that_never_got_a_summary_kind_is_still_reset()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(name: null), now);
        store.StoreGeneratedSummary("s1", SessionHistorySummaryKinds.Generated, isPartial: true,
            "A generated account, read out of the prompt log.", null, null, null, null, null, DateTime.UtcNow);
        // Put the row back in the state that has no kind, with a live attempt counter behind it.
        using (var seed = db.CreateContext())
        {
            var row = seed.SessionHistory.Single(e => e.SessionId == "s1");
            row.SummaryKind = null;
            row.SummaryAttempts = 2;
            seed.SaveChanges();
        }

        var erased = store.ErasePromptDerived();

        using var ctx = db.CreateContext();
        var after = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(after.SummaryText);
        Assert.False(after.SummaryIsPartial);
        Assert.Equal(0, after.SummaryAttempts);
        Assert.Equal(1, erased.SessionRows);
    }

    /// <summary>
    /// The reported count is taken by a separate query from the update that does the work, so it can drift
    /// from it silently. This pins it: two rows carry something, one carries nothing, and the row carrying
    /// BOTH a prompt line and a summary is counted ONCE rather than twice.
    /// </summary>
    [Fact]
    public void The_reported_count_is_rows_changed_counted_once_each()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;

        // Carries both - one row, one count.
        store.UpsertLive("dir-1", Session(id: "both", name: null), now);
        store.SetFirstPrompt("both", "a prompt", now.AddMinutes(-1));
        store.StoreGeneratedSummary("both", SessionHistorySummaryKinds.Generated, isPartial: false,
            "a generated summary", null, null, null, null, null, DateTime.UtcNow);
        // Carries only a prompt line.
        store.UpsertLive("dir-1", Session(id: "prompt-only", name: null), now);
        store.SetFirstPrompt("prompt-only", "another prompt", now.AddMinutes(-1));
        // Carries nothing erasable at all: not counted.
        store.UpsertLive("dir-1", Session(id: "bare", name: null), now);

        var erased = store.ErasePromptDerived();

        Assert.Equal(2, erased.SessionRows);
        using var ctx = db.CreateContext();
        Assert.Equal(3, ctx.SessionHistory.AsNoTracking().Count());
    }

    [Fact]
    public void Retention_prunes_only_ended_rows()
    {
        var store = NewStore();
        var old = DateTime.UtcNow.AddDays(-100);
        store.UpsertLive("dir-1", Session(id: "old-ended", createdAt: old), old);
        store.RecordEnding("old-ended", SessionHistoryEndings.Finished, crashed: false, old);
        // An open row past the cutoff is the interrupted ruling's job, never retention's.
        store.UpsertLive("dir-1", Session(id: "old-open", createdAt: old), old);

        var deleted = store.PurgeOlderThan(DateTime.UtcNow.AddDays(-90));

        Assert.Equal(1, deleted);
        var rows = store.ReadRange(old.AddDays(-1), DateTime.UtcNow);
        Assert.Equal("old-open", Assert.Single(rows).SessionId);
    }
}
