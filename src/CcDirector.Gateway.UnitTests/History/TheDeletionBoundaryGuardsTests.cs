using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The guards at the BOUNDARY between two operations rather than inside one.
/// The pattern is worth naming, because it caught the same mechanism three times: each fix put a
/// comparison inside a single statement, and each remaining hole was in the gap between that statement and
/// a DIFFERENT one - the database erase and the file delete, the roll-up insert and its compensation, the
/// seal's arrival and the material it was made from.
///
/// A guard that is atomic in isolation is not atomic in a sequence.
///
/// THE CLASS NAME USED TO SAY THESE RACES ARE "CLOSED". They are not, and clause 2 of the DELETE RULE
/// (in PromptEndpoints) says so: the cross-process file append and the roll-up insert are deliberately
/// contained rather than defeated. What is proved here is what each guard DOES, one fact at a time - and
/// a test name is a claim like any other sentence in this product.
///
/// A SIXTH FACT USED TO LIVE HERE and is now in <c>CcDirector.Gateway.Tests</c>, as
/// <c>TheSealEndpointRefusesAPreErasureSessionTests</c>: it drives the seal refusal over real HTTP, so it
/// binds a socket and belongs behind the machine-wide lock. It is named here rather than left to be
/// noticed, because a guard list that quietly drops one of its guards reads as complete.
/// </summary>
public sealed class TheDeletionBoundaryGuardsTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _promptDir =
        Path.Combine(Path.GetTempPath(), "gw-boundary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { if (Directory.Exists(_promptDir)) Directory.Delete(_promptDir, recursive: true); } catch { /* best effort */ }
    }

    private const string TheMembersOwnWords =
        "Draft the redundancy letters for the Bristol office and cost the notice periods";

    private static SessionDto Session(string id = "s1", DateTime? created = null) => new()
    {
        SessionId = id,
        Name = null,
        RepoPath = @"D:\ReposFred\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "TestAgent",
        MachineName = "SOREN_NORTH",
        CreatedAt = created ?? DateTime.UtcNow.AddHours(-2),
        LastActivityAt = DateTime.UtcNow.AddMinutes(-5),
        ActivityState = "Working",
        Status = "Running",
    };

    private static PromptRecord Record(DateTime tsUtc, string text) => new()
    {
        TsUtc = tsUtc,
        Machine = "SOREN_NORTH",
        SessionId = "s1",
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "TestAgent",
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = 10,
        Text = text,
    };

    private sealed class EchoBrain : IAgentBrain
    {
        public string? LastPrompt { get; private set; }
        public string? SessionId => "echo";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(new AskResult
            {
                Text = """
                {"summary":"Built from whatever transcript was supplied.","what_was_built":["a summary"],
                 "left_unverified":[],"branches":[],"pull_requests":[],"commits":[]}
                """,
            });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    /// <summary>
    /// FINDING 1. The endpoint erases the database and deletes the FILES as two operations, so there is a
    /// window in which the watermark is stamped, the row is cleared - and therefore pending again - and the
    /// pre-delete prompt file is still on disk. A summariser starting inside that window reads the old
    /// records and stamps an honestly recent read time, so the old mechanism accepted its summary and the
    /// member's erased words came back AFTER the delete request had returned success.
    ///
    /// The fix judges the write by the age of the OLDEST RECORD the summary was made from, not by when the
    /// summariser happened to read it. This drives the real summariser inside exactly that window.
    /// </summary>
    [Fact]
    public async Task A_summariser_reading_the_still_present_file_after_the_stamp_does_not_write_its_summary()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        var started = DateTime.UtcNow.AddHours(-2);
        log.Append(TenantId.Local, Enumerable.Range(0, 12)
            .Select(i => Record(started.AddMinutes(i), TheMembersOwnWords + " " + new string('x', 100))).ToList());
        store.UpsertLive("dir-1", Session(created: started), started);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow.AddMinutes(-5));

        // THE WINDOW: the database half of the delete has run; the files have NOT been deleted yet.
        store.ErasePromptDerived();
        Assert.NotEmpty(log.ReadAll(TenantId.Local));

        var brain = new EchoBrain();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));
        await summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);

        // The model WAS asked - the material was there to read, which is what makes this a real window and
        // not a vacuous pass - and the write was still refused.
        Assert.NotNull(brain.LastPrompt);
        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(row.SummaryText);
        Assert.Null(row.WhatWasBuiltJson);
    }

    /// <summary>
    /// The control: the same summariser, the same window, but material the member sent AFTER the erasure.
    /// It must still be summarised - otherwise the fix above is "never summarise after a delete", which
    /// would pass that fact and quietly break the feature.
    /// </summary>
    [Fact]
    public async Task Material_sent_after_the_erasure_is_still_summarised_in_the_same_window()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        store.UpsertLive("dir-1", Session(created: DateTime.UtcNow.AddHours(-2)), DateTime.UtcNow.AddHours(-2));
        store.ErasePromptDerived();

        log.Append(TenantId.Local, Enumerable.Range(0, 12)
            .Select(i => Record(DateTime.UtcNow.AddSeconds(1 + i), "Work after the delete " + new string('y', 100))).ToList());
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow.AddMinutes(-5));

        var brain = new EchoBrain();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));
        await summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);

        using var ctx = _harness.Open().CreateContext();
        Assert.Equal(SessionHistorySummaryKinds.Generated,
            ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").SummaryKind);
    }

    /// <summary>
    /// FINDING 2. The roll-up insert cannot be one conditional statement, so a paragraph computed before an
    /// erasure can be inserted after it and the compensating delete can be interrupted - a process replaced
    /// mid-write is one of the topologies this whole mechanism exists to survive. This seeds exactly that
    /// orphan directly, skipping the compensation entirely, and asserts it is NEVER SERVED.
    ///
    /// The row surviving at rest is not the claim; the claim is that no read can reach it, so the retention
    /// prune and the next erasure are the only things that ever touch it again.
    /// </summary>
    [Fact]
    public void A_rollup_orphaned_by_an_interrupted_compensation_is_never_served()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var day = DateTime.UtcNow.Date;
        var materialReadAtUtc = DateTime.UtcNow.AddMinutes(-1);

        store.ErasePromptDerived();

        // The state a stopped process leaves behind: inserted, saved, never compensated.
        using (var seed = db.CreateContext())
        {
            seed.SessionHistoryRollups.Add(new SessionHistoryRollupEntity
            {
                TenantId = seed.ActiveTenant!,
                RepoKey = "thefrederiksen/devthrottle",
                DayUtc = day,
                SummaryText = "A paragraph made of the member's erased prompts.",
                InputHash = "hash",
                Attempts = 0,
                ComputedAtUtc = DateTime.UtcNow,
                MaterialReadAtUtc = materialReadAtUtc,
            });
            seed.SaveChanges();
        }

        // It is in the table...
        using (var raw = db.CreateContext())
            Assert.Single(raw.SessionHistoryRollups.AsNoTracking().ToList());

        // ...and it is unreachable.
        Assert.Empty(store.ReadRollups(day, day));

        // ...and the NEXT erasure removes it from the table, which is what makes the retention bounded
        // rather than permanent. The name says "never served"; this is the half that says "and not kept".
        store.ErasePromptDerived();
        using (var raw = db.CreateContext())
            Assert.Empty(raw.SessionHistoryRollups.AsNoTracking().ToList());

        // Control: a paragraph whose material is newer than the erasure IS served.
        store.SaveRollup("thefrederiksen/devthrottle", day, "A later paragraph.", "hash2", 0,
            DateTime.UtcNow, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("A later paragraph.", Assert.Single(store.ReadRollups(day, day)).SummaryText);
    }

    /// <summary>
    /// The round-four correction, and the lesson under it: removing the PARAMETER was not the same as
    /// removing the caller's control. Admission moved to the row's <c>StartedAtUtc</c> - which reads like a
    /// server fact and is not one. It is the DIRECTOR'S measured start, pushed over the wire, so a Director
    /// reporting a start in the future would have had a pre-erasure session admitted.
    ///
    /// Admission now uses the first moment THIS GATEWAY saw the session, which we stamp with our own clock
    /// and never move. This fact drives exactly the attack: a session we saw long before the erasure,
    /// re-pushed claiming it started a minute from now.
    /// </summary>
    [Fact]
    public void A_director_claiming_a_future_start_does_not_get_a_pre_erasure_session_sealed()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var weSawItLongAgo = DateTime.UtcNow.AddHours(-3);
        store.UpsertLive("dir-1", Session(created: weSawItLongAgo), weSawItLongAgo);

        store.ErasePromptDerived();

        // The Director re-pushes the same session claiming it started in the future.
        store.UpsertLive("dir-1", Session(created: DateTime.UtcNow.AddMinutes(1)), DateTime.UtcNow);

        Assert.False(store.SealSummary("s1", new SealSessionSummaryRequest
        {
            Summary = "A farewell for a session we saw before the member's delete.",
        }));
        using var ctx = db.CreateContext();
        Assert.Null(ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").SummaryText);
    }

    /// <summary>
    /// FINDING 4. The failure writer was an unguarded read-modify-save, and it looked harmless because it
    /// carries no prompt prose. It is not harmless: it puts the attempt count and possibly an "unavailable"
    /// kind back on a row the erasure cleared, and because PendingSummaries only offers rows with no kind
    /// and attempts under the cap, it can leave the row PERMANENTLY unable to become pending again. The
    /// erasure's self-healing property - reset, re-summarise from an empty log, settle honestly as "none" -
    /// depended on that row still being reachable.
    /// </summary>
    [Fact]
    public void A_failed_summarisation_from_before_the_delete_does_not_re_arm_the_metadata_it_cleared()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session(), now);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, now);

        // A summarisation pass that started before the delete, and failed after it.
        var materialReadAtUtc = DateTime.UtcNow;
        store.ErasePromptDerived();
        store.NoteSummaryFailure("s1", materialReadAtUtc);

        using (var ctx = db.CreateContext())
        {
            var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
            Assert.Equal(0, row.SummaryAttempts);
            Assert.Null(row.SummaryKind);
        }

        // And the row is still reachable by the sweep, which is the property that actually matters.
        Assert.Contains(store.PendingSummaries(DateTime.UtcNow.AddMinutes(1), 10), r => r.SessionId == "s1");

        // Control: a failure whose material is newer than the delete IS counted.
        store.NoteSummaryFailure("s1", DateTime.UtcNow.AddSeconds(1));
        using (var ctx = db.CreateContext())
            Assert.Equal(1, ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").SummaryAttempts);
    }
}
