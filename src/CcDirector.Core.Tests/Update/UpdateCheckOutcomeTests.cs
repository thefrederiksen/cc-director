using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// What a check CONCLUDES, and that it writes the conclusion down (issues #1030 and #1079).
///
/// Two defects meet here. A release is "latest" the instant its tag is pushed and its downloads are
/// attached about five and a half minutes later, so any machine checking inside that window finds a
/// newer release it cannot fetch - and the code reported that as UP TO DATE. And no conclusion of any
/// kind survived the check that produced it, so a Director could not say what its last check had found
/// even seconds afterwards. Together those are two of the five situations that all looked like an
/// unchanged version number.
///
/// These run on Windows only because the check needs a release asset for the running platform, and the
/// build only publishes Windows and macOS assets; on anything else the service correctly declines to
/// look at all.
/// </summary>
public class UpdateCheckOutcomeTests
{
    /// <summary>
    /// Whether this machine is one the release publishes an asset for. The .NET tests run on Windows in
    /// continuous integration, so these do run there; on anything else the service correctly declines to
    /// look for an update at all and there is nothing to assert.
    /// </summary>
    private static bool Supported => OperatingSystem.IsWindows()
        && UpdateService.AssetNameFor(OSPlatform.Windows, RuntimeInformation.OSArchitecture) is not null;

    [Fact]
    public async Task ALatestReleaseWithNoAssetsYet_IsReleaseNotReady_NotUpToDate()
    {
        if (!Supported) return;

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false);

        Assert.Equal(UpdatePhase.ReleaseNotReady, outcome);
        Assert.Equal("ReleaseNotReady", state.LastCheckOutcome);
        Assert.Equal("9.9.9", state.LastCheckLatestVersion);
        // Nothing was downloaded, so nothing may be recorded as staged.
        Assert.Null(state.StagedVersion);
    }

    [Fact]
    public async Task AReleaseThatIsNotNewer_IsUpToDate_AndSaysWhenItLooked()
    {
        if (!Supported) return;

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v1.0.0", withAssets: false);

        Assert.Equal(UpdatePhase.UpToDate, outcome);
        Assert.Equal("UpToDate", state.LastCheckOutcome);
        Assert.NotNull(state.LastCheckedAt);
    }

    [Fact]
    public async Task AFailedCheck_IsWrittenDown_SoTheDisplayStopsClaimingUpToDate()
    {
        if (!Supported) return;

        // The machine was up to date an hour ago and the network has since gone. The old code left the
        // successful conclusion in place, so the display kept asserting "up to date" on the strength of
        // a check that had not worked since.
        var previous = new UpdaterState
        {
            LastCheckedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastCheckOutcome = "UpToDate",
        };

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false,
            seed: previous, handler: new ThrowingHandler());

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Equal("Failed", state.LastCheckOutcome);
        Assert.False(string.IsNullOrWhiteSpace(state.LastCheckError));
    }

    [Fact]
    public async Task TheConclusionIsWhatTheFoldThenShows()
    {
        if (!Supported) return;

        // The end-to-end claim of #1030: what the check concluded is what the screen says. Reading the
        // persisted record is the ONLY way anything other than the checking code can know.
        var (_, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false);

        var view = UpdateStatusFold.Fold(new UpdateStatusFacts(
            CurrentVersion: "1.0.0",
            AutomaticUpdatesEnabled: true,
            State: state,
            Live: null,
            RunningSessionCount: 0,
            LauncherPort: null,
            Now: DateTimeOffset.UtcNow));

        Assert.Equal("ReleaseNotReady", view.State);
        Assert.Contains("9.9.9", view.Detail);
    }

    // ---- Harness ----------------------------------------------------------

    /// <summary>
    /// Run one check against a fabricated "latest release", with the storage root redirected so the
    /// state file written is this test's own. Returns the conclusion and the state as it was left.
    /// </summary>
    private static async Task<(UpdatePhase Outcome, UpdaterState State)> CheckAgainstReleaseAsync(
        string tag, bool withAssets, UpdaterState? seed = null, HttpMessageHandler? handler = null, bool withManifest = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-update-outcome-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            seed?.Save();

            var service = new UpdateService(
                new UpdateOptions
                {
                    Enabled = true,
                    CurrentVersion = new Version(1, 0, 0),
                    InstallTarget = Path.Combine(root, "cc-director.exe"),
                },
                handler ?? new ReleaseHandler(tag, withAssets, withManifest));

            var outcome = await service.CheckAndStageAsync();
            return (outcome, UpdaterState.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Answers the releases/latest call with a release that has, or has not, had its files attached.</summary>
    private sealed class ReleaseHandler(string tag, bool withAssets, bool withManifest = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var entries = new List<string>();
            if (withAssets)
                entries.Add("""{"name":"cc-director-win-x64.exe","browser_download_url":"https://example.invalid/a"}""");
            if (withAssets || withManifest)
                entries.Add("""{"name":"release-manifest.json","browser_download_url":"https://example.invalid/m"}""");
            var assets = "[" + string.Join(",", entries) + "]";
            var body = $$"""{"tag_name":"{{tag}}","assets":{{assets}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("the network is unreachable");
    }

    [Fact]
    public async Task ACompleteReleaseWithNoAssetForThisPlatform_IsItsOwnFault_NotThePublishWindow()
    {
        if (!Supported) return;

        // These two shared one line and reported UpToDate together. A release that HAS its manifest is
        // finished publishing, so a missing platform asset is a real fault - waiting does not fix it and
        // it must not drive the short retry.
        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false, withManifest: true);

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Equal("NoBuildForThisPlatform", state.LastCheckOutcome);
        Assert.Contains("cc-director-win-x64.exe", state.LastCheckError);
        Assert.Null(state.StagedVersion);

        // The retry policy must treat it as ordinary, or a finished release gets polled for ever.
        Assert.Equal(TimeSpan.FromHours(1),
            new ReleaseNotReadyRetry().NextDelay(outcome, TimeSpan.FromHours(1)));
    }
}
