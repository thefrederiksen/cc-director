using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Phase 2 of the turn-push mission: Chat reads the Gateway's stored conversation, and the Gateway decides
/// what an empty screen SAYS. "There are no messages" has six different causes and only one of them means
/// "wait, it is coming" - the client used to show that one sentence for all of them, which is how a person
/// ends up waiting for something that will never arrive.
/// </summary>
public sealed class SessionConversationFoldTests
{
    private static SessionTurnHeadEntity Head(bool supported = true, string agent = "ClaudeCode", string? state = null, bool raw = false) => new()
    {
        SessionId = "s1",
        DirectorId = "d1",
        Generation = "abc",
        GenerationSource = @"C:\t\a.jsonl",
        Agent = agent,
        IsSupported = supported,
        IsRawText = raw,
        HistoryState = state,
        Count = 2,
    };

    private static List<HistoryMessageDto> Messages(params string[] texts) =>
        texts.Select(t => new HistoryMessageDto { Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = t } } }).ToList();

    private static SessionHistoryDto Fold(SessionTurnHeadEntity? head, List<HistoryMessageDto>? messages = null,
        bool known = true, bool connected = true, bool pushes = true)
        => SessionConversationFold.Fold("s1", head, messages ?? new List<HistoryMessageDto>(), "d1", known, connected, pushes);

    [Fact]
    public void AStoredConversation_IsServedWithItsMessages_AndNoEmptyText()
    {
        var dto = Fold(Head(state: "BackgroundRunning"), Messages("hello", "hi"));

        Assert.Equal("ok", dto.Status);
        Assert.Equal(new[] { "hello", "hi" }, dto.Messages.Select(m => m.Parts[0].Text));
        Assert.Equal("ClaudeCode", dto.Agent);
        Assert.Equal("BackgroundRunning", dto.HistoryState);
        Assert.Null(dto.EmptyText);      // there is content; the empty line is not the client's to render
        Assert.Null(dto.Error);
    }

    [Fact]
    public void StoredRows_AreServedEvenWhenTheDirectorIsOfflineAndTooOldAndUnknown()
    {
        // The whole point of storing the conversation: it is readable when the machine that produced it is
        // not. Chat used to go blank the moment that computer went away.
        var dto = Fold(Head(), Messages("said before it went offline"), known: false, connected: false, pushes: false);

        Assert.Equal("ok", dto.Status);
        Assert.Single(dto.Messages);
        Assert.Null(dto.EmptyText);
    }

    [Fact]
    public void AFrozenConversation_CarriesANoticeAboveIt_SoItDoesNotReadAsLive()
    {
        // The words are real; what stopped being true is that they are current. Without this the screen is
        // identical to a live session, and a reader types a prompt into a session whose agent is not running.
        var dto = Fold(Head(), Messages("said before it went offline"), connected: false);

        Assert.Equal("ok", dto.Status);
        Assert.Single(dto.Messages);                                  // the conversation is still served
        Assert.Equal(SessionConversationFold.FrozenOfflineNotice, dto.StaleNotice);
        Assert.Null(dto.EmptyText);                                   // a notice rides ALONGSIDE, never instead
    }

    [Fact]
    public void AConversationFromAComputerTooOldToSendMore_SaysWhatItStopsAt()
    {
        var dto = Fold(Head(), Messages("the last turn it ever sent"), connected: true, pushes: false);

        Assert.Equal(SessionConversationFold.FrozenTooOldNotice, dto.StaleNotice);
        Assert.Contains("Update it", dto.StaleNotice);
    }

    [Fact]
    public void TheSentencesClaimOnlyWhatTheGatewayObserved_NotThatAComputerIsSwitchedOff()
    {
        // All this Gateway knows is that a Director has not pushed inside the freshness window. The machine
        // may be running perfectly behind a dropped connection, so the words a person reads must not say it
        // is off (found in review - and the same imprecision the owner corrected in conversation).
        foreach (var text in new[] { SessionConversationFold.DirectorOfflineText, SessionConversationFold.FrozenOfflineNotice })
        {
            Assert.Contains("has not checked in", text);
            Assert.DoesNotContain("is offline", text);
        }
        Assert.Contains("until it reconnects", SessionConversationFold.FrozenOfflineNotice);
    }

    [Fact]
    public void ALiveConversation_CarriesNoNotice()
    {
        var dto = Fold(Head(), Messages("hello"), connected: true, pushes: true);
        Assert.Null(dto.StaleNotice);
    }

    [Fact]
    public void AnEmptyScreen_NeverCarriesBothASentenceAndANotice()
    {
        // The two channels are exclusive by construction: EmptyText replaces the conversation, StaleNotice
        // sits above one. A screen carrying both would say the same thing twice, differently.
        var cases = new[]
        {
            Fold(head: null, known: false),
            Fold(head: null, connected: false),
            Fold(head: null, pushes: false),
            Fold(head: null),
            Fold(Head()),
            Fold(Head(supported: false)),
            Fold(Head(), Messages("live")),
            Fold(Head(), Messages("frozen"), connected: false),
        };

        Assert.All(cases, dto => Assert.False(dto.EmptyText is not null && dto.StaleNotice is not null));
    }

    [Fact]
    public void AnAgentThatKeepsNoConversation_SaysSo_EvenWhenItsComputerIsAlsoOffline()
    {
        // Terminal, and true whether the machine is on or off. Saying "your computer is offline" would send
        // the reader to fix something that is not the reason nothing will ever appear.
        var dto = Fold(Head(supported: false, agent: "Gemini"), connected: false);

        Assert.Equal("unsupported", dto.Status);
        Assert.False(dto.IsSupported);
        Assert.Equal(SessionConversationFold.UnsupportedText, dto.EmptyText);
    }

    [Fact]
    public void AnUnknownSession_SaysItIsUnknown_NotThatSomeComputerIsOffline()
    {
        var dto = Fold(head: null, known: false, connected: false);

        Assert.Equal("unknown-session", dto.Status);
        Assert.Equal(SessionConversationFold.UnknownSessionText, dto.EmptyText);
    }

    [Fact]
    public void AKnownSessionWhoseComputerIsAway_SaysOffline()
    {
        var dto = Fold(head: null, known: true, connected: false);

        Assert.Equal("director-offline", dto.Status);
        Assert.Equal(SessionConversationFold.DirectorOfflineText, dto.EmptyText);
        Assert.Equal(dto.EmptyText, dto.Error);
    }

    [Fact]
    public void ADirectorTooOldToSend_SaysToUpdateIt_NotToWait()
    {
        // THE SENTENCE THIS FOLD EXISTS FOR. Nothing is ever going to arrive from this computer, so telling
        // the reader to wait would be a lie they could sit in front of indefinitely.
        var dto = Fold(head: null, connected: true, pushes: false);

        Assert.Equal("director-too-old", dto.Status);
        Assert.Contains("Update it", dto.EmptyText);
        Assert.DoesNotContain("moment", dto.EmptyText);
    }

    [Fact]
    public void NothingStored_WithAConnectedPushingDirector_SaysItIsComing()
    {
        var dto = Fold(head: null, connected: true, pushes: true);

        Assert.Equal("not-pushed-yet", dto.Status);
        Assert.Equal(SessionConversationFold.NotPushedYetText, dto.EmptyText);
        Assert.Empty(dto.Messages);
    }

    [Fact]
    public void AStoredSessionThatHasNotSpokenYet_SaysTheConversationHasNotStarted()
    {
        var dto = Fold(Head());

        Assert.Equal("ok", dto.Status);
        Assert.Equal(SessionConversationFold.NotStartedText, dto.EmptyText);
        Assert.Null(dto.Error);          // not a fault - a new session really has said nothing yet
    }

    [Fact]
    public void AnEmptyStoredSession_WhoseComputerThenWentAway_StopsSayingItIsAboutToStart()
    {
        // A head registered, nothing was ever said, and then the machine left. The first version of this
        // fold stopped at "the conversation has not started" the moment a head existed, so this session sat
        // on that sentence forever while the reader waited (found in review).
        var dto = Fold(Head(), connected: false);

        Assert.Equal("director-offline", dto.Status);
        Assert.Equal(SessionConversationFold.DirectorOfflineText, dto.EmptyText);
    }

    [Fact]
    public void AnEmptyStoredSession_WhoseComputerDowngraded_SaysToUpdateIt()
    {
        var dto = Fold(Head(), connected: true, pushes: false);

        Assert.Equal("director-too-old", dto.Status);
        Assert.Equal(SessionConversationFold.DirectorTooOldText, dto.EmptyText);
    }

    [Fact]
    public void RawTextAgents_KeepTheirRawFlag_SoTheClientRendersThemVerbatim()
    {
        var dto = Fold(Head(agent: "Gemini", raw: true), Messages("scrollback"));
        Assert.True(dto.IsRawText);
    }

    [Fact]
    public void EveryEmptyAnswer_CarriesASentence_AndEveryFaultCarriesItAsTheErrorToo()
    {
        // A screen with nothing on it and nothing to say is the failure this whole fold exists to prevent.
        var cases = new[]
        {
            Fold(head: null, known: false),
            Fold(head: null, connected: false),
            Fold(head: null, pushes: false),
            Fold(head: null),
            Fold(Head()),
            Fold(Head(supported: false)),
        };

        Assert.All(cases, dto => Assert.False(string.IsNullOrWhiteSpace(dto.EmptyText)));
        // The two non-faults - a new session, and an agent that keeps no history - are NOT errors.
        Assert.All(cases.Where(d => d.Status is "ok" or "unsupported"), dto => Assert.Null(dto.Error));
        Assert.All(cases.Where(d => d.Status is not ("ok" or "unsupported")), dto => Assert.Equal(dto.EmptyText, dto.Error));
    }
}
