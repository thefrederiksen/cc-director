using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for the model producer (issue #1637): <see cref="Session.SetCurrentModel"/> semantics
/// (a missed read never erases the last known model) and <see cref="SessionCurrentModelWatcher.RefreshModel"/>
/// stamping from the driver - the pre-first-turn launch-args answer for Claude, and the no-throw,
/// no-stamp behaviour for an agent whose driver does not report a model.
/// </summary>
public sealed class SessionCurrentModelWatcherTests
{
    [Fact]
    public void SetCurrentModel_TrimsAndLogsOnce_IgnoresNullAndBlank()
    {
        using var session = NewSession(claudeArgs: null);

        Assert.Null(session.CurrentModel);

        session.SetCurrentModel("  claude-fable-5  ");
        Assert.Equal("claude-fable-5", session.CurrentModel);

        // A missed read (null/blank) is NOT evidence the session lost its model - the value stands.
        session.SetCurrentModel(null);
        Assert.Equal("claude-fable-5", session.CurrentModel);
        session.SetCurrentModel("   ");
        Assert.Equal("claude-fable-5", session.CurrentModel);

        // A real switch replaces it.
        session.SetCurrentModel("claude-opus-4-8");
        Assert.Equal("claude-opus-4-8", session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_ClaudeNoTurnYet_NeverStampsTheLaunchAlias()
    {
        // A brand-new Claude session launched with an explicit --model: no transcript exists (random
        // ids), and the launch value is an ALIAS (opus[1m]) whose concrete transcript id differs
        // (claude-opus-4-8) - two names for one model that would split a statistics fold. The
        // producer is records-only: it must stamp NOTHING here, not the alias, so an alias can never
        // accompany a non-zero input-stats delta (the bucket increments at submission, before any
        // turn-end stamp - the gateway-sqlite Architect's review finding on #1651).
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions --model opus[1m]");

        SessionCurrentModelWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_ClaudeNoTurnNoLaunchModel_StampsNothing()
    {
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions");

        SessionCurrentModelWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_AgentWithoutModelReport_DoesNotThrowAndStampsNothing()
    {
        // Gemini's driver does not declare ModelReport; the refresh is a caught no-op, never a crash.
        using var session = NewSession(claudeArgs: "--model gemini-2.5-pro");
        session.AgentKind = AgentKind.Gemini;

        SessionCurrentModelWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    /// <summary>A session over a random (nonexistent) repo path so no real transcript can match.</summary>
    private static Session NewSession(string? claudeArgs)
    {
        var repo = Path.Combine(Path.GetTempPath(), "model-watcher-" + Guid.NewGuid().ToString("N"));
        return new Session(
            Guid.NewGuid(), repoPath: repo, workingDirectory: repo, claudeArgs: claudeArgs,
            backend: new NullBackend(), claudeSessionId: Guid.NewGuid().ToString(),
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null);
    }

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
