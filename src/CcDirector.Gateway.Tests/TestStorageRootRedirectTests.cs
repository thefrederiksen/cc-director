using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the storage-root redirect is actually IN FORCE during a test run.
///
/// This exists because <see cref="TestStorageRootRedirect"/> is the only thing standing between this test
/// suite and the owner's live fleet state, and a module initializer that silently does not run leaves no
/// trace at all - the suite would simply go back to writing into his real %LOCALAPPDATA%\cc-director, and
/// every test would still pass. That is the dead-test-that-looks-like-coverage shape, so the guard gets a
/// guard.
/// </summary>
public sealed class TestStorageRootRedirectTests
{
    [Fact]
    public void CcStorageRoot_ResolvesUnderTheTemporaryRoot_NotTheOwnersRealStorage()
    {
        var root = CcStorage.Root();

        // The property being pinned: whatever this suite resolves as "the storage root" is disposable.
        Assert.Equal(TestStorageRootRedirect.Root, root);
        Assert.StartsWith(Path.GetTempPath(), root, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void CcStorageRoot_IsNotTheRealLocalApplicationDataRoot()
    {
        // Named explicitly rather than inferred from the temp path, because THIS is the location that must
        // never be touched: it holds missions.json, cronjobs.json, keyvault.json and the statistics stores.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cc-director");

        Assert.NotEqual(real, CcStorage.Root());
    }

    [Fact]
    public void GatewayHostDefaultPaths_LandUnderTheTemporaryRoot()
    {
        // The actual exposure path: GatewayHost resolves these from CcStorage.Root() when given no explicit
        // path, and 26 of the 48 test files that construct one pass nothing. Pinning the resolution rather
        // than constructing a host keeps this test cheap while still failing if the redirect stops working.
        foreach (var store in new[] { "missions.json", "cronjobs.json", "gateway-input-stats.json", "gateway-stats.db" })
        {
            var resolved = Path.Combine(CcStorage.Root(), store);
            Assert.StartsWith(Path.GetTempPath(), resolved, StringComparison.OrdinalIgnoreCase);
        }
    }
}
