using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class SessionManagerTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe", // Use cmd.exe for testing - it's always available
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void CreateSession_InvalidPath_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateSession(@"C:\nonexistent\path\that\does\not\exist"));
    }

    [Fact]
    public void GetSession_UnknownId_ReturnsNull()
    {
        var result = _manager.GetSession(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void ListSessions_Empty_ReturnsEmpty()
    {
        var sessions = _manager.ListSessions();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task KillSession_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _manager.KillSessionAsync(Guid.NewGuid()));
    }

    [Fact]
    public void CreateSession_WithCmdExe_Succeeds()
    {
        var tempDir = Path.GetTempPath();
        var session = _manager.CreateSession(tempDir);

        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.Equal(tempDir, session.RepoPath);
        Assert.True(session.ProcessId > 0);

        var listed = _manager.ListSessions();
        Assert.Single(listed);

        var fetched = _manager.GetSession(session.Id);
        Assert.NotNull(fetched);
        Assert.Equal(session.Id, fetched.Id);
    }

    [Fact]
    public async Task CreateAndKillSession_StatusChanges()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Equal(SessionStatus.Running, session.Status);

        await _manager.KillSessionAsync(session.Id);

        // After kill, status should be Exiting or Exited
        Assert.True(session.Status is SessionStatus.Exiting or SessionStatus.Exited);
    }

    [Fact]
    public async Task CreateSession_CmdExe_ReceivesOutput()
    {
        // Use a separate manager to avoid contention with other tests
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        // Send a command to force output
        session.SendText("echo hello");

        // Poll for output with timeout
        byte[] dump = Array.Empty<byte>();
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            dump = session.Buffer.DumpAll();
            if (dump.Length > 0) break;
        }

        Assert.True(dump.Length > 0, "Expected cmd.exe to produce some output.");
    }

    [Fact]
    public void ScanForOrphans_DoesNotThrow()
    {
        // Just verify it doesn't crash
        _manager.ScanForOrphans();
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
