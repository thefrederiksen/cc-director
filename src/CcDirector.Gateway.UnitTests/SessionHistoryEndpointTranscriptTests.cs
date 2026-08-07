using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// BuildHistory must FAIL LOUDLY when a Claude session's transcript cannot be located:
/// Status "transcript-not-found" plus a diagnosable Error, never a silent empty history
/// with Status "ok" (which is indistinguishable from a genuinely empty conversation and
/// starved the Cockpit history view and Gateway voice mode with no signal).
/// </summary>
public class SessionHistoryEndpointTranscriptTests
{
    [Fact]
    public void BuildHistory_NoPointerAndNoSessionId_ReportsTranscriptNotFound()
    {
        var session = MakeClaudeSession(claudeSessionId: null);

        var dto = SessionHistoryEndpoint.BuildHistory(session, session.Id.ToString());

        Assert.Equal("transcript-not-found", dto.Status);
        Assert.NotNull(dto.Error);
        Assert.Contains("hook", dto.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dto.Messages);
    }

    [Fact]
    public void BuildHistory_PointerToMissingFile_ReportsTranscriptNotFoundWithPath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "cc-history-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var session = MakeClaudeSession(claudeSessionId: "stale-id");
        session.UpdateClaudeSessionPointer("cccccccc-3333-4333-8333-cccccccccccc", missingPath, "clear");

        var dto = SessionHistoryEndpoint.BuildHistory(session, session.Id.ToString());

        Assert.Equal("transcript-not-found", dto.Status);
        Assert.NotNull(dto.Error);
        Assert.Contains(missingPath, dto.Error);
        Assert.Empty(dto.Messages);
    }

    [Fact]
    public void BuildHistory_PointerToRealTranscript_ReturnsOkWithMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-history-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path,
            """{"type":"user","message":{"role":"user","content":"hello there"}}""" + "\n" +
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi back"}]}}""" + "\n");
        try
        {
            var session = MakeClaudeSession(claudeSessionId: "some-id");
            session.UpdateClaudeSessionPointer("dddddddd-4444-4444-8444-dddddddddddd", path, "startup");

            var dto = SessionHistoryEndpoint.BuildHistory(session, session.Id.ToString());

            Assert.Equal("ok", dto.Status);
            Assert.Null(dto.Error);
            Assert.Equal(2, dto.Messages.Count);
            Assert.Equal("User", dto.Messages[0].Role);
            Assert.Equal("Assistant", dto.Messages[1].Role);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static Session MakeClaudeSession(string? claudeSessionId)
    {
        var repo = Path.GetTempPath();
        return new Session(
            Guid.NewGuid(),
            repoPath: repo,
            workingDirectory: repo,
            claudeArgs: null,
            backend: new IdleStubBackend(),
            claudeSessionId: claudeSessionId,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "history-endpoint-test",
            customColor: null);
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
