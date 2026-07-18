namespace CcDirectorSetup.Services;

/// <summary>How the Complete step should read an install/update pass.</summary>
public enum InstallCompletionKind
{
    /// <summary>An update pass that found nothing to do (Director already current).</summary>
    AlreadyUpToDate,

    /// <summary>Every component that was meant to install/update did so.</summary>
    Success,

    /// <summary>At least one component did not install - the pass finished with problems.</summary>
    Problems,
}

/// <summary>
/// Pure completion-state decisions for the setup wizard, factored out of the WPF
/// <c>CompleteStep</c>/<c>MainWindow</c> so the honesty rules can be unit-tested without a UI thread.
///
/// The verdict is computed in ONE place and rendered verbatim: a pass with ANY skipped (failed)
/// component reads as <see cref="InstallCompletionKind.Problems"/>, and a "skipped" count is the only
/// signal that separates an honest failure from "Everything went perfectly." A failed Gateway refresh
/// on an existing Gateway machine MUST feed that count (<see cref="SkippedAfterGateway"/>) or the
/// wizard would report success while the Gateway component did not actually update.
/// </summary>
public static class InstallCompletion
{
    /// <summary>
    /// The skipped (failed) component count AFTER accounting for the Gateway tray refresh. A failed
    /// refresh is a component that did NOT install, so it adds one to the count; a successful refresh
    /// leaves it unchanged. Swallowing the failure (returning <paramref name="skippedBefore"/> on a
    /// failed refresh) is exactly the false-success bug this guards against.
    /// </summary>
    public static int SkippedAfterGateway(int skippedBefore, bool gatewaySuccess)
        => gatewaySuccess ? skippedBefore : skippedBefore + 1;

    /// <summary>
    /// Classify a finished pass. Any skipped component wins and reads as
    /// <see cref="InstallCompletionKind.Problems"/> - the "finished with problems" state - so a
    /// failure can never be rendered as "Everything went perfectly" or "Already Up to Date".
    /// </summary>
    public static InstallCompletionKind Classify(int installed, int skipped, bool isUpdate, bool alreadyUpToDate)
    {
        _ = installed;
        _ = isUpdate;
        if (skipped > 0)
            return InstallCompletionKind.Problems;
        return alreadyUpToDate ? InstallCompletionKind.AlreadyUpToDate : InstallCompletionKind.Success;
    }
}
