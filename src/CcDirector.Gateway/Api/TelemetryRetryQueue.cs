using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 1 (issue #629): the durable, bounded, restart-surviving RETRY QUEUE
/// that sits BEHIND the login-telemetry relay (issue #628). The relay no longer forwards inline; it
/// hands every accepted event to this queue, which owns delivery to the backend.
///
/// Behaviour:
/// <list type="bullet">
///   <item>FIFO per tenant: events flush in the order they were enqueued WITHIN a tenant (best-effort
///     FIFO, at-least-once).</item>
///   <item>Retry with backoff: a failed or unreachable forward leaves the event at the head of ITS
///     TENANT's line and the flusher waits the retry interval before trying again, so a backend outage
///     queues events instead of dropping them.</item>
///   <item>Bounded PER TENANT: each tenant's queued events never grow past <see cref="MaxSize"/>; when a
///     tenant is full its OWN oldest event is evicted (dropped) with a logged WARNING, so one tenant's
///     volume can never evict another tenant's queued event.</item>
///   <item>Durable: the whole queue is persisted to one JSON file (the WorkListStore precedent: atomic
///     temp + rename write-through, reload on construction, corrupt-file quarantine) under the Gateway
///     config directory, so queued events survive a Gateway restart.</item>
/// </list>
///
/// Multi-tenancy (audit MTR gap C: telemetry queue): every queued event is tagged with the
/// SERVER-RESOLVED tenant of the request that enqueued it (never a client-supplied value). The bound and
/// the flush are PER TENANT: a caller can only evict its own tenant's oldest event, and a poison event
/// that the backend permanently rejects blocks only its own tenant's line - other tenants keep flushing
/// past it (no cross-tenant head-of-line block). On self-host every event is <see cref="TenantId.Local"/>
/// and the behaviour is exactly the single-line FIFO it always was.
///
/// Security (issue #628 property preserved): a queued payload carries the inbound access token (the
/// Bearer) in memory and on disk so it can be replayed, but the token value is NEVER written to the
/// Gateway log on any path - every log line records only the target URL, the queue depth, and the
/// outcome.
/// </summary>
public sealed class TelemetryRetryQueue : IAsyncDisposable
{
    /// <summary>The default maximum number of queued events before the oldest is evicted.</summary>
    public const int DefaultMaxSize = 1000;

    /// <summary>
    /// The isolated partition legacy, pre-tag persisted events are loaded into (audit MTR gap C). A queue
    /// file written before the tenant tag existed has events with an EMPTY tenant; on a hosted Gateway those
    /// events came from many real accounts but carry no way to tell them apart, so they must NOT be collapsed
    /// into any real tenant's partition - least of all the shared <see cref="TenantId.Local"/> one, where one
    /// account's legacy poison event would head-of-line-block every other account's legacy event.
    ///
    /// They are instead quarantined under this reserved partition key, which is deliberately NOT a valid
    /// <see cref="TenantId"/> (the '#' can never appear in a real tenant id, Local, or System), so it can
    /// never collide with a real tenant's line. Because the bound and the flush are keyed by this string, the
    /// quarantine lane is fully isolated: it still drains (at-least-once preserved - legacy events are
    /// delivered, not dropped) and a poison event in it blocks ONLY the quarantine lane, never any real
    /// tenant's flush. Newly-enqueued events always carry a real server-resolved tenant and never land here.
    /// </summary>
    public const string LegacyUntaggedPartition = "__legacy-untagged#quarantine__";

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly HttpClient _client;
    private readonly TimeSpan _retryInterval;
    private readonly IGatewayTelemetryTokenSource? _gatewayTokenSource;
    private readonly LinkedList<QueuedEvent> _events = new();

    private readonly CancellationTokenSource _flushCts = new();
    private Task? _flushLoop;
    private bool _disposed;

    /// <summary>The maximum number of events the queue holds before evicting the oldest.</summary>
    public int MaxSize { get; }

    /// <summary>The current number of queued events awaiting delivery.</summary>
    public int Depth
    {
        get { lock (_gate) return _events.Count; }
    }

    /// <param name="path">
    /// The JSON file the queue persists to. REQUIRED so no caller silently lands on the real user's
    /// file: production (<see cref="GatewayHost"/>) passes telemetry-queue.json in the Gateway config
    /// directory; tests pass an isolated temp path.
    /// </param>
    /// <param name="client">The HttpClient used to forward queued events to the backend.</param>
    /// <param name="retryInterval">
    /// How long the flusher waits between drain passes when the backend is unreachable (also the
    /// idle poll interval when the queue is empty).
    /// </param>
    /// <param name="maxSize">The bound; the oldest event is evicted once the queue exceeds this.</param>
    /// <param name="gatewayTokenSource">
    /// Gateway Centralization Phase 2 (issue #639): the source of the GATEWAY's own account token,
    /// attached at FORWARD time when the Gateway acts as the single egress to the cloud. When supplied:
    /// the Gateway's token is attached and any per-event stored <see cref="QueuedEvent.Bearer"/> (a
    /// leftover inbound Director token) is IGNORED; and when the Gateway is NOT signed in the forward is
    /// deferred (the event stays queued, FIFO preserved, and flushes once the Gateway signs in). When
    /// null (a host with no credential service, or Phase 1 callers) the queue falls back to the stored
    /// per-event Bearer - the original #628/#629 behaviour, unchanged.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxSize or retryInterval is not positive.</exception>
    public TelemetryRetryQueue(
        string path,
        HttpClient client,
        TimeSpan retryInterval,
        int maxSize = DefaultMaxSize,
        IGatewayTelemetryTokenSource? gatewayTokenSource = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("queue path is required", nameof(path));
        if (client is null)
            throw new ArgumentNullException(nameof(client));
        if (maxSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSize), "maxSize must be positive");
        if (retryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryInterval), "retryInterval must be positive");

        _path = path;
        _client = client;
        _retryInterval = retryInterval;
        _gatewayTokenSource = gatewayTokenSource;
        MaxSize = maxSize;
        Load();
    }

    /// <summary>
    /// Start the background flush loop. Called once by the host after construction. A second call is
    /// a no-op so the loop is never double-started.
    /// </summary>
    public void StartFlushing()
    {
        lock (_gate)
        {
            if (_flushLoop is not null)
                return;
            _flushLoop = Task.Run(() => FlushLoopAsync(_flushCts.Token));
        }
        FileLog.Write($"[TelemetryRetryQueue] StartFlushing: retryInterval={_retryInterval.TotalSeconds}s, maxSize={MaxSize}, depth={Depth}");
    }

    /// <summary>
    /// Enqueue one accepted telemetry event for durable delivery. The body and Bearer are stored
    /// verbatim so they replay UNCHANGED. The event is tagged with <paramref name="tenant"/> - the tenant
    /// the CALLING endpoint resolved from the authenticated request (never a client-supplied value) - and
    /// the bound is enforced PER TENANT: when this tenant already holds <see cref="MaxSize"/> events, THIS
    /// tenant's own oldest is evicted first (logged WARNING), so a flood from one tenant never drops
    /// another tenant's queued event. The token value and the raw tenant id are never logged.
    /// </summary>
    /// <param name="targetUrl">The backend URL to forward to.</param>
    /// <param name="body">The event JSON, forwarded unchanged.</param>
    /// <param name="bearer">The inbound access token, replayed unchanged; NEVER logged.</param>
    /// <param name="tenant">
    /// The server-resolved owning tenant (<see cref="TenantId.Local"/> on self-host). REQUIRED and must be
    /// valid - an unresolved tenant is a denied request at the endpoint, never a queued event under a guess.
    /// </param>
    /// <exception cref="ArgumentException">targetUrl is null/empty/whitespace, or tenant is invalid.</exception>
    public void Enqueue(string targetUrl, string body, string? bearer, TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            throw new ArgumentException("targetUrl is required", nameof(targetUrl));
        if (!tenant.IsValid)
            throw new ArgumentException("a valid tenant is required to enqueue a telemetry event", nameof(tenant));

        var item = new QueuedEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            EnqueuedAtUtc = DateTime.UtcNow,
            TargetUrl = targetUrl,
            Body = body ?? string.Empty,
            Bearer = bearer,
            Tenant = tenant.Value,
        };

        int depth;
        bool evicted = false;
        lock (_gate)
        {
            _events.AddLast(item);
            // Per-tenant bound: count and evict only THIS tenant's own events, so one tenant's volume can
            // never push another tenant's event out of the shared list.
            while (CountForTenant(item.Tenant) > MaxSize)
            {
                var oldest = FirstNodeForTenant(item.Tenant);
                if (oldest is null)
                    break;
                _events.Remove(oldest);
                evicted = true;
                FileLog.Write($"[TelemetryRetryQueue] WARNING per-tenant bound exceeded (maxSize={MaxSize}, tenant={tenant.ToLogString()}); evicted OLDEST event id={oldest.Value.Id} enqueuedAt={oldest.Value.EnqueuedAtUtc:O} target={oldest.Value.TargetUrl} (dropped, not delivered)");
            }
            depth = _events.Count;
            Save();
        }

        FileLog.Write($"[TelemetryRetryQueue] Enqueue: target={targetUrl} (bearerPresent={(bearer is not null)}, tenant={tenant.ToLogString()}), depth={depth}{(evicted ? " (tenant oldest evicted)" : "")}");
    }

    /// <summary>Count the queued events belonging to one tenant. Caller holds <see cref="_gate"/>.</summary>
    private int CountForTenant(string tenant)
    {
        var count = 0;
        for (var node = _events.First; node is not null; node = node.Next)
            if (string.Equals(node.Value.Tenant, tenant, StringComparison.Ordinal))
                count++;
        return count;
    }

    /// <summary>The oldest (head-most) queued node for one tenant, or null. Caller holds <see cref="_gate"/>.</summary>
    private LinkedListNode<QueuedEvent>? FirstNodeForTenant(string tenant)
    {
        for (var node = _events.First; node is not null; node = node.Next)
            if (string.Equals(node.Value.Tenant, tenant, StringComparison.Ordinal))
                return node;
        return null;
    }

    /// <summary>The node carrying a given event id, or null. Caller holds <see cref="_gate"/>.</summary>
    private LinkedListNode<QueuedEvent>? FindNodeById(string id)
    {
        for (var node = _events.First; node is not null; node = node.Next)
            if (string.Equals(node.Value.Id, id, StringComparison.Ordinal))
                return node;
        return null;
    }

    /// <summary>
    /// Try to drain the queue once, in per-tenant FIFO order. Each pass repeatedly picks the head-most
    /// event whose tenant is still flushing, forwards it, and on success removes it. On a failure the
    /// event stays queued and its TENANT is marked done-for-this-pass, so that tenant's later events wait
    /// (its FIFO + at-least-once preserved) while EVERY OTHER tenant keeps flushing past it - one tenant's
    /// poison event can never head-of-line-block another tenant. Returns the number of events delivered
    /// this pass. Public so a test can trigger a deterministic drain without waiting on the timer.
    /// </summary>
    public async Task<int> FlushOnceAsync(CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        // Tenants whose earliest event failed THIS pass. Skipped for the rest of the pass so their FIFO
        // order holds; they are retried on the next pass.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            QueuedEvent? next = null;
            lock (_gate)
            {
                for (var node = _events.First; node is not null; node = node.Next)
                {
                    if (!blocked.Contains(node.Value.Tenant))
                    {
                        next = node.Value;
                        break;
                    }
                }
            }
            if (next is null)
                break; // nothing left whose tenant is still flushing this pass

            var ok = await TryForwardAsync(next, cancellationToken);
            if (!ok)
            {
                // Leave it queued; stop this tenant for the pass so its order holds and other tenants
                // are not blocked behind it.
                blocked.Add(next.Tenant);
                continue;
            }

            lock (_gate)
            {
                // Remove that specific event if still present (a single flusher drains; Enqueue only
                // appends to the tail or evicts its own tenant's oldest).
                var node = FindNodeById(next.Id);
                if (node is not null)
                {
                    _events.Remove(node);
                    Save();
                }
            }
            delivered++;
        }
        return delivered;
    }

    /// <summary>
    /// Forward one event to its backend URL with the stored body and the token to attach. Returns true
    /// on a 2xx, false on any non-2xx or transport failure - and false WITHOUT forwarding when a Gateway
    /// token source is configured but the Gateway is not signed in, so the event stays queued for a later
    /// pass (issue #639). The token value is never logged.
    /// </summary>
    private async Task<bool> TryForwardAsync(QueuedEvent item, CancellationToken cancellationToken)
    {
        // Issue #639: when a Gateway token source is wired, the Gateway attaches its OWN account token
        // and the per-event stored Bearer (a leftover inbound Director token) is ignored. If the Gateway
        // is not signed in, the forward is DEFERRED (event stays queued) - never sent without the token.
        string? tokenToAttach;
        if (_gatewayTokenSource is not null)
        {
            if (!_gatewayTokenSource.TryGetAccessToken(out tokenToAttach) || tokenToAttach is null)
            {
                FileLog.Write($"[TelemetryRetryQueue] forward DEFERRED (gateway not signed in): {item.TargetUrl}, id={item.Id} (kept queued, will flush after sign-in)");
                return false;
            }
        }
        else
        {
            // Phase 1 / no-credential-service host: fall back to the stored per-event Bearer unchanged.
            tokenToAttach = item.Bearer;
        }

        try
        {
            using var forward = new HttpRequestMessage(HttpMethod.Post, item.TargetUrl)
            {
                Content = new StringContent(item.Body, System.Text.Encoding.UTF8, "application/json"),
            };
            if (tokenToAttach is not null)
                forward.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenToAttach);

            using var resp = await _client.SendAsync(forward, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[TelemetryRetryQueue] forward OK: {item.TargetUrl} -> {(int)resp.StatusCode}, id={item.Id}");
                return true;
            }

            FileLog.Write($"[TelemetryRetryQueue] forward FAILED (backend status): {item.TargetUrl} -> {(int)resp.StatusCode}, id={item.Id} (will retry)");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown - not a delivery failure to log as an error; just leave it queued.
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TelemetryRetryQueue] forward FAILED (unreachable): {item.TargetUrl} -> {ex.Message}, id={item.Id} (will retry)");
            return false;
        }
    }

    /// <summary>
    /// The background flush loop: drains the queue head-first, then waits the retry interval before
    /// the next pass. A pass that delivers everything still waits the interval before polling again,
    /// so an empty queue costs one timer wakeup per interval and nothing more.
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Depth > 0)
                {
                    var delivered = await FlushOnceAsync(cancellationToken);
                    if (delivered > 0)
                        FileLog.Write($"[TelemetryRetryQueue] flush pass delivered {delivered}, remaining depth={Depth}");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TelemetryRetryQueue] flush loop error: {ex.Message}");
            }

            try { await Task.Delay(_retryInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- persistence (issue #629; WorkListStore precedent, issue #301) -----------------------

    /// <summary>One queued telemetry event, persisted verbatim so it replays unchanged.</summary>
    public sealed class QueuedEvent
    {
        public string Id { get; set; } = string.Empty;
        public DateTime EnqueuedAtUtc { get; set; }
        public string TargetUrl { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;

        /// <summary>The inbound access token, replayed unchanged. On disk, never logged.</summary>
        public string? Bearer { get; set; }

        /// <summary>
        /// The server-resolved owning tenant (audit MTR gap C). The bound and the flush are scoped by this,
        /// so a caller only ever evicts/blocks its own tenant. A queue file written before this field existed
        /// has it empty; <see cref="Load"/> quarantines such legacy events into the isolated
        /// <see cref="LegacyUntaggedPartition"/> so they can never head-of-line-block a real tenant.
        /// </summary>
        public string Tenant { get; set; } = string.Empty;
    }

    /// <summary>The on-disk shape: one document holding the ordered queue.</summary>
    private sealed class QueueFile
    {
        public List<QueuedEvent> Events { get; set; } = new();
    }

    /// <summary>
    /// Load the queue written by a previous Gateway run. Called once from the constructor. A missing
    /// file is the normal first boot (empty queue, logged), never an error. A corrupt file is
    /// quarantined (renamed next to the original with a timestamp suffix) so its bytes are preserved
    /// for the operator and never silently overwritten, and the queue then starts empty so the
    /// Gateway still boots. The token values are NOT logged on any load path.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[TelemetryRetryQueue] Load: no queue file at {_path}; starting empty");
            return;
        }

        QueueFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<QueueFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no queue document)");
            return;
        }

        foreach (var ev in parsed.Events)
        {
            if (string.IsNullOrWhiteSpace(ev.Id) || string.IsNullOrWhiteSpace(ev.TargetUrl))
            {
                Quarantine("a persisted event has an empty id or targetUrl");
                _events.Clear();
                return;
            }
            // A queue file written before the tenant tag existed has an empty Tenant. Such legacy events
            // cannot be attributed to a real account, so they are quarantined into an ISOLATED partition
            // (never the shared Local one) - see <see cref="LegacyUntaggedPartition"/>. This keeps a legacy
            // poison event from head-of-line-blocking any real tenant's flush while still delivering the
            // legacy events (they drain in their own isolated lane).
            if (string.IsNullOrWhiteSpace(ev.Tenant))
                ev.Tenant = LegacyUntaggedPartition;
            _events.AddLast(ev);
        }

        FileLog.Write($"[TelemetryRetryQueue] Load: restored {_events.Count} queued event(s) from {_path}");
    }

    /// <summary>
    /// Preserve an unreadable queue file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly. The
    /// original path is then free for the next write-through. The move is not allowed to fail silently:
    /// if even the quarantine fails, the exception propagates and the Gateway does not start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[TelemetryRetryQueue] Load FAILED: queue file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    /// <summary>
    /// Write-through: serialize the whole queue and atomically replace the file (temp + rename), so a
    /// concurrent reader or a crash mid-write never sees a half-written queue. Called inside the lock by
    /// every mutation. A failed save is a LOGGED error that propagates - never a silent skip.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new QueueFile { Events = _events.ToList() };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TelemetryRetryQueue] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stop the background flush loop. The persisted file already holds every undelivered event (it is
    /// written through on every mutation), so a stop never loses queued events - they reload on the
    /// next Gateway start.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        FileLog.Write($"[TelemetryRetryQueue] DisposeAsync: stopping flush loop, depth={Depth}");
        _flushCts.Cancel();
        Task? loop;
        lock (_gate) loop = _flushLoop;
        if (loop is not null)
        {
            try { await loop; }
            catch (Exception ex) { FileLog.Write($"[TelemetryRetryQueue] flush loop stop error: {ex.Message}"); }
        }
        _flushCts.Dispose();
    }
}
