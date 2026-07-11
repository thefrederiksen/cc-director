using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>A manifest plus the per-asset download URLs needed to fetch each asset.</summary>
public sealed record ResolvedRelease(ReleaseManifest Manifest, IReadOnlyDictionary<string, string> DownloadUrls);

/// <summary>
/// Resolves the release a plan is computed against. Shared by both front-ends
/// (the WPF installer UI and the CLI). Modes:
///   - a local manifest file (LoadLocalManifest): plan/dry-run only, no URLs.
///   - a local release directory (LoadLocalReleaseDir): offline install/update.
///   - "latest" (FetchLatestAsync): the GitHub latest STABLE release, mapping asset
///     download URLs and parsing release-manifest.json. Used by the silent auto-updater.
///   - "for this setup exe" (FetchReleaseForSetupAsync): the release the running setup
///     executable was BUILT for. A stable setup exe resolves the latest stable (identical to
///     FetchLatestAsync); a pre-release setup exe resolves its matching pre-release from the
///     full release list, which /releases/latest hides (issue #1294).
/// </summary>
public sealed class ReleaseSource
{
    private const string ManifestAssetName = "release-manifest.json";

    /// <summary>Total attempts at the release fetch (1 initial + retries) before giving up on a rate limit.</summary>
    private const int MaxAttempts = 4;

    /// <summary>Backoff floor when GitHub gives no reset hint, doubled per attempt.</summary>
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on any single wait, so a far-future reset hint never stalls the install.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ReleaseInfoCache _cache;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ReleaseSource(HttpClient? http = null, ReleaseInfoCache? cache = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("cc-director-setup");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _cache = cache ?? new ReleaseInfoCache();
        // The delay seam lets tests assert the backoff happened without actually waiting.
        _delay = delay ?? Task.Delay;
    }

    public static ResolvedRelease LoadLocalManifest(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest file not found: {path}", path);
        var manifest = ReleaseManifest.Parse(File.ReadAllText(path));
        return new ResolvedRelease(manifest, new Dictionary<string, string>());
    }

