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
/// The two ways the first watermark still let erased material come back, both found by inspection.
///
/// ONE - THE CHECK AND THE WRITE WERE SEPARATE. A writer read the watermark, then read its row, then
/// wrote, and the only thing holding those together was a lock on ONE store instance. The hosted Gateway
/// runs two containers at once during a slot swap (<c>FileLog</c> says so in its own comment, which is why
/// it hands each process a distinct log file), and across two processes that lock means nothing: process B
/// can read the old watermark, process A can erase and stamp, and B can then commit its pre-delete summary
/// over the top. Every guard was green because every test used one instance.
///
/// TWO - "JUDGED ON THE MATERIAL" WAS NOT TRUE OF THE SUMMARY PATH. The summariser stamps the clock just
/// before it READS the prompt log. If a Director re-delivers old records after a delete, that read is
/// honestly recent while the words are the member's erased ones, so the summary was accepted. No race and
/// no clock skew needed: the whole thing can start after the delete has finished.
///
/// These facts use TWO INDEPENDENT store instances over one database - separate write locks, the way two
/// processes have - and drive the REAL summariser over the REAL prompt log.
/// </summary>
public sealed class TheErasureIsJudgedByMaterialAgeTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _promptDir =
        Path.Combine(Path.GetTempPath(), "gw-crossproc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { if (Directory.Exists(_promptDir)) Directory.Delete(_promptDir, recursive: true); } catch { /* best effort */ }
    }

    private const string TheMembersOwnWords =
        "Audit the vendor contract renewals and flag anything auto-renewing this quarter";

    /// <summary>Two stores over ONE database file: two instance locks, as two processes have.</summary>
    private (SessionHistoryStore A, SessionHistoryStore B) TwoIndependentProcesses()
    {
        var a = new SessionHistoryStore(_harness.Open());
        var b = new SessionHistoryStore(_harness.Open());
        Assert.NotSame(a, b);
        return (a, b);
    }

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

    private static PromptRecord Record(DateTime tsUtc, string text, string sessionId = "s1") => new()
    {
        TsUtc = tsUtc,
        Machine = "SOREN_NORTH",
        SessionId = sessionId,
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "TestAgent",
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = 10,
        Text = text,
    };

    /// <summary>A brain that answers immediately with a parseable summary of whatever it was given.</summary>
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
                {
                  "summary": "A summary built from whatever transcript was supplied.",
                  "what_was_built": ["a summary"],
                  "left_unverified": [],
                  "branches": [],
                  "pull_requests": [],
                  "commits": []
                }
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

    // ---- One: the check and the write are now one statement -----------------------------------------

    /// <summary>
    /// The interleave the previous mechanism allowed, driven deliberately: process B decides it may write
    /// (it reads the watermark and finds none), process A erases and stamps, and only THEN does B write.
    /// With the comparison inside B's write, the database refuses it. With the comparison in B's memory,
    /// nothing could refuse it - B had already decided.
    /// </summary>
    [Fact]
    public void A_writer_that_decided_before_the_delete_cannot_commit_after_it_from_another_process()
    {
        var (a, b) = TwoIndependentProcesses();
        var now = DateTime.UtcNow;
        a.UpsertLive("dir-1", Session(), now);

        // B reads the watermark BEFORE the erasure. This is the stale decision the old code kept.
        var materialReadAtUtc = DateTime.UtcNow;
        Assert.Null(b.PromptErasureWatermarkUtc());

        // A erases and stamps, in its own process, while B is between its check and its write.
        a.ErasePromptDerived();

        // B now writes on the strength of that stale read.
        b.StoreGeneratedSummary("s1", SessionHistorySummaryKinds.Generated, isPartial: false,
            "A summary of the member's erased prompts.", new[] { "something" }, null, null, null, null,
            materialReadAtUtc);
        b.SetFirstPrompt("s1", TheMembersOwnWords, materialReadAtUtc);
        b.SaveRollup("thefrederiksen/devthrottle", now.Date, "A paragraph of the same.", "hash", 0,
            DateTime.UtcNow, materialReadAtUtc);

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(row.SummaryText);
        Assert.Null(row.WhatWasBuiltJson);
        Assert.Null(row.FirstPromptLine);
        Assert.Empty(b.ReadRollups(now.Date, now.Date));
    }

    /// <summary>
    /// The control for the fact above: the same three writes from a second process, with material the
    /// Gateway saw AFTER the delete, all land. Without this the guard could be "the second process may
    /// never write", which would be a different defect wearing the same green.
    /// </summary>
    [Fact]
    public void The_same_writer_still_writes_when_its_material_is_newer_than_the_delete()
    {
        var (a, b) = TwoIndependentProcesses();
        var now = DateTime.UtcNow;
        a.UpsertLive("dir-1", Session(), now);
        a.ErasePromptDerived();

        var afterwards = DateTime.UtcNow.AddSeconds(1);
        b.StoreGeneratedSummary("s1", SessionHistorySummaryKinds.Generated, isPartial: false,
            "Work done after the delete.", new[] { "later work" }, null, null, null, null, afterwards);
        b.SetFirstPrompt("s1", "A prompt sent after the delete", afterwards);
        b.SaveRollup("thefrederiksen/devthrottle", now.Date, "A later paragraph.", "hash", 0,
            DateTime.UtcNow, afterwards);

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Equal("Work done after the delete.", row.SummaryText);
        Assert.Equal("A prompt sent after the delete", row.FirstPromptLine);
        Assert.Equal("A later paragraph.", Assert.Single(b.ReadRollups(now.Date, now.Date)).SummaryText);
    }

    /// <summary>
    /// The watermark only ever moves FORWARD, and that has to hold when two processes stamp at once. The
    /// old code read, compared in memory and saved unconditionally, so a slower process holding an older
    /// value could lower the line - the one direction that lets erased material back in.
    /// </summary>
    [Fact]
    public void A_second_process_stamping_cannot_lower_the_erasure_line()
    {
        var (a, b) = TwoIndependentProcesses();
        a.UpsertLive("dir-1", Session(), DateTime.UtcNow);

        a.ErasePromptDerived();
        var highWater = a.PromptErasureWatermarkUtc();
        Assert.NotNull(highWater);

        b.ErasePromptDerived();

        Assert.True(b.PromptErasureWatermarkUtc() >= highWater,
            "a later erasure must never lower the line an earlier one set");
    }

    // ---- Two: old material is refused at the door ---------------------------------------------------

    /// <summary>
    /// THE PATH THE INSPECTION SAID WAS MISSED, and it needs no race at all. A Director re-delivers records
    /// it failed to deliver earlier; they arrive AFTER the member's delete; the real summariser then reads
    /// the log and writes a summary made of the member's erased words, carrying a read time that is
    /// honestly recent. Keeping the material OUT is what stops that, so the retried batch is refused at
    /// <c>Append</c> and there is nothing left to summarise.
    ///
    /// THE NAME NOW CARRIES THE CONDITION THAT MAKES THE BODY TRUE. Arriving after the delete is not it -
    /// the control immediately below sends a batch after the same delete and it IS accepted and summarised.
    /// What is refused here is a batch whose records still carry their ORIGINAL pre-delete timestamps,
    /// which is what a retry of previously-undelivered records looks like.
    /// </summary>
    [Fact]
    public async Task A_retried_batch_still_carrying_its_pre_delete_timestamps_is_refused_and_leaves_nothing_to_summarise()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        var started = DateTime.UtcNow.AddHours(-2);

        var oldBatch = Enumerable.Range(0, 12)
            .Select(i => Record(started.AddMinutes(i), TheMembersOwnWords + " " + new string('x', 100)))
            .ToList();
        Assert.Equal(12, log.Append(TenantId.Local, oldBatch));
        store.UpsertLive("dir-1", Session(created: started), started);
        store.SetFirstPrompt("s1", TheMembersOwnWords, started);
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow.AddMinutes(-5));

        // The member deletes. Files gone, derived fields cleared, watermark stamped.
        store.ErasePromptDerived();
        log.DeleteAll(TenantId.Local);

        // The Director retries the SAME old records, minutes later. Nothing is racing.
        var acceptedOnRetry = log.Append(TenantId.Local, oldBatch);
        Assert.Equal(0, acceptedOnRetry);
        Assert.Empty(log.ReadAll(TenantId.Local));

        // The real summariser runs afterwards and has nothing of the member's to work with.
        var brain = new EchoBrain();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));
        await summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Null(row.SummaryText);
        Assert.Null(row.FirstPromptLine);
        // The model was never asked, because there was no transcript to ask about.
        Assert.Null(brain.LastPrompt);
        // The row is settled honestly rather than left pending forever.
        Assert.Equal(SessionHistorySummaryKinds.None, row.SummaryKind);
    }

    /// <summary>
    /// The control: a batch the member sends AFTER the delete is theirs, is accepted, and IS summarised.
    /// Without it, "the log refuses everything after a delete" would pass the fact above.
    /// </summary>
    [Fact]
    public async Task Prompts_sent_after_the_delete_are_accepted_and_summarised_normally()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        store.UpsertLive("dir-1", Session(created: DateTime.UtcNow.AddHours(-2)), DateTime.UtcNow.AddHours(-2));
        store.ErasePromptDerived();
        log.DeleteAll(TenantId.Local);

        var fresh = Enumerable.Range(0, 12)
            .Select(i => Record(DateTime.UtcNow.AddSeconds(1 + i), "New work after the delete " + new string('y', 100)))
            .ToList();
        Assert.Equal(12, log.Append(TenantId.Local, fresh));
        store.RecordEnding("s1", SessionHistoryEndings.Finished, crashed: false, DateTime.UtcNow.AddMinutes(-5));

        var brain = new EchoBrain();
        var summarizer = new SessionHistorySummarizer(store, log, (_, _) => Task.FromResult<IAgentBrain>(brain));
        await summarizer.SummarizePendingAsync(TenantId.Local, maxSessions: 5, CancellationToken.None);

        using var ctx = _harness.Open().CreateContext();
        var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
        Assert.Equal(SessionHistorySummaryKinds.Generated, row.SummaryKind);
        Assert.NotNull(brain.LastPrompt);
    }

    /// <summary>
    /// Admission is decided on the time the CALLER CLAIMS the material dates from, unclamped - and this
    /// fact pins the cost of that, because the previous version of this code clamped to receipt time and
    /// carried a comment claiming the clamp "cannot refuse anything legitimate". That was false: a clamp to
    /// the OLDER of the two values keeps a slow clock's mistake, so a Director running behind has genuinely
    /// new prompts dated into the past and REFUSED.
    ///
    /// The behaviour is kept, because believing a caller that says its material is old is the direction
    /// that cannot resurrect anything - but it is a real cost and it is asserted here rather than described
    /// as impossible. Nothing is destroyed: the prompt is still on the member's machine and a corrected
    /// clock re-delivers.
    /// </summary>
    [Fact]
    public void A_slow_clock_gets_its_new_prompts_refused_which_is_the_accepted_cost_of_believing_the_caller()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        store.UpsertLive("dir-1", Session(), DateTime.UtcNow);
        store.ErasePromptDerived();

        // Sent a moment ago by the member; the Director's clock is an hour behind, so it is dated before
        // the erasure that has just happened.
        var slow = Record(DateTime.UtcNow.AddHours(-1), "A prompt the member sent AFTER the delete");

        Assert.Equal(0, log.Append(TenantId.Local, new[] { slow }));
        Assert.Empty(log.ReadAll(TenantId.Local));
    }

    /// <summary>
    /// A far-future caller timestamp must not choose our retention. Retention deletes by the date in the
    /// FILE NAME, so an unclamped future date would create a file that ages out long after the published
    /// ninety-day maximum - the caller setting our policy. The file day is clamped to our receipt day; a
    /// record honestly dated in the past keeps its own day and is swept earlier, not later.
    /// </summary>
    [Fact]
    public void A_future_dated_record_lands_in_todays_file_so_retention_still_reaches_it()
    {
        var log = new GatewayPromptLog(_promptDir);
        var receivedAtUtc = DateTime.UtcNow;
        var future = Record(receivedAtUtc.AddDays(400), "A prompt claiming to be from next year");
        var past = Record(receivedAtUtc.AddDays(-3), "A prompt honestly from three days ago");

        Assert.Equal(receivedAtUtc.Date, GatewayPromptLog.FileDayUtc(future, receivedAtUtc));
        Assert.Equal(past.TsUtc.Date, GatewayPromptLog.FileDayUtc(past, receivedAtUtc));

        log.Append(TenantId.Local, new[] { future, past });

        var files = Directory.GetFiles(_promptDir, "conversation-*.jsonl", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        Assert.Contains($"conversation-{receivedAtUtc:yyyyMMdd}.jsonl", files);
        Assert.Contains($"conversation-{past.TsUtc:yyyyMMdd}.jsonl", files);
        Assert.DoesNotContain($"conversation-{future.TsUtc:yyyyMMdd}.jsonl", files);
    }

    /// <summary>
    /// WHAT THE ADMISSION RULE DOES NOT DECIDE, pinned so nobody mistakes it for covered. A record dated
    /// AFTER an erasure is accepted, because the Gateway has no evidence distinguishing it from a prompt
    /// the member sent a second ago - it keeps no ledger of what it has seen before. A Director whose clock
    /// runs ahead and retries an old record produces exactly that shape.
    ///
    /// This is the limit of what a receiving service can know about material it did not see the first time.
    /// Closing it means the Director honouring the delete rather than retrying at all - issue #2380 - and
    /// the customer-facing wording says the service refuses material it can TELL is older rather than
    /// claiming resurrection is impossible.
    /// </summary>
    [Fact]
    public void A_record_dated_after_the_erasure_is_accepted_and_that_is_the_known_limit()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var log = new GatewayPromptLog(_promptDir, tenant => store.PromptErasureWatermarkUtc(tenant));
        store.UpsertLive("dir-1", Session(), DateTime.UtcNow);
        store.ErasePromptDerived();

        var skewed = Record(DateTime.UtcNow.AddHours(1), TheMembersOwnWords);

        Assert.Equal(1, log.Append(TenantId.Local, new[] { skewed }));
    }
}
