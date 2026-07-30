using System.Net;
using System.Text;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The incomplete-release window (issue #1079): a release becomes "latest" when it is published, and
/// its assets used to be attached minutes later. Measured on v1.8.8 - published 10:48:48Z, assets
/// attached 10:54:11Z - and a launcher that checked at 10:54:05Z logged "update check failed" for a
/// release that was perfectly fine.
///
/// These tests pin the two halves of the fix on the client side: the condition is CLASSIFIED (so a
/// caller can tell "wait a few minutes" from "something is wrong"), and the wait is bounded (so a
/// release that never completes does not get polled forever).
/// </summary>
public class ReleaseNotReadyTests
{
    /// <summary>Serves one canned JSON body for the release fetch; any second request is a failure.</summary>
    private sealed class ReleaseJsonHandler(string json) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string ReleaseJson(string tag, params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $"{{\"name\":\"{n}\",\"browser_download_url\":\"https://release.test/{n}\"}}"));
        return $"{{\"tag_name\":\"{tag}\",\"prerelease\":false,\"assets\":[{assets}]}}";
    }

    // ---- classification --------------------------------------------------

    /// <summary>
    /// The exact shape observed during the window: the release is published, some assets are already
    /// attached, the manifest is not. It must come back as NOT READY - naming the release - rather
    /// than as an unclassified failure that every caller then logs as "update check failed".
    /// </summary>
    [Fact]
    public async Task FetchLatest_PublishedReleaseWithoutManifest_RaisesNotReadyNamingTheRelease()
    {
        var handler = new ReleaseJsonHandler(ReleaseJson("v1.8.8", "cc-director-win-x64.exe", "cc-launcher-win-x64.exe"));
        var source = new ReleaseSource(new HttpClient(handler), new ReleaseInfoCache(NoCacheFile()));

        var ex = await Assert.ThrowsAsync<ReleaseNotReadyException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Equal("v1.8.8", ex.Tag);
        Assert.Equal("release-manifest.json", ex.MissingAsset);
        // The message has to say the release is being completed, not that something went wrong.
        Assert.Contains("still being attached", ex.Message, StringComparison.Ordinal);
        // And the user-facing wording must not read as an error.
        Assert.Contains("Nothing is wrong", ex.UserMessage(), StringComparison.Ordinal);
        Assert.Contains("1.8.8", ex.UserMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A release with NO assets at all is the same condition seen a few seconds earlier in the window.
    /// It must classify identically - the caller's decision is the same either way.
    /// </summary>
    [Fact]
    public async Task FetchLatest_PublishedReleaseWithNoAssetsAtAll_RaisesNotReady()
    {
        var handler = new ReleaseJsonHandler(ReleaseJson("v1.9.1"));
        var source = new ReleaseSource(new HttpClient(handler), new ReleaseInfoCache(NoCacheFile()));

        var ex = await Assert.ThrowsAsync<ReleaseNotReadyException>(() => source.FetchLatestAsync(CancellationToken.None));
        Assert.Equal("v1.9.1", ex.Tag);
    }

    /// <summary>
    /// The guard has two failure directions, and this is the one that would make it useless: a
    /// COMPLETE release must not be classified as not-ready. Without this the type would be a
    /// permanent "wait a few minutes" that never installs anything.
    /// </summary>
    [Fact]
    public async Task FetchLatest_CompleteRelease_DoesNotRaiseNotReady()
    {
        // The manifest fetch is served by the same handler, so it must parse as a manifest.
        const string manifest = """
            {"version":"1.9.1","tag":"v1.9.1","assets":{"cc-director-win-x64.exe":{"version":"1.9.1","size":10,"sha256":"ab","platform":"windows"}}}
            """;
        var handler = new ManifestThenReleaseHandler(
            ReleaseJson("v1.9.1", "cc-director-win-x64.exe", "release-manifest.json"), manifest);
        var source = new ReleaseSource(new HttpClient(handler), new ReleaseInfoCache(NoCacheFile()));

        var resolved = await source.FetchLatestAsync(CancellationToken.None);

        Assert.Equal("1.9.1", resolved.Manifest.Version);
        Assert.True(resolved.DownloadUrls.ContainsKey("cc-director-win-x64.exe"));
    }

    /// <summary>Serves the release body for the API URL and the manifest body for the asset URL.</summary>
    private sealed class ManifestThenReleaseHandler(string releaseJson, string manifestJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.ToString().EndsWith("release-manifest.json", StringComparison.Ordinal)
                ? manifestJson
                : releaseJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // ---- retry cadence ---------------------------------------------------

    /// <summary>
    /// The point of the policy: minutes, not the hourly cycle. With hourly checks and a
    /// five-and-a-half minute window, a machine that checked inside it lost up to an hour.
    /// </summary>
    [Fact]
    public void Retry_FirstNotReady_WaitsMinutesNotTheNormalCycle()
    {
        var policy = new ReleaseNotReadyRetry();

        var delay = policy.NextDelay();

        Assert.Equal(ReleaseNotReadyRetry.Interval, delay);
        Assert.True(delay < TimeSpan.FromMinutes(10),
            "the short retry must be on the scale of the window (minutes), not of the normal check cycle");
    }

    /// <summary>
    /// Bounded on purpose: the allowance has to cover the measured 5m23s window with room to spare,
    /// and then stop. A release still missing its manifest after a quarter of an hour is not a
    /// publish window, it is a broken release, and polling it forever fixes nothing.
    /// </summary>
    [Fact]
    public void Retry_CoversTheMeasuredWindowWithMargin_ThenGivesUp()
    {
        var policy = new ReleaseNotReadyRetry();
        var covered = TimeSpan.Zero;

        for (var i = 1; i <= ReleaseNotReadyRetry.MaxConsecutive; i++)
        {
            var delay = policy.NextDelay();
            Assert.NotNull(delay);
            covered += delay!.Value;
            Assert.Equal(i, policy.Consecutive);
        }

        Assert.True(covered >= TimeSpan.FromMinutes(11),
            $"the short-retry allowance covers only {covered.TotalMinutes:0} minutes; the window measured on "
            + "v1.8.8 was 5m23s and this needs real margin over it");

        // Allowance used up: the caller falls back to its normal interval.
        Assert.Null(policy.NextDelay());
        Assert.Null(policy.NextDelay());
    }

    /// <summary>
    /// Per EPISODE, not a budget spent once for the life of the process. A launcher that runs for
    /// weeks sees a publish window at every release; an exhausted counter would leave every release
    /// after the first waiting out the full cycle.
    /// </summary>
    [Fact]
    public void Retry_ResetOnAnyOtherOutcome_RestoresTheFullAllowance()
    {
        var policy = new ReleaseNotReadyRetry();
        for (var i = 0; i < ReleaseNotReadyRetry.MaxConsecutive + 2; i++) policy.NextDelay();
        Assert.Null(policy.NextDelay());

        policy.Reset();

        Assert.Equal(0, policy.Consecutive);
        Assert.Equal(ReleaseNotReadyRetry.Interval, policy.NextDelay());
    }

    /// <summary>
    /// "And SAY so." The log line is the only place a machine-tier update loop is visible, so it has
    /// to name the release, say this is not a failure, and say when it will look again. The line it
    /// replaced was "update check failed", which is none of those.
    /// </summary>
    [Fact]
    public void Retry_Describe_SaysWhatHappenedAndWhatHappensNext()
    {
        var policy = new ReleaseNotReadyRetry();
        var ex = new ReleaseNotReadyException("v1.8.8", "release-manifest.json");

        var waiting = policy.Describe(ex, policy.NextDelay());
        Assert.Contains("v1.8.8", waiting, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", waiting, StringComparison.Ordinal);
        Assert.Contains("NOT a failure", waiting, StringComparison.Ordinal);
        Assert.Contains("3 minutes", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", waiting, StringComparison.OrdinalIgnoreCase);

        // Giving up says something DIFFERENT, so the log distinguishes a window from a broken release.
        var gaveUp = policy.Describe(ex, null);
        Assert.Contains("STILL has no", gaveUp, StringComparison.Ordinal);
        Assert.Contains("incomplete", gaveUp, StringComparison.Ordinal);
    }

    /// <summary>A cache file path in a throwaway directory, so no test reads the machine's real cache.</summary>
    private static string NoCacheFile() =>
        Path.Combine(Path.GetTempPath(), "cc-notready-" + Guid.NewGuid().ToString("N"), "release-info.json");
}
