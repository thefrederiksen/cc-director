using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for the append-only governance audit trail (issue #1771, spine item 4). The claims that
/// matter: category and event-type are validated together (a type must belong to its category); a human or
/// permission decision requires an actor; detail is capped; the trail reads newest-first with working
/// session/category/time filters; the batch lands all-or-nothing; and every row is tenant-scoped.
/// </summary>
public sealed class GovernanceAuditLogTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private GovernanceAuditLog NewLog() => new(_h.Open());

    private const string Session = "sess-audit-1";
    private static readonly Guid Run = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AppendGovernanceAuditEventRequest Intervention(
        string type, string? actor = null, string session = Session) => new()
    {
        SessionId = session,
        RunId = Run,
        Category = GovernanceAuditCategory.Intervention,
        EventType = type,
        Actor = actor,
    };

    private static AppendGovernanceAuditEventRequest Permission(
        string type, string? actor = null, string? detail = null) => new()
    {
        SessionId = Session,
        Category = GovernanceAuditCategory.Permission,
        EventType = type,
        Actor = actor,
        Detail = detail,
    };

    [Fact]
    public void Append_records_a_permission_request_and_server_stamps_it()
    {
        var log = NewLog();
        var before = DateTime.UtcNow;
        var dto = log.Append(Permission(GovernanceAuditEventType.PermissionRequested, detail: "bash"));

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(GovernanceAuditCategory.Permission, dto.Category);
        Assert.Equal(GovernanceAuditEventType.PermissionRequested, dto.EventType);
        Assert.Equal("bash", dto.Detail);
        Assert.True(dto.RecordedUtc >= before);
        Assert.True(dto.OccurredUtc >= before);
    }

    [Fact]
    public void An_event_type_must_belong_to_its_category()
    {
        var log = NewLog();
        // "permission-granted" is a permission type, not an intervention type.
        var ex = Assert.Throws<GovernanceValidationException>(() => log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session,
            Category = GovernanceAuditCategory.Intervention,
            EventType = GovernanceAuditEventType.PermissionGranted,
        }));
        Assert.Contains("not a legal event type for category", ex.Message);
    }

    [Fact]
    public void An_unknown_category_is_rejected()
    {
        var log = NewLog();
        var ex = Assert.Throws<GovernanceValidationException>(() => log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session, Category = "escalation", EventType = "x",
        }));
        Assert.Contains("audit category", ex.Message);
    }

    [Fact]
    public void A_human_or_permission_decision_requires_an_actor()
    {
        var log = NewLog();
        Assert.Throws<GovernanceValidationException>(
            () => log.Append(Intervention(GovernanceAuditEventType.HumanCancelled)));         // no actor
        Assert.Throws<GovernanceValidationException>(
            () => log.Append(Permission(GovernanceAuditEventType.PermissionDenied)));         // no actor

        // An agent-side event does NOT require an actor.
        var needed = log.Append(Intervention(GovernanceAuditEventType.Needed));
        Assert.Null(needed.Actor);
        // With an actor, the human decision is accepted and the actor is recorded.
        var cancelled = log.Append(Intervention(GovernanceAuditEventType.HumanCancelled, actor: "human:soren"));
        Assert.Equal("human:soren", cancelled.Actor);
    }

    [Fact]
    public void An_event_needs_a_session()
    {
        var log = NewLog();
        Assert.Throws<GovernanceValidationException>(() => log.Append(new AppendGovernanceAuditEventRequest
        {
            Category = GovernanceAuditCategory.Permission, EventType = GovernanceAuditEventType.ModeObserved,
        }));
    }

    [Fact]
    public void An_oversize_detail_is_rejected()
    {
        var log = NewLog();
        var ex = Assert.Throws<GovernanceValidationException>(() => log.Append(
            Permission(GovernanceAuditEventType.ModeObserved,
                detail: new string('x', GovernanceAuditLog.MaxDetailChars + 1))));
        Assert.Contains("detail", ex.Message);
    }

    [Fact]
    public void List_filters_by_session_run_category_and_window()
    {
        var log = NewLog();
        var t0 = DateTime.UtcNow.AddHours(-1);
        log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session, RunId = Run, Category = GovernanceAuditCategory.Intervention,
            EventType = GovernanceAuditEventType.Needed, OccurredUtc = t0,
        });
        log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session, Category = GovernanceAuditCategory.Permission,
            EventType = GovernanceAuditEventType.PermissionRequested, Detail = "write", OccurredUtc = t0.AddMinutes(20),
        });
        log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = "other", Category = GovernanceAuditCategory.Permission,
            EventType = GovernanceAuditEventType.ModeObserved, Detail = "acceptEdits", OccurredUtc = t0.AddMinutes(21),
        });

        Assert.Equal(2, log.List(sessionId: Session).Count);
        Assert.Single(log.List(sessionId: Session, category: GovernanceAuditCategory.Permission));
        Assert.Single(log.List(runId: Run));
        Assert.Equal(GovernanceAuditEventType.Needed, log.List(runId: Run)[0].EventType);

        var windowed = log.List(sessionId: Session, sinceUtc: t0.AddMinutes(10), untilUtc: t0.AddMinutes(30));
        Assert.Single(windowed);
        Assert.Equal(GovernanceAuditEventType.PermissionRequested, windowed[0].EventType);
    }

    [Fact]
    public void List_returns_newest_first()
    {
        var log = NewLog();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session, Category = GovernanceAuditCategory.Permission,
            EventType = GovernanceAuditEventType.ElevatedRunStarted, OccurredUtc = t0,
        });
        log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = Session, Category = GovernanceAuditCategory.Permission,
            EventType = GovernanceAuditEventType.ElevatedRunEnded, OccurredUtc = t0.AddMinutes(5),
        });

        var all = log.List(sessionId: Session);
        Assert.Equal(GovernanceAuditEventType.ElevatedRunEnded, all[0].EventType);
        Assert.Equal(GovernanceAuditEventType.ElevatedRunStarted, all[1].EventType);
    }

    [Fact]
    public void AppendBatch_is_all_or_nothing_when_one_entry_is_invalid()
    {
        var log = NewLog();
        Assert.Throws<GovernanceValidationException>(() => log.AppendBatch(new List<AppendGovernanceAuditEventRequest>
        {
            Permission(GovernanceAuditEventType.PermissionRequested, detail: "bash"),
            Permission(GovernanceAuditEventType.PermissionGranted), // invalid - no actor - aborts the batch
        }));
        Assert.Empty(log.List(sessionId: Session));
    }

    [Fact]
    public void AppendBatch_writes_every_valid_event()
    {
        var log = NewLog();
        var written = log.AppendBatch(new List<AppendGovernanceAuditEventRequest>
        {
            Permission(GovernanceAuditEventType.PermissionRequested, detail: "bash"),
            Permission(GovernanceAuditEventType.PermissionGranted, actor: "human:soren"),
            Intervention(GovernanceAuditEventType.Needed),
        });
        Assert.Equal(3, written);
        Assert.Equal(3, log.List(sessionId: Session).Count);
    }

    [Fact]
    public void The_trail_is_tenant_scoped()
    {
        var alpha = new GovernanceAuditLog(_h.Open(new FixedTenantContext(new TenantId("alpha"))));
        var beta = new GovernanceAuditLog(_h.Open(new FixedTenantContext(new TenantId("beta"))));

        alpha.Append(Intervention(GovernanceAuditEventType.Needed, session: "alpha-sess"));
        beta.Append(Intervention(GovernanceAuditEventType.Needed, session: "beta-sess"));

        Assert.Single(alpha.List());
        Assert.Single(beta.List());
        Assert.Equal("alpha-sess", alpha.List()[0].SessionId);
    }
}
