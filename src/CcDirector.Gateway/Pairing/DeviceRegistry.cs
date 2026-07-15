using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Storage;
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
/// survives a Gateway restart (a per-device key must keep working across restarts). The file holds the
/// issued keys, so it is the Gateway host's secret store - locked to the current user by living under
/// the per-user config root.
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

    private readonly string _storePath;
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, DeviceRecord> _byDeviceId =
        new(StringComparer.Ordinal);

    public DeviceRegistry() : this(null) { }

    /// <param name="storePath">Override the registry file (tests pass an isolated temp path);
    /// production omits it for the shared default under the config root.</param>
    public DeviceRegistry(string? storePath)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.Config(), "director", "devices.json")
            : storePath;
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
            DeviceKey = key,
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
    /// Idempotent enrollment for the account-sign-in path (issue #1069): if this device id already holds an
    /// active per-device key, return that SAME key unchanged; otherwise mint one via <see cref="Register"/>.
    /// This is the load-bearing guardrail against minting a fresh key on every call - the auto-mint key leak
    /// that #1136 fixed. Unlike <see cref="Register"/> (which rotates the key so a re-pairing issues a fresh
    /// one), this never rotates: one device identity gets exactly one key until it is revoked.
    /// </summary>
    public DeviceRegistrationResponse RegisterIfAbsent(string deviceId, string machineName, string? platform = null, string? deviceType = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));

        if (_byDeviceId.TryGetValue(deviceId, out var existing)
            && string.Equals(existing.Status, StatusActive, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(existing.DeviceKey))
        {
            FileLog.Write($"[DeviceRegistry] RegisterIfAbsent: device id={deviceId} already has an active key; returning it unchanged (no re-mint)");
            return new DeviceRegistrationResponse
            {
                DeviceKey = existing.DeviceKey,
                DeviceId = deviceId,
                MachineName = existing.MachineName,
                Status = existing.Status,
                DeviceCount = _byDeviceId.Count,
            };
        }

        return Register(deviceId, machineName, platform, deviceType);
    }

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
    /// True when the supplied key matches an active device's per-device key. The lookup is over
    /// all active keys with a constant-time compare so a near-miss reveals nothing through timing.
    /// </summary>
    public bool IsValidDeviceKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var supplied = System.Text.Encoding.ASCII.GetBytes(key);
        foreach (var record in _byDeviceId.Values)
        {
            if (!string.Equals(record.Status, StatusActive, StringComparison.Ordinal)) continue;
            var stored = System.Text.Encoding.ASCII.GetBytes(record.DeviceKey);
            if (stored.Length == supplied.Length &&
                CryptographicOperations.FixedTimeEquals(stored, supplied))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The <c>DeviceType</c> ("phone" / "browser" / "workstation" / ...) of the active device whose
    /// per-device key matches <paramref name="key"/>, or null when no active device matches. Used by the
    /// DevThrottle Stats surface resolution to tag a remote prompt with the surface it came from, from the
    /// SAME verified key that already authenticated the call (no client change, no forgeable header). The
    /// compare is constant-time, mirroring <see cref="IsValidDeviceKey"/>, so a near-miss reveals nothing
    /// through timing.
    /// </summary>
    public string? DeviceTypeForKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var supplied = System.Text.Encoding.ASCII.GetBytes(key);
        foreach (var record in _byDeviceId.Values)
        {
            if (!string.Equals(record.Status, StatusActive, StringComparison.Ordinal)) continue;
            var stored = System.Text.Encoding.ASCII.GetBytes(record.DeviceKey);
            if (stored.Length == supplied.Length &&
                CryptographicOperations.FixedTimeEquals(stored, supplied))
                return record.DeviceType;
        }
        return null;
    }

    /// <summary>The host-readable list of registered devices, newest first. Keys are never included.</summary>
    public IReadOnlyList<RegisteredDeviceDto> List()
    {
        return _byDeviceId.Values
            .OrderByDescending(r => r.IssuedAtUtc)
            .Select(r => new RegisteredDeviceDto
            {
                DeviceId = r.DeviceId,
                MachineName = r.MachineName,
                IssuedAtUtc = r.IssuedAtUtc,
                Status = r.Status,
            })
            .ToList();
    }

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
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.DeviceId)) continue;
            _byDeviceId[record.DeviceId] = record;
        }
        FileLog.Write($"[DeviceRegistry] Loaded {_byDeviceId.Count} device(s) from {_storePath}");
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
            });
            File.WriteAllText(_storePath, json);
        }
    }

    /// <summary>One device's full record, including the issued key (persisted, never listed).</summary>
    private sealed class DeviceRecord
    {
        public string DeviceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string DeviceKey { get; set; } = "";
        public DateTime IssuedAtUtc { get; set; }
        public string Status { get; set; } = StatusActive;

        /// <summary>The child's self-reported platform (mirror attribute). "unknown" when not supplied.</summary>
        public string Platform { get; set; } = UnknownPlatform;

        /// <summary>The child's self-reported device type (mirror attribute). "workstation" when not supplied.</summary>
        public string DeviceType { get; set; } = DefaultDeviceType;

        /// <summary>The cloud roster id assigned when this child was mirrored up, or null until mirrored.</summary>
        public string? CloudDeviceId { get; set; }
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
