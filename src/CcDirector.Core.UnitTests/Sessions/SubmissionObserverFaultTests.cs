using System.Text.RegularExpressions;
using CcDirector.Core.Activity;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// Inspection finding I2-04 of the "Clean up Your Throttle" mission (2026-09-05): the tally and the
/// submission ledger are written by one method, and a throwing observer must not be able to split them.
///
/// The claimed invariant was that <c>Session.StampSubmission</c> makes the two agree unconditionally. It
/// advanced the tally, raised <c>InputStats.Changed</c> UNGUARDED, and only then raised the ledger's
/// <c>OnTurnSubmitted</c>. A <c>Changed</c> subscriber that threw therefore left the backend holding the
/// text and the tally advanced, with no ledger event and an exception handed back to a caller that might
/// retry. There is no production subscriber today; the seam is advertised as the host's persistence hook,
/// so it has to be safe before one arrives. These attach exactly that observer and drive both real
/// submission paths.
/// </summary>
public sealed class SubmissionObserverFaultTests
{
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public List<string> SentTexts { get; } = new();
        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) { SentTexts.Add(text); return Task.CompletedTask; }
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session NewSession(RecordingBackend backend)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        return s;
    }

    [Fact]
    public async Task AThrowingTallyObserver_CannotStopTheSubmissionLedgerEvent_OnTheTextPath()
    {
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var ledger = new List<(SendSource? Source, InputOrigin? Origin)>();
        s.OnTurnSubmitted += (source, origin) => ledger.Add((source, origin));
        var observerCalls = 0;
        s.InputStats.Changed += () => { observerCalls++; throw new InvalidOperationException("the host's persistence hook is broken"); };

        // The caller sees no exception: the text was already delivered, so a throw here would invite a
        // retry that types it twice.
        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopTyped);

        Assert.Equal(1, observerCalls);
        Assert.Single(backend.SentTexts);
        var bucket = Assert.Single(s.InputStats.Snapshot().Buckets, b => b.Modality == "typed" && b.Surface == "desktop");
        Assert.Equal(1, bucket.Turns);
        // THE INVARIANT: the ledger event was raised even though the observer between the two writes threw.
        var entry = Assert.Single(ledger);
        Assert.Equal(SendSource.UserInput, entry.Source);
        Assert.Equal(InputOrigin.DesktopTyped, entry.Origin);
    }

    [Fact]
    public void AThrowingTallyObserver_CannotStopTheSubmissionLedgerEvent_OnTheTerminalPath()
    {
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var ledger = new List<(SendSource? Source, InputOrigin? Origin)>();
        s.OnTurnSubmitted += (source, origin) => ledger.Add((source, origin));
        s.InputStats.Changed += () => throw new InvalidOperationException("the host's persistence hook is broken");

        foreach (var ch in "typed at the terminal")
            s.SendInput(System.Text.Encoding.UTF8.GetBytes(ch.ToString()), InputOrigin.DesktopTyped);
        s.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        var bucket = Assert.Single(s.InputStats.Snapshot().Buckets, b => b.Modality == "typed" && b.Surface == "desktop");
        Assert.Equal(1, bucket.Turns);
        var entry = Assert.Single(ledger);
        Assert.Null(entry.Source);
        Assert.Equal(InputOrigin.DesktopTyped, entry.Origin);
    }

    // ---- final inspection finding F-06: a sibling subscriber ahead of the ledger producer ------------

    [Fact]
    public async Task AThrowingSubscriber_RegisteredBeforeTheLedgerObserver_CannotStopTheLedgerEvent_OnTheTextPath()
    {
        // The inspector's probe: the throwing subscriber is on OnTurnSubmitted ITSELF, ahead of the ledger
        // observer in the invocation list. One guarded multicast invoke stopped at the throw and the ledger
        // heard nothing: expected 1, actual 0. Each subscriber now runs on its own.
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var ledger = new List<(SendSource? Source, InputOrigin? Origin)>();
        var faults = 0;
        s.OnTurnSubmitted += (_, _) => { faults++; throw new InvalidOperationException("an earlier observer is broken"); };
        s.OnTurnSubmitted += (source, origin) => ledger.Add((source, origin));

        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopTyped);

        Assert.Equal(1, faults);
        Assert.Single(backend.SentTexts);
        Assert.Equal(1, Assert.Single(s.InputStats.Snapshot().Buckets).Turns);
        var entry = Assert.Single(ledger);
        Assert.Equal(SendSource.UserInput, entry.Source);
        Assert.Equal(InputOrigin.DesktopTyped, entry.Origin);
    }

    [Fact]
    public void AThrowingSubscriber_RegisteredBeforeTheLedgerObserver_CannotStopTheLedgerEvent_OnTheTerminalPath()
    {
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var ledger = new List<(SendSource? Source, InputOrigin? Origin)>();
        s.OnTurnSubmitted += (_, _) => throw new InvalidOperationException("an earlier observer is broken");
        s.OnTurnSubmitted += (source, origin) => ledger.Add((source, origin));

        foreach (var ch in "typed at the terminal")
            s.SendInput(System.Text.Encoding.UTF8.GetBytes(ch.ToString()), InputOrigin.DesktopTyped);
        s.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        Assert.Equal(1, Assert.Single(s.InputStats.Snapshot().Buckets).Turns);
        var entry = Assert.Single(ledger);
        Assert.Null(entry.Source);
        Assert.Equal(InputOrigin.DesktopTyped, entry.Origin);
    }

    [Fact]
    public async Task EverySubscriber_RunsEvenWhenTwoOfThemThrow_AndTheCallerSeesNoException()
    {
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var heard = new List<string>();
        s.OnTurnSubmitted += (_, _) => { heard.Add("first"); throw new InvalidOperationException("first is broken"); };
        s.OnTurnSubmitted += (_, _) => heard.Add("second");
        s.OnTurnSubmitted += (_, _) => { heard.Add("third"); throw new InvalidOperationException("third is broken"); };
        s.OnTurnSubmitted += (_, _) => heard.Add("fourth");

        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopTyped);

        Assert.Equal(new[] { "first", "second", "third", "fourth" }, heard);
    }

    [Fact]
    public async Task AThrowingTallyObserver_CannotStopTheLedgerEvent_ForAnAgentDrivenTurn()
    {
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var ledger = new List<(SendSource? Source, InputOrigin? Origin)>();
        s.OnTurnSubmitted += (source, origin) => ledger.Add((source, origin));
        s.InputStats.Changed += () => throw new InvalidOperationException("the host's persistence hook is broken");

        await s.SendTextAsync("a fleet message", SendSource.Agent, null);

        Assert.Equal(1, s.InputStats.Snapshot().AgentDrivenTurns);
        var entry = Assert.Single(ledger);
        Assert.Equal(SendSource.Agent, entry.Source);
        Assert.Null(entry.Origin);
    }

    // ---- fix-round finding F-06: the throwing subscriber ahead of the REAL activity producer ------------

    /// <summary>
    /// The real ledger observer, wired the way the Director wires it. ActivityEventProducer.Wire is the
    /// production subscription seam; a lambda standing in for it proved only that some later subscriber ran,
    /// and a regression that removed or changed the producer's own subscription would have left that green.
    /// </summary>
    private sealed class RealProducer : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
        public ActivityEventOutbox Outbox { get; }
        public ActivityEventProducer Producer { get; }

        public RealProducer()
        {
            Directory.CreateDirectory(_dir);
            Outbox = new ActivityEventOutbox(Path.Combine(_dir, "outbox.jsonl"));
            Producer = new ActivityEventProducer(new SessionManager(new AgentOptions()) { DirectorId = "dir-test" }, Outbox);
        }

        public IReadOnlyList<ActivityEventRecord> TurnsSubmitted(Session s) =>
            Outbox.PendingBatch(100).Where(e => e.EventType == ActivityEventTypes.TurnSubmitted && e.SessionId == s.Id.ToString()).ToList();

        public void Dispose()
        {
            Producer.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
        }
    }

    [Fact]
    public async Task AThrowingSubscriber_RegisteredBeforeTheRealActivityProducer_CannotKeepTheLedgerFromTheTurn_OnTheTextPath()
    {
        using var real = new RealProducer();
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        var faults = 0;
        // FIRST in the invocation list: the fault. THEN the real producer subscribes, exactly as it does for
        // every session the manager announces.
        s.OnTurnSubmitted += (_, _) => { faults++; throw new InvalidOperationException("an earlier observer is broken"); };
        real.Producer.Wire(s);

        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopVoice);

        Assert.Equal(1, faults);
        Assert.Single(backend.SentTexts);
        Assert.Equal(1, Assert.Single(s.InputStats.Snapshot().Buckets).Turns);
        // THE INVARIANT, on the durable ledger the Gateway's figure is computed from: the real producer heard
        // the turn and recorded it, with its origin, although the subscriber ahead of it threw.
        var recorded = Assert.Single(real.TurnsSubmitted(s));
        Assert.Equal("voice/desktop", recorded.InputOrigin);
        Assert.Equal("UserInput", recorded.SendSource);
        Assert.Equal("dir-test", recorded.DirectorId);
    }

    [Fact]
    public void AThrowingSubscriber_RegisteredBeforeTheRealActivityProducer_CannotKeepTheLedgerFromTheTurn_OnTheTerminalPath()
    {
        using var real = new RealProducer();
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        s.OnTurnSubmitted += (_, _) => throw new InvalidOperationException("an earlier observer is broken");
        real.Producer.Wire(s);

        foreach (var ch in "typed at the terminal")
            s.SendInput(System.Text.Encoding.UTF8.GetBytes(ch.ToString()), InputOrigin.DesktopTyped);
        s.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped);

        Assert.Equal(1, Assert.Single(s.InputStats.Snapshot().Buckets).Turns);
        var recorded = Assert.Single(real.TurnsSubmitted(s));
        Assert.Equal("typed/desktop", recorded.InputOrigin);
        Assert.Null(recorded.SendSource);
    }

    [Fact]
    public async Task TheRealActivityProducer_UnwiredFromTheSession_HearsNothing_SoTheProofAboveIsOfItsSubscription()
    {
        // The control for the two tests above: it is the producer's subscription that records the turn, not
        // something else on the path. Unwire it and the same submission leaves the ledger empty.
        using var real = new RealProducer();
        var backend = new RecordingBackend();
        var s = NewSession(backend);
        s.OnTurnSubmitted += (_, _) => throw new InvalidOperationException("an earlier observer is broken");
        real.Producer.Wire(s);
        real.Producer.Unwire(s);

        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopVoice);

        Assert.Empty(real.TurnsSubmitted(s));
    }
}
