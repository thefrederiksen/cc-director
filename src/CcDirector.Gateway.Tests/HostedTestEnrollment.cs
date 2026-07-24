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
        // Production requires an active entitlement to enroll (the paid-endpoint 402 gate). This helper
        // registers the device directly, bypassing that gate, so it seeds the entitlement itself - otherwise
        // the MTR-15 request-path cutoff denies this tenant on its first call. A test that wants a
        // non-entitled tenant seeds its own row (or none) instead of using this helper.
        gateway.SeedEntitlementForTest(accountSubject);
        var registration = gateway.Devices.RegisterForTenant(
            tenant,
            accountSubject,
            deviceId,
            machineName);
        return new HostedTestDevice(tenant, registration.DeviceKey);
    }
}

internal readonly record struct HostedTestDevice(TenantId Tenant, string DeviceKey);
