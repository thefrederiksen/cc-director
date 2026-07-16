using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Locks the routing rule for <c>POST /fleet/hold</c> - the exact decision whose old value made
/// <c>cc-devthrottle session hold</c> (an agent holding ITSELF) silently fail.
///
/// The Gateway owns hold: only its SnoozeRegistry defers a hold while the turn runs, lands it when the
/// work settles, and expires it on the clock. Before the fix, a LOCAL session (which a self-hold always
/// is) was short-circuited to a mirror-only write on the Director and never reached the registry, so the
/// hold never landed and evaporated on the next roster fold. These assertions fail against that old rule
/// (local + gateway -> LocalMirror) and pass against the fix (local + gateway -> Gateway).
/// </summary>
public sealed class FleetHoldRouteTests
{
    [Fact]
    public void LocalSession_WithGateway_RoutesToGateway_notTheMirror()
    {
        // The headline case: a session holds itself while a Gateway is connected. It MUST reach the
        // Gateway's registry, not stop at the Director's local rail mirror.
        var route = ControlEndpoints.ChooseHoldRoute(gatewayEnabled: true, sessionIsLocal: true);
        Assert.Equal(ControlEndpoints.HoldRoute.Gateway, route);
    }

    [Fact]
    public void RemoteSession_WithGateway_RoutesToGateway()
    {
        // A manager holding a worker on another Director already worked; it stays routed to the Gateway.
        var route = ControlEndpoints.ChooseHoldRoute(gatewayEnabled: true, sessionIsLocal: false);
        Assert.Equal(ControlEndpoints.HoldRoute.Gateway, route);
    }

    [Fact]
    public void LocalSession_NoGateway_WritesLocalMirror()
    {
        // Standalone (no Gateway): the local mirror is the only owner there is, so the hold is written here.
        var route = ControlEndpoints.ChooseHoldRoute(gatewayEnabled: false, sessionIsLocal: true);
        Assert.Equal(ControlEndpoints.HoldRoute.LocalMirror, route);
    }

    [Fact]
    public void UnknownSession_NoGateway_IsNotFound()
    {
        // No local session and no Gateway to ask -> nothing can own the hold -> 404.
        var route = ControlEndpoints.ChooseHoldRoute(gatewayEnabled: false, sessionIsLocal: false);
        Assert.Equal(ControlEndpoints.HoldRoute.NotFound, route);
    }
}
