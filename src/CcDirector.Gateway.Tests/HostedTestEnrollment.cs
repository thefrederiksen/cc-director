using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tests;

internal static class HostedTestEnrollment
{
    public static HostedTestDevice Enroll(
        GatewayHost gateway,
        string accountSubject,
        string email,
        string deviceId,
        string machineName)
    {
        var tenant = gateway.TenantRegistry.MintOrLookupBySubject(accountSubject, email);
        var registration = gateway.Devices.RegisterForTenant(
            tenant,
            accountSubject,
            deviceId,
            machineName);
        return new HostedTestDevice(tenant, registration.DeviceKey);
    }
}

internal readonly record struct HostedTestDevice(TenantId Tenant, string DeviceKey);
