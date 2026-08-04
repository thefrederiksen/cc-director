using System.Net;
using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The port availability probe must ask about LOOPBACK, because loopback is what ControlApiHost
/// binds in every addressing mode. Two defects came from asking a wider question.
///
/// The bind half opened each candidate port on ALL interfaces before throwing it away, which made
/// Windows raise the firewall question against cc-director.exe - a dialog that lands on top of the
/// first-launch setup wizard and silently eats the clicks aimed at it. That half is a socket call
/// and is proved by a clean-machine install, not by a unit test.
///
/// The scan half is pure and is what these tests pin: an existing listener may only veto a port when
/// it would genuinely block a bind to 127.0.0.1. Rejecting on the port NUMBER alone wrote off ports
/// held by unrelated programs on ordinary network addresses, out of a range of only twenty.
/// </summary>
public sealed class PortAllocatorLoopbackProbeTests
{
    private const int Port = 7885;

    [Fact]
    public void ConflictsWithLoopbackBind_LoopbackListenerOnTheSamePort_Conflicts()
    {
        // The direct collision: another Director already holds 127.0.0.1:7885.
        Assert.True(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.Loopback, Port), Port));
    }

    [Fact]
    public void ConflictsWithLoopbackBind_IPv6LoopbackListenerOnTheSamePort_Conflicts()
    {
        Assert.True(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.IPv6Loopback, Port), Port));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void ConflictsWithLoopbackBind_WildcardListenerOnTheSamePort_Conflicts(string wildcard)
    {
        // A wildcard listener already covers loopback, so it blocks the bind. This is the case the
        // old comment was reaching for, and it is the one that must survive the narrowing: getting
        // it wrong would let the allocator hand out a port that is genuinely taken.
        Assert.True(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.Parse(wildcard), Port), Port));
    }

    [Theory]
    [InlineData("192.168.1.5")]     // an ordinary local network address
    [InlineData("100.64.0.7")]      // a tunnel address
    [InlineData("172.17.0.1")]      // a virtual adapter
    public void ConflictsWithLoopbackBind_SpecificNonLoopbackAddress_DoesNotConflict(string address)
    {
        // The regression this file exists for: a program listening on a real network address does
        // NOT block a loopback bind. Treating it as if it did cost a Director a usable port.
        Assert.False(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.Parse(address), Port), Port));
    }

    [Fact]
    public void ConflictsWithLoopbackBind_LoopbackListenerOnADifferentPort_DoesNotConflict()
    {
        Assert.False(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.Loopback, Port + 1), Port));
    }

    [Fact]
    public void ConflictsWithLoopbackBind_WholeLoopbackRangeIsTreatedAsLoopback()
    {
        // 127.0.0.0/8 is all loopback, not just 127.0.0.1 - a listener anywhere in it blocks us.
        Assert.True(PortAllocator.ConflictsWithLoopbackBind(
            new IPEndPoint(IPAddress.Parse("127.0.0.2"), Port), Port));
    }
}
