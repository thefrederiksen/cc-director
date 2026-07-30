namespace CcDirector.Core.Tools;

/// <summary>What the setup wizard's Tools step should say about one tool.</summary>
public enum ToolRowVerdict
{
    /// <summary>Not on disk yet, and still within the window where that is expected.</summary>
    Installing,

    /// <summary>Not on disk, and past the point where "installing" is an honest thing to say.</summary>
    NotInstalled,

    /// <summary>On disk. Whether it RUNS has not been answered yet - so the screen must not claim it does.</summary>
    Checking,

    /// <summary>On disk and every declared check passed.</summary>
    Working,

    /// <summary>On disk and a declared check failed. <see cref="ToolsScreenRow.Detail"/> says which.</summary>
    NotWorking,
}

/// <summary>How the step's status line should read.</summary>
public enum ToolsScreenTone
{
    Progress,
    Good,
    Bad,
}

/// <summary>One rendered row of the Tools step.</summary>
public sealed record ToolsScreenRow(string Name, ToolRowVerdict Verdict, string Detail);

/// <summary>Everything the Tools step renders, decided in one place.</summary>
public sealed record ToolsScreenView(
    IReadOnlyList<ToolsScreenRow> Rows,
    string StatusText,
    ToolsScreenTone Tone,
    bool OfferRepair,
    bool KeepPolling);

/// <summary>One tool as the wizard knows it before any verdict: its name, its blurb, and whether it is on disk.</summary>
public readonly record struct ToolsScreenInput(string Name, string Description, bool IsAvailable);

/// <summary>
/// Decides what the first-run wizard's Tools step says - every row and the status line - from the
/// catalog (what is on disk) and the shared health snapshot (whether it works).
///
/// It is a fold rather than a handful of conditionals in the view because of what the view did with
/// them (issue #1045). It had only the catalog, which answers "is it here?", and it rendered that
/// answer as "Ready" beneath the heading "All 9 tools are installed and up to date" - a claim about
/// working, made from evidence about presence. Minutes later the Director's board ran the actual checks
/// and named cc-pdf as failing. Two screens of one product, one install, opposite claims, and a new
/// user could see both inside a minute.
///
/// The rule this encodes: presence and function are different questions, a screen may only assert the
/// one it has evidence for, and "no answer yet" renders as CHECKING - never as a pass.
/// </summary>
public static class ToolsScreenFold
{
    public static ToolsScreenView Fold(
        IReadOnlyList<ToolsScreenInput> tools, bool stalled, ToolHealthSnapshot? health)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var verdicts = health?.Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ToolsScreenRow>(tools.Count);

        foreach (var tool in tools)
        {
            if (!tool.IsAvailable)
            {
                rows.Add(stalled
                    ? new ToolsScreenRow(tool.Name, ToolRowVerdict.NotInstalled, $"{tool.Description} - this one did not install")
                    : new ToolsScreenRow(tool.Name, ToolRowVerdict.Installing, tool.Description));
                continue;
            }

            var outcome = verdicts is not null && verdicts.TryGetValue(tool.Name, out var v) ? v : null;
            rows.Add(outcome?.Verdict switch
            {
                ToolVerdict.Working => new ToolsScreenRow(tool.Name, ToolRowVerdict.Working, tool.Description),
                ToolVerdict.NotWorking => new ToolsScreenRow(tool.Name, ToolRowVerdict.NotWorking, $"{tool.Description} - {outcome.Detail}"),
                // Installed, unjudged. The honest row is "checking", which is what this whole fold is for.
                _ => new ToolsScreenRow(tool.Name, ToolRowVerdict.Checking, $"{tool.Description} - installed, checking that it runs"),
            });
        }

        var total = tools.Count;
        var installed = tools.Count(t => t.IsAvailable);
        var missing = total - installed;
        var failures = health?.Summary.Failures ?? Array.Empty<ToolFailure>();

        if (missing > 0 && stalled)
        {
            return new ToolsScreenView(rows,
                missing == 1
                    ? "1 tool did not install. Repairing takes about a minute - you can continue while it runs."
                    : $"{missing} tools did not install. Repairing takes about a minute - you can continue while it runs.",
                ToolsScreenTone.Bad, OfferRepair: true, KeepPolling: false);
        }

        if (missing > 0)
        {
            // Both halves of the trade, so continuing is an informed choice rather than a guess.
            return new ToolsScreenView(rows,
                $"{installed} of {total} installed. Wait here and all of them will be working before you finish. " +
                "Continue and DevThrottle finishes the rest in the background on its own.",
                ToolsScreenTone.Progress, OfferRepair: false, KeepPolling: true);
        }

        if (health is null)
        {
            // Everything is on disk and nothing has run them. Say precisely that much and no more.
            return new ToolsScreenView(rows,
                $"All {total} tools are installed. Checking that each one runs...",
                ToolsScreenTone.Progress, OfferRepair: false, KeepPolling: true);
        }

        if (failures.Count > 0)
        {
            var shown = string.Join(", ", failures.Take(2).Select(f => f.ToString()));
            if (failures.Count > 2) shown += $", +{failures.Count - 2} more";
            return new ToolsScreenView(rows,
                failures.Count == 1
                    ? $"All {total} tools are installed, but one is not working: {shown}. Repairing takes about a minute - you can continue while it runs."
                    : $"All {total} tools are installed, but {failures.Count} are not working: {shown}. Repairing takes about a minute - you can continue while it runs.",
                ToolsScreenTone.Bad, OfferRepair: true, KeepPolling: false);
        }

        return new ToolsScreenView(rows,
            $"All {total} tools are installed and working.",
            ToolsScreenTone.Good, OfferRepair: false, KeepPolling: false);
    }
}
