using System.Runtime.Versioning;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the macOS login Keychain credential store, the counterpart of the Windows Data Protection
/// store. macOS-only - it shells out to the <c>security</c> tool against a real Keychain - so these
/// facts no-op on other platforms (guarded by the OnMac check) and the class is annotated
/// [SupportedOSPlatform("macos")] so the platform-compatibility analyzer is satisfied. Each test uses
/// unique service and account labels so it never touches the real machine-wide Gateway item, and
/// clears them at the end.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainProtectedTokenStoreTests
{
    private static bool OnMac => OperatingSystem.IsMacOS();

    private static (string service, string account) UniqueLabels() =>
        ("com.devthrottle.test." + Guid.NewGuid().ToString("N"), "tokens");

    [Fact]
    public void SaveThenLoad_RoundTripsTheTokenPair()
    {
        if (!OnMac) return;

        var (service, account) = UniqueLabels();
        var store = new MacKeychainProtectedTokenStore(service, account);
        try
        {
            store.Save(new DevThrottleTokens("access-abc", "refresh-xyz"));

            Assert.True(store.HasTokens);
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("access-abc", loaded!.AccessToken);
            Assert.Equal("refresh-xyz", loaded.RefreshToken);
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void Save_ReplacesAnExistingEntry()
    {
        if (!OnMac) return;

        var (service, account) = UniqueLabels();
        var store = new MacKeychainProtectedTokenStore(service, account);
        try
        {
            store.Save(new DevThrottleTokens("first-access", "first-refresh"));
            store.Save(new DevThrottleTokens("second-access", "second-refresh"));

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("second-access", loaded!.AccessToken);
            Assert.Equal("second-refresh", loaded.RefreshToken);
        }
        finally
        {
            store.Clear();
        }
    }

    [Fact]
    public void Clear_RemovesTheStoredEntry()
    {
        if (!OnMac) return;

        var (service, account) = UniqueLabels();
        var store = new MacKeychainProtectedTokenStore(service, account);
        store.Save(new DevThrottleTokens("access", "refresh"));
        Assert.True(store.HasTokens);

        store.Clear();

        Assert.False(store.HasTokens);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNull_WhenNothingStored()
    {
        if (!OnMac) return;

        var (service, account) = UniqueLabels();
        var store = new MacKeychainProtectedTokenStore(service, account);

        Assert.Null(store.Load());
        Assert.False(store.HasTokens);
    }
}
