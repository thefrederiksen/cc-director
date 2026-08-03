using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for the append-only governance event ledger (issue #1771, spine item 2). The claims that
/// matter: a transition is validated (subject/state vocabulary, the subject's own key required) and stamped
/// (server RecordedUtc, occurred defaulted/clamped); the ledger is append-only and reads newest-first with a
/// working session/run/state/time filter; the batch path lands all-or-nothing; and every row is tenant-scoped
/// so one tenant never reads another's transitions.
/// </summary>
public sealed class GovernanceEventLedgerTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private GovernanceEventLedger NewLedger() => new(_h.Open());

    private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Session = "session-guid-abc";

    private static AppendGovernanceEventRequest SessionEvent(
        string state, string? session = Session, DateTime? occurred = null, string? reason = null) => new()
    {
        SubjectKind = GovernanceEventSubject.Session,
        SessionId = session,
        State = state,
        Reason = reason,
        OccurredUtc = occurred,
    };

    private static AppendGovernanceEventRequest RunEvent(string state, Guid? run = null) => new()
    {
        SubjectKind = GovernanceEventSubject.Run,
        RunId = run ?? Run,
        State = state,
    };

    [Fact]
    public void Append_records_a_session_transition_and_server_stamps_it()
    {
        var ledger = NewLedger();
        var before = DateTime.UtcNow;

        var dto = ledger.Append(SessionEvent(GovernanceEventState.WaitingOnHuman, reason: "owner not replied"));

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(GovernanceEventSubject.Session, dto.SubjectKind);
        Assert.Equal(Session, dto.SessionId);
        Assert.Null(dto.RunId);
        Assert.Equal(GovernanceEventState.WaitingOnHuman, dto.State);
        Assert.Equal("owner not replied", dto.Reason);
        Assert.True(dto.RecordedUtc >= before);
        // Occurred defaults to the append time when the caller omits it.
        Assert.True(dto.OccurredUtc >= before);
    }

    [Fact]
    public void Append_defaults_occurred_to_now_and_clamps_a_future_clock()
    {
        var ledger = NewLedger();
        var future = DateTime.UtcNow.AddHours(2);

        var dto = ledger.Append(SessionEvent(GovernanceEventState.Active, occurred: future));

        // A future occurred time is a skewed caller clock, not a real transition: clamp to now.
        Assert.True(dto.OccurredUtc <= DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Append_rejects_an_unknown_subject_kind()
    {
        var ledger = NewLedger();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => ledger.Append(new AppendGovernanceEventRequest
            {
                SubjectKind = "cluster", SessionId = Session, State = GovernanceEventState.Active,
            }));
        Assert.Contains("subject kind", ex.Message);
    }

    [Fact]
    public void Append_rejects_an_unknown_state()
    {
        var ledger = NewLedger();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => ledger.Append(SessionEvent("napping")));
        Assert.Contains("governance state", ex.Message);
    }

    [Fact]
    public void A_session_event_requires_a_session_id()
    {
        var ledger = NewLedger();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => ledger.Append(SessionEvent(GovernanceEventState.Idle, session: null)));
        Assert.Contains("sessionId", ex.Message);
    }

    [Fact]
    public void A_run_event_requires_a_run_id()
    {
        var ledger = NewLedger();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => ledger.Append(new AppendGovernanceEventRequest
            {
                SubjectKind = GovernanceEventSubject.Run,
                RunId = Guid.Empty,
                State = GovernanceEventState.Active,
            }));
        Assert.Contains("runId", ex.Message);
    }

    [Fact]
    public void An_oversize_reason_is_rejected()
    {
        var ledger = NewLedger();
        var ex = Assert.Throws<GovernanceValidationException>(
            () => ledger.Append(SessionEvent(GovernanceEventState.Blocked,
                reason: new string('x', GovernanceEventLedger.MaxReasonChars + 1))));
        Assert.Contains("reason", ex.Message);
    }

    [Fact]
    public void List_returns_transitions_newest_first()
    {
        var ledger = NewLedger();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        ledger.Append(SessionEvent(GovernanceEventState.Active, occurred: t0));
        ledger.Append(SessionEvent(GovernanceEventState.WaitingOnHuman, occurred: t0.AddMinutes(3)));
        ledger.Append(SessionEvent(GovernanceEventState.Recovered, occurred: t0.AddMinutes(6)));

        var all = ledger.List(sessionId: Session);

        Assert.Equal(3, all.Count);
        Assert.Equal(GovernanceEventState.Recovered, all[0].State);
        Assert.Equal(GovernanceEventState.WaitingOnHuman, all[1].State);
        Assert.Equal(GovernanceEventState.Active, all[2].State);
    }

    [Fact]
    public void List_filters_by_session_run_state_and_time_window()
    {
        var ledger = NewLedger();
        var t0 = DateTime.UtcNow.AddHours(-2);
        ledger.Append(SessionEvent(GovernanceEventState.Active, occurred: t0));
        ledger.Append(SessionEvent(GovernanceEventState.Blocked, occurred: t0.AddMinutes(30)));
        ledger.Append(SessionEvent(GovernanceEventState.Active, session: "other-session", occurred: t0.AddMinutes(31)));
        ledger.Append(RunEvent(GovernanceEventState.WaitingOnHuman));

        Assert.Equal(2, ledger.List(sessionId: Session).Count);
        Assert.Single(ledger.List(sessionId: Session, state: GovernanceEventState.Blocked));
        Assert.Single(ledger.List(runId: Run));
        Assert.Equal(GovernanceEventSubject.Run, ledger.List(runId: Run)[0].SubjectKind);

        // Window: [t0+15m, t0+40m) excludes the t0 Active, includes the t0+30m Blocked.
        var windowed = ledger.List(
            sessionId: Session, sinceUtc: t0.AddMinutes(15), untilUtc: t0.AddMinutes(40));
        Assert.Single(windowed);
        Assert.Equal(GovernanceEventState.Blocked, windowed[0].State);
    }

    [Fact]
    public void AppendBatch_writes_every_event_in_one_call()
    {
        var ledger = NewLedger();
        var written = ledger.AppendBatch(new List<AppendGovernanceEventRequest>
        {
            SessionEvent(GovernanceEventState.Active),
            SessionEvent(GovernanceEventState.Idle),
            RunEvent(GovernanceEventState.WaitingOnHuman),
        });

        Assert.Equal(3, written);
        Assert.Equal(2, ledger.List(sessionId: Session).Count);
        Assert.Single(ledger.List(runId: Run));
    }

    [Fact]
    public void AppendBatch_is_all_or_nothing_when_one_entry_is_invalid()
    {
        var ledger = NewLedger();
        Assert.Throws<GovernanceValidationException>(() => ledger.AppendBatch(new List<AppendGovernanceEventRequest>
        {
            SessionEvent(GovernanceEventState.Active),
            SessionEvent("bogus-state"),   // invalid - must abort the whole batch
        }));

        // Nothing landed - the ledger never holds a half-batch.
        Assert.Empty(ledger.List(sessionId: Session));
    }

    [Fact]
    public void The_ledger_is_tenant_scoped()
    {
        // Two ledgers over the SAME database file, each fixed to a different tenant. One tenant must never
        // read the other's transitions (the global query filter), matching the rest of the data layer.
        var alpha = new GovernanceEventLedger(_h.Open(new FixedTenantContext(new TenantId("alpha"))));
        var beta = new GovernanceEventLedger(_h.Open(new FixedTenantContext(new TenantId("beta"))));

        alpha.Append(SessionEvent(GovernanceEventState.Active, session: "alpha-session"));
        beta.Append(SessionEvent(GovernanceEventState.Active, session: "beta-session"));

        var alphaRows = alpha.List();
        var betaRows = beta.List();

        Assert.Single(alphaRows);
        Assert.Single(betaRows);
        Assert.Equal("alpha-session", alphaRows[0].SessionId);
        Assert.Equal("beta-session", betaRows[0].SessionId);
    }
}
