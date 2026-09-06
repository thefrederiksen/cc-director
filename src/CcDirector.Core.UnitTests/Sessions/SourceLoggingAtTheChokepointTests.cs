using CcDirector.Core.Activity;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// SOURCE LOGGING AT THE CHOKE POINT (owner's ruling, 2026-09-05). Every prompt door hands the Session what it
/// knew at entry; the Session adds the digest and length of the text it is about to send; the REAL activity
/// producer writes all of it onto the turn-submitted ledger row. These prove the choke point and the producer
/// end to end over the real outbox - the doors themselves are proven in their own projects, through their
/// real routes.
/// </summary>
public sealed class SourceLoggingAtTheChokepointTests : IDisposable
{
    private const string Words = "deploy the gateway and tell me when it is up";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityEventOutbox _outbox;
    private readonly ActivityEventProducer _producer;

    public SourceLoggingAtTheChokepointTests()
    {
        Directory.CreateDirectory(_dir);
        _outbox = new ActivityEventOutbox(Path.Combine(_dir, "outbox.jsonl"));
        _producer = new ActivityEventProducer(new SessionManager(new AgentOptions()) { DirectorId = "dir-test" }, _outbox);
    }

    public void Dispose()
    {
        _producer.Dispose();
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

    private Session WiredSession()
    {
        var s = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, new NullBackend(), "claude-test",
            ActivityState.Idle, DateTimeOffset.UtcNow, null, null);
        s.MarkRunning();
        _producer.Wire(s);
        return s;
    }

    private ActivityEventRecord TheTurn(Session s) =>
        Assert.Single(_outbox.PendingBatch(100), e => e.EventType == ActivityEventTypes.TurnSubmitted && e.SessionId == s.Id.ToString());

    [Fact]
    public async Task TheTextPath_WritesTheDoorsProvenance_AndTheChokePointsDigestAndLength_OntoTheLedgerRow()
    {
        var s = WiredSession();
        var door = new SubmissionProvenance("a-door", "a-credential", "upload-77",
            new[] { new SpokenTurnRule.SpokenSpan(7, Words.Length) });
        var text = "please " + Words;

        await s.SendTextAsync(text, door, SendSource.UserInput, InputOrigin.DesktopTyped);

        var row = TheTurn(s);
        Assert.Equal("a-door", row.Route);
        Assert.Equal("a-credential", row.IdentityKind);
        Assert.Equal("upload-77", row.TranscriptId);
        Assert.Equal("7+" + Words.Length, row.SpokenSpans);
        Assert.Equal(SubmissionEvidence.Sha256Of(text), row.ContentSha256);
        Assert.Equal(text.Length, row.ContentLength);
        Assert.Equal("typed/desktop", row.InputOrigin);
        // The digest is of the exact text, so a text that differs by one character has another digest.
        Assert.NotEqual(SubmissionEvidence.Sha256Of(text + "!"), row.ContentSha256);
    }

    [Fact]
    public void TheRawKeystrokePath_WritesTheDoor_NoDigest_AndThePrintableKeystrokesAsTheLength()
    {
        var s = WiredSession();
        var door = SubmissionProvenance.Typed("a-terminal", "a-person");
        foreach (var ch in "typed at the terminal")
            s.SendInput(System.Text.Encoding.UTF8.GetBytes(ch.ToString()), InputOrigin.DesktopTyped, door);
        s.SendInput(new byte[] { 0x0D }, InputOrigin.DesktopTyped, door);

        var row = TheTurn(s);
        Assert.Equal("a-terminal", row.Route);
        Assert.Equal("a-person", row.IdentityKind);
        Assert.Null(row.TranscriptId);
        Assert.Null(row.SpokenSpans);
        // The text is never in hand on this path - the line editor mutates it invisibly - so there is no digest,
        // and the length is what the door could count: the printable keystrokes since the last submit.
        Assert.Null(row.ContentSha256);
        Assert.Equal("typed at the terminal".Length, row.ContentLength);
    }

    [Fact]
    public async Task AFrameworkSend_SaysSoOnTheRow_AndIsStillNobodysTurn()
    {
        var s = WiredSession();
        await s.SendTextAsync("/handover", SubmissionProvenance.FrameworkText(), SendSource.Framework);
        var row = TheTurn(s);
        Assert.Equal(SubmissionRoutes.Framework, row.Route);
        Assert.Equal(SubmissionIdentityKinds.Framework, row.IdentityKind);
        Assert.Null(row.InputOrigin);
        Assert.Equal(ActivityCauses.FrameworkSubmit, row.Cause);
        Assert.Equal(SubmissionEvidence.Sha256Of("/handover"), row.ContentSha256);
    }

