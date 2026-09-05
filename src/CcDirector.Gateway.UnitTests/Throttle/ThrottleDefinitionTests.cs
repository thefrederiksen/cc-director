using CcDirector.Gateway.Throttle;
using Xunit;
using static CcDirector.Gateway.Throttle.ThrottleDefinition;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// The guard for ruling R17 of the "Clean up Your Throttle" mission (2026-09-05): the ledger predicate is
/// stated EXACTLY, once, and its three consequences are true in the code. These run the pure fold, so
/// nothing about a database can excuse a wrong count.
///
/// The three consequences, each with a row shaped exactly like the ledger records it:
///   1. a turn typed at the desktop terminal (null SendSource, present InputOrigin) is IN;
///   2. agent traffic (Agent SendSource, no InputOrigin) is OUT by record;
///   3. a submission with no InputOrigin is OUT and disclosed as a count beside the share.
/// </summary>
public sealed class ThrottleDefinitionTests
{
    private static readonly DateTime From = new(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, SessionFacts> NoSessions = new Dictionary<string, SessionFacts>();

    private static LedgerSubmission Row(string? origin, string? source, string session = "s1", string? agent = "ClaudeCode",
        int hoursIn = 1)
        => new(From.AddHours(hoursIn), session, agent, origin, source);

    [Fact]
    public void ThePredicateIsStatedExactlyAsRulingR17StatesIt()
    {
        // Verbatim. A paraphrase here is a paraphrase on the page and in the mentor report, and the ruling
        // says phase three does not get to paraphrase it.
        Assert.Equal(
            "The shared figure is computed over activity_events rows where EventType is turn-submitted and " +
            "InputOrigin is present, grouped by the origin's modality and surface.",
            Predicate);
        Assert.Equal("turn-submitted", TurnSubmitted);
        Assert.Equal("submitted turns", Unit);
        Assert.Equal(30, RetentionDays);
    }

    [Fact]
    public void ATurnTypedAtTheTerminal_NullSendSourceWithAPresentOrigin_IsIn()
    {
        // Consequence 1 - the 594. The raw-byte terminal path carries no SendSource at all.
        var figure = Fold(new[] { Row("typed/desktop", source: null) }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(1, figure.TypedTurns);
        var bucket = Assert.Single(figure.Buckets);
        Assert.Equal("typed", bucket.Modality);
        Assert.Equal("desktop", bucket.Surface);
        Assert.Equal(0, figure.Excluded.NoInputOrigin);
    }

    [Fact]
    public void AgentTraffic_IsOutByRecord_AndReportedBesideTheFigure()
    {
        // Consequence 2. Stamped Agent by R12, no origin. Out of the human figure, counted as the fleet
        // driving itself, attributed to the agent running the session it went into.
        var figure = Fold(new[]
        {
            Row("voice/desktop", "UserInput", agent: "Codex"),
            Row(null, AgentSendSource, agent: "Codex"),
            Row(null, AgentSendSource, agent: "Codex"),
        }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(2, figure.AgentDrivenTurns);
        Assert.Equal(2, figure.Excluded.AgentDriven);
        Assert.Equal(0, figure.Excluded.Unresolved);
        var codex = Assert.Single(figure.Agents);
        Assert.Equal("Codex", codex.Agent);
        Assert.Equal(1, codex.Turns);
        Assert.Equal(2, codex.AgentDrivenTurns);
    }

    [Fact]
    public void ASubmissionWithNoOrigin_IsOut_AndDisclosedAsACountBesideTheShare()
    {
        // Consequence 3 - the 502. A person's submission the product could not place, plus the framework's
        // own seed prompt: neither is a bucket, both are counted in the disclosure, and only the person's is
        // "unresolved".
        var figure = Fold(new[]
        {
            Row("voice/phone", "Delivery"),
            Row(null, "UserInput"),
            Row(null, "UserInput"),
            Row(null, FrameworkSendSource),
            Row(null, null),
        }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(4, figure.Excluded.NoInputOrigin);
        Assert.Equal(1, figure.Excluded.Framework);
        Assert.Equal(0, figure.Excluded.AgentDriven);
        // UserInput x2 and the honest nothing-at-all row: a person's submissions nobody could place.
        Assert.Equal(3, figure.Excluded.Unresolved);
        Assert.Single(figure.Buckets);
    }

    [Fact]
    public void MembershipIsDecidedByTheOriginAlone_NeverByTheSendSource()
    {
        // Every send source with an origin is in; every send source without one is out. If a future edit
        // starts reading SendSource for membership, one of these eight rows moves and this fails.
        var rows = new List<LedgerSubmission>();
        foreach (var source in new[] { null, "UserInput", "Delivery", "Agent", "Framework" })
        {
            rows.Add(Row("typed/desktop", source));
            rows.Add(Row(null, source));
        }
        var figure = Fold(rows, From, To, NoSessions);

        Assert.Equal(5, figure.Turns);
        Assert.Equal(5, figure.Excluded.NoInputOrigin);
    }

    [Fact]
    public void TheUnknownSurface_StaysItsOwnBucket_AndIsStillACountedTurn()
    {
        var figure = Fold(new[] { Row("typed/unknown", "UserInput") }, From, To, NoSessions);
        Assert.Equal(1, figure.Turns);
        Assert.Equal("unknown", Assert.Single(figure.Buckets).Surface);
    }

    [Fact]
    public void RowsOutsideTheWindow_AreIgnored_AndTheWindowIsHalfOpen()
    {
        var rows = new[]
        {
            new LedgerSubmission(From.AddSeconds(-1), "s1", "ClaudeCode", "typed/desktop", null),   // before
            new LedgerSubmission(From, "s1", "ClaudeCode", "typed/desktop", null),                 // at the start: in
            new LedgerSubmission(To.AddSeconds(-1), "s1", "ClaudeCode", "typed/desktop", null),    // just before the end: in
            new LedgerSubmission(To, "s1", "ClaudeCode", "typed/desktop", null),                   // at the end: out
        };
        var figure = Fold(rows, From, To, NoSessions);
        Assert.Equal(2, figure.Turns);
    }

    [Fact]
    public void TheHourlySeries_KeysByUtcClockHour_AndSplitsVoiceOverTyped()
    {
        var figure = Fold(new[]
        {
            new LedgerSubmission(new DateTime(2026, 8, 24, 13, 5, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "voice/desktop", "UserInput"),
            new LedgerSubmission(new DateTime(2026, 8, 24, 13, 59, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "typed/desktop", null),
            new LedgerSubmission(new DateTime(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "typed/desktop", null),
        }, From, To, NoSessions);

        Assert.Equal(2, figure.HourlyTurns.Count);
        Assert.Equal("2026-08-24T13", figure.HourlyTurns[0].Hour);
        Assert.Equal(2, figure.HourlyTurns[0].Turns);
        Assert.Equal(1, figure.HourlyTurns[0].VoiceTurns);
        Assert.Equal(1, figure.HourlyTurns[0].TypedTurns);
        Assert.Equal("2026-08-24T14", figure.HourlyTurns[1].Hour);
    }

    [Fact]
    public void TheRepositorySplit_JoinsThroughSessionHistory_AndDisclosesWhatItCannotPlace()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["named"] = new("thefrederiksen/devthrottle", @"D:\ReposFred\devthrottle-throttle"),
            ["named-2"] = new("thefrederiksen/devthrottle", @"D:\ReposFred\devthrottle"),
            ["path-only"] = new(null, @"D:\ReposFred\mindzieWeb"),
            ["empty"] = new(null, null),
        };
        var figure = Fold(new[]
        {
            Row("typed/desktop", null, session: "named"),
            Row("voice/desktop", "UserInput", session: "named"),
            Row("typed/desktop", null, session: "named-2"),
            Row("typed/desktop", null, session: "path-only"),
            Row("typed/desktop", null, session: "empty"),
            Row("typed/desktop", null, session: "not-in-history"),
        }, From, To, sessions);

        Assert.Equal(6, figure.Turns);
        Assert.Equal(2, figure.Repos.Count);
        var devthrottle = figure.Repos[0];
        Assert.Equal("thefrederiksen/devthrottle", devthrottle.Repo);
        Assert.Equal("devthrottle", devthrottle.RepoName);
        Assert.Equal(3, devthrottle.Turns);
        Assert.Equal(1, devthrottle.VoiceTurns);
        Assert.Equal(2, devthrottle.Sessions);
        Assert.Equal(new[] { @"D:\ReposFred\devthrottle", @"D:\ReposFred\devthrottle-throttle" }, devthrottle.Checkouts);
        var mindzie = figure.Repos[1];
        Assert.Equal("mindzieWeb", mindzie.Repo);
        Assert.Equal("mindzieWeb", mindzie.RepoName);
        // "empty" and "not-in-history" are disclosed, never guessed into a row (R7).
        Assert.Equal(2, figure.ReposUnattributedTurns);
    }

    [Fact]
    public void TheAgentSplit_CountsDistinctSessions_AndRanksMostDrivenFirst()
    {
        var figure = Fold(new[]
        {
            Row("typed/desktop", null, session: "a", agent: "Codex"),
            Row("typed/desktop", null, session: "b", agent: "ClaudeCode"),
            Row("voice/desktop", "UserInput", session: "b", agent: "ClaudeCode"),
            Row("voice/desktop", "UserInput", session: "c", agent: null),
        }, From, To, NoSessions);

        Assert.Equal(3, figure.Sessions);
        Assert.Equal(3, figure.Agents.Count);
        Assert.Equal("ClaudeCode", figure.Agents[0].Agent);
        Assert.Equal("Claude Code", figure.Agents[0].AgentName);
        Assert.Equal(2, figure.Agents[0].Turns);
        Assert.Equal(1, figure.Agents[0].Sessions);
        Assert.Contains(figure.Agents, a => a.Agent == "" && a.AgentName == "(unknown)" && a.Turns == 1);
    }

    [Fact]
    public void AMalformedOrigin_RefusesTheFigure_RatherThanGuessingABucket()
    {
        // The mentor harness's reader exits on the same row, so both consumers fail the same way.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Fold(new[] { Row("desktop", "UserInput") }, From, To, NoSessions));
        Assert.Contains("not '<modality>/<surface>'", ex.Message);

        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            Fold(new[] { Row("spoken/desktop", "UserInput") }, From, To, NoSessions));
        Assert.Contains("neither typed nor voice", ex2.Message);
    }

    [Fact]
    public void TheFigureCarriesItsDefinitionAndUnit_SoAReaderCanCheckTheNumberAgainstTheSentence()
    {
        var figure = Fold(Array.Empty<LedgerSubmission>(), From, To, NoSessions);
        Assert.Equal(Predicate, figure.Definition);
        Assert.Equal(Unit, figure.Unit);
        Assert.Equal(From, figure.Window.FromUtc);
        Assert.Equal(To, figure.Window.ToUtc);
        Assert.Equal(0, figure.Turns);
        Assert.Empty(figure.Buckets);
    }

    [Fact]
    public void TheMeasuredWeek_ReproducesPhaseOnesLedgerFigure()
    {
        // The shape of 2026-W35 from reconciliation.md, population B: 1,015 voice and 771 typed carrying an
        // origin (56.83 per cent spoken), 502 UserInput rows with no origin, 160 Framework rows. The fold
        // over rows of that shape must land on exactly those counts - the number the mentor report was
        // reconciled against.
        var rows = new List<LedgerSubmission>();
        void Add(int n, string? origin, string? source) { for (var i = 0; i < n; i++) rows.Add(Row(origin, source, hoursIn: 1 + (i % 100))); }
        Add(835, "voice/desktop", "UserInput");
        Add(591, "typed/desktop", null);
        Add(3, "typed/phone", null);
        Add(502, null, "UserInput");
        Add(180, "voice/phone", "Delivery");
        Add(105, "typed/desktop", "UserInput");
        Add(65, "typed/phone", "UserInput");
        Add(7, "typed/unknown", "UserInput");
        Add(160, null, FrameworkSendSource);

        var figure = Fold(rows, From, To, NoSessions);

        Assert.Equal(1786, figure.Turns);
        Assert.Equal(1015, figure.VoiceTurns);
        Assert.Equal(771, figure.TypedTurns);
        Assert.Equal(0.5683, Math.Round((double)figure.VoiceTurns / figure.Turns, 4));
        Assert.Equal(502, figure.Excluded.Unresolved);
        Assert.Equal(160, figure.Excluded.Framework);
        Assert.Equal(662, figure.Excluded.NoInputOrigin);
    }
}
