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
    public async Task DrainLoop_CmdExe_ReceivesBanner()
    {
        // Simplest test: does the drain loop put ANY bytes into the buffer?
        // cmd.exe should produce its "Microsoft Windows" banner without us sending anything.
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        long totalBytes = 0;
        for (int i = 0; i < 40; i++) // 8 seconds max
        {
            await Task.Delay(200);
            totalBytes = session.Buffer.TotalBytesWritten;
            if (totalBytes > 0) break;
        }

        var dump = session.Buffer.DumpAll();
        var output = System.Text.Encoding.UTF8.GetString(dump);

        Assert.True(totalBytes > 0,
            $"Expected cmd.exe to produce banner output but TotalBytesWritten={totalBytes}. " +
            $"Session status={session.Status}, PID={session.ProcessId}");
    }

    [Fact]
    public async Task SendText_CmdExe_CommandIsExecuted()
    {
        // Proves whether SendText actually submits a command (not just echoes text).
        // cmd.exe uses cooked mode, so if this works but Claude Code doesn't,
        // the issue is specific to raw-mode TUI apps.
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        using var manager = new SessionManager(options);
        var session = manager.CreateSession(Path.GetTempPath());

        // Wait for cmd.exe to start and show its prompt
        await Task.Delay(500);

        // Send a command with a unique marker
        session.SendText("echo SENDTEXT_MARKER_12345");

        // Poll for the marker in output
        string output = "";
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            var dump = session.Buffer.DumpAll();
            output = System.Text.Encoding.UTF8.GetString(dump);
            // Look for the marker in a context that proves execution (not just echo of the command line)
            // cmd.exe will show: echo SENDTEXT_MARKER_12345\r\nSENDTEXT_MARKER_12345\r\n
            // The marker appearing AFTER a newline following the command means it was executed
            if (output.Contains("SENDTEXT_MARKER_12345"))
            {
                // Count occurrences - should appear at least twice:
                // once in the command echo, once in the output
                int count = 0;
                int idx = 0;
                while ((idx = output.IndexOf("SENDTEXT_MARKER_12345", idx, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    idx += "SENDTEXT_MARKER_12345".Length;
                }
                if (count >= 2) break; // command was executed
            }
        }

        // The marker should appear at least twice: once as the typed command, once as output
        int finalCount = 0;
        int fi = 0;
        while ((fi = output.IndexOf("SENDTEXT_MARKER_12345", fi, StringComparison.Ordinal)) >= 0)
        {
            finalCount++;
            fi += "SENDTEXT_MARKER_12345".Length;
        }

        Assert.True(finalCount >= 2,
            $"Expected SENDTEXT_MARKER_12345 at least twice (command + output) but found {finalCount} time(s).\nFull output:\n{output}");
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
