using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for per-session honest spend (issue #1771, spine item 3). The claims that matter: cumulative
/// totals upsert (overwrite, not add); the billing-mode label is validated and a subscription session is
/// labelled subscription-included (never "unknown"); the metered-dollar column is never set from a record;
/// the coverage summary counts token-captured vs subscription vs no-capture; and the store is tenant-scoped.
/// </summary>
public sealed class SessionSpendStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private SessionSpendStore NewStore() => new(_h.Open());

    private static RecordSessionSpendRequest ClaudeSession(
        string session = "sess-1", long output = 100, string billing = SessionBillingMode.SubscriptionIncluded) => new()
    {
        SessionId = session,
        AgentKind = "claude",
        Model = "claude-opus-4-8",
        RepoPath = "D:/repo",
        TokensCaptured = true,
        InputTokens = 10,
        OutputTokens = output,
        CacheReadTokens = 5,
        CacheCreationTokens = 3,
        BillingMode = billing,
    };

    [Fact]
    public void Record_stores_a_session_with_raw_tokens_and_no_dollar_figure()
    {
        var store = NewStore();
        var dto = store.Record(ClaudeSession());

        Assert.Equal("sess-1", dto.SessionId);
        Assert.Equal("claude", dto.AgentKind);
        Assert.True(dto.TokensCaptured);
        Assert.Equal(100, dto.OutputTokens);
        Assert.Equal(SessionBillingMode.SubscriptionIncluded, dto.BillingMode);
        // Subscription traffic never carries a fabricated dollar figure - the column is null this phase.
        Assert.Null(dto.MeteredCostMicros);
    }

    [Fact]
    public void Record_upserts_cumulative_totals_by_overwrite_not_add()
    {
        var store = NewStore();
        store.Record(ClaudeSession(output: 100));
        var second = store.Record(ClaudeSession(output: 250)); // a later cumulative read

        // Totals are running sums from the driver, so the newer read overwrites - it is not added to the old.
        Assert.Equal(250, second.OutputTokens);
        Assert.Single(store.List());
    }

    [Fact]
    public void Record_rejects_an_unknown_billing_mode()
    {
        var store = NewStore();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => store.Record(ClaudeSession(billing: "invoice")));
        Assert.Contains("billing mode", ex.Message);
    }

    [Fact]
    public void Record_rejects_a_missing_session_or_agent()
    {
        var store = NewStore();
        Assert.Throws<GovernanceValidationException>(
            () => store.Record(new RecordSessionSpendRequest { AgentKind = "claude", BillingMode = SessionBillingMode.Metered }));
        Assert.Throws<GovernanceValidationException>(
            () => store.Record(new RecordSessionSpendRequest { SessionId = "s", BillingMode = SessionBillingMode.Metered }));
    }

    [Fact]
    public void A_context_gauge_only_driver_records_as_no_token_capture()
    {
        var store = NewStore();
        // Codex reports occupancy, not additive spend: token sums are UNKNOWN, disclosed via TokensCaptured=false.
        store.Record(new RecordSessionSpendRequest
        {
            SessionId = "codex-1",
            AgentKind = "codex",
            TokensCaptured = false,
            BillingMode = SessionBillingMode.Unknown,
        });

        var dto = store.Get("codex-1")!;
        Assert.False(dto.TokensCaptured);
        Assert.Null(dto.MeteredCostMicros);
    }

    [Fact]
    public void Coverage_discloses_token_capture_and_billing_split()
    {
        var store = NewStore();
        store.Record(ClaudeSession(session: "sub-1"));                                    // subscription, tokens
        store.Record(ClaudeSession(session: "sub-2"));                                    // subscription, tokens
        store.Record(new RecordSessionSpendRequest                                        // codex, no capture
        {
            SessionId = "codex-1", AgentKind = "codex", TokensCaptured = false,
            BillingMode = SessionBillingMode.Unknown,
        });

        var coverage = store.Coverage();
        Assert.Equal(3, coverage.Sessions);
        Assert.Equal(2, coverage.SessionsWithTokens);
        Assert.Equal(2, coverage.SessionsSubscriptionIncluded);
        Assert.Equal(1, coverage.SessionsWithoutTokenCapture);
        Assert.Equal(0, coverage.SessionsWithMeteredDollars);
    }

    [Fact]
    public void List_filters_by_agent_and_billing_mode()
    {
        var store = NewStore();
        store.Record(ClaudeSession(session: "c1"));
        store.Record(new RecordSessionSpendRequest
        {
            SessionId = "x1", AgentKind = "codex", TokensCaptured = false, BillingMode = SessionBillingMode.Unknown,
        });

        Assert.Single(store.List(agentKind: "claude"));
        Assert.Single(store.List(billingMode: SessionBillingMode.SubscriptionIncluded));
        Assert.Single(store.List(billingMode: SessionBillingMode.Unknown));
    }

    [Fact]
    public void The_store_is_tenant_scoped()
    {
        var alpha = new SessionSpendStore(_h.Open(new FixedTenantContext(new TenantId("alpha"))));
        var beta = new SessionSpendStore(_h.Open(new FixedTenantContext(new TenantId("beta"))));

        alpha.Record(ClaudeSession(session: "alpha-sess"));
        beta.Record(ClaudeSession(session: "beta-sess"));

        Assert.Single(alpha.List());
        Assert.Single(beta.List());
        Assert.Equal("alpha-sess", alpha.List()[0].SessionId);
        Assert.Null(alpha.Get("beta-sess"));
    }
}
