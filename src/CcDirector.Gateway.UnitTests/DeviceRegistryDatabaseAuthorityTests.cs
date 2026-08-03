using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class DeviceRegistryDatabaseAuthorityTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private string LegacyPath => _harness.LegacyPath("devices.json");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ResolveCredential_TwoLiveRegistries_ObservesEnrollmentAndRotationImmediately()
    {
        using var first = new DeviceRegistry(_harness.Open(), LegacyPath);
        using var second = new DeviceRegistry(_harness.Open(), LegacyPath);

        var originalKey = first.RegisterIfAbsent("shared-device", "WORKSTATION").DeviceKey;

        Assert.True(second.IsValidDeviceKey(originalKey));

        var rotatedKey = first.RegisterIfAbsent("shared-device", "WORKSTATION").DeviceKey;

        Assert.False(second.IsValidDeviceKey(originalKey));
        Assert.True(second.IsValidDeviceKey(rotatedKey));
    }

    [Fact]
    public void ResolveCredential_TwoLiveRegistries_ObservesTenantRevocationImmediately()
    {
        var firstDatabase = _harness.Open();
        var secondDatabase = _harness.Open();
        using var first = new DeviceRegistry(firstDatabase, LegacyPath, isHosted: true);
        using var second = new DeviceRegistry(secondDatabase, LegacyPath, isHosted: true);
        var tenant = new TenantRegistry(firstDatabase)
            .MintOrLookupBySubject("subject-authority", "owner@example.com");
        var key = first.RegisterForTenant(
            tenant,
            "subject-authority",
            "tenant-device",
            "WORKSTATION").DeviceKey;

        Assert.Equal(DeviceCredentialResolutionKind.Active, second.ResolveCredential(key).Kind);

        Assert.Equal(1, first.RevokeTenant(tenant, "security_test"));

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, second.ResolveCredential(key).Kind);
        Assert.False(second.IsValidDeviceKey(key));
    }

    [Fact]
    public void RegisterForTenant_PersistsOnlyHashAndCommitsTenantBindingAtomically()
    {
        var database = _harness.Open();
        using var registry = new DeviceRegistry(database, LegacyPath, isHosted: true);
        var tenant = new TenantRegistry(database)
            .MintOrLookupBySubject("subject-persistence", "owner@example.com");

        var issued = registry.RegisterForTenant(
            tenant,
            "subject-persistence",
            "persisted-device",
            "WORKSTATION");

        using var context = database.CreateUnscopedContext();
        var stored = context.DeviceCredentials.AsNoTracking().Single();
        var resolution = registry.ResolveCredential(issued.DeviceKey);

        Assert.DoesNotContain(issued.DeviceKey, context.Entry(stored).CurrentValues.Properties
            .Select(property => context.Entry(stored).Property(property.Name).CurrentValue?.ToString() ?? ""));
        Assert.NotEqual(issued.DeviceKey, stored.DeviceKeyHash);
        Assert.Equal(tenant.Value, stored.TenantId);
        Assert.Equal("subject-persistence", stored.AccountSubject);
        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
        Assert.Equal(tenant.Value, resolution.Identity?.TenantId);
    }

    [Fact]
    public void ResolveCredential_HostedBindingWithoutCanonicalTenantMapping_IsRejected()
    {
        var database = _harness.Open();
        using var registry = new DeviceRegistry(database, LegacyPath, isHosted: true);
        var unrecognizedTenant = new TenantId(Guid.NewGuid().ToString());
        var issued = registry.RegisterForTenant(
            unrecognizedTenant,
            "subject-without-canonical-mapping",
            "inconsistent-device",
            "WORKSTATION");

        Assert.Null(new TenantRegistry(database).LookupBySubject("subject-without-canonical-mapping"));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, registry.ResolveCredential(issued.DeviceKey).Kind);
        Assert.False(registry.IsValidDeviceKey(issued.DeviceKey));
    }

    [Fact]
    public async Task RegisterIfAbsent_TwoLiveRegistriesRace_LeavesOneRowAndOneValidKey()
    {
        using var first = new DeviceRegistry(_harness.Open(), LegacyPath);
        using var second = new DeviceRegistry(_harness.Open(), LegacyPath);

        var issued = await Task.WhenAll(
            Task.Run(() => first.RegisterIfAbsent("raced-device", "FIRST").DeviceKey),
            Task.Run(() => second.RegisterIfAbsent("raced-device", "SECOND").DeviceKey));

        Assert.Equal(2, issued.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(issued, first.IsValidDeviceKey);
        Assert.Equal(first.IsValidDeviceKey(issued[0]), second.IsValidDeviceKey(issued[0]));
        Assert.Equal(first.IsValidDeviceKey(issued[1]), second.IsValidDeviceKey(issued[1]));
        Assert.Equal(1, first.Count);
    }

    [Fact]
    public async Task Register_TwoLiveRegistriesWriteDistinctDevices_PreservesBoth()
    {
        using var first = new DeviceRegistry(_harness.Open(), LegacyPath);
        using var second = new DeviceRegistry(_harness.Open(), LegacyPath);

        var issued = await Task.WhenAll(
            Task.Run(() => first.Register("device-a", "FIRST").DeviceKey),
            Task.Run(() => second.Register("device-b", "SECOND").DeviceKey));

        Assert.True(first.IsValidDeviceKey(issued[0]));
        Assert.True(first.IsValidDeviceKey(issued[1]));
        Assert.True(second.IsValidDeviceKey(issued[0]));
        Assert.True(second.IsValidDeviceKey(issued[1]));
        Assert.Equal(2, second.Count);
    }
}
