using System.Diagnostics;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof of the macOS/Linux session-pointer hook chain: the ACTUAL
/// report-session.sh that <see cref="ClaudeHookInstaller"/> writes is executed with
/// /bin/sh and curl against a REAL ControlApiHost, fed Claude's raw hook event JSON on
/// stdin - exactly what Claude Code does at SessionStart (startup/resume/clear/compact).
/// Asserts the Director's transcript pointer updates and the fleet preamble comes back
/// as ready-made hookSpecificOutput JSON on stdout. This chain is what keeps session
/// history (and everything built on it, like Gateway voice mode) alive on macOS.
/// </summary>
public sealed class ClaudeHookShellScriptIntegrationTests : IAsyncLifetime
{
    private SessionManager _sm = null!;
    private ControlApiHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions { OpenAiKey = null });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        _port = await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();

        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task ShellHook_RawClaudeEventOnStdin_UpdatesPointerAndPrintsPreamble()
    {
        if (OperatingSystem.IsWindows())
            return; // /bin/sh + curl are the Unix hook runtime; Windows uses the PowerShell hook.

        // Arrange: a session the host can route to, and the real hook files on disk.
        var session = MakeSession();
        _sm.AdoptSession(session);

        var hookDir = Path.Combine(Path.GetTempPath(), "cc-hook-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            ClaudeHookInstaller.EnsureInstalled(hookDir, forWindows: false);
            var scriptPath = Path.Combine(hookDir, "report-session.sh");
            Assert.True(File.Exists(scriptPath));

            var newClaudeId = Guid.NewGuid().ToString();
            var newTranscript = $"/tmp/{newClaudeId}.jsonl";
            var rawEvent = $$"""
                {"session_id":"{{newClaudeId}}","transcript_path":"{{newTranscript}}","hook_event_name":"SessionStart","source":"clear","cwd":"/tmp"}
                """;

            // Act: run the script the way Claude Code runs a hook command.
            var psi = new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["CC_DIRECTOR_API"] = $"http://127.0.0.1:{_port}";
            psi.Environment["CC_SESSION_ID"] = session.Id.ToString();

            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(rawEvent);
            proc.StandardInput.Close();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            // Assert: the hook must never fail the session.
            Assert.Equal(0, proc.ExitCode);

            // The Director's pointer now tracks the rotated session id + transcript path.
            Assert.Equal(newClaudeId, session.ClaudeSessionId);
            Assert.Equal(newTranscript, session.ClaudeTranscriptPath);

            // And the fleet preamble came back as ready-made SessionStart hook output.
            Assert.Contains("hookSpecificOutput", stdout);
            Assert.Contains("additionalContext", stdout);
            Assert.True(string.IsNullOrWhiteSpace(stderr), $"hook stderr should be empty but was: {stderr}");
        }
        finally
        {
            try { Directory.Delete(hookDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ShellHook_DirectorUnreachable_StillExitsZeroAndPrintsNothing()
    {
        if (OperatingSystem.IsWindows())
            return;

        var hookDir = Path.Combine(Path.GetTempPath(), "cc-hook-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            ClaudeHookInstaller.EnsureInstalled(hookDir, forWindows: false);
            var scriptPath = Path.Combine(hookDir, "report-session.sh");

            var psi = new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // A port nothing listens on: both curl calls fail; the hook must stay harmless.
            psi.Environment["CC_DIRECTOR_API"] = "http://127.0.0.1:9";
            psi.Environment["CC_SESSION_ID"] = Guid.NewGuid().ToString();

            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync("""{"session_id":"x"}""");
            proc.StandardInput.Close();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            Assert.Equal(0, proc.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stdout), $"unreachable Director must produce no hook output, got: {stdout}");
        }
        finally
        {
            try { Directory.Delete(hookDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static Session MakeSession()
    {
        var repo = Path.GetTempPath();
        var session = new Session(
            Guid.NewGuid(),
            repoPath: repo,
            workingDirectory: repo,
            claudeArgs: null,
            backend: new IdleStubBackend(),
            claudeSessionId: "launch-time-id",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "hook-e2e-test",
            customColor: null);
        session.MarkRunning();
        return session;
    }

    private sealed class IdleStubBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
