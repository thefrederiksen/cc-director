using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// The Gateway-side registry of enrolled devices and their unique per-device keys (issue #469).
/// This is the single issuer and record of credentials in the per-device-key trust model: each
/// machine that completes pairing gets ONE distinct, individually-revocable key, recorded here
/// alongside its name, machine, issued-at, and status.
///
/// Persisted to <c>%LOCALAPPDATA%\cc-director\config\director\devices.json</c> so the registry
/// survives a Gateway restart (a per-device key must keep working across restarts).
///
/// The file stores each key ONLY as a one-way hash (issue #1878). A device key is a bearer secret that
/// authenticates every Director-to-Gateway call, and on the hosted Gateway this one file holds every
/// tenant's device binding on shared infrastructure - so anything that can read the file used to be able
/// to impersonate every tenant's Director. The Gateway only ever needs to VERIFY a presented key, exactly
/// like a password, so the plaintext is returned to its owner at enrollment and never written to the file.
/// A read of <c>devices.json</c> now yields no usable credential.
///
/// One deliberate exception, and it is memory-only: an issued key is held in this process for a short replay
/// window so that a RETRIED enrollment gets the same key back rather than a rotated one (see
/// <see cref="RegisterIfAbsent"/>). It is never serialized and does not survive a restart, so it does not
/// weaken the at-rest property above.
///
/// The hash is a plain SHA-256 of the key, deliberately NOT a slow password-stretching function: the
/// secret is a 256-bit value this class generated with a cryptographic random number generator, not a
/// human-chosen password, so there is no guessable search space for stretching to defend and a
/// deliberate per-verification delay would be paid on the hot path of EVERY authenticated request.
/// For the same reason there is no per-record salt - salts defend low-entropy secrets against
/// precomputation, which does not apply to a 256-bit random value.
///
/// Migration: a registry file written before this change holds the plaintext key in <c>DeviceKey</c>.
/// <see cref="Load"/> hashes any such record on the spot, drops the plaintext, and rewrites the file, so
/// every device enrolled before the change keeps working with the key it already holds and the plaintext is
/// gone from the LIVE registry file after the first load following the upgrade.
///
/// The scope of that claim is exactly the live file and no more. Rewriting a file cannot erase a copy of the
/// old contents that something else already took: a share snapshot, a backup, a soft-delete retention window,
/// or file-system history all hold whatever was there before, and this code has no reach into any of them.
/// Where such retention exists on a deployment, purging it is separate operational work and must be tracked
/// as such. Any key that a pre-change file may have exposed through a retained copy is only truly dealt with
/// by rotating that key, not by this migration.
///
/// Thread-safe: registration happens on request threads while GET /devices lists devices.
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>The status of an actively-enrolled device.</summary>
    public const string StatusActive = "active";

    /// <summary>The default device type recorded when a child app enrolls without supplying one.</summary>
    public const string DefaultDeviceType = "workstation";

    /// <summary>The platform recorded when a child app enrolls without supplying one.</summary>
    public const string UnknownPlatform = "unknown";

    /// <summary>
    /// How long a key issued by <see cref="RegisterIfAbsent"/> stays replayable, so that a retried or
    /// duplicated enrollment gets the SAME key back instead of rotating one the caller may not have saved
    /// yet. See <see cref="RegisterIfAbsent"/> for why this exists.
    /// </summary>
    public static readonly TimeSpan DefaultEnrollmentReplayWindow = TimeSpan.FromMinutes(5);

    private readonly string _storePath;
    private readonly object _saveLock = new();
    private readonly object _enrollLock = new();
    private readonly TimeSpan _replayWindow;
    private readonly ConcurrentDictionary<string, DeviceRecord> _byDeviceId =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The key most recently issued by <see cref="RegisterIfAbsent"/> per device id, held IN MEMORY ONLY
    /// for <see cref="_replayWindow"/> so a retry of the same enrollment can be answered with the same key.
    /// Never serialized, never written to <see cref="_storePath"/>, and gone when the process exits.
    /// </summary>
    private readonly ConcurrentDictionary<string, ReplayableIssue> _replayableIssues =
        new(StringComparer.Ordinal);

    public DeviceRegistry() : this(null) { }

    /// <param name="storePath">Override the registry file (tests pass an isolated temp path);
    /// production omits it for the shared default under the config root.</param>
    public DeviceRegistry(string? storePath) : this(storePath, DefaultEnrollmentReplayWindow) { }

    /// <param name="storePath">Override the registry file (tests pass an isolated temp path);
    /// production omits it for the shared default under the config root.</param>
    /// <param name="enrollmentReplayWindow">How long a key issued by <see cref="RegisterIfAbsent"/> stays
    /// replayable. Production uses <see cref="DefaultEnrollmentReplayWindow"/>; a test passes
    /// <see cref="TimeSpan.Zero"/> to exercise the behaviour after the window has closed.</param>
    public DeviceRegistry(string? storePath, TimeSpan enrollmentReplayWindow)
    {
        if (enrollmentReplayWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(enrollmentReplayWindow), "the replay window cannot be negative");

        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.Config(), "director", "devices.json")
            : storePath;
        _replayWindow = enrollmentReplayWindow;
        Load();
    }

    /// <summary>The on-disk registry file path.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Enroll a device: generate a unique per-device key, record the device, and return the issued
    /// key. A repeat enrollment of the SAME device id re-issues a fresh key (a re-pairing rotates
    /// the device's own key) and keeps one entry. Each call produces a distinct key.
    ///
    /// <paramref name="platform"/> and <paramref name="deviceType"/> are the child's self-reported
    /// display attributes used when the Gateway mirrors the child up to the cloud roster (Path B): a
    /// blank/absent type defaults to <see cref="DefaultDeviceType"/> and a blank platform to
    /// <see cref="UnknownPlatform"/>. A re-enrollment clears any prior cloud mapping
    /// (<see cref="DeviceRecord.CloudDeviceId"/>) so the child is mirrored afresh (idempotent per
    /// install id on the cloud). These attributes are never an admission credential - the per-device
    /// key is.
    /// </summary>
    public DeviceRegistrationResponse Register(string deviceId, string machineName, string? platform = null, string? deviceType = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));

        var key = GenerateDeviceKey();
        var record = new DeviceRecord
        {
            DeviceId = deviceId,
            MachineName = machineName ?? "",
            DeviceKeyHash = HashKey(key),
            IssuedAtUtc = DateTime.UtcNow,
            Status = StatusActive,
            Platform = string.IsNullOrWhiteSpace(platform) ? UnknownPlatform : platform.Trim(),
            DeviceType = string.IsNullOrWhiteSpace(deviceType) ? DefaultDeviceType : deviceType.Trim(),
            CloudDeviceId = null,
        };
        _byDeviceId[deviceId] = record;
        Save();
        FileLog.Write($"[DeviceRegistry] Registered device id={deviceId}, machine={machineName}, type={record.DeviceType}, platform={record.Platform}, total={_byDeviceId.Count}");
        return new DeviceRegistrationResponse
        {
            DeviceKey = key,
            DeviceId = deviceId,
            MachineName = record.MachineName,
            Status = record.Status,
            DeviceCount = _byDeviceId.Count,
        };
    }

    /// <summary>
    /// Enrollment for the account-sign-in path (issue #1069): this device identity ends up holding exactly
    /// ONE valid per-device key, whether it is enrolling for the first time or re-enrolling.
    ///
    /// A device that is already enrolled keeps its one entry - its account and tenant binding, its cloud
    /// mapping, and its display attributes are all preserved - and is issued a fresh key IN PLACE, which
    /// immediately retires the previous one. It does not get a second entry and it does not end up with two
    /// working keys. That is the guardrail against the #1136 auto-mint key leak, which was an enrollment
    /// call ACCUMULATING valid credentials in the registry; the count of valid keys per device identity is
    /// still capped at one.
    ///
    /// Before issue #1878 this returned the device's existing key byte-for-byte, so calling it twice was
    /// harmless. It cannot return a stored key any more - the registry keeps only a one-way hash - so the
    /// repeat-call safety is preserved a different way, by a short IN-MEMORY replay window
    /// (<see cref="DefaultEnrollmentReplayWindow"/>).
    ///
    /// A repeat call inside that window returns the SAME key that the previous call issued and does not
    /// rotate anything. This matters because the caller has not necessarily saved the first response yet:
    /// a lost or delayed response, a double submit, or two enrollment calls in flight would otherwise leave
    /// a caller holding a key that a later call had already retired, locking that device out until a human
    /// re-enrolled it. That is a self-inflicted outage on a retry, which is exactly the failure that only
    /// shows up on a flaky connection. The whole method is serialized, so two simultaneous calls resolve to
    /// one issue rather than racing to rotate.
    ///
    /// After the window closes, a further call rotates the key in place: same single registry entry, same
    /// account and tenant binding, same cloud mapping, and still exactly one key that validates, with the
    /// previous one retired. That one-key-per-device cap is the #1136 auto-mint guardrail (the leak was
    /// enrollment ACCUMULATING valid credentials) and it holds on both paths.
    ///
    /// The replayable key lives in this process's memory and nowhere else - it is never serialized and it
    /// does not survive a restart - so the at-rest property this change exists for is untouched.
    ///
    /// An ALREADY-enrolled device that simply keeps using its existing key never calls here at all; its key
    /// keeps verifying against the stored hash.
    /// </summary>
    public DeviceRegistrationResponse RegisterIfAbsent(string deviceId, string machineName, string? platform = null, string? deviceType = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));

        // Serialized so that two enrollment calls in flight for the same device cannot both rotate. The
        // path runs at enrollment only, never on the per-request authentication hot path.
        lock (_enrollLock)
        {
            if (_byDeviceId.TryGetValue(deviceId, out var existing)
                && string.Equals(existing.Status, StatusActive, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(existing.DeviceKeyHash))
            {
                if (TryReplayIssuedKey(deviceId, existing, out var replayed))
                {
                    FileLog.Write($"[DeviceRegistry] RegisterIfAbsent: device id={deviceId} enrolled again within the replay window; returned the SAME key, nothing rotated");
                    return new DeviceRegistrationResponse
                    {
                        DeviceKey = replayed,
                        DeviceId = deviceId,
                        MachineName = existing.MachineName,
                        Status = existing.Status,
                        DeviceCount = _byDeviceId.Count,
                    };
                }

                var reissued = GenerateDeviceKey();
                var reissuedHash = HashKey(reissued);
                existing.DeviceKeyHash = reissuedHash;
                existing.IssuedAtUtc = DateTime.UtcNow;
                Save();
                RecordReplayableIssue(deviceId, reissued, reissuedHash);
                FileLog.Write($"[DeviceRegistry] RegisterIfAbsent: device id={deviceId} is already enrolled and past the replay window; re-issued its key in place (one entry, one valid key, binding preserved)");
                return new DeviceRegistrationResponse
                {
                    DeviceKey = reissued,
                    DeviceId = deviceId,
                    MachineName = existing.MachineName,
                    Status = existing.Status,
                    DeviceCount = _byDeviceId.Count,
                };
            }

            var fresh = Register(deviceId, machineName, platform, deviceType);
            RecordReplayableIssue(deviceId, fresh.DeviceKey, HashKey(fresh.DeviceKey));
            return fresh;
        }
    }

    /// <summary>
    /// The key this registry last issued for <paramref name="deviceId"/> through
    /// <see cref="RegisterIfAbsent"/>, when that issue is still inside the replay window AND is still the
    /// key the stored record verifies against. Both conditions matter: an expired entry must not be handed
    /// out, and neither must one that some other path (an explicit <see cref="Register"/> re-pairing, a
    /// revoke-and-re-enroll) has already superseded.
    /// </summary>
    private bool TryReplayIssuedKey(string deviceId, DeviceRecord existing, out string key)
    {
        key = "";
        PruneExpiredIssues();

        if (!_replayableIssues.TryGetValue(deviceId, out var issue)) return false;
        if (DateTime.UtcNow - issue.IssuedAtUtc >= _replayWindow) return false;
        if (!string.Equals(issue.KeyHash, existing.DeviceKeyHash, StringComparison.Ordinal)) return false;

        key = issue.Key;
        return true;
    }

    /// <summary>
    /// Remembers the key just issued so the next call inside the window can replay it. This deliberately
    /// records unconditionally, including for a zero window: the window is enforced in exactly ONE place,
    /// the expiry check in <see cref="TryReplayIssuedKey"/>. A second short-circuit here would mean a broken
    /// expiry check could still look correct under a zero window, which is the guard that would hide it.
    /// </summary>
    private void RecordReplayableIssue(string deviceId, string key, string keyHash)
    {
        _replayableIssues[deviceId] = new ReplayableIssue(key, keyHash, DateTime.UtcNow);
    }

    /// <summary>Drops replayable keys whose window has closed, so a plaintext key is not held in memory any
    /// longer than the retry it exists to absorb.</summary>
    private void PruneExpiredIssues()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _replayableIssues)
        {
            if (now - pair.Value.IssuedAtUtc >= _replayWindow)
                _replayableIssues.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>A key handed out by <see cref="RegisterIfAbsent"/>, kept in memory only, so that a retry of
    /// the same enrollment can be answered with the same key instead of rotating one the caller may not
    /// have saved yet.</summary>
    /// <param name="Key">The issued plaintext key. In memory for the replay window; never persisted.</param>
    /// <param name="KeyHash">The stored form, used to confirm this issue is still the current one.</param>
    /// <param name="IssuedAtUtc">When it was issued, for the window.</param>
    private sealed record ReplayableIssue(string Key, string KeyHash, DateTime IssuedAtUtc);

    /// <summary>
    /// Records the cloud roster id assigned to a mirrored child (Path B, Diagram 2b) so a later
    /// revoke-pull (Diagram 2c) can match this local child against the cloud list by id, and so a
    /// restart does not re-mirror an already-mirrored child. A no-op when the device id is unknown
    /// (it was removed between mirror and record). The cloud id is not a secret; it is logged.
    /// </summary>
    public void SetCloudDeviceId(string deviceId, string cloudDeviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(cloudDeviceId))
            throw new ArgumentException("cloudDeviceId is required", nameof(cloudDeviceId));

        if (!_byDeviceId.TryGetValue(deviceId, out var record))
        {
            FileLog.Write($"[DeviceRegistry] SetCloudDeviceId: no local device id={deviceId} (removed before the mirror recorded) -> no-op");
            return;
        }

        record.CloudDeviceId = cloudDeviceId;
        Save();
        FileLog.Write($"[DeviceRegistry] SetCloudDeviceId: device id={deviceId} mirrored to cloud id={cloudDeviceId}");
    }

    /// <summary>
    /// Drops a child and its per-device key from the registry (Path B revoke-down, Diagram 2c): after
    /// this the child's key no longer validates and the child can no longer talk to this Gateway.
    /// Returns true when a device was removed, false when the id was not present. Persisted so the drop
    /// survives a restart.
    /// </summary>
    public bool Remove(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        if (!_byDeviceId.TryRemove(deviceId, out _))
            return false;

        _replayableIssues.TryRemove(deviceId, out _);
        Save();
        FileLog.Write($"[DeviceRegistry] Removed device id={deviceId} (local key revoked), total={_byDeviceId.Count}");
        return true;
    }

    /// <summary>
    /// A snapshot of every enrolled child with the fields the mirror reconcile needs: the local device
    /// id, machine name, self-reported platform/type, and the cloud roster id once mirrored (null until
    /// then). The per-device key is never included.
    /// </summary>
    public IReadOnlyList<ChildMirrorEntry> MirrorSnapshot()
    {
        return _byDeviceId.Values
            .Select(r => new ChildMirrorEntry(r.DeviceId, r.MachineName, r.Platform, r.DeviceType, r.CloudDeviceId))
            .ToList();
    }

    /// <summary>
    /// True when the supplied key matches an active device's per-device key. The lookup hashes the
    /// presented key once and compares that digest against each active device's STORED digest (issue #1878)
    /// with a constant-time DIGEST comparison. See <see cref="FindActiveByKey"/> for what that does and does
    /// not cover - the comparison is constant-time; the surrounding lookup is not.
    /// </summary>
    public bool IsValidDeviceKey(string? key) => FindActiveByKey(key) is not null;

    /// <summary>
    /// The active device whose stored per-device key hash matches <paramref name="key"/>, or null when none
    /// does. This is the single place a presented key is turned back into a device (issue #1878): the key is
    /// hashed once, then that digest is compared against each active record's stored digest with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>.
    ///
    /// Be precise about what that buys, because it is easy to overstate. The DIGEST COMPARISON is
    /// constant-time: it always reads both digests in full, so it does not leak how many leading bytes of a
    /// wrong digest happened to match, and it is that partial-match signal which would otherwise let an
    /// attacker walk a guess towards a stored value byte by byte. The comparison is the part that has to be
    /// constant-time and it is.
    ///
    /// The LOOKUP AROUND IT is not constant-time and does not claim to be. It walks the records, it skips
    /// inactive and unhashed ones, and it returns as soon as it matches - so its running time varies with how
    /// many devices are enrolled and with where in the walk a match falls. What that can leak is coarse
    /// registry shape (roughly how many devices exist, and a matching key's rough position among them), not
    /// any information about the SECRET, because the compared digest is the output of a hash of the presented
    /// value and a wrong guess produces an unrelated digest whose position is meaningless. Making the walk
    /// constant-time would defend nothing that matters here.
    ///
    /// This is the hot path - it runs on every authenticated request - which is why the stored form is a
    /// plain hash of a 256-bit random secret rather than a deliberately slow password-stretching function.
    /// </summary>
    private DeviceRecord? FindActiveByKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var supplied = HashKeyBytes(key);
        foreach (var record in _byDeviceId.Values)
        {
            if (!string.Equals(record.Status, StatusActive, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(record.DeviceKeyHash)) continue;
            var stored = DecodeHash(record.DeviceKeyHash);
            if (stored is null) continue;
            if (stored.Length == supplied.Length &&
                CryptographicOperations.FixedTimeEquals(stored, supplied))
                return record;
        }
        return null;
    }

    /// <summary>
    /// The <c>DeviceType</c> ("phone" / "browser" / "workstation" / ...) of the active device whose
    /// per-device key matches <paramref name="key"/>, or null when no active device matches. Used by the
    /// DevThrottle Stats surface resolution to tag a remote prompt with the surface it came from, from the
    /// SAME verified key that already authenticated the call (no client change, no forgeable header). The
    /// digest comparison is constant-time, mirroring <see cref="IsValidDeviceKey"/>; see
    /// <see cref="FindActiveByKey"/> for the exact scope of that.
    /// </summary>
    public string? DeviceTypeForKey(string? key) => FindActiveByKey(key)?.DeviceType;

    /// <summary>
    /// The tenant id of the active device whose per-device key matches <paramref name="key"/>, or null when
    /// no active device matches OR the matched device has no tenant binding (Hosted Multi-Tenancy increment
    /// 1). The tunnel reads this at Hello from the SAME verified key that already authenticated the
    /// connection, so the resolved tenant comes from the AUTHENTICATED credential, never from anything the
    /// client sent in its Hello payload. The digest comparison is constant-time, mirroring
    /// <see cref="IsValidDeviceKey"/>; see <see cref="FindActiveByKey"/> for the exact scope of that. A null
    /// return on hosted is a DENY, never a fall-back to the local tenant.
    /// </summary>
    public string? TenantForKey(string? key)
    {
        var record = FindActiveByKey(key);
        if (record is null) return null;
        return string.IsNullOrEmpty(record.TenantId) ? null : record.TenantId;
    }

    /// <summary>
    /// Bind an enrolled device to its verified account and resolved tenant (Hosted Multi-Tenancy increment
    /// 1). Called at hosted enrollment AFTER the account token has been validated and the tenant minted or
    /// looked up. Idempotent for an unchanged binding. Returns false when the device id is unknown (nothing
    /// to bind). The subject and tenant are personally identifying / security-relevant, so neither is logged.
    /// </summary>
    public bool SetAccountBinding(string deviceId, string accountSubject, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("accountSubject is required", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId is required", nameof(tenantId));

        if (!_byDeviceId.TryGetValue(deviceId, out var record))
        {
            FileLog.Write($"[DeviceRegistry] SetAccountBinding: no local device id={deviceId} -> no-op");
            return false;
        }

        record.AccountSubject = accountSubject.Trim();
        record.TenantId = tenantId.Trim();
        Save();
        FileLog.Write($"[DeviceRegistry] SetAccountBinding: bound device id={deviceId} to its account tenant");
        return true;
    }

    /// <summary>
    /// The host-readable list of EVERY registered device, newest first, ACROSS ALL TENANTS. Keys are never
    /// included. This is the unscoped internal view (self-host has one tenant, so it is that tenant's list);
    /// on the hosted Gateway it spans every account's devices and must NEVER be the answer to a client request
    /// - that is <see cref="ListForTenant(TenantId)"/>. Same shape as <see cref="Discovery.DirectorRegistry"/>'s
    /// fleet-global vs tenant-scoped listing pair.
    /// </summary>
    public IReadOnlyList<RegisteredDeviceDto> List()
    {
        return _byDeviceId.Values
            .OrderByDescending(r => r.IssuedAtUtc)
            .Select(ToDto)
            .ToList();
    }

    /// <summary>
    /// The devices ONE tenant owns - what the host-readable <c>GET /devices</c> listing serves (MTR-12). Before
    /// this, that route returned <see cref="List"/> (every id / machine name / issued time across every tenant),
    /// so any authenticated account read back a full multi-tenant device inventory. Scoped here, a caller sees
    /// only its own tenant's devices.
    ///
    /// A device with no account binding (<see cref="DeviceRecord.TenantId"/> null/empty) is a Local-tenant
    /// device - the single-tenant self-host shape, where every request also resolves to <see cref="TenantId.Local"/>,
    /// so a self-host caller still gets its own devices exactly as <see cref="List"/> returned them. On hosted,
    /// where a request's tenant is a real account GUID and each device is bound at enrollment, an unbound device
    /// matches no account. Deny by default: the caller's tenant must match a device's own resolved tenant; there
    /// is no fall-back to the unscoped list.
    /// </summary>
    public IReadOnlyList<RegisteredDeviceDto> ListForTenant(TenantId tenant)
    {
        return _byDeviceId.Values
            .Where(r => EffectiveTenant(r).Equals(tenant))
            .OrderByDescending(r => r.IssuedAtUtc)
            .Select(ToDto)
            .ToList();
    }

    /// <summary>
    /// The tenant a device record resolves to. A bound device (hosted enrollment) carries its account tenant; an
    /// unbound device is the single Local tenant, mirroring <see cref="Tenancy.HostedTenantBoundary.ResolveForDeviceKey"/>
    /// answering <see cref="TenantId.Local"/> on self-host and <see cref="TenantForKey"/> treating an empty
    /// binding as none.
    /// </summary>
    private static TenantId EffectiveTenant(DeviceRecord record)
        => string.IsNullOrEmpty(record.TenantId) ? TenantId.Local : new TenantId(record.TenantId);

    private static RegisteredDeviceDto ToDto(DeviceRecord record)
        => new RegisteredDeviceDto
        {
            DeviceId = record.DeviceId,
            MachineName = record.MachineName,
            IssuedAtUtc = record.IssuedAtUtc,
            Status = record.Status,
        };

    /// <summary>The number of registered devices.</summary>
    public int Count => _byDeviceId.Count;

    private static string GenerateDeviceKey()
    {
        // Same 32-byte URL-safe-base64 shape the machine token uses (issue #469 Assumption 3),
        // but unique per device rather than shared.
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>The stored form of a device key: the lower-case hexadecimal SHA-256 of the key (issue #1878).</summary>
    private static string HashKey(string key) => Convert.ToHexString(HashKeyBytes(key)).ToLowerInvariant();

    private static byte[] HashKeyBytes(string key) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    /// <summary>The bytes of a stored hash, or null when the stored text is not valid hexadecimal (a
    /// hand-edited or corrupt record, which then simply matches nothing).</summary>
    private static byte[]? DecodeHash(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        var json = File.ReadAllText(_storePath);
        if (string.IsNullOrWhiteSpace(json)) return;

        var records = JsonSerializer.Deserialize<List<DeviceRecord>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (records is null) return;
        var migrated = 0;
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.DeviceId)) continue;

            // Migration (issue #1878): a file written before device keys were hashed carries the plaintext
            // key. Hash it here and drop the plaintext, so the device keeps working with the key it already
            // holds and the plaintext is gone from the live file after the first save following this load.
            // That is the whole of the claim: rewriting the live file cannot reach a snapshot, backup, or
            // soft-delete copy of the old contents. See the type documentation.
            if (!string.IsNullOrEmpty(record.DeviceKey))
            {
                if (string.IsNullOrEmpty(record.DeviceKeyHash))
                    record.DeviceKeyHash = HashKey(record.DeviceKey);
                record.DeviceKey = null;
                migrated++;
            }

            _byDeviceId[record.DeviceId] = record;
        }
        FileLog.Write($"[DeviceRegistry] Loaded {_byDeviceId.Count} device(s) from {_storePath}");

        if (migrated > 0)
        {
            Save();
            FileLog.Write($"[DeviceRegistry] Migrated {migrated} device(s) from a plaintext key to a stored hash; every migrated device keeps its existing key and the plaintext is gone from the live registry file. Any snapshot, backup, or soft-delete copy of the pre-change file still holds those keys and is not reachable from here - purge it separately, or rotate the affected keys.");
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_byDeviceId.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            File.WriteAllText(_storePath, json);
        }
    }

    /// <summary>One device's full record. It carries the HASH of the issued key, never the key (issue #1878).</summary>
    private sealed class DeviceRecord
    {
        public string DeviceId { get; set; } = "";
        public string MachineName { get; set; } = "";

        /// <summary>
        /// The plaintext key as written by a registry file from BEFORE issue #1878. This property exists
        /// only so <see cref="Load"/> can read such a file and migrate it; it is nulled the moment it is
        /// hashed and is never written back out. Nothing else may read or set it.
        /// </summary>
        public string? DeviceKey { get; set; }

        /// <summary>The lower-case hexadecimal SHA-256 of this device's key - the only form of the key that
        /// is ever persisted. Verified against, never reversed.</summary>
        public string DeviceKeyHash { get; set; } = "";

        public DateTime IssuedAtUtc { get; set; }
        public string Status { get; set; } = StatusActive;

        /// <summary>The child's self-reported platform (mirror attribute). "unknown" when not supplied.</summary>
        public string Platform { get; set; } = UnknownPlatform;

        /// <summary>The child's self-reported device type (mirror attribute). "workstation" when not supplied.</summary>
        public string DeviceType { get; set; } = DefaultDeviceType;

        /// <summary>The cloud roster id assigned when this child was mirrored up, or null until mirrored.</summary>
        public string? CloudDeviceId { get; set; }

        /// <summary>The verified Supabase subject (<c>sub</c>) this device's account resolved to at hosted
        /// enrollment (Hosted Multi-Tenancy increment 1), or null on the single-tenant local install (where a
        /// device binds to no account). Personally identifying - never logged.</summary>
        public string? AccountSubject { get; set; }

        /// <summary>The tenant id this device resolved to at hosted enrollment - the value the tunnel binds at
        /// Hello and every stored row is scoped to. Null on the single-tenant local install (which resolves to
        /// "local" without a per-device binding).</summary>
        public string? TenantId { get; set; }
    }
}

/// <summary>
/// A child's mirror-relevant state read out of the <see cref="DeviceRegistry"/> for the Path B reconcile:
/// the local device id, machine name, self-reported platform/type, and the cloud roster id once the child
/// has been mirrored up (null until then). Carries no per-device key (security: the key never leaves the
/// registry).
/// </summary>
/// <param name="DeviceId">The child's stable local device id (also its cloud install id when mirrored).</param>
/// <param name="MachineName">The child's machine name, mirrored as the roster display name.</param>
/// <param name="Platform">The child's self-reported platform, or "unknown".</param>
/// <param name="DeviceType">The child's device type ("workstation" | "phone").</param>
/// <param name="CloudDeviceId">The cloud roster id once mirrored, or null when not yet mirrored.</param>
public sealed record ChildMirrorEntry(
    string DeviceId,
    string MachineName,
    string Platform,
    string DeviceType,
    string? CloudDeviceId);
