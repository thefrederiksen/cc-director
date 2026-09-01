using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The erasure is only an erasure if nothing puts the material back afterwards. Clearing the columns and
/// returning success is not enough: three writers compute from material read BEFORE a delete and commit
/// AFTER it, and the worst of them is the summariser, because it takes a pending row, spends a model call
/// on it, and then writes every prompt-derived field back.
///
/// The metadata reset makes that WORSE rather than better, which is the part worth understanding: it moves
/// the summary kind back to null, and null is precisely the state in which
/// <see cref="SessionHistoryStore.StoreGeneratedSummary"/> does not refuse a write. So the erasure re-armed
/// the writer that undoes it.
///
/// These facts hold a REAL summarisation open across a REAL delete - the model call blocks until the delete
/// has completed - and assert the member's words do not come back. A sequential test cannot see any of this:
/// the previous facts on this branch proved the eleven statements' immediate effects, and every one of them
/// stayed green while this hole was open.
/// </summary>
public sealed class AnInFlightWriterIsRefusedByTheWatermarkTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _promptDir =
        Path.Combine(Path.GetTempPath(), "gw-inflight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { if (Directory.Exists(_promptDir)) Directory.Delete(_promptDir, recursive: true); } catch { /* best effort */ }
    }

    private const string TheMembersOwnWords =
        "Reconcile the payroll export against the tax ledger and tell me which rows disagree";

    /// <summary>A brain whose reply is held until the test releases it - the model call, stopped mid-air.</summary>
    private sealed class HeldBrain : IAgentBrain
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the summariser is INSIDE the model call, so the test never races it.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public string? SessionId => "held";

        public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new AskResult
            {
                Text = """
                {
                  "summary": "The member asked to reconcile the payroll export against the tax ledger.",
                  "what_was_built": ["the reconciliation"],
                  "left_unverified": [],
                  "branches": [],
                  "pull_requests": [],
                  "commits": []
                }
                """,
            };
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    /// <summary>Seed an ended session with enough prompt text in the log to earn a real model call.</summary>
    private (SessionHistoryStore Store, GatewayPromptLog Log) SeedEndedSessionAwaitingASummary()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var log = new GatewayPromptLog(_promptDir);
        var started = DateTime.UtcNow.AddHours(-2);

        var records = new List<PromptRecord>();
        for (var i = 0; i < 12; i++)
        {
            records.Add(new PromptRecord
            {
                TsUtc = started.AddMinutes(i),
                Machine = "SOREN_NORTH",
                SessionId = "s1",
                RepoPath = @"D:\ReposFred\devthrottle",
                Agent = "TestAgent",
                Role = "user",
                TimestampFromAgent = true,
                CharCount = TheMembersOwnWords.Length + 101,
                WordCount = 12,
                Text = TheMembersOwnWords + " " + new string('x', 100),
            });
        }
        log.Append(TenantId.Local, records);

        store.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "s1",
            Name = null,
            RepoPath = @"D:\ReposFred\devthrottle",
            RepoName = "thefrederiksen/devthrottle",
            Agent = "TestAgent",
            MachineName = "SOREN_NORTH",
            CreatedAt = started,
            LastActivityAt = started.AddMinutes(20),
            ActivityState = "Working",
            Status = "Running",
        }, started);
        // The ingest copy the delete is supposed to erase. Without it the row carries nothing erasable and
        // the erasure would honestly report zero - a scenario that proves nothing about resurrection.
        store.SetFirstPrompt("s1", TheMembersOwnWords, started);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow.AddMinutes(-5));
        return (store, log);
    }

    /// <summary>
    /// THE FACT THE REJECTION ASKED FOR. A summarisation that began before the delete finishes after it and
    /// must not write the member's erased words back.
    /// </summary>
    [Fact]
    public async Task A_summarisation_held_across_the_delete_does_not_write_the_erased_words_back()
    {
        var (store, log) = SeedEndedSessionAwaitingASummary();
        var brain = new HeldBrain();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));

        // The summariser starts, reads the prompt log, and stops inside the model call.
        var inFlight = summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);
        await brain.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // The member deletes while it hangs there. This is the whole scenario.
        var erased = store.ErasePromptDerived();
        Assert.Equal(1, erased.SessionRows);
        var deletedFiles = log.DeleteAll(TenantId.Local);
        Assert.True(deletedFiles > 0);

        // Now let the model answer. Before the watermark, this call wrote every prompt-derived field back.
        brain.Release();
        await inFlight.WaitAsync(TimeSpan.FromSeconds(30));

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(row.SummaryText);
        Assert.Null(row.WhatWasBuiltJson);
        Assert.Null(row.FirstPromptLine);
        // And the row was not left claiming a summary that is not there.
        Assert.Null(row.SummaryKind);
    }

    /// <summary>
    /// THE CONTROL, without which the fact above is satisfied by a guard that refuses everything.
    ///
    /// The name used to say "starts after the delete", and starting later is not what makes this pass -
    /// the body deletes, then re-seeds the log with a record TIMESTAMPED after the erasure, and it is that
    /// timestamp the writer is judged on. A summarisation that merely began later, over material dated
    /// before the delete, is refused; the fact above is exactly that case.
    /// </summary>
    [Fact]
    public async Task A_summarisation_whose_material_is_dated_after_the_delete_still_writes_its_summary()
    {
        var (store, log) = SeedEndedSessionAwaitingASummary();

        // Delete FIRST, then re-seed the log with material the member sent afterwards.
        store.ErasePromptDerived();
        log.DeleteAll(TenantId.Local);
        var afterwards = DateTime.UtcNow.AddSeconds(1);
        log.Append(TenantId.Local, new[]
        {
            new PromptRecord
            {
                TsUtc = afterwards,
                Machine = "SOREN_NORTH",
                SessionId = "s1",
                RepoPath = @"D:\ReposFred\devthrottle",
                Agent = "TestAgent",
                Role = "user",
                TimestampFromAgent = true,
                CharCount = 533,
                WordCount = 7,
                Text = "New work, sent after the delete. " + new string('y', 500),
            },
        });

        var brain = new HeldBrain();
        brain.Release();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));
        await summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Equal(SessionHistorySummaryKinds.Generated, row.SummaryKind);
        Assert.Contains("payroll export", row.SummaryText ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The roll-up half of the same race: a cached paragraph computed from pre-delete session summaries
    /// must not recreate the row the delete removed. Driven at the store, because that is where the refusal
    /// lives and the summariser's roll-up pass differs only in who supplies the timestamp.
    /// </summary>
    [Fact]
    public void A_rollup_computed_before_the_delete_is_refused_when_it_tries_to_save()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var day = DateTime.UtcNow.Date;
        var readAt = DateTime.UtcNow;
        store.SaveRollup("thefrederiksen/devthrottle", day, "A paragraph made of the member's prompts.",
            "hash", 0, DateTime.UtcNow, readAt);

        store.ErasePromptDerived();

        // The pass that was already running saves its paragraph afterwards.
        store.SaveRollup("thefrederiksen/devthrottle", day, "A paragraph made of the member's prompts.",
            "hash", 0, DateTime.UtcNow, readAt);

        // Nothing is served AND nothing is left in the table: on this path the conditional update matches
        // no row, the insert runs, and the compensating delete removes it. The ORPHAN case - where that
        // compensation is interrupted and a row does survive - is a different scenario and is proved
        // separately in TheDeletionBoundaryGuardsTests, which seeds the orphan directly.
        Assert.Empty(store.ReadRollups(day, day));
        using (var raw = db.CreateContext())
            Assert.Empty(raw.SessionHistoryRollups.AsNoTracking().ToList());

        // Control: a pass that read its inputs after the delete still caches its paragraph.
        store.SaveRollup("thefrederiksen/devthrottle", day, "A paragraph made only of what came later.",
            "hash2", 0, DateTime.UtcNow, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("A paragraph made only of what came later.",
            Assert.Single(store.ReadRollups(day, day)).SummaryText);
    }

    /// <summary>
    /// The ingest half, and the one the inspection was sharpest about: the Director RETRIES records it
    /// previously failed to deliver, so a push arriving after the delete can carry prompts from before it -
    /// exactly the prompts the member erased. The refusal is on the PROMPT'S timestamp, not on the moment
    /// of writing, which is the only comparison that can tell those apart.
    /// </summary>
    [Fact]
    public void A_retried_old_prompt_arriving_after_the_delete_is_not_copied_back()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "s1",
            Name = null,
            RepoPath = @"D:\ReposFred\devthrottle",
            RepoName = "thefrederiksen/devthrottle",
            Agent = "TestAgent",
            MachineName = "SOREN_NORTH",
            CreatedAt = now.AddHours(-1),
            LastActivityAt = now,
            ActivityState = "Working",
            Status = "Running",
        }, now);

        store.ErasePromptDerived();

        // A record the member sent BEFORE the delete, delivered late.
        store.SetFirstPrompt("s1", TheMembersOwnWords, now.AddMinutes(-30));
        using (var ctx = db.CreateContext())
            Assert.Null(ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").FirstPromptLine);

        // Control: a prompt the member sends AFTER the delete is theirs to keep, and still lands.
        store.SetFirstPrompt("s1", "Work started after the delete", DateTime.UtcNow.AddSeconds(1));
        using (var ctx = db.CreateContext())
            Assert.Equal("Work started after the delete",
                ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").FirstPromptLine);
    }

    /// <summary>
    /// The watermark has to survive a restart, because the retry above can arrive days later. A fresh
    /// database handle over the same file - the harness's "restart" - must still refuse the old material.
    /// A watermark held in memory would pass every fact above and fail this one.
    /// </summary>
    [Fact]
    public void The_watermark_is_read_back_through_a_fresh_database_handle()
    {
        var before = DateTime.UtcNow.AddMinutes(-30);
        var first = new SessionHistoryStore(_harness.Open());
        first.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "s1",
            Name = null,
            RepoPath = @"D:\ReposFred\devthrottle",
            RepoName = "thefrederiksen/devthrottle",
            Agent = "TestAgent",
            MachineName = "SOREN_NORTH",
            CreatedAt = before,
            LastActivityAt = before,
            ActivityState = "Working",
            Status = "Running",
        }, before);
        first.ErasePromptDerived();

        // A second store over the SAME file is this suite's restart.
        var afterRestart = new SessionHistoryStore(_harness.Open());
        Assert.NotNull(afterRestart.PromptErasureWatermarkUtc());
        afterRestart.SetFirstPrompt("s1", TheMembersOwnWords, before);

        using var ctx = _harness.Open().CreateContext();
        Assert.Null(ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").FirstPromptLine);
    }
}
