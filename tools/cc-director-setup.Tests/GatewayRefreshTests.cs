using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Guards the honest completion reporting (D2b) at the PRODUCTION SEAM: <see cref="GatewayRefresh"/>,
/// the exact gateway-outcome -> completion-state transition that <c>MainWindow.RunGatewayTrayInstallAsync</c>
/// runs (MainWindow has no other path). A returned failure AND a thrown failure both add one to the
/// skipped (failed) count, so the resulting completion model reads as Problems - it CANNOT render
/// "Everything went perfectly" or "Already Up to Date." Before this, a failed refresh was painted on
/// the row but never counted, so the wizard reported a clean success while the Gateway never updated.
/// </summary>
public sealed class GatewayRefreshTests
{
    // A returned Gateway failure counts one skip and forces Problems - not Success, not AlreadyUpToDate -
    // even when the Director itself was already up to date.
    // Revert-proof: in GatewayRefresh.RunAsync, stop counting the failure (return `skippedBefore` on a
    // failed result) -> Skipped stays 0, Classify yields AlreadyUpToDate/Success, and this goes red.
    [Fact]
    public async Task RunAsync_ReturnedFailure_CountsSkip_CannotRenderSuccess()
    {
        var outcome = await GatewayRefresh.RunAsync(
            () => Task.FromResult(new GatewayTrayLauncher.Result(false, 3, "Gateway install failed (exit 3).")),
            skippedBefore: 0);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.Skipped);

        var kind = InstallCompletion.Classify(outcome.Skipped, alreadyUpToDate: true);
        Assert.Equal(InstallCompletionKind.Problems, kind);
        Assert.NotEqual(InstallCompletionKind.AlreadyUpToDate, kind);
        Assert.NotEqual(InstallCompletionKind.Success, kind);
    }

    // A thrown Gateway failure travels the same production catch: it counts one skip, forces Problems,
    // and carries the reason (for the Complete screen, R2).
    // Revert-proof: in GatewayRefresh.RunAsync, return `skippedBefore` in the catch instead of
    // `skippedBefore + 1` -> Skipped stays 0 and Classify yields Success, so this goes red.
    [Fact]
    public async Task RunAsync_ThrownFailure_CountsSkip_CarriesReason_CannotRenderSuccess()
    {
        var outcome = await GatewayRefresh.RunAsync(
            () => throw new InvalidOperationException("cli vanished"),
            skippedBefore: 0);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.Skipped);
        Assert.Contains("cli vanished", outcome.Message);

        var kind = InstallCompletion.Classify(outcome.Skipped, alreadyUpToDate: true);
        Assert.Equal(InstallCompletionKind.Problems, kind);
        Assert.NotEqual(InstallCompletionKind.Success, kind);
    }

    // A successful refresh leaves the count untouched, so a clean update still renders success.
    [Fact]
    public async Task RunAsync_Success_KeepsCleanCompletion()
    {
        var outcome = await GatewayRefresh.RunAsync(
            () => Task.FromResult(new GatewayTrayLauncher.Result(true, 0, "Gateway tray app installed.")),
            skippedBefore: 0);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(InstallCompletionKind.AlreadyUpToDate, InstallCompletion.Classify(outcome.Skipped, alreadyUpToDate: true));
        Assert.Equal(InstallCompletionKind.Success, InstallCompletion.Classify(outcome.Skipped, alreadyUpToDate: false));
    }

    // A Gateway failure on top of existing engine skips accumulates (never overwrites the earlier count).
    [Fact]
    public async Task RunAsync_ReturnedFailure_AddsToExistingSkips()
    {
        var outcome = await GatewayRefresh.RunAsync(
            () => Task.FromResult(new GatewayTrayLauncher.Result(false, 1, "boom")),
            skippedBefore: 2);
        Assert.Equal(3, outcome.Skipped);
    }
}
