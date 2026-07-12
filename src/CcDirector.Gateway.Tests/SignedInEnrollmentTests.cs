using System.Net;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the two load-bearing guardrails of the sign-in enrollment path (issue #1069):
/// the AUTH gate (POST /devices/enroll-signed-in only mints for a proven-loopback caller when the
/// Gateway is signed in) and the IDEMPOTENT mint (a device that already holds a key gets the SAME key
/// back, never a fresh one). The idempotency test pins the guardrail against the #1136 auto-mint key
/// leak: enrolling the same device twice must not rotate or multiply keys.
/// </summary>
public sealed class SignedInEnrollmentTests
{
    private static readonly AccountIdentity Signed = new("person@example.com", "google");

    // ---- Auth gate (SignedInEnrollmentEndpoint.Evaluate) ---------------------------------------

    [Fact]
    public void Evaluate_LoopbackAndSignedIn_Allows()
    {
        Assert.Equal(EnrollGateDecision.Allow,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: true, Signed));
        Assert.Equal(EnrollGateDecision.Allow,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.IPv6Loopback, isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_NonLoopbackCaller_IsRejectedAsNotSameMachine()
    {
        // A tailnet / LAN address must never reach the mint, even when the Gateway is signed in.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("100.86.144.11"), isSignedIn: true, Signed));
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("192.168.1.50"), isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_UnknownCaller_IsRejected_NeverAssumedSameMachine()
    {
        // A null remote address is NOT treated as same-machine here (this mints a credential), unlike the
        // conservative sign-in routing default.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(null, isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_LoopbackButNotSignedIn_IsRejected()
    {
        Assert.Equal(EnrollGateDecision.NotSignedIn,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: false, identity: null));
    }

    [Fact]
    public void Evaluate_LoopbackSignedInButNoIdentity_IsRejected()
    {
        // Signed-in flag without a resolved identity (stale credential) must not mint - there is nothing
        // to bind the key to.
        Assert.Equal(EnrollGateDecision.NotSignedIn,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: true, identity: null));
    }

    [Fact]
    public void Evaluate_SameMachineCheckOutranksSignedIn()
    {
        // A non-loopback caller is rejected as not-same-machine even if also not signed in - the transport
        // proof is checked first so a remote caller never learns the sign-in state.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("10.0.0.5"), isSignedIn: false, identity: null));
    }

    // ---- Idempotent mint (DeviceRegistry.RegisterIfAbsent) -------------------------------------

    [Fact]
    public void RegisterIfAbsent_SameDeviceTwice_ReturnsTheSameKey_NoReMint()
    {
        using var temp = new TempStore();
        var registry = new DeviceRegistry(temp.Path);

        var first = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");
        var second = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");

        Assert.False(string.IsNullOrEmpty(first.DeviceKey));
        Assert.Equal(first.DeviceKey, second.DeviceKey); // idempotent: the SAME key, not a fresh one
        Assert.Equal(1, registry.Count); // exactly one device, no duplicates
    }

    [Fact]
    public void RegisterIfAbsent_ManyCalls_MintOnlyOnce()
    {
        // The poll loop is guarded to never call enroll repeatedly, but the server is idempotent anyway:
        // even ten calls yield one key and one device (the #1136 guardrail).
        using var temp = new TempStore();
        var registry = new DeviceRegistry(temp.Path);

        var key = registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey;
        for (var i = 0; i < 10; i++)
            Assert.Equal(key, registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey);

        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RegisterIfAbsent_DistinctDevices_GetDistinctKeys()
    {
        using var temp = new TempStore();
        var registry = new DeviceRegistry(temp.Path);

        var a = registry.RegisterIfAbsent("device-a", "MACHINE_A").DeviceKey;
        var b = registry.RegisterIfAbsent("device-b", "MACHINE_B").DeviceKey;

        Assert.NotEqual(a, b);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void RegisterIfAbsent_MintedKeyValidates()
    {
        using var temp = new TempStore();
        var registry = new DeviceRegistry(temp.Path);

        var key = registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey;

        Assert.True(registry.IsValidDeviceKey(key));
    }

    [Fact]
    public void RegisterIfAbsent_BlankDeviceId_Throws()
    {
        using var temp = new TempStore();
        var registry = new DeviceRegistry(temp.Path);

        Assert.Throws<ArgumentException>(() => registry.RegisterIfAbsent("  ", "MACHINE_A"));
    }

    // An isolated on-disk registry file per test, cleaned up afterward.
    private sealed class TempStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cc-enroll-test-" + System.IO.Path.GetRandomFileName(), "devices.json");

        public void Dispose()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
