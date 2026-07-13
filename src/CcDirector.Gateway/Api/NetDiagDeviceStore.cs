using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable per-device state that must survive a Gateway restart (Network Diagnostics mission, Phase 1 /
/// Architect guardrail C). Persists exactly two things per device, and DELIBERATELY NOT a third:
///   - the bounded HOME + DIRECT + LAN-path good-sample list, so the per-device baseline survives a restart
///     (no ~6-minute re-warmup) - persisted as the raw good samples so <see cref="NetDiagDrift.ComputeBaseline"/>
///     runs UNCHANGED (never derived from the hourly-sum rollup, which can't yield the median and blends
///     away/relay data);
///   - the (cachedLanIp, cachedMac) presence identity, so an ARP presence probe can run immediately after a
///     restart instead of waiting for the device to be seen LAN-direct again.
///
/// It does NOT persist the drift <see cref="NetDiagDrift.MachineState"/>: restoring a mid-episode Drifted
/// state with a stale FirstBadUtc could immediately re-fire an alert or mis-time the 5-minute floor on boot.
/// A restart starts every device fresh at Unknown and re-accrues from live observations - baseline seeds
/// instantly, but the drift-episode clock begins clean.
///
/// Same atomic write-through + corrupt-file quarantine contract as <see cref="CronRunHistoryStore"/>.
/// </summary>
public sealed class NetDiagDeviceStore
{
    public const int MaxSamplesPerDevice = 50;

    /// <summary>One device's persisted baseline samples and presence identity (never its drift state).</summary>
    public sealed class PersistedDevice
    {
        public List<NetDiagDrift.GoodSample> Samples { get; set; } = new();
        public string? LanIp { get; set; }
        public string? Mac { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, PersistedDevice> _devices = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public NetDiagDeviceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>Every device's persisted baseline samples (home+direct filtered) and presence identity.</summary>
    public IReadOnlyDictionary<string, PersistedDevice> LoadAll()
    {
        lock (_gate)
            return _devices.ToDictionary(
                kv => kv.Key,
                kv => new PersistedDevice
                {
                    Samples = kv.Value.Samples.Where(IsGood).ToList(),
                    LanIp = kv.Value.LanIp,
                    Mac = kv.Value.Mac,
                },
                StringComparer.Ordinal);
    }

    /// <summary>Replace one device's baseline samples + presence identity and persist. Drift state is never stored.</summary>
    public void Save(string deviceKey, IReadOnlyList<NetDiagDrift.GoodSample> samples, string? lanIp, string? mac)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return;
        lock (_gate)
        {
            var kept = samples.Where(IsGood).ToList();
            if (kept.Count > MaxSamplesPerDevice)
                kept.RemoveRange(0, kept.Count - MaxSamplesPerDevice);
            _devices[deviceKey] = new PersistedDevice { Samples = kept, LanIp = lanIp, Mac = mac };
            Save();
        }
    }

    private static bool IsGood(NetDiagDrift.GoodSample s) => s.IsHome && s.Direct && s.IsLanPath;

    // ---- persistence (CronRunHistoryStore precedent) ----

    private sealed class StoreFile
    {
        public Dictionary<string, PersistedDevice> Devices { get; set; } = new(StringComparer.Ordinal);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[NetDiagDeviceStore] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        foreach (var (key, dev) in parsed.Devices)
            if (!string.IsNullOrWhiteSpace(key) && dev is not null)
            {
                dev.Samples = dev.Samples.Where(IsGood).ToList();
                _devices[key] = dev;
            }

        FileLog.Write($"[NetDiagDeviceStore] Load: restored {_devices.Count} device(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[NetDiagDeviceStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Devices = _devices };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NetDiagDeviceStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
