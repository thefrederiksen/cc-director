using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director's own local account-state provider (two-step install, Slice A). A gateway-less
/// Director that holds its own credential reads as signed in locally; with a gateway configured it
/// defers to the Gateway (the account authority, issue #642/#651); with no credential it is not signed
/// in. These tests use the cross-platform in-memory store so the read seam is provable without the
/// Windows-only Data Protection store (that store is covered by <see cref="WindowsProtectedTokenStoreTests"/>).
/// </summary>
public sealed class DirectorAccountStateProviderTests
{
    // --- The pure decision ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_GatewayConfigured_DefersToGateway()
    {
        Assert.Equal(DirectorAccountState.DeferToGateway,
            DirectorAccountStateProvider.Resolve(gatewayConfigured: true, directorCredentialPresent: true));
        Assert.Equal(DirectorAccountState.DeferToGateway,
            DirectorAccountStateProvider.Resolve(gatewayConfigured: true, directorCredentialPresent: false));
    }

    [Fact]
    public void Resolve_NoGatewayWithCredential_IsSignedInLocal()
    {
        Assert.Equal(DirectorAccountState.SignedInLocalNoGateway,
            DirectorAccountStateProvider.Resolve(gatewayConfigured: false, directorCredentialPresent: true));
    }

    [Fact]
    public void Resolve_NoGatewayNoCredential_IsNotSignedIn()
    {
        Assert.Equal(DirectorAccountState.NotSignedIn,
            DirectorAccountStateProvider.Resolve(gatewayConfigured: false, directorCredentialPresent: false));
    }

    // --- The read seam over an explicit store --------------------------------------------------------

    // Revert-proof #2: with no gateway configured AND a Director credential present, the provider reads
    // its own credential and reports SignedInLocalNoGateway. Making the provider ignore the credential
    // (always return NotSignedIn when no gateway) reds this test.
    [Fact]
    public void ResolveFromStore_NoGatewayWithCredential_ReadsSignedInLocal()
    {
        var config = new GatewayConfig { Url = "" };
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens("access-token", "refresh-token"));

        var state = DirectorAccountStateProvider.ResolveFromStore(config, store);

        Assert.Equal(DirectorAccountState.SignedInLocalNoGateway, state);
    }

    [Fact]
    public void ResolveFromStore_NoGatewayNoCredential_IsNotSignedIn()
    {
        var config = new GatewayConfig { Url = "" };
        var store = new InMemoryTokenStore();

        var state = DirectorAccountStateProvider.ResolveFromStore(config, store);

        Assert.Equal(DirectorAccountState.NotSignedIn, state);
    }

    // Control: with a gateway configured the provider defers to the Gateway and does NOT read the local
    // credential as a local sign-in - so the gateway-present authority (issue #642/#651) is untouched,
    // even when a Director blob happens to be present.
    [Fact]
    public void ResolveFromStore_GatewayConfigured_DefersEvenWithCredentialPresent()
    {
        var config = new GatewayConfig { Url = "https://gateway.example.com" };
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens("access-token", "refresh-token"));

        var state = DirectorAccountStateProvider.ResolveFromStore(config, store);

        Assert.Equal(DirectorAccountState.DeferToGateway, state);
    }

    // --- The verbatim display strings the surface renders --------------------------------------------

    // Revert-proof #2 (the render): the signed-in-local state maps to the exact signed-in-local string
    // the SettingsDialog Account tab shows verbatim. A provider that returned NotSignedIn instead would
    // render the "No Gateway configured" string, not this one.
    [Fact]
    public void DescribeNoGateway_SignedInLocal_RendersSignedInLocalString()
    {
        Assert.Equal("Signed in to DevThrottle - connect a gateway to use AI.",
            DirectorAccountStateProvider.DescribeNoGateway(DirectorAccountState.SignedInLocalNoGateway));
    }

    [Fact]
    public void DescribeNoGateway_NotSignedIn_RendersNoGatewayString()
    {
        Assert.Equal("No Gateway configured. Connect this Director to a Gateway on the Gateway tab.",
            DirectorAccountStateProvider.DescribeNoGateway(DirectorAccountState.NotSignedIn));
    }

    // DeferToGateway has no Director-owned line (a gateway-configured Director defers to the Gateway
    // status path), so asking for its string is a programming error rather than a silent fallback.
    [Fact]
    public void DescribeNoGateway_DeferToGateway_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectorAccountStateProvider.DescribeNoGateway(DirectorAccountState.DeferToGateway));
    }
}
