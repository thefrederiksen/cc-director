using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the turn-end spend emitter (issue #1771, spine item 3). The claims that matter: a Claude
/// snapshot records real tokens labelled subscription-included; a snapshot with no token totals is still
/// recorded but as UNKNOWN coverage (never a fabricated zero-with-captured); a non-Claude agent is labelled
/// unknown (never a guessed "metered"); and the agent label is normalised to a lowercase family.
/// </summary>
public sealed class SessionSpendEmitterTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private (SessionSpendEmitter Emitter, SessionSpendStore Store) NewEmitter()
    {
        var store = new SessionSpendStore(_h.Open());
        return (new SessionSpendEmitter(store), store);
    }

    private static SessionDto Session(
        string id = "sess-1", string agent = "ClaudeCode", TokenTotalsDto? totals = null) => new()
    {
        SessionId = id,
        Agent = agent,
        RepoPath = "D:/repo",
        CurrentModel = "claude-opus-4-8",
        TokenTotals = totals,
    };

    [Theory]
    [InlineData("ClaudeCode", "claude")]
    [InlineData("claude", "claude")]
    [InlineData("Codex", "codex")]
    [InlineData("Pi", "pi")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void ResolveAgentKind_normalises_to_a_lowercase_family(string? agent, string expected)
    {
        Assert.Equal(expected, SessionSpendEmitter.ResolveAgentKind(agent));
    }

    [Fact]
    public void ResolveBillingMode_labels_claude_subscription_and_others_unknown()
    {
        Assert.Equal(SessionBillingMode.SubscriptionIncluded, SessionSpendEmitter.ResolveBillingMode("claude"));
        Assert.Equal(SessionBillingMode.Unknown, SessionSpendEmitter.ResolveBillingMode("codex"));
        Assert.Equal(SessionBillingMode.Unknown, SessionSpendEmitter.ResolveBillingMode("pi"));
    }

    [Fact]
    public void Emit_records_real_tokens_as_subscription_included_for_claude()
    {
        var (emitter, store) = NewEmitter();
        emitter.Emit(Session(totals: new TokenTotalsDto
        {
            InputTokens = 10,
            OutputTokens = 100,
            CacheReadTokens = 5,
            CacheCreationTokens = 3,
        }));

        var dto = store.Get("sess-1");
        Assert.NotNull(dto);
        Assert.Equal("claude", dto!.AgentKind);
        Assert.True(dto.TokensCaptured);
        Assert.Equal(100, dto.OutputTokens);
        Assert.Equal(SessionBillingMode.SubscriptionIncluded, dto.BillingMode);
        // Subscription traffic never carries a fabricated dollar figure.
        Assert.Null(dto.MeteredCostMicros);
    }

    [Fact]
    public void Emit_records_a_gauge_only_session_as_uncaptured_not_zero()
    {
        var (emitter, store) = NewEmitter();
        // A driver that reports no additive totals (context gauge only) still gets a row, disclosed as
        // uncaptured so the coverage summary counts the gap rather than reading it as a real zero.
        emitter.Emit(Session(id: "sess-2", agent: "Codex", totals: null));

        var dto = store.Get("sess-2");
        Assert.NotNull(dto);
        Assert.Equal("codex", dto!.AgentKind);
        Assert.False(dto.TokensCaptured);
        Assert.Equal(0, dto.OutputTokens);
        Assert.Equal(SessionBillingMode.Unknown, dto.BillingMode);
    }

    [Fact]
    public void Emit_upserts_cumulative_totals_by_overwrite()
    {
        var (emitter, store) = NewEmitter();
        emitter.Emit(Session(totals: new TokenTotalsDto { OutputTokens = 100 }));
        emitter.Emit(Session(totals: new TokenTotalsDto { OutputTokens = 250 })); // a later cumulative read

        var dto = store.Get("sess-1");
        Assert.Equal(250, dto!.OutputTokens); // overwrite, not add
        Assert.Single(store.List());
    }
}
