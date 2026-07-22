using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1878 / #1899: the device registry file used to hold every enrolled device's per-device key in
/// PLAINTEXT. That key is a bearer secret - it authenticates every Director-to-Gateway call - and on the
/// hosted Gateway the one file holds every tenant's device on shared infrastructure, so anything that
/// could read the file could impersonate every tenant's Director.
///
/// These tests pin the fix from several ends:
///   1. WHAT IS WRITTEN: the file must contain no usable credential - the plaintext key must not appear
///      in it, and what does appear must be the one-way hash.
///   2. WHAT KEEPS WORKING: a device enrolled BEFORE the change - whose key exists only in the plaintext
///      file and in that machine's credential file - must still authenticate afterwards, and must still
///      resolve to its bound tenant. There is no re-enrollment path that does not involve a human, so a
///      change that invalidated those keys would take the live hosted box down.
///   3. NO PLAINTEXT IS EVER RETAINED (issue #1899): plaintext is disclosed at exactly one point - the
///      enrollment response - and kept nowhere. A REPEAT enrollment cannot re-reveal the current key; it
///      rotates to a fresh one instead. The registry holds no plaintext cache to leak or to replay.
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
    public void Migration_RecordsTheMaskedKeyIdentity_FromThePlaintextBeforeDroppingIt()
    {
        // Issue #1899: a migrated device must gain the same non-secret masked key identity a freshly-enrolled
        // one has, computed from the plaintext while it is still in hand and BEFORE it is dropped - otherwise a
        // pre-change device would list with no key identity at all.
        const string alreadyIssuedKey = "pre-change-key-Ab3xQ9zK-do-not-invalidate";
        WritePreChangeStore("device-already-enrolled", alreadyIssuedKey, "tenant-already-enrolled");

        var entry = Assert.Single(new DeviceRegistry(StorePath).List());

        Assert.Equal(alreadyIssuedKey.Substring(0, 8), entry.KeyPrefix);
        Assert.Equal(alreadyIssuedKey.Substring(alreadyIssuedKey.Length - 4), entry.KeyLast4);
        // And the masked identity is not the whole key - it is a hint, never a credential.
        Assert.False(alreadyIssuedKey.Contains(entry.KeyPrefix + "..." + entry.KeyLast4, StringComparison.Ordinal));
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

    // ---- 3. No plaintext is ever retained: a repeat enrollment ROTATES, never replays -----------
    //
    // Hashing the stored key removed the registry's ability to hand a re-enrolling device its existing key
    // back. The first attempt at retry-safety kept a short-lived plaintext copy so a duplicate could REPLAY
    // it - but that made the current bearer credential retrievable again by simply repeating enrollment,
    // which defeats hashing at rest (issue #1899). The fix removes the plaintext cache entirely: a repeat
    // enrollment rotates to a FRESH key and retires the previous one, so nothing is retained and nothing is
    // re-revealed.
    //
    // Retry-safety survives because enrollment is a one-shot, deliberate act (never a poll-loop call): the
    // retry that actually happens is a sequential re-attempt after a failed response, and each attempt hands
    // back a fresh working key the caller writes down. These tests pin that, together with the #1136
    // one-key-per-device cap.

    [Fact]
    public void EnrollingTwice_RotatesToAFreshKey_AndNeverReplaysThePreviousOne()
    {
        var registry = new DeviceRegistry(StorePath);

        var first = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        var second = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(first, second);
        Assert.True(registry.IsValidDeviceKey(second), "the freshly returned key must authenticate");
        Assert.False(registry.IsValidDeviceKey(first),
            "a repeat enrollment must NOT re-reveal or keep alive the previous key - that is the #1899 leak");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ASequentialRetry_AlwaysReturnsAWorkingKey_SoTheCallerIsNeverLockedOut()
    {
        // The lost-response scenario stated directly: the caller never received the first key (so it holds
        // nothing), retries, and must come away holding a valid credential. Rotation makes every attempt hand
        // back a fresh working key - the caller writes whichever one it receives.
        var registry = new DeviceRegistry(StorePath);

        string last = "";
        for (var retry = 0; retry < 5; retry++)
            last = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.True(registry.IsValidDeviceKey(last),
            "the key from the caller's latest successful response must authenticate");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void EnrollingManyTimes_LeavesExactlyOneValidKey()
    {
        // Rotation must not have been bought by leaving OLD keys working alongside new ones - that is exactly
        // the #1136 accumulation leak. One device, one key that validates, however many calls.
        var registry = new DeviceRegistry(StorePath);

        var keys = new List<string>();
        for (var call = 0; call < 6; call++)
            keys.Add(registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey);

        Assert.Equal(6, keys.Distinct(StringComparer.Ordinal).Count()); // every call rotated
        Assert.Single(keys, registry.IsValidDeviceKey);                 // and only the last still validates
        Assert.Equal(keys[^1], keys.Single(registry.IsValidDeviceKey));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ConcurrentEnrollmentsOfOneDevice_LeaveExactlyOneValidKey_AndOneEntry()
    {
        // Sixteen enrollment calls in flight at once must not corrupt the one-key-per-device invariant: the
        // method is serialized, so they rotate one after another and end with a single entry and exactly one
        // key that validates (the last winner).
        var registry = new DeviceRegistry(StorePath);
        var issued = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 16, _ => issued.Add(registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey));

        Assert.Equal(1, registry.Count);
        Assert.Single(issued, registry.IsValidDeviceKey);
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

    [Fact]
    public void AnExplicitRePairing_AlsoRetiresThePreviousKey()
    {
        // Register (a re-pairing) rotates in place too: the previous key stops validating.
        var registry = new DeviceRegistry(StorePath);
        var enrolled = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        var rePaired = registry.Register("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(enrolled, rePaired);
        Assert.True(registry.IsValidDeviceKey(rePaired));
        Assert.False(registry.IsValidDeviceKey(enrolled),
            "an explicit re-pairing must retire the previous key");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void NothingReplayableSurvivesARestart_AndTheRegistryHoldsNoPlaintext()
    {
        // Direct evidence that no plaintext is persisted or cached: a registry re-read from disk holds only
        // hashes, so a post-restart enrollment rotates and the previous key is gone - it is nowhere to replay
        // from, and nowhere in the file.
        var first = new DeviceRegistry(StorePath).RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        var afterRestart = new DeviceRegistry(StorePath);
        var second = afterRestart.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(first, second);
        Assert.False(afterRestart.IsValidDeviceKey(first));
        Assert.DoesNotContain(first, File.ReadAllText(StorePath));
        Assert.DoesNotContain(second, File.ReadAllText(StorePath));
    }

    // ---- Masked key identity is present and non-secret (issue #1899) ----------------------------

    [Fact]
    public void AFreshlyEnrolledDevice_ListsWithAMaskedKeyIdentity_NeverTheRawKey()
    {
        var registry = new DeviceRegistry(StorePath);
        var issued = registry.Register("device-a", "MACHINE-A").DeviceKey;

        var entry = Assert.Single(registry.List());
        Assert.Equal(issued.Substring(0, 8), entry.KeyPrefix);
        Assert.Equal(issued.Substring(issued.Length - 4), entry.KeyLast4);

        // The listing shape carries the mask, and the raw key never appears anywhere in it.
        Assert.NotEqual(issued, entry.KeyPrefix);
        Assert.True(issued.Length > entry.KeyPrefix.Length + entry.KeyLast4.Length,
            "the masked identity must reveal only a fraction of the key");
    }

    [Fact]
    public void TheMaskedIdentity_IsPersisted_AndSurvivesARestart()
    {
        var issued = new DeviceRegistry(StorePath).Register("device-a", "MACHINE-A").DeviceKey;

        var entry = Assert.Single(new DeviceRegistry(StorePath).List());
        Assert.Equal(issued.Substring(0, 8), entry.KeyPrefix);
        Assert.Equal(issued.Substring(issued.Length - 4), entry.KeyLast4);
    }
}
