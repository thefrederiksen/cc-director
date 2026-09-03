using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE PRODUCTION SCREEN READ, WITH THE PRODUCTION CODE IN THE PATH (fix round E, ruling E3). Inspection E
/// found that every hosted test substituted the whole reader, so the join that establishes provenance -
/// the caller's tenant, the roster locate, the Director route, the tunnel read, the roster-owned origin -
/// was exercised by nothing. These tests keep <see cref="GatewayRuleScreenReader"/> in the path and fake
/// only the two seams beneath it, and they observe the FAR SIDE: the exact rows, the origin off the
/// roster row, and each refusal with the authoring call never made.
///
/// Presence controls come first. A reader that refused everything would pass every refusal test below.
/// </summary>
public sealed class GatewayRuleScreenReaderTests
{
    private static readonly TenantId TenantA = new("tenant-a-reader");
    private static readonly TenantId TenantB = new("tenant-b-reader");
    private const string SessionA = "sess-a";
    private const string DirectorA = "director-a";

    private static readonly string[] TheRows = { "> carry on with the refactor  ", "", "Claude usage limit reached. Your limit will reset at 11:50pm.", "", ">" };

    private static SessionDto RowA() => new() { SessionId = SessionA, Agent = "ClaudeCode", MachineName = "SOREN_NORTH", DirectorId = DirectorA };

    /// <summary>A roster on which tenant A owns session A and nothing else exists.</summary>
    private static GatewayRuleScreenReader.LocateSession RosterWithOnlyA() =>
        (tenant, sid) => tenant == TenantA && sid == SessionA ? (DirectorA, RowA()) : null;

    private static GatewayRuleScreenReader.ReadRows Rows(IReadOnlyList<string>? rows) =>
        (_, _, _, _) => Task.FromResult(rows);

    /// <summary>An author over the reader whose model call is counted, so a refusal can assert the model
    /// was never asked.</summary>
    private static (RuleAuthor Author, Func<int> Asked) AuthorOver(GatewayRuleScreenReader reader)
    {
        var asked = 0;
        var author = new RuleAuthor(
            (_, _, _) => { asked++; return Task.FromResult<string?>(null); },
            reader.ReadAsync);
        return (author, () => asked);
    }

    private static IReadOnlyList<RuleDraftTurn> Said() =>
        new[] { new RuleDraftTurn(RuleDraftSpeakers.Person, "when the limit hits, wait and carry on") };

    // ---- presence first -----------------------------------------------------------------------------

    [Fact]
    public async Task An_owned_session_reads_as_its_exact_rows_with_the_origin_taken_from_the_roster_row()
    {
        var reader = new GatewayRuleScreenReader(RosterWithOnlyA(), Rows(TheRows));

        var result = await reader.ReadAsync(TenantA, SessionA, CancellationToken.None);

        Assert.Null(result.Refusal);
        var screen = Assert.IsType<RuleScreenReading>(result.Screen);
        Assert.Equal(SessionA, screen.SessionId);
        // THE EXACT ROWS, trailing space trimmed, blank rows dropped by the excerpt - and nothing else.
        Assert.Equal(
            "> carry on with the refactor\nClaude usage limit reached. Your limit will reset at 11:50pm.\n>",
            screen.Excerpt);
        // THE ORIGIN IS THE ROSTER'S, not anything a caller said.
        Assert.Equal(new RuleSessionOrigin("ClaudeCode", "SOREN_NORTH"), screen.Origin);
    }

    [Fact]
    public async Task The_read_is_made_for_the_director_the_roster_named_in_the_callers_tenant()
    {
        (TenantId, string, string)? readFor = null;
        var reader = new GatewayRuleScreenReader(RosterWithOnlyA(), (tenant, director, sid, _) =>
        {
            readFor = (tenant, director, sid);
            return Task.FromResult<IReadOnlyList<string>?>(TheRows);
        });

        await reader.ReadAsync(TenantA, SessionA, CancellationToken.None);

        Assert.Equal((TenantA, DirectorA, SessionA), readFor);
    }

    // ---- the refusals, each with the authoring call never made -----------------------------------------

    /// <summary>Tenant B names tenant A's session: the roster is looked up as tenant B, finds nothing, and
    /// the model is never asked.</summary>
    [Fact]
    public async Task A_second_tenants_session_is_refused_and_the_model_is_never_asked()
    {
        var (author, asked) = AuthorOver(new GatewayRuleScreenReader(RosterWithOnlyA(), Rows(TheRows)));

        var reading = await author.DraftAsync(TenantB, Said(), SessionA, false, CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains($"session {SessionA} is not on this account's roster", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked());
    }

    [Fact]
    public async Task A_session_that_is_not_on_the_roster_is_refused_and_the_model_is_never_asked()
    {
        var (author, asked) = AuthorOver(new GatewayRuleScreenReader(RosterWithOnlyA(), Rows(TheRows)));

        var reading = await author.DraftAsync(TenantA, Said(), "sess-nobody", false, CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains("sess-nobody is not on this account's roster", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked());
    }

    /// <summary>
    /// THE DIRECTOR VANISHED BETWEEN THE LOCATE AND THE READ. The roster still names it, the read answers
    /// nothing at all, and that is a FAILURE with its own sentence - never an empty screen, which is a
    /// state a rule could be authored against.
    /// </summary>
    [Fact]
    public async Task A_director_that_vanished_between_locate_and_read_is_refused_with_its_own_sentence_never_as_an_empty_screen()
    {
        var reader = new GatewayRuleScreenReader(RosterWithOnlyA(), Rows(null));
        var (author, asked) = AuthorOver(reader);

        var result = await reader.ReadAsync(TenantA, SessionA, CancellationToken.None);
        var reading = await author.DraftAsync(TenantA, Said(), SessionA, false, CancellationToken.None);

        Assert.Null(result.Screen);
        Assert.Contains("could not be read", result.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("empty", result.Refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", reading.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("empty screen", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked());
    }

    /// <summary>And the other one: the Director answered, and the screen is blank. A reading, which the
    /// author refuses as an empty screen - a different sentence from the failure above.</summary>
    [Fact]
    public async Task A_screen_that_reads_empty_is_refused_as_an_empty_screen_and_the_model_is_never_asked()
    {
        var reader = new GatewayRuleScreenReader(RosterWithOnlyA(), Rows(new[] { "", "   ", "" }));
        var (author, asked) = AuthorOver(reader);

        var result = await reader.ReadAsync(TenantA, SessionA, CancellationToken.None);
        var reading = await author.DraftAsync(TenantA, Said(), SessionA, false, CancellationToken.None);

        Assert.NotNull(result.Screen);
        Assert.Equal("", result.Screen!.Excerpt);
        Assert.Contains("empty screen", reading.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked());
    }
}
