using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>One cached workflow-index value: the rendered index text and when it was cached.</summary>
public sealed record WorkflowIndexCacheEntry(
    [property: JsonPropertyName("index")] string Index,
    [property: JsonPropertyName("cachedAtUtc")] DateTime CachedAtUtc);

/// <summary>
/// The Director-side reader of the Gateway's workflow CATALOG INDEX (Workflows mission, phase 5).
/// Workflows are fleet-wide, Gateway-stored units of conduct; the index is the few-line discoverability
/// block that rides the fleet preamble (the <c>[WORKFLOW_INDEX]</c> placeholder) - one line per
/// published workflow, so every session knows the catalog exists and how to fetch a workflow's
/// conduct, without any session paying the cost of the conduct bodies it never uses.
///
/// Structurally a sibling of <see cref="InjectedTextStore"/>, split the same way so a synchronous
/// launch never blocks on the network:
///   - <see cref="RefreshAsync"/> fetches <c>GET /gateway/workflows</c> off the launch path (the host
///     calls it on Gateway-connect and on the same refresh timer as the injected text), renders the
///     index text once, and writes the on-disk cache.
///   - <see cref="ActiveIndex"/> reads that cache synchronously at session launch.
///
/// Degraded behaviour: the last-known cache; when nothing has ever been cached, the EMPTY string -
/// a fresh Director that has never reached the Gateway simply injects no index (the agent can still
/// discover workflows through <c>cc-devthrottle actions</c>), which is exactly what sessions got
/// before this feature existed.
/// </summary>
public sealed class WorkflowIndexStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Gateway path the catalog is read from, appended to <c>gateway.url</c>.</summary>
    public const string GatewayPath = "/gateway/workflows";

    /// <summary>One summary line per workflow; a longer summary is cut so the whole index stays a
    /// few-line block, not a page.</summary>
    public const int MaxSummaryChars = 160;

    private readonly string _cachePath;
    private readonly HttpClient _client;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;

    /// <summary>The store over the real Director cache file and the real <c>gateway.url</c>.</summary>
    public WorkflowIndexStore() : this(null) { }

    /// <summary>Creates the store; parameters mirror <see cref="InjectedTextStore"/> (tests inject a
    /// temporary cache path, a stub client, and a hermetic gateway url + token).</summary>
    public WorkflowIndexStore(string? cachePath = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null)
    {
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? CcStorage.WorkflowIndexCache() : cachePath;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    /// <summary>
    /// The index text that will ride the next session's preamble, read synchronously from the
    /// last-known cache. Empty when nothing has ever been cached. Never a network call.
    /// </summary>
    public string ActiveIndex()
    {
        var cached = ReadCache();
        return cached is null ? "" : cached.Index;
    }

    /// <summary>
    /// Refresh the Director's last-known workflow index from the Gateway: fetch the published catalog,
    /// render the index block once, and write it to the on-disk cache. The ASYNC, network path - the
    /// host calls it off the launch path. When no Gateway is configured this is a logged no-op that
    /// keeps the current cache.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        string? gatewayUrl;
        string? token;
        if (_gatewayUrlOverride is not null)
        {
            gatewayUrl = _gatewayUrlOverride.Trim();
            token = _tokenOverride;
        }
        else
        {
            var config = GatewayConfig.Load();
            gatewayUrl = config.Url?.Trim();
            token = config.Token;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            FileLog.Write("[WorkflowIndexStore] RefreshAsync: no gateway.url configured -> keeping the last-known cache");
            return;
        }

        var endpoint = $"{gatewayUrl.TrimEnd('/')}{GatewayPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FileLog.Write($"[WorkflowIndexStore] RefreshAsync: GET {endpoint}");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CatalogResponse>(JsonOpts, ct).ConfigureAwait(false);
        if (payload is null)
            throw new InvalidOperationException($"Gateway returned an empty body from {endpoint}");

        var index = BuildIndexText(payload.Workflows ?? new List<CatalogWorkflow>());
        WriteCache(new WorkflowIndexCacheEntry(index, DateTime.UtcNow));
        FileLog.Write($"[WorkflowIndexStore] RefreshAsync: {payload.Workflows?.Count ?? 0} workflow(s) indexed (cached)");
    }

    /// <summary>
    /// Render the catalog into the index block. Pure and static so the format is pinned by test.
    /// Empty catalog renders the empty string (no header floating over nothing). ASCII only.
    /// </summary>
    public static string BuildIndexText(IReadOnlyList<CatalogWorkflow> workflows)
    {
        if (workflows.Count == 0)
            return "";

        var text = new StringBuilder();
        text.Append("[Workflows] Named ways of working this fleet defines - usable by ANY agent. Before taking\n");
        text.Append("on work that matches one, fetch its conduct and FOLLOW it:  cc-devthrottle workflow instructions <id>\n");
        foreach (var workflow in workflows)
        {
            // ONE line per workflow is the index's structural promise. Summaries are authored data,
            // so any run of whitespace - including newlines and control characters that could dress a
            // summary up as extra preamble lines in every session - collapses to a single space
            // before the length cap is applied.
            var summary = string.Join(' ',
                (workflow.Summary ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (summary.Length > MaxSummaryChars)
                summary = summary[..MaxSummaryChars].TrimEnd() + "...";
            text.Append($"  - {workflow.Id}: {summary}\n");
        }
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The last-known cached value on disk, or null when nothing has been cached yet.</summary>
    public WorkflowIndexCacheEntry? ReadCache()
    {
        if (!File.Exists(_cachePath))
            return null;

        var json = File.ReadAllText(_cachePath);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<WorkflowIndexCacheEntry>(json, JsonOpts);
    }

    /// <summary>Write the cache. Used by <see cref="RefreshAsync"/> and by tests to seed a state.</summary>
    public void WriteCache(WorkflowIndexCacheEntry entry)
    {
        var dir = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_cachePath}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(entry, JsonOpts));
    }

    /// <summary>The slice of <c>GET /gateway/workflows</c> the index needs.</summary>
    public sealed record CatalogWorkflow(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("summary")] string? Summary);

    private sealed record CatalogResponse(
        [property: JsonPropertyName("workflows")] List<CatalogWorkflow>? Workflows);
}
