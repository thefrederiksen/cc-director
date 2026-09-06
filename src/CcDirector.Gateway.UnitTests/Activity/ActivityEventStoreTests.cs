using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Activity;

/// <summary>
/// The durable activity ledger's store contract (the trustworthy-Working-start plan): idempotent append by
/// producer-minted event id, whole-batch fail-loud validation, chronological tenant-scoped reads, and the
/// 30-day purge. Runs over the real EF store on a throwaway SQLite file (the data-layer harness), exactly
/// as the Gateway runs it locally.
/// </summary>
public sealed class ActivityEventStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private ActivityEventStore NewStore(ITenantContext? tenant = null)
        => new(_harness.Open(tenant));

    private static ActivityEventRecord Rec(
        Guid? id = null, string sessionId = "s1", string eventType = ActivityEventTypes.ActivityTransition,
        string cause = ActivityCauses.TerminalOutputOnly, DateTime? occurredUtc = null, long sequence = 1) => new()
    {
        EventId = id ?? Guid.NewGuid(),
        DirectorSequence = sequence,
        OccurredUtc = occurredUtc ?? DateTime.UtcNow,
        DirectorId = "dir-1",
        SessionId = sessionId,
        Machine = "SOREN_NORTH",
        AgentKind = "Claude",
        EventType = eventType,
        PreviousState = "WaitingForInput",
        NewState = "Working",
        Cause = cause,
        DetectorMode = "byte",
        DetectorVersion = "v1",
        OutputByteCount = 512,
        BeforeScreenHash = "aaa",
        AfterScreenHash = "bbb",
        BoundedScreenDiff = "row 41: [watcher] tick",
    };

    [Fact]
    public void Append_and_read_roundtrip_preserves_the_facts()
    {
        var store = NewStore();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        var first = Rec(occurredUtc: t0, sequence: 1);
        var second = Rec(occurredUtc: t0.AddMinutes(1), sequence: 2,
            eventType: ActivityEventTypes.TurnSubmitted, cause: ActivityCauses.OwnerSubmit);

        var (written, duplicates) = store.AppendBatch(new[] { first, second });

        Assert.Equal(2, written);
        Assert.Equal(0, duplicates);

        var events = store.Read(sessionId: "s1");
        Assert.Equal(2, events.Count);
        // Chronological (OccurredUtc ascending) - the diagnosis order.
        Assert.Equal(first.EventId, events[0].EventId);
        Assert.Equal(second.EventId, events[1].EventId);
        // The evidence facts survive the roundtrip.
        Assert.Equal("Claude", events[0].AgentKind);
        Assert.Equal("byte", events[0].DetectorMode);
        Assert.Equal(512, events[0].OutputByteCount);
        Assert.Equal("row 41: [watcher] tick", events[0].BoundedScreenDiff);
        Assert.Equal(ActivityCauses.OwnerSubmit, events[1].Cause);
    }

    [Fact]
    public void A_replayed_batch_is_acknowledged_without_duplicating_rows()
    {
        var store = NewStore();
        var batch = new[] { Rec(), Rec(sequence: 2) };

        var firstPush = store.AppendBatch(batch);
        var retry = store.AppendBatch(batch);

        Assert.Equal((2, 0), firstPush);
        // The retry is a SUCCESSFUL idempotent replay: fully acknowledged, zero new rows.
        Assert.Equal((0, 2), retry);
        Assert.Equal(2, store.Read(sessionId: "s1").Count);
    }

    [Fact]
    public void An_id_repeated_within_one_batch_lands_once()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        var (written, duplicates) = store.AppendBatch(new[] { Rec(id: id), Rec(id: id) });

        Assert.Equal(1, written);
        Assert.Equal(1, duplicates);
        Assert.Single(store.Read(sessionId: "s1"));
    }

    [Fact]
    public void One_invalid_event_rejects_the_whole_batch_and_writes_nothing()
    {
        var store = NewStore();
        var good = Rec();
        var bad = Rec() with { EventType = "not-a-real-type" };

        Assert.Throws<ActivityValidationException>(() => store.AppendBatch(new[] { good, bad }));

        // The ledger never lands a half-batch: the valid event did not sneak in.
        Assert.Empty(store.Read(sessionId: "s1"));
    }

    [Fact]
    public void An_unknown_cause_is_rejected()
    {
        var store = NewStore();
        Assert.Throws<ActivityValidationException>(
            () => store.AppendBatch(new[] { Rec() with { Cause = "vibes" } }));
    }

    [Fact]
    public void An_over_length_diff_is_rejected_not_truncated()
    {
        var store = NewStore();
        var oversized = Rec() with { BoundedScreenDiff = new string('x', ActivityEventStore.MaxDiffChars + 1) };
        Assert.Throws<ActivityValidationException>(() => store.AppendBatch(new[] { oversized }));
    }

    [Fact]
    public void A_batch_over_the_size_ceiling_is_rejected()
    {
        var store = NewStore();
        var tooMany = Enumerable.Range(0, ActivityEventStore.MaxBatchSize + 1).Select(_ => Rec()).ToList();
        Assert.Throws<ActivityValidationException>(() => store.AppendBatch(tooMany));
    }

    [Fact]
    public void A_missing_session_id_is_rejected()
    {
        var store = NewStore();
        Assert.Throws<ActivityValidationException>(
            () => store.AppendBatch(new[] { Rec(sessionId: " ") }));
    }

    [Fact]
    public void An_empty_event_id_is_rejected()
    {
        var store = NewStore();
        Assert.Throws<ActivityValidationException>(
            () => store.AppendBatch(new[] { Rec(id: Guid.Empty) }));
    }

    [Fact]
    public void Reads_filter_by_event_type_and_window()
    {
        var store = NewStore();
        var t0 = DateTime.UtcNow.AddHours(-2);
        store.AppendBatch(new[]
        {
            Rec(occurredUtc: t0, eventType: ActivityEventTypes.TurnSubmitted, cause: ActivityCauses.OwnerSubmit),
            Rec(occurredUtc: t0.AddMinutes(30)),
            Rec(occurredUtc: t0.AddMinutes(60), eventType: ActivityEventTypes.SnoozeEnded, cause: ActivityCauses.WorkingObservation),
        });

        var submits = store.Read(sessionId: "s1", eventType: ActivityEventTypes.TurnSubmitted);
        Assert.Equal(ActivityCauses.OwnerSubmit, Assert.Single(submits).Cause);

        var window = store.Read(sessionId: "s1", fromUtc: t0.AddMinutes(15), toUtc: t0.AddMinutes(45));
        Assert.Equal(ActivityCauses.TerminalOutputOnly, Assert.Single(window).Cause);
    }

    [Fact]
    public void A_future_occurred_time_is_clamped_to_the_append_time()
    {
        var store = NewStore();
        store.AppendBatch(new[] { Rec(occurredUtc: DateTime.UtcNow.AddDays(2)) });

        var stored = Assert.Single(store.Read(sessionId: "s1"));
        Assert.True(stored.OccurredUtc <= DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public void Purge_removes_only_events_older_than_the_cutoff()
    {
        var store = NewStore();
        var old = Rec(occurredUtc: DateTime.UtcNow.AddDays(-40));
        var recent = Rec(occurredUtc: DateTime.UtcNow.AddDays(-1));
        store.AppendBatch(new[] { old, recent });

        var deleted = store.PurgeOlderThan(DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, deleted);
        var remaining = Assert.Single(store.Read(sessionId: "s1"));
        Assert.Equal(recent.EventId, remaining.EventId);
    }

    [Fact]
    public void Two_tenants_never_see_each_other_and_the_same_id_cannot_collide()
    {
        // Two tenants over the SAME database file - the hosted shape. The same producer-minted event id
        // appended by both must be TWO rows (the composite key scopes the id space per tenant): no
        // cross-tenant squat, no existence oracle, and the replay dedup of one tenant must never swallow
        // the other's event.
        var tenantA = new TenantId("tenant-aaaa");
        var tenantB = new TenantId("tenant-bbbb");
        var storeA = NewStore(new FixedTenantContext(tenantA));
        var storeB = NewStore(new FixedTenantContext(tenantB));

        var sharedId = Guid.NewGuid();
        var secretA = Rec(id: sharedId) with { BoundedScreenDiff = "alpha-account-secret-rows" };
        var secretB = Rec(id: sharedId) with { BoundedScreenDiff = "bravo-account-secret-rows" };

        Assert.Equal((1, 0), storeA.AppendBatch(new[] { secretA }));
        Assert.Equal((1, 0), storeB.AppendBatch(new[] { secretB }));   // NOT a duplicate: B owns its own id space

        // Positive control first, then the bidirectional absence claims.
        var readA = Assert.Single(storeA.Read(sessionId: "s1"));
        var readB = Assert.Single(storeB.Read(sessionId: "s1"));
        Assert.Equal("alpha-account-secret-rows", readA.BoundedScreenDiff);
        Assert.Equal("bravo-account-secret-rows", readB.BoundedScreenDiff);

        // And one tenant's purge never touches the other's rows.
        Assert.Equal(1, storeA.PurgeOlderThan(DateTime.UtcNow.AddDays(1)));
        Assert.Empty(storeA.Read(sessionId: "s1"));
        Assert.Equal("bravo-account-secret-rows", Assert.Single(storeB.Read(sessionId: "s1")).BoundedScreenDiff);
    }

    // ---- source logging (owner's ruling, 2026-09-05): what the door knew survives the ledger round trip

    [Fact]
    public void The_doors_provenance_and_the_choke_points_digest_survive_the_roundtrip()
    {
        var store = NewStore();
        var row = Rec(eventType: ActivityEventTypes.TurnSubmitted, cause: ActivityCauses.OwnerSubmit) with
        {
            InputOrigin = "voice/phone",
            SendSource = "Delivery",
            Route = "gateway-dictation",
            IdentityKind = "device",
            TranscriptId = "upload-42",
            SpokenSpans = "0+44",
            ContentSha256 = new string('a', 64),
            ContentLength = 44,
        };
        store.AppendBatch(new[] { row });

        var read = Assert.Single(store.Read(sessionId: "s1"));
        Assert.Equal("gateway-dictation", read.Route);
        Assert.Equal("device", read.IdentityKind);
        Assert.Equal("upload-42", read.TranscriptId);
        Assert.Equal("0+44", read.SpokenSpans);
        Assert.Equal(new string('a', 64), read.ContentSha256);
        Assert.Equal(44, read.ContentLength);
    }

    [Fact]
    public void A_row_from_a_director_older_than_the_fields_reads_back_with_them_null()
    {
        var store = NewStore();
        store.AppendBatch(new[] { Rec(eventType: ActivityEventTypes.TurnSubmitted, cause: ActivityCauses.OwnerSubmit) });
        var read = Assert.Single(store.Read(sessionId: "s1"));
        Assert.Null(read.Route);
        Assert.Null(read.IdentityKind);
        Assert.Null(read.SpokenSpans);
        Assert.Null(read.ContentSha256);
        Assert.Null(read.ContentLength);
    }
}
