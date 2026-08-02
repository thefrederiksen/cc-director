using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.Tests.Instances;

/// <summary>
/// Tests for <see cref="DirectorHandle"/> - the shared rule for what NAMES a Director, used by the
/// toolbar that hands a handle out, the Director floor that decides whether a named target is itself,
/// and (in the same shape) the Gateway resolve that finds it in the registry.
///
/// The test that matters most is the round trip: the Copy button's text has to carry a handle the
/// matcher accepts. If those two drift the paste fails on the FAR side of the fleet, in someone else's
/// session, where whoever pressed Copy will never see it.
/// </summary>
public sealed class DirectorHandleTests
{
    private const string Id = "6f0a2b41-1c33-4f9e-9a10-2b7d5e8c1234";

    private const string OtherId = "11111111-2222-3333-4444-555555555555";

    private sealed record Dir(string DirectorId, string DisplayName);

    private static List<Dir> Pick(string? token, params Dir[] fleet)
        => DirectorHandle.Pick(fleet, token, d => d.DirectorId, d => d.DisplayName);

    [Fact]
    public void MatchesId_isCaseInsensitive_andIgnoresSurroundingSpace()
    {
        Assert.True(DirectorHandle.MatchesId(Id.ToUpperInvariant(), Id));
        Assert.True(DirectorHandle.MatchesId($"  {Id}  ", Id));
        Assert.False(DirectorHandle.MatchesId(OtherId, Id));
    }

    [Fact]
    public void MatchesDisplayName_isCaseInsensitive()
    {
        Assert.True(DirectorHandle.MatchesDisplayName("north BUILD", "North build"));
        Assert.False(DirectorHandle.MatchesDisplayName("North daily", "North build"));
    }

    // A blank token is the ABSENCE of a target, not a wildcard. Were it a match, every ordinary spawn -
    // which names no Director at all - would be claimed by whichever Director asked itself first, and
    // --machine routing would quietly stop working.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankToken_namesNothing(string? token)
    {
        Assert.False(DirectorHandle.MatchesId(token, Id));
        Assert.False(DirectorHandle.MatchesDisplayName(token, "North build"));
        Assert.Empty(Pick(token, new Dir(Id, "North build")));
    }

    // An instance with no display name must not match on its empty name, or EVERY unnamed Director in
    // the fleet would answer to the same blank handle.
    [Fact]
    public void DoesNotMatchAnEmptyDisplayName()
        => Assert.False(DirectorHandle.MatchesDisplayName("   x   ", displayName: ""));

    [Fact]
    public void Pick_findsByIdAndByDisplayName()
    {
        var fleet = new[] { new Dir(Id, "North build"), new Dir(OtherId, "North daily") };

        Assert.Equal(Id, Pick(Id, fleet).Single().DirectorId);
        Assert.Equal(OtherId, Pick("north DAILY", fleet).Single().DirectorId);
        Assert.Empty(Pick("North experiments", fleet));
    }

    [Fact]
    public void Pick_returnsEveryDirectorSharingADisplayName_soTheCallerCanRefuse()
        => Assert.Equal(2, Pick("Build box", new Dir(Id, "Build box"), new Dir(OtherId, "Build box")).Count);

    // ID PRECEDENCE. A display name is free text, so one Director can be named the literal id of
    // another - by accident or to hijack it. With equal-rank matching, a request carrying A's id comes
    // back ambiguous (or resolves to B), which destroys the one guarantee the id exists to give: that
    // it cannot collide. An exact id match must win outright, with display names never consulted.
    [Fact]
    public void Pick_anIdMatchWins_evenWhenAnotherDirectorIsNamedThatId()
    {
        var impostor = new Dir(OtherId, Id);          // B's display name IS A's id
        var owner = new Dir(Id, "North build");

        var picked = Pick(Id, impostor, owner);       // the impostor is listed FIRST

        Assert.Equal(Id, Assert.Single(picked).DirectorId);
    }

    [Fact]
    public void Label_prefersTheDisplayName()
        => Assert.Equal("North build", DirectorHandle.Label("North build", "SOREN_NORTH"));

    [Fact]
    public void Label_fallsBackToTheMachineName_whenTheInstanceIsUnnamed()
    {
        Assert.Equal("SOREN_NORTH", DirectorHandle.Label(null, "SOREN_NORTH"));
        Assert.Equal("SOREN_NORTH", DirectorHandle.Label("   ", "SOREN_NORTH"));
    }

    [Fact]
    public void Label_neverRendersBlank()
        => Assert.False(string.IsNullOrWhiteSpace(DirectorHandle.Label(null, null)));

    // The Control API port is gone from the label on purpose (nothing dials a Director by port). A test
    // rather than a comment, because the old format is the thing a future edit would restore by reflex.
    [Fact]
    public void Label_carriesNoPortNumber()
        => Assert.DoesNotContain(":", DirectorHandle.Label("North build", "SOREN_NORTH"));

    [Fact]
    public void Identity_statesTheName_theId_andTheMachine()
    {
        var text = DirectorHandle.Identity("North build", Id, "SOREN_NORTH");

        Assert.Equal(
            "Director: North build\n"
          + $"Director ID: {Id}\n"
          + "Machine: SOREN_NORTH", text);
    }

    // THE ROUND TRIP. The id in the clipboard text must be one the matcher accepts - being pasted
    // somewhere that resolves it is the only thing this text is for. A copy that hands out an id the
    // resolver rejects fails on the FAR side of the fleet, in someone else's session.
    [Fact]
    public void Identity_carriesAnIdThatMatchesBack()
    {
        var text = DirectorHandle.Identity("North build", Id, "SOREN_NORTH");

        var line = text.Split('\n').Single(l => l.StartsWith("Director ID:", StringComparison.Ordinal));
        var handed = line["Director ID:".Length..].Trim();

        Assert.Equal(Id, Pick(handed, new Dir(Id, "North build")).Single().DirectorId);
    }

    // It states facts and stops. What to DO with this Director is the pasting person's instruction, and
    // a command baked in here could not be run as pasted anyway - the Director cannot know which
    // repository is meant.
    [Fact]
    public void Identity_isNotACommand()
    {
        var text = DirectorHandle.Identity("North build", Id, "SOREN_NORTH");

        Assert.DoesNotContain("cc-devthrottle", text);
        Assert.DoesNotContain("--director", text);
        Assert.Equal(3, text.Split('\n').Length);
    }

    // An unnamed instance still identifies itself: the label falls back to the machine, and the id -
    // the line that actually addresses it - is unaffected either way.
    [Fact]
    public void Identity_worksForAnUnnamedInstance()
    {
        var text = DirectorHandle.Identity(null, Id, "SOREN_NORTH");

        Assert.Contains("Director: SOREN_NORTH", text);
        Assert.Contains($"Director ID: {Id}", text);
    }
}
