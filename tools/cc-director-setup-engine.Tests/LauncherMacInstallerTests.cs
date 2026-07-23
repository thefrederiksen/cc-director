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

    [Fact]
    public async Task InstallAsync_FirstInstall_StartsDirectlyAndVerifiesPlist()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlaceLauncherBinary();

        var startedWith = new List<(string Path, string Arguments)>();
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (_, _) => throw new InvalidOperationException("launchctl must not be used on a first install"),
            startProcess: (path, arguments, _) =>
            {
                startedWith.Add((path, arguments));
                // The launcher writes and bootstraps its own launch agent on startup; the fake
                // start simulates exactly that contract.
                WritePlist();
                return 4321;
            },
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.True(result.Success, result.Message);
        var started = Assert.Single(startedWith);
        Assert.Equal(_layout.PathFor(ComponentRegistry.Launcher), started.Path);
        Assert.Equal(LauncherTrayInstaller.InstalledArguments, started.Arguments);
    }

    [Fact]
    public async Task InstallAsync_Reinstall_RestartsUnderLaunchdWithoutDirectStart()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlaceLauncherBinary();
        WritePlist();

        var commands = new List<string>();
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (executable, arguments) =>
            {
                commands.Add($"{executable} {arguments}");
                return executable.EndsWith("/id") ? (0, "501\n") : (0, "");
            },
            startProcess: (_, _, _) => throw new InvalidOperationException("a loaded agent must be restarted by launchd, not started directly"),
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.Contains(commands, c => c.Contains("kickstart -k gui/501/" + LauncherLaunchdAutostart.Label));
    }

    [Fact]
    public async Task InstallAsync_KickstartFails_StartsDirectlySoTheLauncherReRegisters()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlaceLauncherBinary();
        WritePlist();

        var startedDirectly = false;
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (executable, _) => executable.EndsWith("/id") ? (0, "501\n") : (113, "Could not find service"),
            startProcess: (_, _, _) => { startedDirectly = true; return 4321; },
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(startedDirectly);
    }

    [Fact]
    public async Task InstallAsync_HealthNeverAnswers_Fails()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlaceLauncherBinary();

        var installer = new LauncherMacInstaller(_layout,
            DeadClient(),
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => { WritePlist(); return 4321; },
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("did not answer", result.Message);
    }

    [Fact]
    public async Task InstallAsync_HealthyButNoPlist_Fails()
    {
        if (!OperatingSystem.IsMacOS()) return;
        PlaceLauncherBinary();

        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 4321, // the fake launcher never registers its agent
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromSeconds(1));

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("launch agent", result.Message);
    }

    [Fact]
    public async Task InstallAsync_WrongVersionAnswersThePort_FailsLoudNamingBothVersions()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // The real incident (issue #2042): the machine's OLD launcher held port 7900 and answered
        // the health poll, so a completely failed install of the new binary still looked green.
        PlaceLauncherBinary();
        WritePlist();
        RecordPlacedLauncherVersion("1.7.4");
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(version: "1.7.1"),
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath,
            healthTimeout: TimeSpan.FromMilliseconds(1200));

        var result = await installer.InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("1.7.1", result.Message);
        Assert.Contains("1.7.4", result.Message);
        Assert.Contains("refusing to certify", result.Message);
    }

    [Fact]
    public async Task InstallAsync_MatchingVersionAnswers_SucceedsAndReportsIdentity()
    {
        if (!OperatingSystem.IsMacOS()) return;

        PlaceLauncherBinary();
        WritePlist();
        RecordPlacedLauncherVersion("1.7.4");
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(version: "1.7.4+abcdef"),   // build metadata must not break the match
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath);

        var result = await installer.InstallAsync();

        Assert.True(result.Success);
        Assert.Contains(result.Steps, s => s.Contains("OK (version 1.7.4+abcdef, process id 4242)"));
    }

    [Fact]
    public async Task InstallAsync_NoRecordedVersion_LegacyOkAnswerStillSucceeds()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // No installed.json entry (nothing was recorded): identity cannot be checked, so a
        // well-formed ok answer is accepted - the pre-#2042 behavior, minus blind 200-trust.
        PlaceLauncherBinary();
        WritePlist();
        var installer = new LauncherMacInstaller(_layout,
            HealthyClient(),
            runCommand: (_, _) => (0, ""),
            startProcess: (_, _, _) => 1234,
            launchAgentPlistPath: _plistPath);

        var result = await installer.InstallAsync();

        Assert.True(result.Success);
    }
}
