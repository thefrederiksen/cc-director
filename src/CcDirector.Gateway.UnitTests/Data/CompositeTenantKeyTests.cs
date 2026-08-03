using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The composite tenant keys (Hosted Multi-Tenancy). Tables whose natural/fixed key is only unique PER TENANT
/// - workflows (fixed built-in id), workflow_versions (built-in WorkflowId + version), mission_notes (mission
/// name), cron_jobs (per-tenant minted short id), snoozes and session_spend (the session id a caller
/// presents), push_subscriptions (the browser push endpoint a caller presents) - carry a COMPOSITE primary
/// key/unique index that includes tenant_id, so the reserved SYSTEM tenant and a real (or leftover 'local')
/// tenant can each hold their own copy without colliding at the database.
///
/// The first test reproduces the EXACT production crash: a live single-tenant gateway (built-ins under
/// 'local') upgrading to multi-tenant, whose startup SYSTEM seed would collide with the leftover 'local'
/// built-ins on a non-composite key. With the composite key it upgrades automatically - no crash, no truncate.
///
/// The last three tests cover the CALLER-SUPPLIED half of the same hazard. Every store upserts by reading
/// THROUGH the tenant query filter and adding when it finds nothing, so on a single-column key a second
/// tenant presenting an identifier the first tenant already holds finds nothing (the filter hides the other
/// tenant's row), inserts, and hits a PRIMARY KEY violation. That is a cross-tenant SQUAT (the second tenant
/// cannot use an identifier the first holds) and an EXISTENCE ORACLE (the failure tells it the first holds
/// it). These three drive the REAL stores, not the context directly, so they exercise the actual upsert.
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

    [Fact]
    public void TwoTenants_MintTheSameCronJobId_BothSucceed_AndStayIsolated()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());

        // CronJobStore mints a short cj_ id checked per tenant, so two tenants CAN mint the same id. The
        // composite (tenant_id, Id) must let both coexist rather than collide on a global Id PK.
        WriteCronJob(db, ambient, tenantA, "cj_shared", "alice-job");
        WriteCronJob(db, ambient, tenantB, "cj_shared", "bob-job");

        Assert.Equal("alice-job", ReadCronName(db, ambient, tenantA, "cj_shared"));
        Assert.Equal("bob-job", ReadCronName(db, ambient, tenantB, "cj_shared"));
    }

    // ---- Caller-supplied keys: two tenants present the SAME session id / push endpoint ------------------

    [Fact]
    public void TwoTenants_SnoozeTheSameSessionId_BothSucceed_AndStayIsolated()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());

        SnoozeRegistry registry;
        using (ambient.Enter(tenantA))
            registry = new SnoozeRegistry(db, harness.LegacyPath("snoozes.json"));

        // The session id is CALLER-SUPPLIED: it arrives on the snooze request from whatever Director asked.
        // Nothing stops two tenants from presenting the same string.
        const string sharedSessionId = "11111111-1111-1111-1111-111111111111";
        var untilA = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var untilB = new DateTime(2031, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        using (ambient.Enter(tenantA))
            registry.Snooze(sharedSessionId, untilA, "director-a");

        // On a SessionId-ONLY primary key the tenant filter hides tenant A's row, the store's upsert takes
        // the insert branch, and the database rejects it - tenant A has squatted the id for everyone.
        var crash = Record.Exception(() =>
        {
            using (ambient.Enter(tenantB))
                registry.Snooze(sharedSessionId, untilB, "director-b");
        });
        Assert.Null(crash);

        // Both directions of isolation: each tenant reads back its OWN hold and never the other's.
        using (ambient.Enter(tenantA))
        {
            Assert.Equal(untilA, registry.SnoozeUntilFor(sharedSessionId));
            Assert.Equal("director-a", registry.DirectorIdFor(sharedSessionId));
        }
        using (ambient.Enter(tenantB))
        {
            Assert.Equal(untilB, registry.SnoozeUntilFor(sharedSessionId));
            Assert.Equal("director-b", registry.DirectorIdFor(sharedSessionId));
        }
    }

    [Fact]
    public void TwoTenants_RecordSpendForTheSameSessionId_BothSucceed_AndStayIsolated()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());
        var store = new SessionSpendStore(db);

        const string sharedSessionId = "22222222-2222-2222-2222-222222222222";

        using (ambient.Enter(tenantA))
            store.Record(SpendRequest(sharedSessionId, "claude", inputTokens: 111));

        var crash = Record.Exception(() =>
        {
            using (ambient.Enter(tenantB))
                store.Record(SpendRequest(sharedSessionId, "codex", inputTokens: 222));
        });
        Assert.Null(crash);

        using (ambient.Enter(tenantA))
        {
            var a = store.Get(sharedSessionId);
            Assert.NotNull(a);
            Assert.Equal("claude", a!.AgentKind);
            Assert.Equal(111, a.InputTokens);
        }
        using (ambient.Enter(tenantB))
        {
            var b = store.Get(sharedSessionId);
            Assert.NotNull(b);
            Assert.Equal("codex", b!.AgentKind);
            Assert.Equal(222, b.InputTokens);
        }
    }

    [Fact]
    public void TwoTenants_RegisterTheSamePushEndpoint_BothSucceed_AndStayIsolated()
    {
        using var harness = new GatewayDbTestHarness();
        var ambient = new AsyncLocalTenantContext();
        var db = harness.Open(ambient);

        var tenantA = new TenantId(Guid.NewGuid().ToString());
        var tenantB = new TenantId(Guid.NewGuid().ToString());

        PushSubscriptionStore store;
        using (ambient.Enter(tenantA))
            store = new PushSubscriptionStore(db, harness.LegacyPath("push-subscriptions.json"));

        // The endpoint is the browser's push URL - entirely caller-supplied, and the table's whole key.
        const string sharedEndpoint = "https://push.example.test/send/shared-endpoint";

        // Written in the OTHER order from the snooze test (tenant B first) so neither order is privileged.
        using (ambient.Enter(tenantB))
            store.Add(sharedEndpoint, "p256dh-b", "auth-b");

        var crash = Record.Exception(() =>
        {
            using (ambient.Enter(tenantA))
                store.Add(sharedEndpoint, "p256dh-a", "auth-a");
        });
        Assert.Null(crash);

        using (ambient.Enter(tenantA))
        {
            var only = Assert.Single(store.All());
            Assert.Equal(sharedEndpoint, only.Endpoint);
            Assert.Equal("p256dh-a", only.P256dh);
        }
        using (ambient.Enter(tenantB))
        {
            var only = Assert.Single(store.All());
            Assert.Equal(sharedEndpoint, only.Endpoint);
            Assert.Equal("p256dh-b", only.P256dh);
        }
    }

    private static RecordSessionSpendRequest SpendRequest(string sessionId, string agentKind, long inputTokens)
        => new()
        {
            SessionId = sessionId,
            AgentKind = agentKind,
            TokensCaptured = true,
            InputTokens = inputTokens,
            BillingMode = "subscription-included",
        };

    private static void WriteCronJob(CcDirector.Gateway.Data.GatewayDatabase db, AsyncLocalTenantContext ambient,
        TenantId tenant, string id, string name)
    {
        using (ambient.Enter(tenant))
        using (var ctx = db.CreateContext())
        {
            ctx.CronJobs.Add(new CronJobEntity
            {
                Id = id,
                Name = name,
                TenantId = ctx.ActiveTenant!,
                ScheduleKind = "cron",
                TimeZoneId = "UTC",
                CreatedUtc = DateTime.UtcNow,
            });
            ctx.SaveChanges();
        }
    }

    private static string? ReadCronName(CcDirector.Gateway.Data.GatewayDatabase db, AsyncLocalTenantContext ambient,
        TenantId tenant, string id)
    {
        using (ambient.Enter(tenant))
        using (var ctx = db.CreateContext())
            return ctx.CronJobs.Where(j => j.Id == id).Select(j => j.Name).SingleOrDefault();
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
