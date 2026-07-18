using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The "Dumb Clients" palette slice: the Gateway stamps the dot's pixel HEX beside the colour NAME in the
/// same fold, resolved through the ONE canonical map (<see cref="SessionColorPalette"/>). Every client
/// paints that stamp; none keeps its own name-&gt;hex table that can drift.
///
/// These drive the REAL fold (<c>StampFleetRolesAndFold</c> - the same method the /sessions roster and the
/// desktop display-state push both fold through), not a hand-stamped DTO, so they cannot pass on a value
/// production never puts there. Revert the stamp line in StampFleetRolesAndFold and
/// <see cref="TheFoldStampsTheCanonicalHexForTheFoldedColour"/> goes red with a null hex - which is the web
/// session dot painting the magenta sentinel instead of the real colour.
/// </summary>
public sealed class EffectiveColorHexStampTests
{
    private static SessionDto Fold(SessionDto s)
    {
        var list = new List<SessionDto> { s };
        GatewayEndpoints.StampFleetRolesAndFold(list, list);
        return s;
    }

    [Theory]
    [InlineData("Working", "blue", "#3B82F6")]
    [InlineData("Starting", "blue", "#3B82F6")]
    [InlineData("WaitingForInput", "red", "#EF4444")]
    [InlineData("Idle", "red", "#EF4444")]
    public void TheFoldStampsTheCanonicalHexForTheFoldedColour(string activityState, string expectedColor, string expectedHex)
    {
        var s = Fold(new SessionDto { SessionId = "s", ActivityState = activityState });

        Assert.Equal(expectedColor, s.EffectiveColor);
        Assert.Equal(expectedHex, s.EffectiveColorHex);
        // The hex is exactly the canonical map's answer for whatever colour the fold chose, so the stamped
        // hex and the stamped name can never disagree.
        Assert.Equal(SessionColorPalette.HexFor(s.EffectiveColor), s.EffectiveColorHex);
    }

    [Fact]
    public void ASnoozedSession_StampsTheOneGrey()
    {
        var s = Fold(new SessionDto { SessionId = "s", ActivityState = "WaitingForInput", OnHold = true });

        Assert.Equal("grey", s.EffectiveColor);
        Assert.Equal(SessionColorPalette.Grey, s.EffectiveColorHex);
    }

    [Fact]
    public void ACrashedSession_StampsTheDeepRed_NotNeedsYouRed()
    {
        var s = Fold(new SessionDto { SessionId = "s", ActivityState = "Exited", Crashed = true });

        Assert.Equal("error", s.EffectiveColor);
        Assert.Equal(SessionColorPalette.Error, s.EffectiveColorHex);
        // Issue #959: a session that DIED must never paint the bright needs-you red.
        Assert.NotEqual(SessionColorPalette.Red, s.EffectiveColorHex);
    }
}
