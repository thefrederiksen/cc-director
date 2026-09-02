using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Phase 3 of the turn-push mission: the wingman reads the Gateway's stored conversation instead of asking
/// the owning Director to re-read the user's transcript. These pin the adapter that keeps the SHAPE the
/// translator already reads, so the only thing this phase changes is where the words come from - if the
/// narration ever comes out different, it is not because the widget list did.
/// </summary>
public sealed class StoredConversationWidgetsTests
{
    private static HistoryMessageDto Msg(string role, params (string Kind, string Text)[] parts) => new()
    {
        Role = role,
        Parts = parts.Select(p => new HistoryPartDto { Kind = p.Kind, Text = p.Text }).ToList(),
    };

    [Fact]
    public void AnAgentsText_IsTheKindTheNarrationReads_AndAPersonsIsNot()
    {
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("User", ("Text", "do the thing")),
            Msg("Assistant", ("Text", "done")),
        });

        Assert.Equal(new[] { "UserMessage", "Text" }, widgets.Select(w => w.Kind));
        Assert.Equal("done", StoredConversationWidgets.LastAgentText(widgets));
    }

    [Fact]
    public void ToolWorkAndThinking_KeepTheirOwnKinds_SoTheContextWindowReadsAsItDidBefore()
    {
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("Assistant", ("Thinking", "considering"), ("ToolUse", "{\"path\":\"x\"}"), ("Text", "here it is")),
        });

        Assert.Equal(new[] { "Thinking", "ToolUse", "Text" }, widgets.Select(w => w.Kind));
        Assert.Equal("here it is", StoredConversationWidgets.LastAgentText(widgets));
    }

    [Fact]
    public void TheLastAgentText_IsTheLastOne_NotTheFirstOrThePersonsReply()
    {
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("Assistant", ("Text", "first answer")),
            Msg("User", ("Text", "and again")),
            Msg("Assistant", ("Text", "second answer")),
            Msg("User", ("Text", "thanks")),
        });

        Assert.Equal("second answer", StoredConversationWidgets.LastAgentText(widgets));
    }

    [Fact]
    public void AConversationWithNoAgentText_HasNothingToReadAloud()
    {
        // A session waiting on a prompt, or one whose turn was all tool work. This is a fact about the
        // conversation, not a failure to read it - the difference that took a session silent for 48 minutes.
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("User", ("Text", "do the thing")),
            Msg("Assistant", ("ToolUse", "{}")),
        });

        Assert.Null(StoredConversationWidgets.LastAgentText(widgets));
    }

    [Fact]
    public void OnlyAnAssistantsText_IsNarratable_SoAnUnknownRoleIsNeverReadAloudAsTheAgent()
    {
        // "Anything that is not the user is the agent" would make a system or tool role narratable if the
        // stored role set ever grew, and what it would read aloud is text the person never heard the agent
        // say (found in review). An unrecognised role keeps its own name: still context, never the reply.
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("Assistant", ("Text", "the real answer")),
            Msg("System", ("Text", "internal housekeeping")),
        });

        Assert.Equal(new[] { "Text", "System" }, widgets.Select(w => w.Kind));
        Assert.Equal("the real answer", StoredConversationWidgets.LastAgentText(widgets));
    }

    [Fact]
    public void EmptyAndMissingPieces_AreDropped_RatherThanDilutingTheContext()
    {
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("Assistant", ("Text", "   "), ("Text", "real")),
            new HistoryMessageDto { Role = "Assistant", Parts = null! },
        });

        Assert.Single(widgets);
        Assert.Equal("real", widgets[0].Content);
        Assert.Empty(StoredConversationWidgets.From(null));
    }

    [Fact]
    public void TheAdaptedWidgets_FeedTheSameRecentContextTheTranslatorAlreadyBuilds()
    {
        // The proof that the shape survived the move: the translator's own context builder, unchanged,
        // reads these widgets and produces the conversation it always did.
        var widgets = StoredConversationWidgets.From(new[]
        {
            Msg("User", ("Text", "what is the status")),
            Msg("Assistant", ("Text", "one moment")),
            Msg("User", ("Text", "any luck")),
            Msg("Assistant", ("Text", "all done")),
        });

        var context = WingmanTranslator.BuildRecentContext(widgets);

        Assert.Contains("You: what is the status", context);
        Assert.Contains("Agent: one moment", context);
        Assert.DoesNotContain("all done", context);   // the latest reply is what gets narrated, not context
    }
}
