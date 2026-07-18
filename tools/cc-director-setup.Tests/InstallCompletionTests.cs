using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Guards the honest completion reporting (D2b). A failed Gateway refresh on an existing Gateway
/// machine must feed the "skipped" (failed) count so the Complete step reads as a failure instead of
/// "Everything went perfectly." Before this, RunGatewayTrayInstallAsync painted the row failed but
/// never counted it, so CompleteStep reported a clean success while the Gateway never updated.
/// </summary>
public sealed class InstallCompletionTests
{
    // The headline D2b guard: a failed Gateway refresh increments the skipped (failed) count.
    // Revert-proof: swallow the failure again (return skippedBefore on a failed refresh) and this goes
    // red - the count stays 0, which is exactly the false-success symptom.
    [Fact]
    public void SkippedAfterGateway_FailedRefresh_CountsOne()
    {
        Assert.Equal(1, InstallCompletion.SkippedAfterGateway(skippedBefore: 0, gatewaySuccess: false));
    }

    [Fact]
    public void SkippedAfterGateway_SuccessfulRefresh_LeavesCountUnchanged()
    {
        Assert.Equal(0, InstallCompletion.SkippedAfterGateway(skippedBefore: 0, gatewaySuccess: true));
        Assert.Equal(2, InstallCompletion.SkippedAfterGateway(skippedBefore: 2, gatewaySuccess: true));
    }

    [Fact]
    public void SkippedAfterGateway_FailedRefresh_AddsToExistingFailures()
    {
        Assert.Equal(3, InstallCompletion.SkippedAfterGateway(skippedBefore: 2, gatewaySuccess: false));
    }

    // The other half of the false-success symptom: any skipped component reads as Problems - never
    // the "Everything went perfectly" (Success) or "Already Up to Date" state. So once a failed
    // Gateway refresh has bumped the count to >= 1, the Complete step cannot report success.
    [Fact]
    public void Classify_AnySkipped_IsProblems()
    {
        Assert.Equal(InstallCompletionKind.Problems, InstallCompletion.Classify(installed: 5, skipped: 1, isUpdate: true, alreadyUpToDate: false));
        Assert.Equal(InstallCompletionKind.Problems, InstallCompletion.Classify(installed: 0, skipped: 1, isUpdate: true, alreadyUpToDate: true));
    }

    [Fact]
    public void Classify_NoSkipped_UpToDateOrSuccess()
    {
        Assert.Equal(InstallCompletionKind.AlreadyUpToDate, InstallCompletion.Classify(installed: 0, skipped: 0, isUpdate: true, alreadyUpToDate: true));
        Assert.Equal(InstallCompletionKind.Success, InstallCompletion.Classify(installed: 3, skipped: 0, isUpdate: true, alreadyUpToDate: false));
        Assert.Equal(InstallCompletionKind.Success, InstallCompletion.Classify(installed: 3, skipped: 0, isUpdate: false, alreadyUpToDate: false));
    }

    // End-to-end reasoning: an update that found the Director already current (alreadyUpToDate) but
    // whose Gateway refresh then FAILED must not report "Already Up to Date" - the failed refresh
    // bumps the count and the verdict flips to Problems.
    [Fact]
    public void FailedGatewayRefresh_FlipsAlreadyUpToDate_ToProblems()
    {
        var skipped = InstallCompletion.SkippedAfterGateway(skippedBefore: 0, gatewaySuccess: false);
        var kind = InstallCompletion.Classify(installed: 0, skipped: skipped, isUpdate: true, alreadyUpToDate: true);
        Assert.Equal(InstallCompletionKind.Problems, kind);
    }
}
