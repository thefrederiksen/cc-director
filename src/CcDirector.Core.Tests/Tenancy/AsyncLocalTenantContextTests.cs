using System;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests.Tenancy;

/// <summary>
/// The hosted ambient tenant context (Hosted Multi-Tenancy increment 1). These pin the deny-by-default
/// contract: no scope -> Current THROWS (never a default tenant), a scope resolves its tenant, scopes nest
/// and restore, and the ambient value flows across async boundaries down to the code that reads it.
/// </summary>
public sealed class AsyncLocalTenantContextTests
{
    [Fact]
    public void NoScope_Current_ThrowsFailClosed()
    {
        var ctx = new AsyncLocalTenantContext();

        // Deny-by-default: reading the tenant with no scope in effect must fail loud, never default.
        Assert.Throws<InvalidOperationException>(() => _ = ctx.Current);
    }

    [Fact]
    public void NoScope_CurrentOrNull_IsNull()
    {
        var ctx = new AsyncLocalTenantContext();

        Assert.Null(ctx.CurrentOrNull);
    }

    [Fact]
    public void InsideScope_Current_ResolvesThatTenant()
    {
        var ctx = new AsyncLocalTenantContext();

        using (ctx.Enter(new TenantId("t-alice")))
        {
            Assert.Equal("t-alice", ctx.Current.Value);
            Assert.Equal("t-alice", ctx.CurrentOrNull!.Value.Value);
        }
    }

    [Fact]
    public void AfterScopeDisposed_Current_ThrowsAgain()
    {
        var ctx = new AsyncLocalTenantContext();

        using (ctx.Enter(new TenantId("t-alice"))) { }

        Assert.Throws<InvalidOperationException>(() => _ = ctx.Current);
    }

    [Fact]
    public void NestedScopes_RestoreTheOuterTenantOnDispose()
    {
        var ctx = new AsyncLocalTenantContext();

        using (ctx.Enter(new TenantId("t-outer")))
        {
            Assert.Equal("t-outer", ctx.Current.Value);
            using (ctx.Enter(new TenantId("t-inner")))
            {
                Assert.Equal("t-inner", ctx.Current.Value);
            }
            Assert.Equal("t-outer", ctx.Current.Value);
        }
    }

    [Fact]
    public void SystemScope_ResolvesTheReservedSystemTenant()
    {
        var ctx = new AsyncLocalTenantContext();

        using (ctx.Enter(TenantId.System))
        {
            Assert.True(ctx.Current.IsSystem);
            Assert.Equal("system", ctx.Current.Value);
        }
    }

    [Fact]
    public void Enter_InvalidTenant_Throws()
    {
        var ctx = new AsyncLocalTenantContext();

        Assert.Throws<ArgumentException>(() => ctx.Enter(default));
    }

    [Fact]
    public async Task Scope_FlowsAcrossAnAwait()
    {
        var ctx = new AsyncLocalTenantContext();

        using (ctx.Enter(new TenantId("t-alice")))
        {
            await Task.Yield();
            // The ambient value flows across the await, so a store call after an await still resolves it.
            Assert.Equal("t-alice", ctx.Current.Value);
        }
    }
}
