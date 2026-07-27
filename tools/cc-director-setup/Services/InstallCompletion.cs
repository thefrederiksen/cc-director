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
/// Pure completion-state classification for the setup wizard, factored out of the WPF
/// <c>CompleteStep</c> so the rule is single-sourced and unit-testable. Any skipped (failed) component
/// reads as <see cref="InstallCompletionKind.Problems"/> - the "finished with problems" state - so a
/// failure can never be rendered as "Everything went perfectly" or "Already Up to Date." The skipped
/// count is fed by <see cref="GatewayRefresh"/> so a failed Gateway refresh lands here as Problems.
/// </summary>
public static class InstallCompletion
{
    /// <summary>
    /// Classify a finished pass. Any skipped component wins and reads as
    /// <see cref="InstallCompletionKind.Problems"/>; otherwise an update that found nothing to do is
    /// <see cref="InstallCompletionKind.AlreadyUpToDate"/> and everything else is
    /// <see cref="InstallCompletionKind.Success"/>.
    /// </summary>
    public static InstallCompletionKind Classify(int skipped, bool alreadyUpToDate)
    {
        if (skipped > 0)
            return InstallCompletionKind.Problems;
        return alreadyUpToDate ? InstallCompletionKind.AlreadyUpToDate : InstallCompletionKind.Success;
    }

    /// <summary>
    /// May the Complete screen tell the user they are ready to go?
    ///
    /// <see cref="Classify"/> answers "did this install do its job", which is a different question.
    /// A pass where every component landed can still leave a machine that cannot run anything: a
    /// required prerequisite may be missing, or there may be no coding agent installed at all. The
    /// screen said "Everything went perfectly. You're ready to go." in exactly those cases, with
    /// the amber list of what was missing sitting directly underneath it.
    /// </summary>
    /// <param name="skipped">Components that did not install.</param>
    /// <param name="allRequiredMet">Every REQUIRED prerequisite is present.</param>
    /// <param name="anyCodingAgentPresent">Claude Code or any other agent command line tool is installed.</param>
    public static bool IsReadyToGo(int skipped, bool allRequiredMet, bool anyCodingAgentPresent)
        => skipped == 0 && allRequiredMet && anyCodingAgentPresent;
}
