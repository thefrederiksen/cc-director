using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.TurnLog;
using Xunit;

namespace CcDirector.Gateway.Tests.TurnLog;

/// <summary>
/// The recorder: what a record contains, what happens when a part cannot be collected, and - the property
/// the whole instrument rests on - that with capture switched off it touches nothing at all.
/// </summary>
public sealed class TurnLogRecorderTests
{
    private static readonly TenantId Tenant = new("acct-a");

    private static TurnEndSignal Signal(bool isNewTurn = true, string? previous = "Working")
        => new("sid-1", "director-1", Tenant, isNewTurn, previous);

    [Fact]
    public void OnTurnEnd_CaptureSwitchedOff_ReadsNothingAtAll()
    {
        // The instrument must not change what it observes. Off means no screen read, no scrollback read, no
        // conversation read and no file - not "a capture that is thrown away", which would still cost the
        // Director a tunnel round trip on every turn end on the fleet.
        var env = new FakeEnvironment { Enabled = false };
        using var recorder = new TurnLogRecorder(env);

        recorder.OnTurnEnd(Signal());

        Assert.Equal(0, env.ScreenReads);
        Assert.Equal(0, env.ScrollbackReads);
        Assert.Equal(0, env.ConversationReads);
        Assert.Empty(env.Written);
    }

    [Fact]
    public async Task CaptureAsync_AGoodTurn_WritesOneRecordWithTheScreenAndTheConversation()
    {
        var env = new FakeEnvironment();
        env.Grid = new ScreenGridResponse
        {
            SessionId = "sid-1",
            HasGrid = true,
            Rows = { "> waiting for you", "" },
            CursorRow = 1,
            CursorCol = 2,
            CursorVisible = true,
        };
        env.Scrollback = new BufferResponse { Text = "ran the build\nbuild succeeded\n" };
        env.Conversation = new StoredConversationSnapshot(true, "transcript-1", new[]
        {
            Message("User", "do the thing"),
            Message("Assistant", "done"),
        });
        var recorder = new TurnLogRecorder(env);

        var path = await recorder.CaptureAsync(Signal(), CancellationToken.None);

        Assert.NotNull(path);
        var record = Assert.Single(env.Written);
        Assert.True(record.Terminal.HasGrid);
        Assert.Equal(2, record.Terminal.RowCount);
        Assert.Equal("> waiting for you", record.Terminal.Rows[0]);
        Assert.True(record.Terminal.CursorVisible);
        Assert.Contains("build succeeded", record.Terminal.Scrollback);
        Assert.Equal(2, record.Conversation.Messages.Count);
        Assert.Equal("transcript-1", record.Conversation.Generation);
        Assert.True(record.Moment.IsNewTurn);
        Assert.Equal("Working", record.Moment.ActivityStateBefore);
        Assert.Empty(record.Gaps);
        // Unlabelled, and it must stay that way until a person says otherwise.
        Assert.Null(record.Verdict);
    }

    [Fact]
    public async Task CaptureAsync_TheScreenCannotBeRead_StillWritesARecordAndNamesTheGap()
    {
        // Unreadable is not an empty screen, and it is not a turn that did not happen. Both confusions
        // would quietly bias the corpus away from exactly the sessions worth looking at - the ones whose
        // machine had dropped off.
        var env = new FakeEnvironment { Grid = null };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.False(record.Terminal.HasGrid);
        Assert.Empty(record.Terminal.Rows);
        Assert.Contains(record.Gaps, g => g.Part == "terminal");
    }

    [Fact]
    public async Task CaptureAsync_AReadThatThrows_IsRecordedAsAGapRatherThanLosingTheTurn()
    {
        var env = new FakeEnvironment { ScreenThrows = new InvalidOperationException("the tunnel closed") };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Contains(record.Gaps, g => g.Part == "terminal" && g.Reason.Contains("the tunnel closed"));
    }

    [Fact]
    public async Task CaptureAsync_TheSessionHasGone_StillWritesARecordAndNamesTheGap()
    {
        var env = new FakeEnvironment { Session = null };
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Null(record.Session);
        Assert.Contains(record.Gaps, g => g.Part == "session");
    }

