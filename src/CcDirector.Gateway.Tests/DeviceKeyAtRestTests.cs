using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1878: the device registry file used to hold every enrolled device's per-device key in
/// PLAINTEXT. That key is a bearer secret - it authenticates every Director-to-Gateway call - and on the
/// hosted Gateway the one file holds every tenant's device on shared infrastructure, so anything that
/// could read the file could impersonate every tenant's Director.
///
/// These tests pin the fix from both ends:
///   1. WHAT IS WRITTEN: the file must contain no usable credential - the plaintext key must not appear
///      in it, and what does appear must be the one-way hash.
///   2. WHAT KEEPS WORKING: a device enrolled BEFORE the change - whose key exists only in the plaintext
///      file and in that machine's credential file - must still authenticate afterwards, and must still
///      resolve to its bound tenant. There is no re-enrollment path that does not involve a human, so a
///      change that invalidated those keys would take the live hosted box down.
/// </summary>
public sealed class DeviceKeyAtRestTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-devkeys-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "devices.json");

    public DeviceKeyAtRestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string ExpectedHash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    // ---- 1. What is written -------------------------------------------------------------------

    [Fact]
    public void EnrolledKey_IsNeverWrittenToTheRegistryFileInPlaintext()
    {
        var registry = new DeviceRegistry(StorePath);
        var issued = registry.Register("device-a", "MACHINE-A").DeviceKey;

        var onDisk = File.ReadAllText(StorePath);

        // The canary. It names the value that got through, so a failure is a one-line diagnosis: if the
        // plaintext key is in the file, the file IS the credential and a read of it is a full compromise.
        Assert.False(
            onDisk.Contains(issued, StringComparison.Ordinal),
            $"devices.json still holds the plaintext device key. The key that got through is: {issued}");
    }

    [Fact]
    public void RegistryFile_HoldsTheOneWayHashOfTheKey()
    {
        var registry = new DeviceRegistry(StorePath);
        var issued = registry.Register("device-a", "MACHINE-A").DeviceKey;

        var onDisk = File.ReadAllText(StorePath);

        // The other half of the canary: the key is not merely absent (which an empty or broken file would
        // also satisfy) - its hash is what took its place, so the record is still a usable verifier.
        Assert.Contains(ExpectedHash(issued), onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEnrolledKey_IsAbsentFromTheFile_EvenWithManyDevices()
    {
        var registry = new DeviceRegistry(StorePath);
        var keys = new List<string>();
        for (var i = 0; i < 5; i++)
            keys.Add(registry.Register($"device-{i}", $"MACHINE-{i}").DeviceKey);

        var onDisk = File.ReadAllText(StorePath);

        foreach (var key in keys)
            Assert.False(
                onDisk.Contains(key, StringComparison.Ordinal),
                $"devices.json still holds a plaintext device key. The key that got through is: {key}");
    }

    [Fact]
    public void IssuedKey_StillAuthenticates_AndSurvivesARestart()
    {
        var registry = new DeviceRegistry(StorePath);
        var issued = registry.Register("device-a", "MACHINE-A").DeviceKey;

        Assert.True(registry.IsValidDeviceKey(issued));
        Assert.True(new DeviceRegistry(StorePath).IsValidDeviceKey(issued),
            "a per-device key must keep working across a Gateway restart");
    }

    [Fact]
    public void AWrongKey_IsStillRejected()
    {
        var registry = new DeviceRegistry(StorePath);
        registry.Register("device-a", "MACHINE-A");

        Assert.False(registry.IsValidDeviceKey("not-a-real-key"));
        Assert.False(registry.IsValidDeviceKey(""));
        Assert.False(registry.IsValidDeviceKey(null));
        Assert.Null(registry.TenantForKey("not-a-real-key"));
        Assert.Null(registry.DeviceTypeForKey("not-a-real-key"));
    }

    // ---- 2. What keeps working: a device enrolled BEFORE the change ----------------------------

    /// <summary>
    /// A registry file in EXACTLY the shape the Gateway wrote before issue #1878: the plaintext key in
    /// <c>DeviceKey</c>, no hash field at all, with the hosted account and tenant binding alongside it.
    /// This is what sits on the live hosted box's file share right now.
    /// </summary>
    private void WritePreChangeStore(string deviceId, string plaintextKey, string tenantId)
    {
        File.WriteAllText(StorePath, $$"""
        [
          {
            "DeviceId": "{{deviceId}}",
            "MachineName": "ALREADY-ENROLLED-MACHINE",
            "DeviceKey": "{{plaintextKey}}",
            "IssuedAtUtc": "2026-07-01T12:00:00Z",
            "Status": "active",
            "Platform": "windows",
            "DeviceType": "workstation",
            "CloudDeviceId": "cloud-device-77",
            "AccountSubject": "sub-already-enrolled",
            "TenantId": "{{tenantId}}"
          }
        ]
        """);
    }

    [Fact]
    public void ADeviceEnrolledBeforeTheChange_StillAuthenticatesAfterIt()
    {
        // This is the test that stops the fix from taking the live hosted box down: the machine holds this
        // key in its own credential file and there is no way to re-issue it without a human.
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        var registry = new DeviceRegistry(StorePath);

        Assert.True(registry.IsValidDeviceKey(alreadyIssuedKey),
            "a device enrolled before the keys were hashed must keep authenticating with the key it already holds");
    }

    [Fact]
    public void ADeviceEnrolledBeforeTheChange_StillResolvesToItsBoundTenant()
    {
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        var registry = new DeviceRegistry(StorePath);

        // The hosted tenant boundary resolves the tenant from this same verified key on every request.
        Assert.Equal("tenant-already-enrolled", registry.TenantForKey(alreadyIssuedKey));
        Assert.Equal("workstation", registry.DeviceTypeForKey(alreadyIssuedKey));
    }

    [Fact]
    public void LoadingAPreChangeStore_ScrubsThePlaintextKeyFromDisk()
    {
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");
        Assert.Contains(alreadyIssuedKey, File.ReadAllText(StorePath), StringComparison.Ordinal);

        _ = new DeviceRegistry(StorePath);

        var onDisk = File.ReadAllText(StorePath);
        Assert.False(
            onDisk.Contains(alreadyIssuedKey, StringComparison.Ordinal),
            $"loading a pre-change registry must rewrite it without the plaintext. The key still on disk is: {alreadyIssuedKey}");
        Assert.Contains(ExpectedHash(alreadyIssuedKey), onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void AMigratedDevice_KeepsWorkingAcrossTheNextRestartToo()
    {
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        _ = new DeviceRegistry(StorePath);          // first start after the upgrade: migrates and rewrites
        var afterRestart = new DeviceRegistry(StorePath); // and every start after that reads the hashed file

        Assert.True(afterRestart.IsValidDeviceKey(alreadyIssuedKey));
        Assert.Equal("tenant-already-enrolled", afterRestart.TenantForKey(alreadyIssuedKey));
    }

    [Fact]
    public void Migration_KeepsEveryOtherFieldOfTheRecord()
    {
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        var registry = new DeviceRegistry(StorePath);

        var entry = Assert.Single(registry.List());
        Assert.Equal("device-already-enrolled", entry.DeviceId);
        Assert.Equal("ALREADY-ENROLLED-MACHINE", entry.MachineName);
        Assert.Equal(DeviceRegistry.StatusActive, entry.Status);

        // The cloud mirror mapping must survive too, or the next reconcile would re-mirror the device.
        var mirrored = Assert.Single(registry.MirrorSnapshot());
        Assert.Equal("cloud-device-77", mirrored.CloudDeviceId);
        Assert.Equal("windows", mirrored.Platform);
        Assert.Equal("workstation", mirrored.DeviceType);
    }

    [Fact]
    public void Migration_DoesNotMakeEveryKeyValid()
    {
        // The control on the migration: hashing an existing record must not turn the registry into
        // something that accepts anything - only the one key that was already issued still works.
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        var registry = new DeviceRegistry(StorePath);

        Assert.False(registry.IsValidDeviceKey("pre-change-key-Ab3xQ9zK-do-not-invalidat"), "a truncated key must not pass");
        Assert.False(registry.IsValidDeviceKey("PRE-CHANGE-KEY-AB3XQ9ZK-DO-NOT-INVALIDATE"), "a case-flipped key must not pass");
        Assert.False(registry.IsValidDeviceKey(ExpectedHash(alreadyIssuedKey)), "presenting the STORED HASH must not pass as the key");
    }

    [Fact]
    public void PresentingTheStoredHashInsteadOfTheKey_IsRejected()
    {
        // The stored value must be a verifier, not a password-equivalent: whoever reads devices.json must
        // not be able to authenticate by replaying what they read.
        var registry = new DeviceRegistry(StorePath);
        var issued = registry.Register("device-a", "MACHINE-A").DeviceKey;

        Assert.False(registry.IsValidDeviceKey(ExpectedHash(issued)));
        Assert.False(registry.IsValidDeviceKey(ExpectedHash(issued).ToUpperInvariant()));
    }

    // ---- Re-enrollment: one entry, exactly one valid key ---------------------------------------

    [Fact]
    public void ReEnrollingADevice_LeavesExactlyOneValidKeyAndOneEntry()
    {
        var registry = new DeviceRegistry(StorePath);

        var first = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        var second = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.Equal(1, registry.Count);
        Assert.True(registry.IsValidDeviceKey(second), "the key just handed to the caller must work");
        Assert.False(registry.IsValidDeviceKey(first),
            "re-enrolling must RETIRE the previous key, never leave two working credentials for one device");
    }

    [Fact]
    public void ReEnrollingADevice_KeepsItsAccountAndTenantBinding()
    {
        var registry = new DeviceRegistry(StorePath);
        registry.RegisterIfAbsent("device-a", "MACHINE-A");
        registry.SetAccountBinding("device-a", "sub-alice", "tenant-alice");

        var reissued = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.Equal("tenant-alice", registry.TenantForKey(reissued));
    }
}
