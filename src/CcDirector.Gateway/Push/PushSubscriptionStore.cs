using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Push;

/// <summary>
/// The Gateway-side set of Web Push subscriptions - one per phone/browser that opted in to the
/// app-icon "needs you" dot. A subscription is the browser's push endpoint plus the two client
/// public keys (<c>p256dh</c> and <c>auth</c>) needed to encrypt a message to it, exactly the shape
/// a browser's <c>PushSubscription.toJSON()</c> produces.
///
/// Persisted to <c>%LOCALAPPDATA%\cc-director\config\gateway\push-subscriptions.json</c> so opt-in
/// survives a Gateway restart. The endpoint URL is a capability (whoever holds it can ask the push
/// service to deliver to that device), so the file is treated as a secret store - it lives under the
/// per-user config root and the endpoint/keys are never written to the log (only counts are).
///
/// Keyed by endpoint, which is unique per subscription, so re-subscribing the same device is an
/// idempotent upsert rather than a duplicate. Thread-safe: subscribe/unsubscribe run on request
/// threads while the background notifier enumerates the set.
/// </summary>
public sealed class PushSubscriptionStore
{
    private readonly string _storePath;
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, StoredPushSubscription> _byEndpoint =
        new(StringComparer.Ordinal);

    public PushSubscriptionStore() : this(null) { }

    /// <param name="storePath">Override the store file (tests pass an isolated temp path); production
    /// omits it for the shared default under the gateway config root.</param>
    public PushSubscriptionStore(string? storePath)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.ToolConfig("gateway"), "push-subscriptions.json")
            : storePath;
        Load();
    }

    /// <summary>The on-disk store file path.</summary>
    public string StorePath => _storePath;

    /// <summary>The number of registered subscriptions.</summary>
    public int Count => _byEndpoint.Count;

    /// <summary>
    /// Record (or refresh) a subscription. A repeat of the same endpoint replaces the prior keys and
    /// keeps one entry. Returns true when a NEW endpoint was added, false when an existing one was
    /// refreshed - the caller uses this to decide whether a fresh device should get an immediate
    /// "current count" push.
    /// </summary>
    public bool Add(string endpoint, string p256dh, string auth)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("endpoint is required", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(p256dh))
            throw new ArgumentException("p256dh key is required", nameof(p256dh));
        if (string.IsNullOrWhiteSpace(auth))
            throw new ArgumentException("auth key is required", nameof(auth));

        var isNew = !_byEndpoint.ContainsKey(endpoint);
        _byEndpoint[endpoint] = new StoredPushSubscription
        {
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAtUtc = DateTime.UtcNow,
        };
        Save();
        FileLog.Write($"[PushSubscriptionStore] {(isNew ? "Added" : "Refreshed")} a subscription, total={_byEndpoint.Count}");
        return isNew;
    }

    /// <summary>Drop a subscription by endpoint. Returns true when one was removed.</summary>
    public bool Remove(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;
        if (!_byEndpoint.TryRemove(endpoint, out _))
            return false;
        Save();
        FileLog.Write($"[PushSubscriptionStore] Removed a subscription, total={_byEndpoint.Count}");
        return true;
    }

    /// <summary>A snapshot of every stored subscription.</summary>
    public IReadOnlyList<StoredPushSubscription> All() => _byEndpoint.Values.ToList();

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        var json = File.ReadAllText(_storePath);
        if (string.IsNullOrWhiteSpace(json)) return;

        var records = JsonSerializer.Deserialize<List<StoredPushSubscription>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (records is null) return;
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Endpoint)) continue;
            _byEndpoint[record.Endpoint] = record;
        }
        FileLog.Write($"[PushSubscriptionStore] Loaded {_byEndpoint.Count} subscription(s) from {_storePath}");
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_byEndpoint.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            // Atomic replace so a crash mid-write never leaves a half-written subscription file.
            var temp = _storePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _storePath, overwrite: true);
        }
    }
}

/// <summary>One stored Web Push subscription: the browser push endpoint and the client keys needed
/// to encrypt a message to it. Mirrors a browser <c>PushSubscription.toJSON()</c> (endpoint + keys).</summary>
public sealed class StoredPushSubscription
{
    public string Endpoint { get; set; } = "";

    /// <summary>The client P-256 ECDH public key (base64url), from <c>subscription.keys.p256dh</c>.</summary>
    public string P256dh { get; set; } = "";

    /// <summary>The client auth secret (base64url), from <c>subscription.keys.auth</c>.</summary>
    public string Auth { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
}
