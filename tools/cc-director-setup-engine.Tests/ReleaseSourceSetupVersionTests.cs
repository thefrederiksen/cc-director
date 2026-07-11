using System.Net;
using System.Net.Http;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #1294: a pre-release setup executable must install its matching pre-release, resolved in
/// memory from the setup exe's own stamped version - never the latest stable (which /releases/latest
/// would hand it). A stable setup exe still installs the latest stable, unchanged.
/// </summary>
public class ReleaseSourceSetupVersionTests
{
    private static readonly string Slug = GitHubRepositoryDefaults.Slug;
    private static string LatestUrl => $"https://api.github.com/repos/{Slug}/releases/latest";
    private static string ListUrl => $"https://api.github.com/repos/{Slug}/releases";

    private const string Rc4ManifestUrl = "https://release.test/rc4/release-manifest.json";
    private const string Rc3ManifestUrl = "https://release.test/rc3/release-manifest.json";
    private const string StableManifestUrl = "https://release.test/stable/release-manifest.json";

    /// <summary>A minimal valid release-manifest.json whose only asset is the manifest itself.</summary>
    private static string ManifestJson(string version) =>
        $$"""
        { "version": "{{version}}", "assets": {
            "release-manifest.json": { "version": "{{version}}", "sha256": "", "platform": "any", "size": 0 }
        } }
        """;

    /// <summary>One GitHub release object carrying a single release-manifest.json asset.</summary>
    private static string ReleaseObj(string tag, bool prerelease, string manifestUrl) =>
        $$"""
        { "tag_name": "{{tag}}", "prerelease": {{(prerelease ? "true" : "false")}}, "assets": [
            { "name": "release-manifest.json", "browser_download_url": "{{manifestUrl}}" }
        ] }
        """;

    /// <summary>The /releases list, newest-first: rc4, rc3, then the 1.0.7 stable.</summary>
    private static string ReleaseListJson() =>
        "[" +
        ReleaseObj("v1.1.0-rc4", prerelease: true, Rc4ManifestUrl) + "," +
        ReleaseObj("v1.1.0-rc3", prerelease: true, Rc3ManifestUrl) + "," +
        ReleaseObj("v1.0.7", prerelease: false, StableManifestUrl) +
        "]";

    /// <summary>Serves a fixed body per exact URL and records every requested URL.</summary>
    private sealed class UrlRouter(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no stub for {url}") });
        }
    }

    private static ReleaseInfoCache TempCache() =>
        new(Path.Combine(Path.GetTempPath(), "cc-relcache-" + Guid.NewGuid().ToString("N") + ".json"));

    private static ReleaseSource NewSource(UrlRouter router) =>
        new(new HttpClient(router), TempCache(), (_, _) => Task.CompletedTask);

    [Fact]
    public async Task FetchReleaseForSetup_StableSetupVersion_InstallsLatestStableViaLatestEndpoint()
    {
        // A stable setup exe resolves the latest stable release - identical to today's behavior.
        var router = new UrlRouter(new()
        {
            [LatestUrl] = ReleaseObj("v1.0.7", prerelease: false, StableManifestUrl),
            [StableManifestUrl] = ManifestJson("1.0.7"),
        });
        var source = NewSource(router);

        var release = await source.FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.0.7");

        Assert.Equal("1.0.7", release.Manifest.Version);
        Assert.Contains(LatestUrl, router.Requests);
        Assert.DoesNotContain(ListUrl, router.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseSetupVersion_InstallsMatchingPreReleaseFromList()
    {
        // The rc4 setup exe must resolve rc4 from the FULL list, not the stable /releases/latest.
        var router = new UrlRouter(new()
        {
            [ListUrl] = ReleaseListJson(),
            [Rc4ManifestUrl] = ManifestJson("1.1.0-rc4"),
            [Rc3ManifestUrl] = ManifestJson("1.1.0-rc3"),
            [StableManifestUrl] = ManifestJson("1.0.7"),
            // Present but must NOT be consulted on the pre-release path.
            [LatestUrl] = ReleaseObj("v1.0.7", prerelease: false, StableManifestUrl),
        });
        var source = NewSource(router);

        var release = await source.FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc4");

        Assert.Equal("1.1.0-rc4", release.Manifest.Version);
        Assert.Contains(ListUrl, router.Requests);
        Assert.Contains(Rc4ManifestUrl, router.Requests);
        Assert.DoesNotContain(LatestUrl, router.Requests);
        Assert.DoesNotContain(StableManifestUrl, router.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseWithLeadingVAndBuildMetadata_StillMatches()
    {
        // The stamped version carries "+commit"; the tag carries a leading "v". Both must normalize.
        var router = new UrlRouter(new()
        {
            [ListUrl] = ReleaseListJson(),
            [Rc4ManifestUrl] = ManifestJson("1.1.0-rc4"),
            [Rc3ManifestUrl] = ManifestJson("1.1.0-rc3"),
            [StableManifestUrl] = ManifestJson("1.0.7"),
        });
        var source = NewSource(router);

        var release = await source.FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc3+deadbeef");

        Assert.Equal("1.1.0-rc3", release.Manifest.Version);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseNotInList_FallsBackToNewestPreRelease()
    {
        // rc9 was never published; the newest pre-release (rc4, first in the newest-first list) is used.
        var router = new UrlRouter(new()
        {
            [ListUrl] = ReleaseListJson(),
            [Rc4ManifestUrl] = ManifestJson("1.1.0-rc4"),
            [Rc3ManifestUrl] = ManifestJson("1.1.0-rc3"),
            [StableManifestUrl] = ManifestJson("1.0.7"),
        });
        var source = NewSource(router);

        var release = await source.FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc9");

        Assert.Equal("1.1.0-rc4", release.Manifest.Version);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseButListHasNoPreRelease_Throws()
    {
        // The list carries only stable releases; a pre-release installer has nothing to install and
        // must fail loudly rather than silently downgrade to stable.
        var listWithNoPreRelease = "[" + ReleaseObj("v1.0.7", prerelease: false, StableManifestUrl) + "]";
        var router = new UrlRouter(new()
        {
            [ListUrl] = listWithNoPreRelease,
            [StableManifestUrl] = ManifestJson("1.0.7"),
        });
        var source = NewSource(router);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc4"));
    }
}
