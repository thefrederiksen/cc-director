using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The account-to-tenant resolver (Hosted Multi-Tenancy increment 1). These tests exercise the mint-or-lookup
/// contract over a real (throwaway) EF database: a first-seen account subject mints a fresh tenant, the SAME
/// subject resolves to the SAME tenant (so a second device of one account lands in one tenant), distinct
/// subjects get distinct tenants, and the email is display metadata only - never the mapping key.
/// </summary>
public sealed class TenantRegistryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void MintOrLookup_FirstSeenSubject_MintsAValidTenant()
    {
        var registry = new TenantRegistry(_harness.Open());

        var tenant = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.True(tenant.IsValid);
        Assert.False(tenant.IsLocal);
        // A code-generated GUID string, not the subject and not the email.
        Assert.True(Guid.TryParse(tenant.Value, out _));
    }

    [Fact]
    public void MintOrLookup_SameSubjectTwice_ResolvesToTheSameTenant()
    {
        var registry = new TenantRegistry(_harness.Open());

        var first = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var second = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void MintOrLookup_SameSubjectAfterRestart_StillResolvesToTheSameTenant()
    {
        var minted = new TenantRegistry(_harness.Open()).MintOrLookupBySubject("sub-alice", "alice@example.com");

        // Re-open the SAME database file (a Gateway restart) and resolve again.
        var afterRestart = new TenantRegistry(_harness.Open()).MintOrLookupBySubject("sub-alice", "alice@example.com");

        Assert.Equal(minted.Value, afterRestart.Value);
    }

    [Fact]
    public void MintOrLookup_DifferentSubjects_GetDifferentTenants()
    {
        var registry = new TenantRegistry(_harness.Open());

        var alice = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var bob = registry.MintOrLookupBySubject("sub-bob", "bob@example.com");

        Assert.NotEqual(alice.Value, bob.Value);
    }

    [Fact]
    public void MintOrLookup_EmailIsNotTheKey_TwoSubjectsSharingAnEmailGetTwoTenants()
    {
        var registry = new TenantRegistry(_harness.Open());

        // The stable subject is the key; a shared (or reused) email must NEVER collapse two accounts.
        var first = registry.MintOrLookupBySubject("sub-one", "shared@example.com");
        var second = registry.MintOrLookupBySubject("sub-two", "shared@example.com");

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void MintOrLookup_NullOrBlankEmail_StillMints()
    {
        var registry = new TenantRegistry(_harness.Open());

        var tenant = registry.MintOrLookupBySubject("sub-no-email", null);

        Assert.True(tenant.IsValid);
    }

    [Fact]
    public void MintOrLookup_BlankSubject_Throws()
    {
        var registry = new TenantRegistry(_harness.Open());

        Assert.Throws<ArgumentException>(() => registry.MintOrLookupBySubject("   ", "x@example.com"));
    }

    [Fact]
    public void Lookup_UnknownSubject_ReturnsNull()
    {
        var registry = new TenantRegistry(_harness.Open());

        Assert.Null(registry.LookupBySubject("sub-never-seen"));
    }

    [Fact]
    public void Lookup_KnownSubject_ReturnsTheMintedTenant()
    {
        var registry = new TenantRegistry(_harness.Open());
        var minted = registry.MintOrLookupBySubject("sub-alice", "alice@example.com");

        var looked = registry.LookupBySubject("sub-alice");

        Assert.NotNull(looked);
        Assert.Equal(minted.Value, looked!.Value.Value);
    }
}
