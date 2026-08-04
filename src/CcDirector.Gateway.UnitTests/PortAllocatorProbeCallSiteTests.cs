using System.Net;
using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Call-site cover for the availability probe: the two production decisions in
/// <c>PortAllocator.IsPortFree</c> that this change exists to alter - WHICH listeners may veto a port,
/// and WHICH address the probe then binds.
///
/// These drive the real method with both collaborators stubbed, so each decision is observed rather
/// than inferred, with no operating-system socket involved. Three earlier approaches failed and each
/// one is why this file looks like this:
///
///   * Testing the predicate alone left the CALL SITE free to revert - a review reverted it and every
///     test stayed green.
///   * Binding a real socket could not distinguish the two addresses (Windows lets an all-interfaces
///     bind coexist with a listener holding a specific address) and raced on port reuse, going red at
///     random on correct code. A red that means nothing trains people to ignore reds.
///   * Reading the source was defeated by moving the construction into a helper and leaving the
///     expected text in a comment. A review restored the defect that way and got a full green.
///
/// Capturing the address handed to the bind has none of those problems: it is deterministic, it sees
/// through any spelling or indirection, and it fails when either line goes back.
/// </summary>
public sealed class PortAllocatorProbeCallSiteTests
{
    private const int Port = 7885;

    /// <summary>Records what the probe asked to bind, and whether it asked at all.</summary>
    private sealed class BindSpy
    {
        public IPAddress? Address { get; private set; }
        public bool WasCalled { get; private set; }
        public bool Result { get; init; } = true;

        public bool Bind(IPAddress address, int port)
        {
            WasCalled = true;
            Address = address;
            return Result;
        }
    }

    private static Func<IPEndPoint[]> Listeners(params IPEndPoint[] endpoints) => () => endpoints;

    [Fact]
    public void Bind_line_asks_for_loopback_not_all_interfaces()
    {
        // PINS THE BIND LINE - the one that raises the Windows firewall question and freezes the
        // first-launch setup wizard. Whatever address the probe hands to the bind is the address it
        // would really open. Any all-interfaces form - IPAddress.Any, IPv6Any, an IPEndPoint overload,
        // a call moved into a helper - changes what is captured here and turns this red.
        var spy = new BindSpy();

        var free = PortAllocator.IsPortFree(Port, Listeners(), spy.Bind);

        Assert.True(spy.WasCalled);
        Assert.Equal(IPAddress.Loopback, spy.Address);
        Assert.True(free);
    }

    [Fact]
    public void Scan_line_ignores_a_listener_on_an_ordinary_network_address()
    {
        // PINS THE SCAN LINE, and is the defect itself: a program holding 192.168.1.5 on this port
        // does not block a loopback bind, so the probe must go on to bind. Revert the scan to
        // comparing the port NUMBER alone and it returns before the bind is ever attempted - which
        // both assertions below catch.
        var spy = new BindSpy();
        var listeners = Listeners(new IPEndPoint(IPAddress.Parse("192.168.1.5"), Port));

        var free = PortAllocator.IsPortFree(Port, listeners, spy.Bind);

        Assert.True(spy.WasCalled);
        Assert.True(free);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    public void Scan_line_vetoes_a_listener_that_covers_loopback(string address)
    {
        // The other direction of the same line. A wildcard or loopback listener really does block the
        // bind, so the probe must refuse WITHOUT binding. Narrowing the scan must not go so far that
        // it hands out a port something already holds.
        var spy = new BindSpy();
        var listeners = Listeners(new IPEndPoint(IPAddress.Parse(address), Port));

        var free = PortAllocator.IsPortFree(Port, listeners, spy.Bind);

        Assert.False(free);
        Assert.False(spy.WasCalled);
    }

    [Fact]
    public void A_failing_bind_means_the_port_is_not_free()
    {
        // The scan passing is not the verdict - the bind is. A port nothing is listening on can still
        // refuse a bind (a Windows exclusion, a reservation), and the probe must report that.
        var spy = new BindSpy { Result = false };

        Assert.False(PortAllocator.IsPortFree(Port, Listeners(), spy.Bind));
        Assert.True(spy.WasCalled);
    }
}
