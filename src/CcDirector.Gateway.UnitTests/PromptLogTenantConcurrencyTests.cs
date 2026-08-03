using System;
using System.IO;
using System.Reflection;
using System.Threading;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Audit gap audit-a: the prompt log correctly partitions each tenant's daily FILES by directory, but every
/// tenant's append and daily-file read used to share ONE process-global lock. Retention is unbounded, so an
/// append or read of one tenant's large file (which holds the lock for the whole File.AppendAllLines /
/// File.ReadAllLines) would stall every OTHER tenant's unrelated prompt IO - correctness partitioning without
/// concurrency partitioning. The fix gives each tenant its OWN gate, keyed by <see cref="TenantId"/>, so a
/// caller only ever takes the lock of its own partition.
///
/// This mirrors the proven shape of <c>FleetSessionNumberAllocatorTenancyTests</c>: hold tenant A's gate, prove
/// tenant B's append completes concurrently WITHOUT waiting, and prove a same-tenant A append DOES wait (the
/// destructibility control - it proves the gate this test holds is the very one A's own operations take, so
/// "B proceeded" is real independence rather than a lock nobody uses). Revert to a single shared lock and the
/// tenant-B property assertion reddens: B would then block on the exact lock this test holds.
/// </summary>
public sealed class PromptLogTenantConcurrencyTests
{
    // Two DISTINCT minted account tenants - the canonical lowercase GUID shape the prompt log requires for a
    // non-local partition. Two tenants means two partitions means, after the fix, two gates.
    private static readonly TenantId TenantA = new("aaaaaaaa-1111-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly TenantId TenantB = new("bbbbbbbb-2222-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public void One_tenant_holding_its_gate_does_not_block_another_tenants_append()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-plog-conc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new GatewayPromptLog(root);

            // Materialize both partitions' gates so each has a lock object to reach, and grab the EXACT gate
            // tenant A's own appends take.
            var tenantAGate = GateFor(log, TenantA);
            _ = GateFor(log, TenantB);
            var aBlockedAppendDone = new ManualResetEventSlim(false);

            lock (tenantAGate)
            {
                // Tenant A's gate is HELD by this thread; any file IO on A's partition must wait on it.

                // THE PROPERTY - tenant B appends on ANOTHER thread and completes without waiting on A's gate.
                var bWritten = -1;
                var bDone = new ManualResetEventSlim(false);
                var bThread = new Thread(() =>
                {
                    bWritten = log.Append(TenantB, new[] { Record("bravo-concurrent") });
                    bDone.Set();
                }) { IsBackground = true };
                bThread.Start();

                Assert.True(bDone.Wait(TimeSpan.FromSeconds(5)),
                    "Tenant B's append blocked while tenant A held ITS OWN gate - the prompt log is serializing tenants on a shared lock.");
                Assert.Equal(1, bWritten);

                // DESTRUCTIBILITY CONTROL - a same-tenant (A) append really DOES block on the held gate.
                var aThread = new Thread(() =>
                {
                    log.Append(TenantA, new[] { Record("alpha-concurrent") });
                    aBlockedAppendDone.Set();
                }) { IsBackground = true };
                aThread.Start();
                Assert.False(aBlockedAppendDone.Wait(TimeSpan.FromSeconds(1)),
                    "Tenant A's own append completed while A's gate was held - the gate held is not the one A's partition takes, so the property assertion above proves nothing.");
            }

            // Once A's gate is released, the previously-blocked A append completes - closing the control.
            Assert.True(aBlockedAppendDone.Wait(TimeSpan.FromSeconds(5)),
                "Tenant A's append never completed after its gate was released.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    private static PromptRecord Record(string text) => new()
    {
        TsUtc = DateTime.UtcNow,
        Machine = "M",
        SessionId = "s",
        Agent = "ClaudeCode",
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = 1,
        Text = text,
    };

    /// <summary>
    /// Reach the per-tenant gate object a caller for that tenant takes. This is a concurrency proof, so it must
    /// hold the EXACT lock the tenant's own append/read takes - there is no public seam for that by design.
    /// </summary>
    private static object GateFor(GatewayPromptLog log, TenantId tenant)
    {
        var method = typeof(GatewayPromptLog).GetMethod("GateFor", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.Invoke(log, new object[] { tenant })!;
    }
}
