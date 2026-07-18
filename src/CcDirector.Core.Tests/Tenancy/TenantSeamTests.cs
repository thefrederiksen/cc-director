using System;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests.Tenancy;

/// <summary>
/// The tenancy seam primitives. These pin the two invariants the isolation design rests on - a tenant
/// id is validated (never a bare/empty string), and the single-tenant core resolves everything to the
/// one well-known local tenant so behavior is unchanged.
/// </summary>
public sealed class TenantSeamTests
{
    [Fact]
    public void Local_IsValidAndFlaggedLocal()
    {
        Assert.True(TenantId.Local.IsValid);
        Assert.True(TenantId.Local.IsLocal);
        Assert.Equal("local", TenantId.Local.Value);
    }

    [Fact]
    public void System_IsValidReservedAndDistinctFromLocalAndAccounts()
    {
        Assert.True(TenantId.System.IsValid);
        Assert.True(TenantId.System.IsSystem);
        Assert.Equal("system", TenantId.System.Value);
        // Distinct from local (self-host) and never flagged as local.
        Assert.False(TenantId.System.IsLocal);
        Assert.NotEqual(TenantId.Local.Value, TenantId.System.Value);
        // A real account tenant (a GUID) is neither system nor local.
        var account = new TenantId(Guid.NewGuid().ToString());
        Assert.False(account.IsSystem);
        Assert.False(account.IsLocal);
    }

    [Fact]
    public void Construct_TrimsAndKeepsValue()
    {
        var id = new TenantId("  acme  ");

        Assert.True(id.IsValid);
        Assert.False(id.IsLocal);
        Assert.Equal("acme", id.Value);
        Assert.Equal("acme", id.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Construct_RejectsNullEmptyOrWhitespace(string? bad)
    {
        Assert.Throws<ArgumentException>(() => new TenantId(bad!));
    }

    [Fact]
    public void Default_IsNotValid()
    {
        // A default(TenantId) bypasses the constructor; it must never be treated as a real tenant.
        TenantId uninitialized = default;

        Assert.False(uninitialized.IsValid);
        Assert.False(uninitialized.IsLocal);
        Assert.Equal("<invalid-tenant>", uninitialized.ToString());
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new TenantId("acme"), new TenantId("acme"));
        Assert.NotEqual(new TenantId("acme"), new TenantId("globex"));
    }

    [Fact]
    public void SingleTenantContext_AlwaysResolvesToLocal()
    {
        ITenantContext context = new SingleTenantContext();

        Assert.Equal(TenantId.Local, context.Current);
        Assert.True(context.Current.IsLocal);
    }
}
