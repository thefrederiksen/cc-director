using System.Runtime.InteropServices;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression test for the re-inspection's P1 finding: the host created and started the terminal
/// recorder only when capture was enabled, and the purge-on-removal handler lived exclusively on
/// that recorder. With the new default OFF, no recorder existed, so recordings made by the old
/// default-on release were orphaned forever the moment their session was removed - the committed
/// policy test proved the recorder's own purge, but manually constructed and started one, which the
/// production wiring did not.
///
/// This test goes through the PRODUCTION wiring: a real <see cref="ControlApiHost"/> is started in
/// a fresh storage root where capture is off (the clean-install default, asserted), a pre-existing
/// recording is arranged the way an upgrade leaves one, and the session is removed through the
/// <see cref="SessionManager"/>. The recording directory must be gone.
///
/// Isolation: CC_DIRECTOR_ROOT points at a fresh temp root, and the machine's own
/// CC_DIRECTOR_RECORD_SESSIONS override is cleared for the duration - a test about the DEFAULT must
/// not read the machine's opinion and call it the default.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiRecorderPurgeTests : IAsyncLifetime
{
    private static string TestShellPath =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevRecordEnv;
    private readonly string _instancesDir;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;

    public ControlApiRecorderPurgeTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-recorder-purge-root-" + unique);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _prevRecordEnv = Environment.GetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, null);
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-recorder-purge-instances-" + unique);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions { ClaudePath = TestShellPath });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: _instancesDir);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, _prevRecordEnv);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Removing_a_session_purges_its_recording_even_when_capture_is_off_by_default()
    {
        // The posture under test: this is the clean-install default, not an arranged special case.
        Assert.False(SessionRecordingConfig.IsEnabled(),
            "arrangement failed: capture should be OFF by default in a fresh root");

        var session = _sm.CreateSession(Path.GetTempPath());

        // A recording left behind by an earlier default-on release, in the exact spelling the
        // recorder writes with ("N" - no dashes). Asserted present before the act, so a purge aimed
        // at a directory that never existed cannot pass this test while deleting nothing.
        var sessionDir = Path.Combine(CcStorage.SessionRecordings(), session.Id.ToString("N"));
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "grid.jsonl"), "{\"rows\":[\"a secret on a screen\"]}\n");
        Assert.True(Directory.Exists(sessionDir), "arrangement failed: the pre-existing recording was not created");

        _sm.RemoveSession(session.Id);

        Assert.False(Directory.Exists(sessionDir),
            "default-off production wiring left an existing recording behind");
    }
}