    /// <summary>
    /// Treat a local directory as a release: it must contain release-manifest.json
    /// plus each asset file. The "download URL" for an asset is its local file
    /// path, so a full install/update can run offline with no network and no admin
    /// (the Workstation flow needs neither). Used for hermetic testing.
    /// </summary>
    public static ResolvedRelease LoadLocalReleaseDir(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestAssetName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {ManifestAssetName} in release dir: {dir}", manifestPath);

        var manifest = ReleaseManifest.Parse(File.ReadAllText(manifestPath));
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetName in manifest.Assets.Keys)
        {
            var assetPath = Path.Combine(dir, assetName);
            if (File.Exists(assetPath)) urls[assetName] = assetPath; // local path acts as the URL
        }
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// Fetch the GitHub latest STABLE release (the /releases/latest endpoint, which excludes
    /// pre-releases and drafts). Used by the silent auto-updater and by a stable setup exe.
    /// The request is hardened against the shared-network-address rate limit (issue #266) - see
    /// <see cref="GetReleaseBodyAsync"/>.
    /// </summary>
    /// <exception cref="GitHubRateLimitException">The fetch was rate-limited and retries were exhausted.</exception>
    public async Task<ResolvedRelease> FetchLatestAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{GitHubRepositoryDefaults.Slug}/releases/latest";
        var body = await GetReleaseBodyAsync(url, useCache: true, ct);
        return await BuildResolvedReleaseAsync(body, ct);
    }

    /// <summary>
    /// Resolve the release the running setup executable was BUILT for, decided in memory from the
    /// setup exe's own stamped version - nothing is written to disk and no channel file is used
    /// (issue #1294).
    ///
    /// A stable setup exe (version with no pre-release suffix, or an unreadable stamp) resolves the
    /// latest stable release - identical to <see cref="FetchLatestAsync"/> and unchanged for stable
    /// users. A pre-release setup exe (for example "1.1.0-rc4") resolves its MATCHING pre-release
    /// from the full <c>/releases</c> list, because GitHub's <c>/releases/latest</c> hides
    /// pre-releases and would otherwise hand a pre-release installer the newest stable build.
    /// </summary>
    /// <param name="setupVersion">
    /// The setup exe's stamped version. Null reads it from the entry assembly
    /// (<see cref="SetupExecutableVersion.Read"/>); tests pass it explicitly.
    /// </param>
    /// <exception cref="GitHubRateLimitException">The fetch was rate-limited and retries were exhausted.</exception>
    public async Task<ResolvedRelease> FetchReleaseForSetupAsync(CancellationToken ct, string? setupVersion = null)
    {
        setupVersion ??= SetupExecutableVersion.Read();

        if (!VersionUtil.HasPreReleaseSuffix(setupVersion))
        {
            EngineLog.Write($"[ReleaseSource] Setup version '{setupVersion}' is stable; installing the latest stable release.");
            return await FetchLatestAsync(ct);
        }

        EngineLog.Write($"[ReleaseSource] Setup version '{setupVersion}' is a pre-release; resolving from the full release list.");
        var url = $"https://api.github.com/repos/{GitHubRepositoryDefaults.Slug}/releases";
        // The list body is not cached: the ETag cache is keyed to the single /releases/latest entity,
        // and the list is only consulted on the rare pre-release install path.
        var listBody = await GetReleaseBodyAsync(url, useCache: false, ct);
        var releaseJson = SelectPreRelease(listBody, setupVersion);
        return await BuildResolvedReleaseAsync(releaseJson, ct);
    }

    /// <summary>
    /// GET a GitHub release resource, hardened against the shared-network-address rate limit that
    /// dead-ended first-run installs (issue #266):
    ///   - when <paramref name="useCache"/> and a cached entity tag exists, sends a conditional
    ///     request (If-None-Match); a 304 Not Modified serves the cached body and does NOT consume
    ///     the rate-limit budget;
    ///   - on a 403/429 rate-limit response, retries with bounded backoff that honours GitHub's
    ///     reset hint (Retry-After or X-RateLimit-Reset) within a sane cap, then raises a classified
    ///     <see cref="GitHubRateLimitException"/> rather than a raw
    ///     "status code does not indicate success: 403".
    /// Returns the raw response body; the caller parses it (a single release object or a list).
    /// </summary>
    private async Task<string> GetReleaseBodyAsync(string url, bool useCache, CancellationToken ct)
    {
        var cached = useCache ? _cache.Read() : null;
        DateTimeOffset? lastResetHint = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached is { } c)
                request.Headers.TryAddWithoutValidation("If-None-Match", c.ETag);

            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // 304: the release has not changed since we cached it - serve the cached body.
            // GitHub does not charge a 304 against the rate-limit budget.
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is not { } hit)
                    throw new InvalidOperationException(
                        "GitHub returned 304 Not Modified but no cached release info is available.");
                EngineLog.Write("[ReleaseSource] Fetch: 304 Not Modified, serving cached release info");
                return hit.Body;
            }

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (useCache)
                {
                    var etag = resp.Headers.ETag?.ToString();
                    if (!string.IsNullOrEmpty(etag))
                        _cache.Write(etag, body);
                }
                return body;
            }

            if (IsRateLimited(resp))
            {
                lastResetHint = ReadResetHint(resp);
                if (attempt >= MaxAttempts)
                    break;

                var wait = ComputeBackoff(attempt, lastResetHint);
                EngineLog.Write(
                    $"[ReleaseSource] Fetch: 403 rate-limit, retry {attempt}/{MaxAttempts - 1} after {wait.TotalSeconds:0}s");
                await _delay(wait, ct);
                continue;
            }

            // Any other non-success is a real, non-rate-limit failure - surface it as before.
            resp.EnsureSuccessStatusCode();
        }

        var hint = lastResetHint is { } r
            ? $" GitHub says the limit resets at {r.UtcDateTime:u}."
            : "";
        EngineLog.Write(
            $"[ReleaseSource] Fetch: rate-limit retries exhausted after {MaxAttempts} attempts.{hint}");
        throw new GitHubRateLimitException(
            "GitHub rate limit exceeded while fetching release info." + hint, MaxAttempts, lastResetHint);
    }

    /// <summary>
    /// From a GitHub <c>/releases</c> list body, pick the release a pre-release setup exe should
    /// install: the release whose tag matches the setup exe's own version exactly, else the newest
    /// pre-release in the list (GitHub returns releases newest-first). Returns that release's JSON,
    /// ready for <see cref="BuildResolvedReleaseAsync"/>. Throws when the list carries no pre-release.
    /// </summary>
    private static string SelectPreRelease(string listJson, string setupVersion)
    {
        using var doc = JsonDocument.Parse(listJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub /releases did not return a JSON array.");

        var wanted = VersionUtil.CanonicalTag(setupVersion);
        JsonElement? newestPreRelease = null;

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

            // An exact version match is exactly what this setup exe is, so it wins outright -
            // the rc4 installer installs rc4 even when a newer rc5 exists.
            if (!string.IsNullOrEmpty(tag) && VersionUtil.CanonicalTag(tag) == wanted)
            {
                EngineLog.Write($"[ReleaseSource] SelectPreRelease: matched release tagged '{tag}'.");
                return rel.GetRawText();
            }

            var isPreRelease = rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (isPreRelease)
                newestPreRelease ??= rel.Clone(); // first pre-release seen == newest (list is newest-first)
        }

        if (newestPreRelease is { } fallback)
        {
            var tag = fallback.TryGetProperty("tag_name", out var t) ? t.GetString() : "(untagged)";
            EngineLog.Write($"[ReleaseSource] SelectPreRelease: no release tagged '{setupVersion}'; using newest pre-release '{tag}'.");
            return fallback.GetRawText();
        }

        throw new InvalidOperationException(
            $"Setup executable is a pre-release ({setupVersion}) but the GitHub release list contains no pre-release to install.");
    }

    /// <summary>Parse a release JSON body into the resolved release (asset URLs + manifest).</summary>
    private async Task<ResolvedRelease> BuildResolvedReleaseAsync(string releaseJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        string? manifestUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                urls[name] = dl;
                if (name == ManifestAssetName) manifestUrl = dl;
            }
        }

        if (manifestUrl is null)
            throw new InvalidOperationException($"Latest release has no {ManifestAssetName} asset.");

        var manifestJson = await _http.GetStringAsync(manifestUrl, ct);
        var manifest = ReleaseManifest.Parse(manifestJson);
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// True when the response is GitHub's rate-limit signal: a 403 or 429 carrying an
    /// exhausted X-RateLimit-Remaining (primary limit) or a Retry-After (secondary limit).
    /// A 403 that is NOT a rate limit (e.g. a private repo without auth) is left to the
    /// normal EnsureSuccessStatusCode path.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage resp)
    {
        if (resp.StatusCode != HttpStatusCode.Forbidden &&
            resp.StatusCode != HttpStatusCode.TooManyRequests)
            return false;

        if (resp.Headers.Contains("Retry-After"))
            return true;

        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            int.TryParse(remaining.FirstOrDefault(), out var left) && left <= 0)
            return true;

        return false;
    }

    /// <summary>
    /// The reset moment GitHub advertised, from Retry-After (delta seconds or HTTP date)
    /// or the X-RateLimit-Reset epoch-seconds header. Null when neither is present.
    /// </summary>
    private static DateTimeOffset? ReadResetHint(HttpResponseMessage resp)
    {
        var retryAfter = resp.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } delta)
                return DateTimeOffset.UtcNow + delta;
            if (retryAfter.Date is { } date)
                return date;
        }

        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var epochSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);

        return null;
    }

    /// <summary>
    /// How long to wait before the next attempt: the time until GitHub's reset hint when
    /// one was given, otherwise exponential backoff from <see cref="BaseBackoff"/>. Always
    /// clamped to [0, <see cref="MaxBackoff"/>] so a stale or far-future hint never stalls.
    /// </summary>
    private static TimeSpan ComputeBackoff(int attempt, DateTimeOffset? resetHint)
    {
        if (resetHint is { } reset)
        {
            var untilReset = reset - DateTimeOffset.UtcNow;
            if (untilReset < TimeSpan.Zero) untilReset = TimeSpan.Zero;
            return untilReset > MaxBackoff ? MaxBackoff : untilReset;
        }

        var exponential = TimeSpan.FromTicks(BaseBackoff.Ticks * (1L << (attempt - 1)));
        return exponential > MaxBackoff ? MaxBackoff : exponential;
    }

    /// <summary>
    /// Stage an asset to a temp file and return its path. The resolved value is
    /// either an http(s) URL (latest/online mode) or a local file path
    /// (release-dir mode); each is handled explicitly. Throws when no source is
    /// known for the asset.
    /// Byte-level progress (downloaded, total) is reported roughly once per MiB;
    /// total is 0 when the server sends no Content-Length. A final report is
    /// always made on completion.
    /// </summary>
    public async Task<string> DownloadAssetAsync(string assetName, IReadOnlyDictionary<string, string> urls, CancellationToken ct,
        IProgress<(long downloaded, long total)>? progress = null)
    {
        if (!urls.TryGetValue(assetName, out var source))
            throw new InvalidOperationException($"No source for asset '{assetName}'. Use latest or a release dir.");

        var dest = Path.Combine(Path.GetTempPath(), $"devthrottle-setup-{Guid.NewGuid():N}-{assetName}");

        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(dest);

            if (progress is null)
            {
                await src.CopyToAsync(fs, ct);
                return dest;
            }

            var buffer = new byte[81920];
            long downloaded = 0, lastReported = 0;
            const long reportEvery = 1024 * 1024; // ~1 MiB between reports keeps UI marshaling cheap
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (downloaded - lastReported >= reportEvery)
                {
                    lastReported = downloaded;
                    progress.Report((downloaded, total));
                }
            }
            progress.Report((downloaded, total > 0 ? total : downloaded));
            return dest;
        }

        // Local release-dir mode: the source is a file path; stage a copy.
        if (!File.Exists(source))
            throw new FileNotFoundException($"Local asset not found: {source}", source);
        File.Copy(source, dest, overwrite: true);
        var size = new FileInfo(dest).Length;
        progress?.Report((size, size));
        return dest;
    }
}
