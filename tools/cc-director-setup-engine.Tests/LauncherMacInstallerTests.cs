using System.Net;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the macOS launcher install step. Every external effect is injected (the command
/// runner, the process starter, the health HTTP handler, the launch agent property list path),
/// so these tests exercise the real decision flow with no launchd, no network, and no binary.
/// The class under test is macOS-only, so each test exits early on other platforms.
/// </summary>
public class LauncherMacInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _plistPath;

    public LauncherMacInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launcher-mac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        _plistPath = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>An HTTP handler whose every response has the given status code and body.</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    /// <summary>A launcher /healthz answer: identity travels in the body (issue #2042).</summary>
    private static string HealthBody(string version, int pid = 4242) =>
        $$"""{"ok":true,"version":"{{version}}","pid":{{pid}},"uptimeS":1}""";

    private HttpClient HealthyClient(string version = "1.7.4") =>
        new(new FixedStatusHandler(HttpStatusCode.OK, HealthBody(version)));
    private HttpClient DeadClient() => new(new FixedStatusHandler(HttpStatusCode.ServiceUnavailable));

    /// <summary>Record the launcher version the runner would have written when placing the binary -
    /// the identity the health check must then see answering the port.</summary>
    private void RecordPlacedLauncherVersion(string version)
    {
        var manifest = InstalledManifest.Load(_layout);
        manifest.Set(ComponentRegistry.Launcher.Id, version);
        manifest.Save(_layout);
    }

    private void PlaceLauncherBinary()
    {
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "fake launcher binary");
    }

    private void WritePlist() => File.WriteAllText(_plistPath, "<plist/>");

    [Fact]
    public async Task InstallAsync_BinaryMissing_Fails()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("not present", result.Message);
    }

    // RETIRED, deliberately: two tests here pinned the behaviour that broke a Mac.
    //
    // InstallAsync_FirstInstall_StartsDirectlyAndVerifiesPlist asserted that a first install starts the
    // launcher DIRECTLY and relies on it registering its own launch agent afterwards. That is precisely
    // how a launcher launchd does not own comes into existence, and nothing could then stop it: the
    // uninstall only asked launchd, which had never heard of that process. The first install now
    // registers the agent and lets launchd start it, so the test asserted the defect.
    //
    // The kickstart-failure test required a direct-start fallback for the same reason, and that fallback
    // is gone: a registered agent that will not start is now a reported failure, not an unmanaged
    // process started behind the user's back.
    //
    // They also could not have caught the change: the production first-install path calls the STATIC
    // LauncherLaunchdAutostart.EnsureRegistered, not the injected _runCommand or _startProcess seams, so
    // on a Mac they would have written the developer's own real launch agent pointing at a temporary
    // file. On Windows every one of them returned early rather than being skipped, so they counted as
    // green while covering nothing.
    //
    // What replaces them: LauncherStopperTests covers stopping by process and scope, and
    // LaunchdPidParseTests covers reading the process id back from launchd. Proving that a first install
    // on a real Mac yields a launchd-managed launcher needs a real Mac, and is recorded as such in
    // docs/MISSION-installer-both-platforms-2026-07-29.md.
}
