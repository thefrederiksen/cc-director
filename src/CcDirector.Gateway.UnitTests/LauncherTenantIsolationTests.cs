using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Two hostile tenants, one COLLIDING machine name. Machine names are not unique across tenants, so the
/// launcher registries key by (tenant, machine): a tenant naming a machine can only ever reach its OWN
/// launcher entry / connection, never another tenant's launcher of the same bare name. This is the
/// structural isolation the launcher subsystem lacked (it keyed on bare machine name), the same composite
/// keying DirectorRegistry uses.
/// </summary>
public sealed class LauncherTenantIsolationTests
{
    private static readonly TenantId Alpha = new("alpha");
    private static readonly TenantId Beta = new("beta");

    private static LauncherRegistrationRequest Req(string machine, int port, string token, string net = "") =>
        new()
        {
            MachineName = machine,
            Port = port,
            Token = token,
            NetworkAddress = net,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };

    [Fact]
    public void Two_tenants_registering_the_same_machine_name_are_isolated()
    {
        var reg = new LauncherRegistry();
        const string machine = "SHARED-BOX";
        reg.Upsert(Alpha, Req(machine, 1111, "alpha-token", "alpha.net"));
        reg.Upsert(Beta, Req(machine, 2222, "beta-token", "beta.net"));

        // Each tenant sees ONLY its own launcher for the colliding machine name - port, token and address.
        Assert.Equal(1111, reg.Get(Alpha, machine)!.Port);
        Assert.Equal(2222, reg.Get(Beta, machine)!.Port);
        Assert.Equal("alpha-token", reg.GetToken(Alpha, machine));
        Assert.Equal("beta-token", reg.GetToken(Beta, machine));
        Assert.Equal("alpha.net", reg.GetNetworkAddress(Alpha, machine));
        Assert.Equal("beta.net", reg.GetNetworkAddress(Beta, machine));

        // Lists are per-tenant: neither tenant's list carries the other's machine.
        Assert.Single(reg.ListLaunchers(Alpha));
        Assert.Single(reg.ListLaunchers(Beta));

        // Removing Alpha's launcher does not touch Beta's same-named entry.
        reg.Remove(Alpha, machine);
        Assert.Null(reg.Get(Alpha, machine));
        Assert.Equal(2222, reg.Get(Beta, machine)!.Port);
        Assert.Empty(reg.ListLaunchers(Alpha));
        Assert.Single(reg.ListLaunchers(Beta));
    }

    [Fact]
    public void A_tenant_never_reaches_another_tenants_launcher_or_its_token()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(Alpha, Req("alpha-only", 3000, "alpha-secret"));

        // A foreign machine id under another tenant resolves to nothing - and leaks no token.
        Assert.Null(reg.Get(Beta, "alpha-only"));
        Assert.Null(reg.GetToken(Beta, "alpha-only"));
        Assert.Null(reg.GetNetworkAddress(Beta, "alpha-only"));
        Assert.Empty(reg.ListLaunchers(Beta));

        // Case-insensitive WITHIN the owning tenant.
        Assert.NotNull(reg.Get(Alpha, "ALPHA-ONLY"));
    }

    [Fact]
    public void Heartbeat_is_scoped_to_the_owning_tenant()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(Alpha, Req("box", 4000, "t"));

        Assert.True(reg.Heartbeat(Alpha, "box"));    // owner heartbeat lands
        Assert.False(reg.Heartbeat(Beta, "box"));    // another tenant's heartbeat for the same name is unknown (410)
    }

    [Fact]
    public void Two_tenants_same_machine_name_are_isolated_in_the_connection_registry()
    {
        var reg = new LauncherConnectionRegistry();
        const string machine = "SHARED-BOX";
        reg.RegisterConnection(Alpha, machine, "conn-alpha");
        reg.RegisterConnection(Beta, machine, "conn-beta");

        Assert.Equal("conn-alpha", reg.GetActiveConnectionId(Alpha, machine));
        Assert.Equal("conn-beta", reg.GetActiveConnectionId(Beta, machine));
        Assert.True(reg.IsStreamConnected(Alpha, machine));
        Assert.True(reg.IsStreamConnected(Beta, machine));

        // Alpha reconnecting for the same machine name does NOT supersede Beta's connection.
        reg.RegisterConnection(Alpha, machine, "conn-alpha-2");
        Assert.Equal("conn-alpha-2", reg.GetActiveConnectionId(Alpha, machine));
        Assert.Equal("conn-beta", reg.GetActiveConnectionId(Beta, machine));

        // Disconnect by connection id clears only that one entry.
        reg.Unregister("conn-beta");
        Assert.False(reg.IsStreamConnected(Beta, machine));
        Assert.True(reg.IsStreamConnected(Alpha, machine));
    }
}
