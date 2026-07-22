using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Multi-tenancy regression for the durable telemetry retry queue (audit MTR gap C). The queue is ONE
/// process-global durable FIFO shared by every tenant's <c>/telemetry/*</c> writes. Before the fix its
/// bound evicted the globally-oldest event and its flush stopped the whole FIFO on the first non-2xx, so
/// one tenant's volume could evict another tenant's queued event and one tenant's poison event could
/// head-of-line-block every other tenant's delivery.
///
/// Each test is written so that REVERTING the fix reddens it:
/// <list type="bullet">
///   <item><see cref="PerTenantCap_OneTenantsOverflow_DoesNotEvictAnotherTenantsEvent"/> - reverting the
///     per-tenant eviction to a global <c>_events.First</c> drop evicts tenant B's event, failing the
///     positive control that B is still delivered.</item>
///   <item><see cref="PerTenantFlush_OneTenantsPoison_DoesNotBlockAnotherTenant"/> - reverting the
///     per-tenant skip to the single-line "stop on first failure" leaves tenant B's event undelivered
///     behind tenant A's poison.</item>
///   <item><see cref="LegacyUntaggedPoison_DoesNotHeadOfLineBlock_ARealTenant"/> - reverting the legacy
///     quarantine lane to a shared Local default lets a pre-tag poison event block a real tenant.</item>
/// </list>
/// Delivery is driven deterministically through a fake handler + the public
/// <see cref="TelemetryRetryQueue.FlushOnceAsync"/> - no timers, no network.
/// </summary>
public sealed class TelemetryRetryQueueTenantTests
{
    private const string Url = "http://backend.test/api/v1/telemetry/login";
    private static readonly TenantId TenantA = new("tenant-a-guid-0000000000000000");
    private static readonly TenantId TenantB = new("tenant-b-guid-1111111111111111");

    /// <summary>
    /// A fake backend that DELIVERS any body not marked POISON (recording it) and PERMANENTLY rejects a
    /// POISON body with a non-2xx - the shape of an event the backend will never accept.
    /// </summary>
    private sealed class PoisonAwareHandler : HttpMessageHandler
    {
        public readonly List<string> Delivered = new();
        private readonly object _lock = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (body.Contains("POISON"))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            lock (_lock) Delivered.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (TelemetryRetryQueue queue, PoisonAwareHandler handler, string path) NewQueue(int maxSize = 1000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-tenant-{Guid.NewGuid():N}.json");
        var handler = new PoisonAwareHandler();
        var queue = new TelemetryRetryQueue(path, new HttpClient(handler), TimeSpan.FromMilliseconds(50), maxSize);
        return (queue, handler, path);
    }

