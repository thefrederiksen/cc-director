namespace CcDirectorSetup.Services;

/// <summary>
/// Pure navigation policy for the install wizard's step rail. Separated from the WPF
/// <see cref="MainWindow"/> so the step ordering is unit-testable without a UI.
///
/// The step ids match MainWindow's wizard step numbers: 1 Welcome, 2 Prerequisites,
/// 7 Install, 8 Complete.
///
/// There is now ONE linear path for every install and update. The installer always lays down the
/// Director set (the DevThrottle app + every cc-* tool + the Launcher) with no role decision and no
/// account gate; connecting a gateway is a later, optional step done from the app, not here (issue
/// #1807). So no step is role-aware or update-aware.
///
/// Historical ids 3-6 are retired. The surviving ids keep their old numbers so MainWindow's step
/// switch remains stable.
///
/// Step 6, the Skills screen, is gone too. It listed three internal identifiers as tick-boxes that
/// could not be ticked, described a completely different set of skills, said "for Claude Code" on a
/// machine that may not have Claude Code, and asked the user for no decision at all. The installer
/// no longer places skills on the machine either (issue 995): skills are held on the Gateway and
/// fetched by whichever agent actually uses one, so there is nothing here to choose or to install.
/// </summary>
public static class WizardStepFlow
{
    /// <summary>The step ids shown, in order. One linear path for every install and update.</summary>
    private static readonly int[] Steps = [1, 2, 7, 8];

    /// <summary>The step ids shown, in order.</summary>
    public static List<int> VisibleSteps() => [.. Steps];

    /// <summary>The next visible step after <paramref name="step"/>, or <paramref name="step"/> itself
    /// when it is the last step (callers guard against advancing past Complete).</summary>
    public static int NextStep(int step)
    {
        var idx = Array.IndexOf(Steps, step);
        return idx >= 0 && idx < Steps.Length - 1 ? Steps[idx + 1] : step;
    }

    /// <summary>The previous visible step before <paramref name="step"/>, or <paramref name="step"/>
    /// itself when it is the first step.</summary>
    public static int PrevStep(int step)
    {
        var idx = Array.IndexOf(Steps, step);
        return idx > 0 ? Steps[idx - 1] : step;
    }
}
