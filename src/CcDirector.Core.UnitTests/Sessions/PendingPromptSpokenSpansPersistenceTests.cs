using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// Ruling R20: a dictation left in the compose box is still a dictation after a session switch AND after a
/// Director restart. The desktop saves the box's spoken ranges on the Session beside the pending text;
/// these prove the Session keeps them honest and that they ride the persisted state to disk and back
/// through the real store and the real restore.
/// </summary>
public sealed class PendingPromptSpokenSpansPersistenceTests : IDisposable
{
    private const string Words = "deploy the gateway and tell me when it is up";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
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

    private static Session NewSession() => new(
        Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, new NullBackend(), "claude-test",
        ActivityState.Idle, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public void SettingThePendingText_ClearsTheSpans_SoTheyAreSetAfterTheTextTheyDescribe()
    {
        var s = NewSession();
        s.PendingPromptText = Words;
        s.PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(0, Words.Length) };
        Assert.Single(s.PendingPromptSpokenSpans);

        // Another writer sets a different text: whatever was spoken in the old box is not in this one.
        s.SetPendingPromptText("something the wingman suggested", "wingman");
        Assert.Empty(s.PendingPromptSpokenSpans);

        // The same text again is not a change and keeps nothing either way.
        s.PendingPromptText = Words;
        Assert.Empty(s.PendingPromptSpokenSpans);
    }

    [Fact]
    public void ASpanOutsideThePendingText_IsRefused()
    {
        var s = NewSession();
        s.PendingPromptText = "short";
        Assert.Throws<ArgumentException>(() => s.PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(0, 40) });
        Assert.Throws<ArgumentException>(() => s.PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(-1, 2) });
        Assert.Throws<ArgumentException>(() => s.PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(0, 0) });
    }

    [Fact]
    public void TheSpans_RideThePersistedStateToDiskAndBack_ThroughTheRealStoreAndRestore()
    {
        var manager = new SessionManager(new AgentOptions());
        var restored = manager.RestoreEmbeddedSession(new PersistedSession
        {
            Id = Guid.NewGuid(),
            RepoPath = @"C:\test\repo",
            WorkingDirectory = @"C:\test\repo",
            ClaudeSessionId = "claude-test",
            CreatedAt = DateTimeOffset.UtcNow,
            PendingPromptText = "please " + Words,
            PendingPromptSpokenSpans = new List<PersistedSpokenSpan> { new() { Start = 7, Length = Words.Length } },
        }, new NullBackend());
        Assert.Equal(new SpokenTurnRule.SpokenSpan(7, Words.Length), Assert.Single(restored.PendingPromptSpokenSpans));

        Directory.CreateDirectory(_dir);
        var store = new SessionStateStore(Path.Combine(_dir, "sessions.json"));
        manager.SaveCurrentState(store);

        var loaded = store.Load();
        Assert.True(loaded.Success, loaded.ErrorMessage);
        var persisted = Assert.Single(loaded.Sessions, p => p.Id == restored.Id);
        Assert.Equal("please " + Words, persisted.PendingPromptText);
        var span = Assert.Single(persisted.PendingPromptSpokenSpans!);
        Assert.Equal(7, span.Start);
        Assert.Equal(Words.Length, span.Length);

        var again = new SessionManager(new AgentOptions()).RestoreEmbeddedSession(persisted, new NullBackend());
        Assert.Equal(new SpokenTurnRule.SpokenSpan(7, Words.Length), Assert.Single(again.PendingPromptSpokenSpans));
    }

    [Fact]
    public void ASessionWithNoDictationInItsBox_PersistsNoSpans()
    {
        var manager = new SessionManager(new AgentOptions());
        manager.RestoreEmbeddedSession(new PersistedSession
        {
            Id = Guid.NewGuid(), RepoPath = @"C:\test\repo", WorkingDirectory = @"C:\test\repo",
            ClaudeSessionId = "claude-test", CreatedAt = DateTimeOffset.UtcNow, PendingPromptText = "typed words",
        }, new NullBackend());
        Directory.CreateDirectory(_dir);
        var store = new SessionStateStore(Path.Combine(_dir, "sessions.json"));
        manager.SaveCurrentState(store);
        Assert.Null(Assert.Single(store.Load().Sessions).PendingPromptSpokenSpans);
    }
}
