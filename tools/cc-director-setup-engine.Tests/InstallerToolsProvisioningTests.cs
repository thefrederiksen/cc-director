using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Revert-proof for the snappy install (slice: tools out of the installer): the installer's tools step
/// (<see cref="InstallerToolsProvisioning.ProvisionDuringInstallAsync"/>) - the SINGLE production seam both
/// the WPF and Avalonia wizard install runners route their tools step through - must NOT invoke the real
/// Python-tools provisioner. The app provisions the bundle from nothing on first launch instead.
///
/// Revert-proof: change ProvisionDuringInstallAsync to `return provisionTools(ct);` (re-adding the real
/// call site) -> the provisioner is invoked, the count is non-zero, and both tests below go red.
/// </summary>
public sealed class InstallerToolsProvisioningTests
{
    [Fact]
    public async Task ProvisionDuringInstallAsync_DoesNotInvokeTheRealProvisioner()
    {
        var invoked = false;
        var count = await InstallerToolsProvisioning.ProvisionDuringInstallAsync(
            _ => { invoked = true; return Task.FromResult(26); },
            CancellationToken.None);

        Assert.False(invoked, "the installer must NOT provision the Python tools bundle - the app does it on first launch");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProvisionDuringInstallAsync_NullProvisioner_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => InstallerToolsProvisioning.ProvisionDuringInstallAsync(null!, CancellationToken.None));
    }
}
