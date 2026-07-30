using System.Net;
using System.Net.Sockets;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A loopback port that is guaranteed to REFUSE connections and guaranteed NOT to be taken by anyone
/// else, for as long as this object is alive (issue #1156).
///
/// WHAT IT REPLACES, AND WHY THAT PATTERN WAS A CROSS-PROCESS RACE. Three tests needed "an address where
/// nothing is listening" and got it by opening a <see cref="TcpListener"/> on port 0, reading the assigned
/// number, and then STOPPING the listener - handing back a port that was free at the moment of the read and
/// unowned from then on. That is a time-of-check/time-of-use race, and the window is not intra-process: any
/// other process on the machine - notably a second run of this suite - can bind that number before the test
/// uses it. A test asserting "unreachable" then talks to somebody else's listener, and a test expecting a
/// child to refuse before binding gets an address-conflict failure instead of the contract failure it meant
/// to prove. Both look like unrelated flakes in whichever suite happens to lose the race, which is exactly
/// the shape of failure that made concurrent runs of this assembly untrustworthy.
///
/// HOW THIS IS DIFFERENT: BIND WITHOUT LISTEN. The socket is bound to the port and never placed into the
/// listening state. That single distinction gives both properties at once, and they are the two the callers
/// actually need:
///
///  * RESERVED - the port is held by this socket for its lifetime, so no other process (or run) can bind it.
///    A second bind fails with <see cref="SocketError.AddressAlreadyInUse"/>.
///  * DEAD - with no listen backlog the kernel answers an incoming connection with a reset, so a connect
///    attempt fails with <see cref="SocketError.ConnectionRefused"/> - which is precisely what "nothing is
///    listening here" is supposed to mean.
///
/// Both properties were verified on this platform before this type was written, rather than assumed from
/// the socket API's documentation.
///
/// LIFETIME IS THE WHOLE POINT. The reservation is only true while this object is alive, so callers must
/// hold it for as long as the dead address must stay dead - typically the lifetime of the fixture that
/// registered the address - and dispose it with that fixture. Reserving a port and immediately disposing it
/// would reintroduce the exact race this replaces.
/// </summary>
internal sealed class DeadPortReservation : IDisposable
{
    private readonly Socket _socket;

    private DeadPortReservation(Socket socket, int port)
    {
        _socket = socket;
        Port = port;
    }

    /// <summary>The reserved loopback port. Refuses connections while this reservation is alive.</summary>
    public int Port { get; }

    /// <summary>Convenience for the common use: an endpoint nothing will ever answer on.</summary>
    public string LoopbackUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>
    /// Reserve a fresh dead port. The operating system assigns the number (bind to port 0), so this never
    /// guesses and never collides with a port already in use.
    /// </summary>
    public static DeadPortReservation Reserve()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Deliberately NOT ExclusiveAddressUse=false and deliberately no Listen() call. The default
            // exclusive binding is what makes the reservation hold against another process.
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            return new DeadPortReservation(socket, port);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => _socket.Dispose();
}
