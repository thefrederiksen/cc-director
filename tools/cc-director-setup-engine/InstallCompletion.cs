namespace CcDirector.Setup.Engine;

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
/// Pure completion-state classification for the setup wizard. Any skipped (failed) component reads
/// as <see cref="InstallCompletionKind.Problems"/> - the "finished with problems" state - so a
/// failure can never be rendered as "Everything went perfectly" or "Already Up to Date."
///
/// This lives in the shared engine, not in either wizard, because both wizards must reach the same
/// verdict from the same facts. It used to live in the Windows project alone, which is how macOS
/// came to branch for itself and say "Everything went perfectly" about a pass that had not gone
/// perfectly.
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
    /// A pass where every component landed can still leave a machine that cannot run anything,
    /// because there is no coding agent on it. "You're ready to go" is false then, and the screen
    /// used to say it anyway.
    ///
    /// The wizard no longer checks prerequisites - nothing it places needs anything already on the
    /// machine - so an agent being present is the only remaining fact this turns on.
    /// </summary>
    /// <param name="skipped">Components that did not install.</param>
    /// <param name="anyCodingAgentPresent">Any agent command line tool the Director drives is installed.</param>
    public static bool IsReadyToGo(int skipped, bool anyCodingAgentPresent)
        => skipped == 0 && anyCodingAgentPresent;
}
