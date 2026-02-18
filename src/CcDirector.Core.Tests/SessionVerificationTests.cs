using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for Session.VerifyClaudeSession and the SessionVerificationStatus enum.
/// </summary>
public class SessionVerificationTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionVerificationTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = "cmd.exe",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void VerifyClaudeSession_NoClaudeSessionId_NotVerified()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Null(session.ClaudeSessionId);

        session.VerifyClaudeSession();

        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void VerifyClaudeSession_NonexistentSessionId_StaysNotLinked()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        session.ClaudeSessionId = "nonexistent-session-id-that-wont-exist";

        session.VerifyClaudeSession();

        // No .jsonl file means no content to verify, so stays NotLinked
        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void VerificationStatus_DefaultIsNotVerified()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Equal(SessionVerificationStatus.NotLinked, session.VerificationStatus);
    }

    [Fact]
    public void SessionVerificationStatus_HasExpectedValues()
    {
        // Verify the enum has all expected values
        Assert.Equal(0, (int)SessionVerificationStatus.Verified);
        Assert.Equal(1, (int)SessionVerificationStatus.FileNotFound);
        Assert.Equal(2, (int)SessionVerificationStatus.NotLinked);
        Assert.Equal(3, (int)SessionVerificationStatus.Error);
        Assert.Equal(4, (int)SessionVerificationStatus.ContentMismatch);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
