using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 12: the hold state crosses the wire as THREE states, not one boolean, and the boolean
/// <see cref="SessionDto.OnHold"/> is DERIVED from it so the two can never disagree.
///
/// This is the root cause of defect 20 and it is not cosmetic. When the wire carried only the boolean,
/// <c>None</c> and <c>DeferredHold</c> were byte-identical - both false - so nothing downstream could tell
/// "not held" from "about to be held". The Gateway's expiry sweep read that boolean, concluded a deferred
/// snooze was over, and deleted its timer 15 seconds after it was asked for.
/// See docs/new_architecture/session-state.html.
/// </summary>
public sealed class HoldStateWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(HoldStates.None, false)]
    [InlineData(HoldStates.Held, true)]
    [InlineData(HoldStates.DeferredHold, false)]
    public void OnHold_isDerivedFromHoldState_andIsTrueOnlyForHeld(string holdState, bool expectedOnHold)
    {
        // A DeferredHold reads FALSE here, correctly - it is not parked yet. That is exactly why nothing
        // that must distinguish it from None may read this boolean.
        Assert.Equal(expectedOnHold, new SessionDto { HoldState = holdState }.OnHold);
    }

    [Fact]
    public void ADefaultSessionIsNotHeld()
    {
        var s = new SessionDto();
        Assert.Equal(HoldStates.None, s.HoldState);
        Assert.False(s.OnHold);
    }

    [Theory]
    [InlineData(HoldStates.None)]
    [InlineData(HoldStates.Held)]
    [InlineData(HoldStates.DeferredHold)]
    public void AllThreeStatesSurviveAJsonRoundTrip(string holdState)
    {
        // THE REGRESSION: DeferredHold used to arrive at the Gateway as plain false, indistinguishable
        // from None. It must now survive the trip intact.
        var json = JsonSerializer.Serialize(new SessionDto { SessionId = "s", HoldState = holdState }, Web);
        var back = JsonSerializer.Deserialize<SessionDto>(json, Web)!;

        Assert.Equal(holdState, back.HoldState);
    }

    [Fact]
    public void TheDerivedBooleanIsStillOnTheWireForAnOlderClient()
    {
        var json = JsonSerializer.Serialize(new SessionDto { SessionId = "s", HoldState = HoldStates.Held }, Web);

        Assert.Contains("\"onHold\":true", json);
    }

    [Theory]
    [InlineData(true, HoldStates.Held)]
    [InlineData(false, HoldStates.None)]
    public void AnOlderDirectorSendingOnlyTheBooleanStillLands(bool onHold, string expected)
    {
        // Backward compatibility: a payload that predates HoldState carries only the boolean, and the
        // setter maps it onto the tri-state rather than leaving the DTO reading "None" for a held session.
        var json = $$"""{"sessionId":"s","onHold":{{(onHold ? "true" : "false")}}}""";

        var dto = JsonSerializer.Deserialize<SessionDto>(json, Web)!;

        Assert.Equal(expected, dto.HoldState);
        Assert.Equal(onHold, dto.OnHold);
    }

    [Theory]
    // Both fields present, in BOTH orders: whichever the deserializer assigns first, the DTO must land on
    // the state the tri-state names. The OnHold setter is idempotent precisely so this cannot go wrong.
    [InlineData("""{"holdState":"DeferredHold","onHold":false}""", HoldStates.DeferredHold)]
    [InlineData("""{"onHold":false,"holdState":"DeferredHold"}""", HoldStates.DeferredHold)]
    [InlineData("""{"holdState":"Held","onHold":true}""", HoldStates.Held)]
    [InlineData("""{"onHold":true,"holdState":"Held"}""", HoldStates.Held)]
    public void WhenBothFieldsArePresentTheTriStateIsWhatSurvives(string json, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<SessionDto>(json, Web)!.HoldState);
    }

    [Fact]
    public void SettingOnHoldFalseDoesNotDestroyADeferredHold()
    {
        // The snooze expiry overlay writes OnHold=false onto the aggregated copy. A DeferredHold already
        // reads false, so that write must be a no-op against it - not a silent downgrade to None by a
        // writer that only knows about the boolean.
        var s = new SessionDto { HoldState = HoldStates.DeferredHold };

        s.OnHold = false;

        Assert.Equal(HoldStates.DeferredHold, s.HoldState);
    }

    [Fact]
    public void SettingOnHoldFalseReleasesALandedHold()
    {
        // ...and it still does the job it exists for: the expiry overlay flips a genuinely-parked session
        // back to un-held, and the tri-state follows it rather than contradicting it.
        var s = new SessionDto { HoldState = HoldStates.Held };

        s.OnHold = false;

        Assert.Equal(HoldStates.None, s.HoldState);
        Assert.False(s.OnHold);
    }

    [Fact]
    public void CloneCarriesTheHoldState()
    {
        // The Gateway serves deep copies of the pushed cache. A copy that dropped the tri-state would
        // reintroduce the exact loss this defect is about - silently, and only in production.
        var clone = new SessionDto { SessionId = "s", HoldState = HoldStates.DeferredHold }.Clone();

        Assert.Equal(HoldStates.DeferredHold, clone.HoldState);
    }

    [Fact]
    public void AWorkingSessionWithADeferredHoldIsStillBlueAndStillReadsWorking()
    {
        // THE LAW: if a session is working, it is BLUE. Always. "Snooze me when this finishes" does not
        // park anything yet, so it cannot touch the colour, the label, or the bucket.
        var s = new SessionDto { SessionId = "s", ActivityState = "Working", HoldState = HoldStates.DeferredHold };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }
}
