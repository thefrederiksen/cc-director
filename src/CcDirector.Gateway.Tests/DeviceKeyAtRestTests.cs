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
    //
    // A registry with a ZERO replay window is a registry in which every call is treated as being past the
    // window, which is how these tests reach the rotate-in-place branch deterministically. The idempotence
    // tests below use the default window and the same constructor, so the two branches are exercised by the
    // same code under two settings rather than by two different code paths.

    private DeviceRegistry PastTheReplayWindow() => new(StorePath, TimeSpan.Zero);

    [Fact]
    public void ReEnrollingADevice_PastTheReplayWindow_LeavesExactlyOneValidKeyAndOneEntry()
    {
        var registry = PastTheReplayWindow();

        var first = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        var second = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(first, second);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.IsValidDeviceKey(second), "the key just handed to the caller must work");
        Assert.False(registry.IsValidDeviceKey(first),
            "re-enrolling must RETIRE the previous key, never leave two working credentials for one device");
    }

    [Fact]
    public void ReEnrollingADevice_KeepsItsAccountAndTenantBinding()
    {
        var registry = PastTheReplayWindow();
        registry.RegisterIfAbsent("device-a", "MACHINE-A");
        registry.SetAccountBinding("device-a", "sub-alice", "tenant-alice");

        var reissued = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.Equal("tenant-alice", registry.TenantForKey(reissued));
    }

    // ---- Enrollment is safe to retry (the idempotence property) --------------------------------
    //
    // Hashing the stored key removed the registry's ability to hand a re-enrolling device its existing key
    // back, which turned every duplicate enrollment into a ROTATION. A caller that had not yet durably saved
    // the first response - a lost or delayed response, a double submit, two calls in flight - would then be
    // holding a key that the later call had already retired, and would be locked out until a human
    // re-enrolled it. That is a production outage created by a security fix, and it only appears on a retry.
    //
    // These tests pin the property that closes it: inside the replay window a duplicate enrollment returns
    // the SAME key and rotates nothing. Each is paired with the destructibility control below, so that "the
    // key survived a second call" is read as protection and not as the registry having quietly lost the
    // ability to retire keys at all.

    [Fact]
    public void EnrollingTwice_ReturnsTheSameKey_SoARetryCannotLockTheCallerOut()
    {
        var registry = new DeviceRegistry(StorePath);

        var first = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        var second = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.Equal(first, second);
        Assert.True(registry.IsValidDeviceKey(first),
            "the key from the FIRST response must still authenticate - a caller may have saved that one and not the second");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ARetriedEnrollment_DoesNotRetireTheKeyTheCallerAlreadyHolds()
    {
        // The lock-out scenario stated directly: the caller keeps the key from the first response, the
        // enrollment call is retried because the response never arrived, and the held key must keep working.
        var registry = new DeviceRegistry(StorePath);
        var keyTheCallerSaved = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        for (var retry = 0; retry < 5; retry++)
            registry.RegisterIfAbsent("device-a", "MACHINE-A");

        Assert.True(registry.IsValidDeviceKey(keyTheCallerSaved),
            "five retried enrollments must not lock out the device that is holding the key from the first response");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ConcurrentEnrollmentsOfOneDevice_AllResolveToTheSameKey()
    {
        // Two (here, sixteen) enrollment calls in flight at once must converge on ONE issued key rather than
        // racing to rotate, or the losers of the race are handed keys that are dead on arrival.
        var registry = new DeviceRegistry(StorePath);
        var issued = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 16, _ => issued.Add(registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey));

        var distinct = issued.Distinct(StringComparer.Ordinal).ToList();
        Assert.Single(distinct);
        Assert.True(registry.IsValidDeviceKey(distinct[0]));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void EnrollingTwice_StillLeavesExactlyOneValidKey()
    {
        // Idempotence must not have been bought by leaving the OLD key working alongside a new one - that is
        // precisely the #1136 accumulation leak. One device, one key that validates, however many calls.
        var registry = new DeviceRegistry(StorePath);

        var keys = new List<string>();
        for (var call = 0; call < 6; call++)
            keys.Add(registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey);

        var distinct = keys.Distinct(StringComparer.Ordinal).ToList();
        Assert.Single(distinct);
        Assert.Single(distinct, registry.IsValidDeviceKey);
    }

    // ---- Destructibility controls ---------------------------------------------------------------
    //
    // Without these, every assertion above is satisfied just as well by a registry that has lost the ability
    // to retire a key at all. These show the mechanism CAN retire one, on both of the paths that are supposed
    // to: an explicit re-pairing, and a re-enrollment once the replay window has closed.

    [Fact]
    public void DestructibilityControl_AnExplicitRePairing_DoesRetireThePreviousKey()
    {
        var registry = new DeviceRegistry(StorePath);
        var enrolled = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        var rePaired = registry.Register("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(enrolled, rePaired);
        Assert.True(registry.IsValidDeviceKey(rePaired));
        Assert.False(registry.IsValidDeviceKey(enrolled),
            "an explicit re-pairing must retire the previous key - if it does not, the idempotence tests above prove nothing");
    }

    [Fact]
    public void DestructibilityControl_OnceTheReplayWindowCloses_ARepeatEnrollmentRetiresThePreviousKey()
    {
        var registry = PastTheReplayWindow();
        var first = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        var second = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(first, second);
        Assert.False(registry.IsValidDeviceKey(first),
            "past the replay window a repeat enrollment must rotate - the window bounds the idempotence, it does not remove rotation");
    }

    [Fact]
    public void DestructibilityControl_AfterARePairing_TheSupersededKeyIsNotReplayedBack()
    {
        // The replay entry must be checked against what the record currently verifies, not handed out on age
        // alone. An explicit re-pairing rotates the key without going through the replay path, so a following
        // enrollment must NOT resurrect the key the re-pairing just retired.
        var registry = new DeviceRegistry(StorePath);
        var enrolled = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        var rePaired = registry.Register("device-a", "MACHINE-A").DeviceKey;

        var afterRePairing = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(enrolled, afterRePairing);
        Assert.False(registry.IsValidDeviceKey(enrolled),
            "the key the re-pairing retired must stay retired - a stale replay entry must never hand it back");
        Assert.True(registry.IsValidDeviceKey(afterRePairing));
        Assert.Equal(1, registry.Count);
        // The re-paired key is still fine on its own terms; the point is that the SUPERSEDED one is gone.
        Assert.False(string.IsNullOrEmpty(rePaired));
    }

    // ---- Retention of the replayable plaintext is actually bounded ------------------------------
    //
    // The retry-safety window holds an issued key in memory. What made that acceptable is that it is
    // SHORT-LIVED - and that was the property nothing tested. The first version pruned only when an
    // already-enrolled device asked to replay, so the ordinary case (a device enrolls once under its own id
    // and never comes back) never reached the prune and its plaintext key stayed resident for the life of the
    // process. Checking an entry's age at replay time stops an expired key being RETURNED; it does not bound
    // RETENTION. These tests assert retention directly rather than inferring it from behaviour that would
    // still look correct while the plaintext sat in memory.
    //
    // A hand-driven scheduler is used so both halves are proven without waiting on a clock: that the registry
    // SCHEDULES an eviction pass at all, and that the pass RELEASES the plaintext.

    /// <summary>Captures the recurring eviction pass instead of running it on a timer, so a test can fire it
    /// on demand. Waiting on a real timer would make the result depend on machine load, not on the code.</summary>
    private sealed class HandDrivenEviction
    {
        public TimeSpan? Interval { get; private set; }
        private Action? _pass;
        public bool WasScheduled => _pass is not null;
        public void Schedule(TimeSpan interval, Action pass) { Interval = interval; _pass = pass; }
        public void RunOnce() => (_pass ?? throw new InvalidOperationException(
            "the registry never scheduled an eviction pass, so there is nothing to run")).Invoke();
    }

    [Fact]
    public void TheRegistry_SchedulesARecurringEvictionPass_AsSoonAsItRetainsAKey()
    {
        // Half one: without this, the eviction policy below is dead code that nothing ever calls. The pass
        // must be running by the time there is any plaintext to release - retention and the clock that bounds
        // it have to begin together.
        var eviction = new HandDrivenEviction();
        using var registry = new DeviceRegistry(StorePath, TimeSpan.FromMinutes(5), eviction.Schedule);
        Assert.False(eviction.WasScheduled, "nothing is retained yet, so no clock is needed yet");

        registry.RegisterIfAbsent("device-a", "MACHINE-A");

        Assert.True(eviction.WasScheduled,
            "the registry must schedule its own eviction pass - retention cannot depend on another caller arriving");
        // Half the window, so worst-case retention is one and a half windows rather than two. The type
        // documentation states that bound, so the interval that produces it is asserted rather than assumed.
        Assert.Equal(TimeSpan.FromMinutes(2.5), eviction.Interval);
    }

    [Fact]
    public void TheEvictionInterval_IsHalfTheWindow_AndNeverZero()
    {
        Assert.Equal(TimeSpan.FromMinutes(2.5), DeviceRegistry.EvictionIntervalFor(TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), DeviceRegistry.EvictionIntervalFor(TimeSpan.FromMinutes(1)));
        // A zero or tiny window must not turn the pass into a busy loop.
        Assert.Equal(TimeSpan.FromSeconds(1), DeviceRegistry.EvictionIntervalFor(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), DeviceRegistry.EvictionIntervalFor(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void ADeviceThatEnrollsOnceAndNeverReturns_DoesNotRetainItsPlaintextKey()
    {
        // The exact path that was broken. One device, one enrollment, a distinct id, and nobody ever calls
        // RegisterIfAbsent again - so nothing ever "knocks" to trigger a lazy prune.
        var eviction = new HandDrivenEviction();
        using var registry = new DeviceRegistry(StorePath, TimeSpan.Zero, eviction.Schedule);

        var issued = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        Assert.Equal(1, registry.RetainedReplayableKeyCount); // held, as designed, until the window closes

        eviction.RunOnce();

        Assert.Equal(0, registry.RetainedReplayableKeyCount);
        Assert.False(string.IsNullOrEmpty(issued));
        Assert.True(registry.IsValidDeviceKey(issued),
            "evicting the retained PLAINTEXT must not revoke the device - the stored hash still authenticates it");
    }

    [Fact]
    public void ManyDevicesEnrollingOnceUnderDistinctIds_DoNotAccumulatePlaintextKeys()
    {
        // The growth half of the same defect: fresh device ids are the ordinary case on a Gateway that runs
        // for weeks, and each one was leaving a plaintext key behind.
        var eviction = new HandDrivenEviction();
        using var registry = new DeviceRegistry(StorePath, TimeSpan.Zero, eviction.Schedule);

        for (var i = 0; i < 25; i++)
            registry.RegisterIfAbsent($"device-{i}", $"MACHINE-{i}");

        Assert.Equal(25, registry.RetainedReplayableKeyCount);
        eviction.RunOnce();

        Assert.Equal(0, registry.RetainedReplayableKeyCount);
        Assert.Equal(25, registry.Count); // the devices themselves are untouched; only the plaintext went
    }

    [Fact]
    public void AnExplicitRePairing_DoesNotLeaveTheOlderReplayableKeyResident()
    {
        // Register does not go through the replay path, so a re-pairing used to leave the previous entry
        // sitting there with nothing that would ever collect it.
        var eviction = new HandDrivenEviction();
        using var registry = new DeviceRegistry(StorePath, TimeSpan.Zero, eviction.Schedule);

        registry.RegisterIfAbsent("device-a", "MACHINE-A");
        registry.Register("device-a", "MACHINE-A");

        eviction.RunOnce();

        Assert.Equal(0, registry.RetainedReplayableKeyCount);
    }

    [Fact]
    public void EvictionControl_AKeyStillInsideItsWindow_IsNotEvicted()
    {
        // The control for the three tests above. Without it, they are all satisfied by an eviction pass that
        // simply clears everything unconditionally - which would silently destroy the retry-safety the window
        // exists to provide. Eviction must be driven by the DEADLINE, not by being called.
        var eviction = new HandDrivenEviction();
        using var registry = new DeviceRegistry(StorePath, TimeSpan.FromMinutes(5), eviction.Schedule);

        var issued = registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;
        eviction.RunOnce();

        Assert.Equal(1, registry.RetainedReplayableKeyCount);
        Assert.Equal(issued, registry.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey);
    }

    [Fact]
    public void Disposing_DropsEveryRetainedPlaintextKey()
    {
        var eviction = new HandDrivenEviction();
        var registry = new DeviceRegistry(StorePath, TimeSpan.FromMinutes(5), eviction.Schedule);
        registry.RegisterIfAbsent("device-a", "MACHINE-A");
        Assert.Equal(1, registry.RetainedReplayableKeyCount);

        registry.Dispose();

        Assert.Equal(0, registry.RetainedReplayableKeyCount);
    }

    [Fact]
    public void DestructibilityControl_ReplayIsNotOfferedAcrossARestart()
    {
        // The replayable key is memory-only. A registry re-read from disk holds no plaintext to replay, so a
        // post-restart enrollment rotates - which is also the direct evidence that nothing was persisted.
        var first = new DeviceRegistry(StorePath).RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        var afterRestart = new DeviceRegistry(StorePath);
        var second = afterRestart.RegisterIfAbsent("device-a", "MACHINE-A").DeviceKey;

        Assert.NotEqual(first, second);
        Assert.False(afterRestart.IsValidDeviceKey(first));
        Assert.DoesNotContain(first, File.ReadAllText(StorePath));
    }
}
