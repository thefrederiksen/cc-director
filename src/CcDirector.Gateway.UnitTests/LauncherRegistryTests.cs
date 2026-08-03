using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="LauncherRegistry"/>: upsert, heartbeat, sweep, listing.
/// Issue #331.
/// </summary>
public sealed class LauncherRegistryTests
{
    // -------------------------------------------------------------------------
    // Upsert
    // -------------------------------------------------------------------------

    [Fact]
    public void Upsert_AddsEntry_CanBeRetrieved()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-A", 7900);

        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, req);

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-A");
        Assert.NotNull(dto);
        Assert.Equal("MACHINE-A", dto.MachineName);
        Assert.Equal(7900, dto.Port);
    }

    [Fact]
    public void Upsert_IsCaseInsensitive()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("machine-b", 7901));

        Assert.NotNull(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-B"));
        Assert.NotNull(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "Machine-B"));
    }

    [Fact]
    public void Upsert_UpdatesExistingEntry()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-C", 7902, version: "1.0.0"));
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-C", 7902, version: "1.0.1"));

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-C");
        Assert.NotNull(dto);
        Assert.Equal("1.0.1", dto!.Version);
    }

    [Fact]
    public void Upsert_ReturnsDto_TokenNotInDto()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-D", 7903);
        req.Token = "SECRET-TOKEN";

        var dto = reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, req);

        // Token MUST NOT be in the public DTO.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("SECRET-TOKEN", json);
    }

    // -------------------------------------------------------------------------
    // Token retrieval (for relay calls)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetToken_ReturnsStoredToken()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-E", 7904);
        req.Token = "my-relay-token";
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, req);

        Assert.Equal("my-relay-token", reg.GetToken(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-E"));
    }

    [Fact]
    public void GetToken_UnknownMachine_ReturnsNull()
    {
        var reg = new LauncherRegistry();
        Assert.Null(reg.GetToken(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY"));
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_KnownMachine_ReturnsTrue()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-F", 7905));

        Assert.True(reg.Heartbeat(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-F"));
    }

    [Fact]
    public void Heartbeat_UnknownMachine_ReturnsFalse()
    {
        var reg = new LauncherRegistry();
        Assert.False(reg.Heartbeat(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY"));
    }

    // -------------------------------------------------------------------------
    // Remove
    // -------------------------------------------------------------------------

    [Fact]
    public void Remove_ExistingEntry_IsGone()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-G", 7906));

        reg.Remove(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-G");

        Assert.Null(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-G"));
    }

    [Fact]
    public void Remove_NonExistentEntry_DoesNotThrow()
    {
        var reg = new LauncherRegistry();
        var ex = Record.Exception(() => reg.Remove(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY"));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    [Fact]
    public void ListLaunchers_ReturnsAllEntries()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-H", 7907));
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-I", 7908));

        var list = reg.ListLaunchers(CcDirector.Core.Tenancy.TenantId.Local);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, l => l.MachineName == "MACHINE-H");
        Assert.Contains(list, l => l.MachineName == "MACHINE-I");
    }

    [Fact]
    public void ListLaunchers_EmptyWhenNoneRegistered()
    {
        var reg = new LauncherRegistry();
        Assert.Empty(reg.ListLaunchers(CcDirector.Core.Tenancy.TenantId.Local));
    }

    // -------------------------------------------------------------------------
    // NetworkAddress (AC2 - cross-machine relay address)
    // -------------------------------------------------------------------------

    [Fact]
    public void Upsert_WithNetworkAddress_StoredInDto()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-NA", 7920, networkAddress: "example-pc.ts.net");

        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, req);

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-NA");
        Assert.NotNull(dto);
        Assert.Equal("example-pc.ts.net", dto!.NetworkAddress);
    }

    [Fact]
    public void GetNetworkAddress_ReturnsStoredAddress()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-NB", 7921, networkAddress: "example-host.tailnet.ts.net"));

        Assert.Equal("example-host.tailnet.ts.net", reg.GetNetworkAddress(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-NB"));
    }

    [Fact]
    public void GetNetworkAddress_EmptyWhenNotSet()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-NC", 7922)); // no networkAddress

        // Empty string = co-located, loopback applies.
        Assert.Equal("", reg.GetNetworkAddress(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-NC"));
    }

    [Fact]
    public void GetNetworkAddress_NullForUnknownMachine()
    {
        var reg = new LauncherRegistry();
        Assert.Null(reg.GetNetworkAddress(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY-NA"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LauncherRegistrationRequest MakeReq(
        string machine, int port, string version = "1.0.0", string networkAddress = "") =>
        new()
        {
            MachineName = machine,
            Port = port,
            NetworkAddress = networkAddress,
            Token = "tok-" + machine,
            Pid = 9999,
            Version = version,
            StartedAt = DateTime.UtcNow,
        };
}