    [Fact]
    public async Task ASpanOutsideTheText_IsADoorsDefect_AndIsRefusedAtTheChokePoint_NeverWrittenAsALie()
    {
        var s = WiredSession();
        var door = new SubmissionProvenance("a-door", "a-credential", null, new[] { new SpokenTurnRule.SpokenSpan(0, 500) });
        await Assert.ThrowsAsync<ArgumentException>(() => s.SendTextAsync("short", door, SendSource.UserInput, InputOrigin.DesktopVoice));
        Assert.DoesNotContain(_outbox.PendingBatch(100), e => e.EventType == ActivityEventTypes.TurnSubmitted);
    }

    [Fact]
    public void TheSpansColumn_IsStartPlusLengthPairs_InTextOrder_AndNullWhenNone()
    {
        Assert.Null(SubmissionProvenance.SpansToText(SubmissionProvenance.NoSpans));
        Assert.Equal("0+5,9+3", SubmissionProvenance.SpansToText(new[] { new SpokenTurnRule.SpokenSpan(0, 5), new SpokenTurnRule.SpokenSpan(9, 3) }));
    }

    [Fact]
    public void AProvenanceOffTheWire_IsRecordedAsItArrived_AndAnAbsentOne_IsTheHonestUnknown()
    {
        var wire = new SubmissionProvenanceDto
        {
            Route = SubmissionRoutes.GatewayDictation, IdentityKind = SubmissionIdentityKinds.Device, TranscriptId = "u1",
            SpokenSpans = new List<SpokenSpanDto> { new() { Start = 2, Length = 9 } },
        };
        var p = SubmissionProvenance.FromWire(wire, SubmissionRoutes.GatewayPrompt);
        Assert.Equal(SubmissionRoutes.GatewayDictation, p.Route);
        Assert.Equal(SubmissionIdentityKinds.Device, p.IdentityKind);
        Assert.Equal("u1", p.TranscriptId);
        Assert.Equal(new SpokenTurnRule.SpokenSpan(2, 9), Assert.Single(p.SpokenSpans));
        Assert.Equal(wire.Route, p.ToDto().Route);

        var absent = SubmissionProvenance.FromWire(null, SubmissionRoutes.GatewayPrompt);
        Assert.Equal(SubmissionRoutes.GatewayPrompt, absent.Route);
        Assert.Equal(SubmissionIdentityKinds.Unknown, absent.IdentityKind);
        Assert.Null(absent.TranscriptId);
        Assert.Empty(absent.SpokenSpans);
    }

    // ---- the compose box's projection to the wire: the text sent and its spans, from one function ----------

    [Fact]
    public void ForSend_MovesTheSpansToWhereTheirCharactersStandInTheSentText()
    {
        // Two leading spaces and a Windows line ending before the dictation, a newline after: the box holds
        // more than the wire gets, and the spoken span must land on the same words in the sent text.
        var box = new SpokenTurnRule.ComposerProvenance();
        var boxText = "  please\r\n" + Words + "\nnow";
        box.Restore(boxText, new[] { new SpokenTurnRule.SpokenSpan(boxText.IndexOf(Words, StringComparison.Ordinal), Words.Length) });

        var (sent, spans) = box.ForSend();

        Assert.Equal("please " + Words + " now", sent);
        var span = Assert.Single(spans);
        Assert.Equal(Words, sent.Substring(span.Start, span.Length));
    }

    [Fact]
    public void ForSend_DropsASpanWhoseCharactersWereAllTrimmedAway_AndKeepsAnUntouchedOne()
    {
        var box = new SpokenTurnRule.ComposerProvenance();
        box.Restore(Words + "   ", new[] { new SpokenTurnRule.SpokenSpan(0, Words.Length), new SpokenTurnRule.SpokenSpan(Words.Length, 3) });
        var (sent, spans) = box.ForSend();
        Assert.Equal(Words, sent);
        Assert.Equal(new SpokenTurnRule.SpokenSpan(0, Words.Length), Assert.Single(spans));
    }
}