    [Fact]
    public async Task CaptureAsync_KeepsTheWholeSessionSnapshotRatherThanAChosenFew()
    {
        var env = new FakeEnvironment();
        env.Session!.Name = "Turn Log Harness - Architect";
        env.Session.MachineName = "SOREN-NORTH";
        env.Session.Agent = "Claude";
        env.Session.RepoPath = "D:/ReposFred/devthrottle";
        env.Session.StateLabel = "Waiting for you";
        var recorder = new TurnLogRecorder(env);

        await recorder.CaptureAsync(Signal(), CancellationToken.None);

        var record = Assert.Single(env.Written);
        Assert.Equal("Turn Log Harness - Architect", record.Session!.Name);
        Assert.Equal("SOREN-NORTH", record.Glance.Computer);
        Assert.Equal("Claude", record.Glance.Agent);
        Assert.Equal("D:/ReposFred/devthrottle", record.Glance.Repository);
        Assert.Equal("Waiting for you", record.Observed.StateLabel);
    }

    [Fact]
    public void BuildConversation_MoreThanTenTurns_KeepsTheLastTenWholeAndSaysItCut()
    {
        // Twelve full turns in; the cut must land ON a user message so no agent reply arrives in the corpus
        // without the prompt that caused it.
        var messages = new List<HistoryMessageDto>();
        for (var turn = 1; turn <= 12; turn++)
        {
            messages.Add(Message("User", $"ask {turn}"));
            messages.Add(Message("Assistant", $"answer {turn}"));
        }

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.True(built.Truncated);
        Assert.Equal(24, built.TotalMessageCount);
        Assert.Equal(20, built.Messages.Count);
        Assert.Equal("User", built.Messages[0].Role);
        Assert.Equal("ask 3", built.Messages[0].Parts[0].Text);
        Assert.Equal("answer 12", built.Messages[^1].Parts[0].Text);
    }

    [Fact]
    public void BuildConversation_FewerThanTenTurns_KeepsEverythingAndSaysItDidNotCut()
    {
        var messages = new List<HistoryMessageDto>
        {
            Message("User", "ask 1"),
            Message("Assistant", "answer 1"),
        };

        var built = TurnLogRecorder.BuildConversation(
            new StoredConversationSnapshot(true, "transcript-1", messages));

        Assert.False(built.Truncated);
        Assert.Equal(2, built.Messages.Count);
    }

    [Fact]
    public void BuildConversation_NothingStored_IsAnEmptyConversationNotACrash()
    {
        var built = TurnLogRecorder.BuildConversation(null);
        Assert.Empty(built.Messages);
        Assert.False(built.IsSupported);
    }

    private static HistoryMessageDto Message(string role, string text) => new()
    {
        Role = role,
        Parts = new List<HistoryPartDto> { new() { Kind = "Text", Text = text } },
    };

    /// <summary>A Gateway that is not there: every read is a field a test sets, and every write is kept.</summary>
    private sealed class FakeEnvironment : ITurnLogEnvironment
    {
        public bool Enabled { get; set; } = true;
        public SessionDto? Session { get; set; } = new() { SessionId = "sid-1", ActivityState = "WaitingForInput" };
        public ScreenGridResponse? Grid { get; set; } = new() { SessionId = "sid-1", HasGrid = true };
        public BufferResponse? Scrollback { get; set; } = new() { Text = "" };
        public StoredConversationSnapshot? Conversation { get; set; }
            = new(true, "transcript-1", Array.Empty<HistoryMessageDto>());
        public Exception? ScreenThrows { get; set; }

        public int ScreenReads { get; private set; }
        public int ScrollbackReads { get; private set; }
        public int ConversationReads { get; private set; }
        public List<TurnLogRecord> Written { get; } = new();

        public bool IsEnabled(string account, string machine) => Enabled;

        public SessionDto? LocateSession(TenantId tenant, string sessionId) => Session;

        public Task<ScreenGridResponse?> ReadScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            ScreenReads++;
            if (ScreenThrows is not null) throw ScreenThrows;
            return Task.FromResult(Grid);
        }

        public Task<BufferResponse?> ReadScrollbackAsync(TenantId tenant, string directorId, string sessionId, int lines, CancellationToken ct)
        {
            ScrollbackReads++;
            return Task.FromResult(Scrollback);
        }

        public StoredConversationSnapshot? ReadConversation(TenantId tenant, string sessionId)
        {
            ConversationReads++;
            return Conversation;
        }

        public bool? SupervisorEnabled(TenantId tenant) => true;

        public bool? IsVoiceSession(TenantId tenant, string sessionId) => false;

        public string? Write(TurnLogRecord record)
        {
            Written.Add(record);
            return "in-memory";
        }
    }
}
