using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// launcher-persistent-join: the SignalR hub each cc-launcher dials OUT to, so the always-on launcher stays
/// joined to the Gateway and the Gateway can PUSH a lifecycle command (start/stop/restart a Director, or a
/// generic launch) DOWN the open stream instead of dialing the launcher's loopback REST API.
///
/// This is the launcher twin of <see cref="DirectorHub"/>, but one-directional up: a launcher only sends
/// <see cref="Hello"/> (the identity binding) - it pushes no session state. Every command flows the other
/// way, invoked by the Gateway on the client. So the only inbound method here is Hello.
///
/// Identity binding: the first message MUST be <see cref="Hello"/>, which binds this connection to one
/// machine name in <see cref="Microsoft.AspNetCore.SignalR.HubCallerContext.Items"/> and registers it in the
/// <see cref="LauncherConnectionRegistry"/>. A re-claim to a different machine on the same connection is
/// rejected. The transport itself is already authenticated by the host-wide token middleware (the shared
/// token or a per-device key), exactly like <see cref="DirectorHub"/>.
///
/// The hub holds no state of its own; it is a thin adapter onto <see cref="LauncherConnectionRegistry"/>.
/// SignalR constructs it per invocation via dependency injection.
/// </summary>
public sealed class LauncherHub : Hub
{
    private const string MachineNameItemKey = "cc.launcherMachine";

    private readonly LauncherConnectionRegistry _registry;

    public LauncherHub(LauncherConnectionRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Bind this connection to a machine's launcher. Must be the first message; aborts on a bad name.</summary>
    public void Hello(LauncherStreamHello hello)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.MachineName))
        {
            FileLog.Write($"[LauncherHub] Hello REJECTED (missing machineName): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        var machine = hello.MachineName.Trim();
        var alreadyBound = BoundMachine();
        if (alreadyBound is not null && !string.Equals(alreadyBound, machine, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[LauncherHub] Hello REJECTED (conn already bound to {alreadyBound}, cannot re-claim {machine}): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        Context.Items[MachineNameItemKey] = machine;
        _registry.RegisterConnection(machine, Context.ConnectionId);
        FileLog.Write($"[LauncherHub] Hello: machine={machine} bound to conn={Short(Context.ConnectionId)} (port={hello.Port}, version={hello.Version})");
    }

    public override Task OnConnectedAsync()
    {
        FileLog.Write($"[LauncherHub] connected: conn={Short(Context.ConnectionId)}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Unregister(Context.ConnectionId);
        FileLog.Write($"[LauncherHub] disconnected: conn={Short(Context.ConnectionId)}, machine={BoundMachine() ?? "(unbound)"} ({exception?.Message ?? "clean"})");
        return base.OnDisconnectedAsync(exception);
    }

    private string? BoundMachine() =>
        Context.Items.TryGetValue(MachineNameItemKey, out var value) && value is string name ? name : null;

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
