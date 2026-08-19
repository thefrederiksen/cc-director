using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The machine name and the Director version on a history row, and where they come from.
///
/// WHY THIS FILE EXISTS RATHER THAN A CASE IN SessionHistoryRecorderTests. That file's fixture
/// builds a session with <c>MachineName = "SOREN_NORTH"</c>, and every real push has
/// <c>MachineName = ""</c> - <c>ControlEndpoints.Map</c> defaults the parameter to empty and no
/// production caller passes it. So the fixture was more generous than production, the column was
/// null on every row of the live table for every account since it was created, and the tests were
/// green throughout. The sessions below push what the wire actually carries.
///
/// The fix stamps both facts from the Director's CONNECTION record instead, which is why it works
/// for clients already in the field: nothing has to ship and nobody has to upgrade.
/// </summary>
public sealed class SessionHistoryDirectorFactsTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (SessionHistoryRecorder Recorder, SessionHistoryStore Store) New(
        Func<TenantId, string, DirectorFacts>? facts)
    {
        var store = new SessionHistoryStore(_harness.Open());
        return (new SessionHistoryRecorder(store, facts), store);
    }

    /// <summary>A session exactly as the push stream delivers one: no machine name on it.</summary>
    private static SessionDto AsPushed(string id = "s1", string? name = "Build the thing") => new()
    {
        SessionId = id,
        Name = name,
        Number = 200,
        RepoPath = @"D:\repos\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "ClaudeCode",
        MachineName = "",          // <-- what every client in the field actually sends
        CreatedAt = DateTime.UtcNow.AddHours(-2),
        ActivityState = "Working",
        Status = "Running",
    };

    private WorkHistorySessionDto Row(SessionHistoryStore store, string id = "s1")
        => store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))
            .Single(r => r.SessionId == id);

    [Fact]
    public void The_bug_reproduced_without_the_fix_the_machine_name_is_never_recorded()
    {
        // No facts resolver: the pre-fix behaviour, and the reason production had 1,475 rows with
        // no machine name on any of them.
        var (recorder, store) = New(facts: null);

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());

        Assert.Null(Row(store).MachineName);
        Assert.Null(Row(store).DirectorVersion);
    }

    [Fact]
    public void The_machine_name_is_stamped_from_the_connection_when_the_push_omits_it()
    {
        var (recorder, store) = New((_, _) => new DirectorFacts("MARIO-OFFICE-PC", "1.9.11"));

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());

        Assert.Equal("MARIO-OFFICE-PC", Row(store).MachineName);
    }

    [Fact]
    public void The_director_version_is_recorded()
    {
        var (recorder, store) = New((_, _) => new DirectorFacts("MARIO-OFFICE-PC", "1.9.11"));

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());

        Assert.Equal("1.9.11", Row(store).DirectorVersion);
    }

    [Fact]
    public void A_pushed_machine_name_wins_over_the_connection_record()
    {
        // So a future client that fills the field in is believed rather than overridden.
        var (recorder, store) = New((_, _) => new DirectorFacts("FROM-CONNECTION", "2.0.4"));
        var pushed = AsPushed();
        pushed.MachineName = "FROM-THE-PUSH";

        recorder.Observe(TenantId.Local, "dir-1", pushed);

        Assert.Equal("FROM-THE-PUSH", Row(store).MachineName);
        Assert.Equal("2.0.4", Row(store).DirectorVersion);
    }

    [Fact]
    public void Known_facts_are_not_erased_by_a_later_push_that_lost_them()
    {
        // A reconnect race can resolve no Director record. That must not blank a row that already
        // knows where it ran - unknown never overwrites known, the rule the birth facts follow.
        var known = true;
        var (recorder, store) = New((_, _) => known
            ? new DirectorFacts("MARIO-OFFICE-PC", "1.9.11")
            : DirectorFacts.Unknown);

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());
        known = false;
        recorder.Observe(TenantId.Local, "dir-1", AsPushed(name: "Renamed, forcing a write"));

        Assert.Equal("MARIO-OFFICE-PC", Row(store).MachineName);
        Assert.Equal("1.9.11", Row(store).DirectorVersion);
    }

    [Fact]
    public void An_upgrade_mid_session_is_written_without_waiting_for_the_heartbeat()
    {
        // Sessions on this platform run for days - one account's median closed session is 28.9
        // hours. A version stamped at first sight and only revisited on the five-minute freshness
        // heartbeat would still be corrected eventually, but the version is part of the material
        // signature so the correction lands on the very next push instead.
        var version = "1.9.11";
        var (recorder, store) = New((_, _) => new DirectorFacts("MARIO-OFFICE-PC", version));

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());
        Assert.Equal("1.9.11", Row(store).DirectorVersion);

        version = "2.0.4";
        recorder.Observe(TenantId.Local, "dir-1", AsPushed());   // identical session payload

        Assert.Equal("2.0.4", Row(store).DirectorVersion);
    }

    [Fact]
    public void A_throwing_resolver_never_fails_the_push()
    {
        // A history hiccup must never cost a Director its push. The row is still written; it just
        // carries no Director facts.
        var (recorder, store) = New((_, _) => throw new InvalidOperationException("registry down"));

        recorder.Observe(TenantId.Local, "dir-1", AsPushed());

        Assert.Equal("s1", Row(store).SessionId);
        Assert.Null(Row(store).MachineName);
    }

    [Fact]
    public void The_resolver_is_asked_for_the_calling_tenant_and_director()
    {
        // The lookup is tenant-scoped, so one account can never be stamped with another's machine.
        var seen = new List<(string Tenant, string Director)>();
        var (recorder, _) = New((t, d) =>
        {
            seen.Add((t.Value, d));
            return new DirectorFacts("M", "2.0.4");
        });

        recorder.Observe(TenantId.Local, "dir-7", AsPushed());

        Assert.Contains(seen, x => x.Tenant == TenantId.Local.Value && x.Director == "dir-7");
    }
}
