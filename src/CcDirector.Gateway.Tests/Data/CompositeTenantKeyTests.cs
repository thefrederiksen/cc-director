using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The composite tenant keys (Hosted Multi-Tenancy). Tables whose natural/fixed key is only unique PER TENANT
/// - workflows (fixed built-in id), workflow_versions (built-in WorkflowId + version), mission_notes (mission
/// name) - carry a COMPOSITE primary key/unique index that includes tenant_id, so the reserved SYSTEM tenant
/// and a real (or leftover 'local') tenant can each hold their own copy without colliding at the database.
///
/// The first test reproduces the EXACT production crash: a live single-tenant gateway (built-ins under
/// 'local') upgrading to multi-tenant, whose startup SYSTEM seed would collide with the leftover 'local'
/// built-ins on a non-composite key. With the composite key it upgrades automatically - no crash, no truncate.
/// </summary>
public sealed class CompositeTenantKeyTests
{
    // ---- The exact-crash repro: pre-existing 'local' built-ins + the SYSTEM seed must NOT crash ----------

    [Fact]
    public void SystemSeed_OverExistingLocalBuiltIns_DoesNotCrash_AndBothCoexist()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        // Simulate a live SINGLE-TENANT gateway: the built-in workflows were seeded under 'local'.
        using (ambient.Enter(TenantId.Local))
        using (var ctx = db.CreateContext())
            BuiltInWorkflowSeeder.Seed(ctx);

        // Now the SAME gateway boots as MULTI-TENANT: startup seeds the built-ins under the reserved SYSTEM
        // tenant. On a non-composite key this hits the leftover 'local' built-in ids and StartAsync crashes.
        var crash = Record.Exception(() =>
        {
            using (ambient.Enter(TenantId.System))
            using (var ctx = db.CreateContext())
                BuiltInWorkflowSeeder.Seed(ctx);
        });

        Assert.Null(crash); // no DbUpdateException - the composite key lets SYSTEM built-ins coexist with 'local'

        // Both tenants hold their own copy of the built-ins.
        using (ambient.Enter(TenantId.Local))
        using (var ctx = db.CreateContext())
            Assert.NotEmpty(ctx.Workflows.ToList());
        using (ambient.Enter(TenantId.System))
        using (var ctx = db.CreateContext())
            Assert.NotEmpty(ctx.Workflows.ToList());
    }

    [Fact]
    public void SystemSeed_IsIdempotent_ReRunningItDoesNotCrashOrDuplicate()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        int SeedSystemAndCount()
        {
            using (ambient.Enter(TenantId.System))
            {
                using (var ctx = db.CreateContext())
                    BuiltInWorkflowSeeder.Seed(ctx);
                using var read = db.CreateContext();
                return read.Workflows.Count();
            }
        }

        var first = SeedSystemAndCount();
        var second = SeedSystemAndCount(); // re-run (a restart) - must not crash and must not duplicate

        Assert.True(first > 0);
        Assert.Equal(first, second);
    }

    // ---- Same-key two-tenant isolation: two tenants use the SAME key, both succeed, isolated --------------

    [Fact]
    public void TwoTenants_WriteTheSameMissionKey_BothSucceed_AndStayIsolated()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());

        // Both tenants write a mission note under the SAME normalized key "shared-mission".
        WriteNote(db, ambient, tenantA, "shared-mission", "alice why");
        WriteNote(db, ambient, tenantB, "shared-mission", "bob why");

        // Each sees ONLY its own note for that key - the composite (tenant_id, Key) kept both rows.
        Assert.Equal("alice why", ReadWhy(db, ambient, tenantA, "shared-mission"));
        Assert.Equal("bob why", ReadWhy(db, ambient, tenantB, "shared-mission"));
    }

    private static void WriteNote(CcDirector.Gateway.Data.GatewayDatabase db, AsyncLocalTenantContext ambient,
        TenantId tenant, string key, string why)
    {
        using (ambient.Enter(tenant))
        using (var ctx = db.CreateContext())
        {
            ctx.MissionNotes.Add(new MissionNoteEntity
            {
                Key = key,
                Mission = key,
                Why = why,
                UpdatedAtUtc = DateTime.UtcNow,
                TenantId = ctx.ActiveTenant!,
            });
            ctx.SaveChanges();
        }
    }

    private static string? ReadWhy(CcDirector.Gateway.Data.GatewayDatabase db, AsyncLocalTenantContext ambient,
        TenantId tenant, string key)
    {
        using (ambient.Enter(tenant))
        using (var ctx = db.CreateContext())
            return ctx.MissionNotes.Where(n => n.Key == key).Select(n => n.Why).SingleOrDefault();
    }
}
