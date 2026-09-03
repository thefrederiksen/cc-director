using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// WHO THE PROMOTION GRANT WILL NAME, AND WHO IT REFUSES ON THE CREDENTIAL ITSELF (fix round D, rulings D5
/// and D11). These tests construct the request exactly as the Gateway's own middleware marks one - the
/// middleware's constants, not strings of this file's own - and ask the grant directly, so they do not
/// depend on the route guard being right in order to pass.
///
/// TWO THINGS WERE FOUND WHILE WRITING THEM, and both are the reason the helper now uses the middleware's
/// constants:
///
///  - The grant read an item named "DeviceKeyId" that nothing in the Gateway ever wrote, and the
///    middleware never sets a principal. So every real device-key request reaching the promote route
///    was refused as having no caller - the Cockpit's "Make it live" could not have worked - while the
///    unit tests, whose helper set the same made-up item, were green. The grant now reads the device
///    identity the middleware actually leaves on the request.
///  - A session key reaching the grant was likewise "nobody" and refused by accident. Ruling D11 wants it
///    refused ON PURPOSE, with its own sentence, so that when the route guard is the only other thing
///    standing there, the refusal at the destination still holds.
/// </summary>
public sealed class RulePromotionGrantCallerTests
{
    private static readonly Guid TheRule = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
    private const string Said = "I have read this rule's dry-run record: 2 firings. I am making it live.";

    /// <summary>A request the device-key middleware authenticated is named after THAT device - the
    /// identity the middleware leaves on the request, read by the middleware's own constant.</summary>
    [Fact]
    public void A_request_the_device_middleware_authenticated_is_named_after_the_device()
    {
        var grant = RulePromotionGrant.FromAuthenticatedRequest(TheRule, AnInboundRequest.FromDevice("dev-ca"), Said, Now);

        Assert.Equal("dev-ca", grant.Actor);
        Assert.Equal(Said, grant.Acknowledgement);
        Assert.Equal(TheRule, grant.RuleId);
    }

    /// <summary>
    /// A SESSION KEY IS REFUSED AT THE GRANT, ON THE CREDENTIAL, WITH ITS OWN SENTENCE (ruling D11). The
    /// route guard already refuses the promote route to a session key; this is the second, deliberately
    /// redundant check at the destination, and it is asserted here WITHOUT the route guard in the path -
    /// a promotion attempt is constructed carrying a session-key identity and handed straight to the
    /// grant. Whether the guard is right has no bearing on whether this passes.
    /// </summary>
    [Fact]
    public void A_request_a_session_key_authenticated_is_refused_on_the_credential_itself()
    {
        var ex = Assert.Throws<RuleRejectedException>(() =>
            RulePromotionGrant.FromAuthenticatedRequest(TheRule, AnInboundRequest.FromSessionKey(), Said, Now));

        Assert.Contains("session key", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("person", ex.Reason, StringComparison.Ordinal);
        // And it is NOT the "nobody" sentence: an agent has to be told it is refused because of what it
        // is, not sent hunting a credential problem that does not exist.
        Assert.DoesNotContain("no caller the Gateway could name", ex.Reason, StringComparison.Ordinal);
    }

    /// <summary>A request carrying BOTH a session identity and a device identity - which the middleware
    /// never produces, but a future change could - is still refused: the session key wins, because the
    /// refusal is the thing that must hold.</summary>
    [Fact]
    public void A_request_carrying_a_session_key_beside_a_device_identity_is_still_refused()
    {
        var http = AnInboundRequest.FromDevice("dev-ca");
        var session = AnInboundRequest.FromSessionKey();
        foreach (var item in session.Items) http.Items[item.Key] = item.Value;

        var ex = Assert.Throws<RuleRejectedException>(() =>
            RulePromotionGrant.FromAuthenticatedRequest(TheRule, http, Said, Now));

        Assert.Contains("session key", ex.Reason, StringComparison.Ordinal);
    }

    /// <summary>A request the pipeline could not name is refused, as before.</summary>
    [Fact]
    public void A_request_the_pipeline_could_not_name_is_refused()
    {
        var ex = Assert.Throws<RuleRejectedException>(() =>
            RulePromotionGrant.FromAuthenticatedRequest(TheRule, AnInboundRequest.FromNobody(), Said, Now));

        Assert.Contains("no caller the Gateway could name", ex.Reason, StringComparison.Ordinal);
    }

    /// <summary>A signed-in principal, as the account routes see one, is still named.</summary>
    [Fact]
    public void A_signed_in_person_is_named()
    {
        var grant = RulePromotionGrant.FromAuthenticatedRequest(
            TheRule, AnInboundRequest.FromSignedInPerson("soren"), Said, Now);

        Assert.Equal("soren", grant.Actor);
    }
}
