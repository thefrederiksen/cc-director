using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// Fills the Director's materialized skill store from the Gateway - the network half of installing
/// skills where each agent looks for them. Runs OFF the launch path, exactly like the skill index it
/// sits beside; <see cref="SkillDirectoryInstaller.InstallFor"/> is the synchronous half that reads
/// what this wrote.
///
/// The store mirrors what the Gateway currently SERVES. Only skills the register reports as ENABLED
/// are materialized, and a skill that disappears or is switched off is deleted from the store, so the
/// next session launch removes it from every agent's directory. That is what keeps a file on disk
/// from outliving the decision that withdrew it.
///
/// A failure leaves the previous store exactly as it was and says so. It never half-writes: each
/// skill directory is rebuilt whole, and a skill whose fetch fails keeps the copy that was already
/// there rather than being replaced by a partial one.
/// </summary>
public sealed class SkillStoreRefresh
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _client;
    private readonly string? _storeRootOverride;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;

    public SkillStoreRefresh() : this(null) { }

    /// <summary>Creates the refresher; the parameters exist so tests can point it at a temporary
    /// store and a hermetic Gateway.</summary>
    public SkillStoreRefresh(
        string? storeRoot = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null)
    {
        _storeRootOverride = storeRoot;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    private string StoreRoot() => _storeRootOverride ?? SkillDirectoryInstaller.StoreRoot();

    /// <summary>Fetch every enabled skill and materialize it into the store. Returns how many skills
    /// the store now holds, or -1 when no Gateway is configured and nothing was attempted.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
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
            FileLog.Write("[SkillStoreRefresh] RefreshAsync: no gateway.url configured -> keeping the current store");
            return -1;
        }

        var baseUrl = gatewayUrl.TrimEnd('/');
        var register = await GetAsync<RegisterResponse>(baseUrl + "/gateway/skills", token, ct)
            .ConfigureAwait(false);
        var wanted = (register?.Skills ?? new List<RegisterRow>())
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Id))
            .ToList();

        var store = StoreRoot();
        Directory.CreateDirectory(store);

        foreach (var row in wanted)
        {
            var detail = await GetAsync<VersionDetail>(
                $"{baseUrl}/gateway/skills/{Uri.EscapeDataString(row.Id)}/versions/{row.Version}", token, ct)
                .ConfigureAwait(false);
            if (detail is null)
            {
                // Leave whatever is already materialized for this skill in place: a skill we could not
                // read is not the same as a skill that was withdrawn, and replacing it with nothing
                // would quietly remove a working capability because one request failed.
                FileLog.Write($"[SkillStoreRefresh] could not read '{row.Id}' v{row.Version} - keeping what is already stored");
                continue;
            }

            SkillDirectoryInstaller.Materialize(store, ToBundle(row.Id, detail));
        }

        // Anything in the store the register no longer serves is gone: switched off, archived, or
        // never ours. Reconcile, never add.
        var keep = new HashSet<string>(wanted.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(store))
        {
            var name = Path.GetFileName(directory);
            if (!keep.Contains(name))
            {
                Directory.Delete(directory, recursive: true);
                FileLog.Write($"[SkillStoreRefresh] dropped '{name}' from the store - the Gateway no longer serves it");
            }
        }

        FileLog.Write($"[SkillStoreRefresh] RefreshAsync: store now holds {wanted.Count} skills");
        return wanted.Count;
    }

    private static SkillBundle ToBundle(string id, VersionDetail detail)
    {
        var files = (detail.Files ?? new List<FileRow>()).Select(f =>
        {
            var isBase64 = string.Equals(f.Encoding?.Trim(), "base64", StringComparison.OrdinalIgnoreCase);
            var bytes = isBase64
                ? Convert.FromBase64String(f.Content ?? "")
                : System.Text.Encoding.UTF8.GetBytes(f.Content ?? "");
            return new SkillFileBytes(f.FileName ?? "", bytes, f.Executable);
        }).ToList();

        return new SkillBundle(
            id,
            detail.Version,
            detail.ContentHash ?? "",
            detail.Summary ?? "",
            detail.Triggers ?? new List<string>(),
            detail.BodyMarkdown ?? "",
            files,
            detail.License,
            detail.Compatibility,
            detail.AllowedTools,
            detail.Metadata);
    }

    private async Task<T?> GetAsync<T>(string url, string? token, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[SkillStoreRefresh] GET {url} -> HTTP {(int)response.StatusCode}");
            return null;
        }

        // A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. This Gateway serves the Cockpit's
        // single-page app and answers UNKNOWN page paths with its HTML shell, HTTP 200 - which is
        // exactly what a Gateway too old to know about skills returns here. Believed at face value it
        // would be written to disk AS A SKILL. So the promised content type is asserted, always.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[SkillStoreRefresh] GET {url} answered '{mediaType}' instead of JSON - this Gateway " +
                          "does not serve the skill library yet. Nothing materialized.");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
    }

    private sealed class RegisterResponse
    {
        [JsonPropertyName("skills")] public List<RegisterRow>? Skills { get; set; }
    }

    private sealed class RegisterRow
    {
        public string Id { get; set; } = "";
        public int Version { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class VersionDetail
    {
        public int Version { get; set; }
        public string? Summary { get; set; }
        public List<string>? Triggers { get; set; }
        public string? BodyMarkdown { get; set; }
        public List<FileRow>? Files { get; set; }
        public string? License { get; set; }
        public string? Compatibility { get; set; }
        public string? AllowedTools { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public string? ContentHash { get; set; }
    }

    private sealed class FileRow
    {
        public string? FileName { get; set; }
        public string? Content { get; set; }
        public string? Encoding { get; set; }
        public bool Executable { get; set; }
    }
}