    // The body carries a short label ("A"/"B") that mirrors which TenantId the event was enqueued under, so
    // a delivered body can be attributed back to its tenant. The queue partitions by the TenantId passed to
    // Enqueue (never by the body), so the label is only the test's read-back handle.
    private static string Body(string label, int n) => JsonSerializer.Serialize(new { tenant = label, n });
    private static bool IsFrom(string body, string label)
        => JsonDocument.Parse(body).RootElement.GetProperty("tenant").GetString() == label;

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup */ }
    }

    // ----- per-tenant bound: one tenant's flood never evicts another tenant's event -----

    [Fact]
    public async Task PerTenantCap_OneTenantsOverflow_DoesNotEvictAnotherTenantsEvent()
    {
        var (queue, handler, path) = NewQueue(maxSize: 3);
        try
        {
            // Tenant B queues one legitimate event (the oldest event in the shared list).
            queue.Enqueue(Url, Body("B", 0), bearer: null, TenantB);
            // Tenant A floods past the bound of 3. A global cap would evict the list head - B's event.
            for (var i = 0; i < 5; i++)
                queue.Enqueue(Url, Body("A", i), bearer: null, TenantA);

            // A is bounded to its OWN 3 newest (2,3,4); B keeps its one. Nothing global was dropped.
            Assert.Equal(4, queue.Depth);

            var delivered = await queue.FlushOnceAsync();
            Assert.Equal(4, delivered);

            // Positive control + the isolation fact: B's event survived A's flood and was delivered.
            Assert.Contains(handler.Delivered, b => IsFrom(b, "B"));
            // A kept exactly its newest three (2,3,4), evicting only its OWN oldest (0,1).
            var aNs = handler.Delivered.Where(b => IsFrom(b, "A"))
                .Select(b => JsonDocument.Parse(b).RootElement.GetProperty("n").GetInt32())
                .OrderBy(n => n).ToArray();
            Assert.Equal(new[] { 2, 3, 4 }, aNs);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- per-tenant flush: one tenant's poison never blocks another tenant -----

    [Fact]
    public async Task PerTenantFlush_OneTenantsPoison_DoesNotBlockAnotherTenant()
    {
        var (queue, handler, path) = NewQueue();
        try
        {
            // Tenant A's poison event is at the HEAD of the shared FIFO - the backend rejects it forever.
            queue.Enqueue(Url, Body("A", 0) + "POISON", bearer: null, TenantA);
            // Tenant B's legitimate event is queued behind it.
            queue.Enqueue(Url, Body("B", 0), bearer: null, TenantB);

            var delivered = await queue.FlushOnceAsync();

            // B flushed PAST A's poison - a single-line "stop on first failure" would leave B undelivered.
            Assert.Equal(1, delivered);
            Assert.Contains(handler.Delivered, b => IsFrom(b, "B"));
            // A's poison stays queued (at-least-once, its own FIFO preserved) - not dropped, not delivered.
            Assert.DoesNotContain(handler.Delivered, b => IsFrom(b, "A"));
            Assert.Equal(1, queue.Depth);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- per-tenant FIFO within a tenant is preserved while other tenants flush past a block -----

    [Fact]
    public async Task PerTenantFlush_BlockedTenantKeepsItsOrder_WhileOthersDrain()
    {
        var (queue, handler, path) = NewQueue();
        try
        {
            queue.Enqueue(Url, Body("A", 0) + "POISON", bearer: null, TenantA); // A head: poison
            queue.Enqueue(Url, Body("A", 1), bearer: null, TenantA);            // A second: must NOT jump ahead
            queue.Enqueue(Url, Body("B", 0), bearer: null, TenantB);            // B: drains
            queue.Enqueue(Url, Body("B", 1), bearer: null, TenantB);            // B: drains

            var delivered = await queue.FlushOnceAsync();

            // Both of B's events delivered, in order; NONE of A's (its head is stuck, so its later event
            // waits behind it - A's FIFO is not violated by delivering A(1) ahead of the stuck A(0)).
            Assert.Equal(2, delivered);
            var bNs = handler.Delivered.Where(b => IsFrom(b, "B"))
                .Select(b => JsonDocument.Parse(b).RootElement.GetProperty("n").GetInt32()).ToArray();
            Assert.Equal(new[] { 0, 1 }, bNs);
            Assert.DoesNotContain(handler.Delivered, b => IsFrom(b, "A"));
            Assert.Equal(2, queue.Depth); // both A events remain queued
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- legacy (pre-tag) events are quarantined into an isolated lane, not a shared Local partition -----

    [Fact]
    public async Task Load_UntaggedFile_QuarantinesLegacyEvents_StillDelivers()
    {
        // A queue file written before the tenant tag existed: events with no "tenant" field.
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-tenant-{Guid.NewGuid():N}.json");
        var legacy = JsonSerializer.Serialize(new
        {
            events = new[]
            {
                new { id = "abc123", enqueuedAtUtc = DateTime.UtcNow, targetUrl = Url, body = "{}", bearer = (string?)null }
            }
        });
        await File.WriteAllTextAsync(path, legacy);
        try
        {
            var handler = new PoisonAwareHandler();
            var queue = new TelemetryRetryQueue(path, new HttpClient(handler), TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, queue.Depth); // loaded (quarantined into the isolated lane), not dropped, not quarantined-as-corrupt
            // It still flushes (at-least-once preserved) - the legacy event is delivered from its own lane.
            var delivered = await queue.FlushOnceAsync();
            Assert.Equal(1, delivered);
            await queue.DisposeAsync();
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task LegacyUntaggedPoison_DoesNotHeadOfLineBlock_ARealTenant()
    {
        // The residual the audit flags: pre-tag persisted events carry no tenant. The OLD behaviour defaulted
        // them to TenantId.Local - the SAME partition a real Local-tenant event lands in - so a single legacy
        // POISON event head-of-line-blocked the real Local tenant's delivery. The fix quarantines legacy events
        // into an ISOLATED lane, so a real tenant flushes past a stuck legacy poison.
        //
        // Revert-proof: this uses TenantId.Local as the real tenant on purpose. Reverting the Load default back
        // to Local puts the legacy poison in Local's partition, HOL-blocking the real Local event below, and
        // this test reddens. (A GUID tenant would NOT catch the revert - it never shared Local's partition.)
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-tenant-{Guid.NewGuid():N}.json");
        var legacy = JsonSerializer.Serialize(new
        {
            events = new[]
            {
                // A legacy untagged event the backend rejects forever, sitting at the HEAD of the file.
                new { id = "legacy-poison", enqueuedAtUtc = DateTime.UtcNow, targetUrl = Url, body = Body("legacy", 0) + "POISON", bearer = (string?)null }
            }
        });
        await File.WriteAllTextAsync(path, legacy);
        try
        {
            var handler = new PoisonAwareHandler();
            var queue = new TelemetryRetryQueue(path, new HttpClient(handler), TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, queue.Depth); // the legacy poison loaded into the quarantine lane

            // A real Local-tenant event enqueued AFTER the legacy poison (which is ahead of it in the list).
            queue.Enqueue(Url, Body("local", 0), bearer: null, TenantId.Local);

            var delivered = await queue.FlushOnceAsync();

            // The real Local event flushed PAST the stuck legacy poison. Under the reverted default-to-Local
            // behaviour the poison would share Local's partition and block it - nothing would deliver.
            Assert.Equal(1, delivered);
            Assert.Contains(handler.Delivered, b => IsFrom(b, "local"));
            Assert.DoesNotContain(handler.Delivered, b => IsFrom(b, "legacy"));
            // The legacy poison remains queued in its isolated lane (at-least-once), blocking only itself.
            Assert.Equal(1, queue.Depth);

            // And it really is in the isolated quarantine partition, NOT collapsed into the real Local one.
            await queue.DisposeAsync();
            var onDisk = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(onDisk);
            var tenants = doc.RootElement.GetProperty("events").EnumerateArray()
                .Select(e => e.GetProperty("tenant").GetString()).ToList();
            Assert.Contains(TelemetryRetryQueue.LegacyUntaggedPartition, tenants);
            Assert.DoesNotContain(TenantId.Local.Value, tenants); // the legacy event did NOT become Local
        }
        finally { Cleanup(path); }
    }

    // ----- the tenant tag survives a restart -----

    [Fact]
    public async Task PersistenceRoundTrip_PreservesTheTenantTag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-tenant-{Guid.NewGuid():N}.json");
        try
        {
            var h1 = new PoisonAwareHandler();
            var q1 = new TelemetryRetryQueue(path, new HttpClient(h1), TimeSpan.FromMilliseconds(50), maxSize: 2);
            // Cannot deliver yet - point at a URL the handler treats as poison so both stay queued.
            q1.Enqueue(Url, Body("A", 0) + "POISON", bearer: null, TenantA);
            q1.Enqueue(Url, Body("B", 0) + "POISON", bearer: null, TenantB);
            await q1.DisposeAsync();

            // Reconstruct over the same file. The per-tenant bound must still see A as A and B as B: enqueue
            // TWO more A events; A is bounded to 2, so A holds its newest 2 while B's one is untouched.
            var h2 = new PoisonAwareHandler();
            var q2 = new TelemetryRetryQueue(path, new HttpClient(h2), TimeSpan.FromMilliseconds(50), maxSize: 2);
            Assert.Equal(2, q2.Depth); // both survived the restart
            q2.Enqueue(Url, Body("A", 1), bearer: null, TenantA);
            q2.Enqueue(Url, Body("A", 2), bearer: null, TenantA);

            // If the tag had been lost, A's overflow would have evicted B. It did not: B's event remains.
            var onDisk = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(onDisk);
            var tenants = doc.RootElement.GetProperty("events").EnumerateArray()
                .Select(e => e.GetProperty("tenant").GetString()).ToList();
            Assert.Contains(TenantB.Value, tenants);
            Assert.Equal(2, tenants.Count(t => t == TenantA.Value)); // A bounded to its own 2
            await q2.DisposeAsync();
        }
        finally { Cleanup(path); }
    }

    // ----- validation: an invalid tenant is a loud reject, never a queued event under a guess -----

    [Fact]
    public async Task Enqueue_InvalidTenant_Throws()
    {
        var (queue, _, path) = NewQueue();
        try
        {
            Assert.Throws<ArgumentException>(() => queue.Enqueue(Url, Body("x", 0), bearer: null, default));
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }
}
