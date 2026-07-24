using CcDirector.Core.Activity;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Activity;

/// <summary>
/// The durable outbox contract (docs/PLAN-trustworthy-working-start-2026-07-24.md, increment 2): an event
/// is minted ONCE - id and monotonic sequence - before it is persisted, survives a Director restart, and
/// leaves the outbox only on Gateway acknowledgement. The identities surviving retry and restart is what
/// makes the Gateway's append idempotent end to end.
/// </summary>
public sealed class ActivityEventOutboxTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-activity-outbox-tests-" + Guid.NewGuid().ToString("N"));

    private string OutboxPath => Path.Combine(_dir, "outbox.jsonl");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ActivityEventRecord Draft(Guid? id = null) => new()
    {
        EventId = id ?? Guid.Empty,
        DirectorSequence = 0,
        OccurredUtc = DateTime.UtcNow,
        DirectorId = "dir-1",
        SessionId = "s1",
        EventType = ActivityEventTypes.ActivityTransition,
        Cause = ActivityCauses.Unknown,
    };

    [Fact]
    public void Enqueue_mints_a_fresh_id_and_a_monotonic_sequence()
    {
        var outbox = new ActivityEventOutbox(OutboxPath);

        var first = outbox.Enqueue(Draft());
        var second = outbox.Enqueue(Draft());

        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.NotEqual(first.EventId, second.EventId);
        Assert.Equal(1, first.DirectorSequence);
        Assert.Equal(2, second.DirectorSequence);
    }

    [Fact]
    public void A_producer_supplied_deterministic_id_is_kept()
    {
        var outbox = new ActivityEventOutbox(OutboxPath);
        var deterministic = Guid.NewGuid();

        var minted = outbox.Enqueue(Draft(id: deterministic));

        Assert.Equal(deterministic, minted.EventId);
    }

    [Fact]
    public void Pending_events_survive_a_restart_with_their_minted_identities()
    {
        var first = new ActivityEventOutbox(OutboxPath);
        var a = first.Enqueue(Draft());
        var b = first.Enqueue(Draft());

        // A new outbox over the same file is the restarted Director.
        var restarted = new ActivityEventOutbox(OutboxPath);

        Assert.Equal(2, restarted.PendingCount);
        var pending = restarted.PendingBatch(10);
        Assert.Equal(new[] { a.EventId, b.EventId }, pending.Select(e => e.EventId));
        Assert.Equal(new[] { 1L, 2L }, pending.Select(e => e.DirectorSequence));

        // The sequence RESUMES - a post-restart event never reuses a pre-restart sequence.
        Assert.Equal(3, restarted.Enqueue(Draft()).DirectorSequence);
    }

    [Fact]
    public void Acknowledge_deletes_exactly_the_confirmed_events_durably()
    {
        var outbox = new ActivityEventOutbox(OutboxPath);
        var a = outbox.Enqueue(Draft());
        var b = outbox.Enqueue(Draft());
        var c = outbox.Enqueue(Draft());

        outbox.Acknowledge(new[] { a.EventId, c.EventId });

        Assert.Equal(b.EventId, Assert.Single(outbox.PendingBatch(10)).EventId);
        // Durably: the survivor is what a restart loads.
        Assert.Equal(b.EventId, Assert.Single(new ActivityEventOutbox(OutboxPath).PendingBatch(10)).EventId);
    }

    [Fact]
    public void An_unacknowledged_batch_is_returned_again_with_the_same_identities()
    {
        var outbox = new ActivityEventOutbox(OutboxPath);
        var minted = outbox.Enqueue(Draft());

        var firstTry = outbox.PendingBatch(10);
        var secondTry = outbox.PendingBatch(10);   // the failed-push retry path

        Assert.Equal(minted.EventId, Assert.Single(firstTry).EventId);
        Assert.Equal(minted.EventId, Assert.Single(secondTry).EventId);
        Assert.Equal(minted.DirectorSequence, secondTry[0].DirectorSequence);
    }

    [Fact]
    public void A_corrupt_line_is_skipped_and_the_rest_of_the_evidence_loads()
    {
        var outbox = new ActivityEventOutbox(OutboxPath);
        var kept = outbox.Enqueue(Draft());
        File.AppendAllText(OutboxPath, "{not json" + Environment.NewLine);
        var alsoKept = outbox.Enqueue(Draft());

        var restarted = new ActivityEventOutbox(OutboxPath);

        Assert.Equal(new[] { kept.EventId, alsoKept.EventId },
            restarted.PendingBatch(10).Select(e => e.EventId));
    }

    [Fact]
    public void Past_the_cap_the_oldest_events_are_dropped_not_the_disk_filled()
    {
        var outbox = new ActivityEventOutbox(OutboxPath, maxPending: 3);
        var a = outbox.Enqueue(Draft());
        var b = outbox.Enqueue(Draft());
        var c = outbox.Enqueue(Draft());
        var d = outbox.Enqueue(Draft());

        Assert.Equal(3, outbox.PendingCount);
        Assert.Equal(new[] { b.EventId, c.EventId, d.EventId },
            outbox.PendingBatch(10).Select(e => e.EventId));
        Assert.DoesNotContain(a.EventId, outbox.PendingBatch(10).Select(e => e.EventId));
    }
}
