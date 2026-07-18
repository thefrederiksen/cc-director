using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Builds the workflow SEAT paragraph a seated session receives at launch (Workflows mission, phase
/// 5b): which run it executes, at which PINNED version to fetch its conduct, and the fail-closed
/// rule (if the fetch fails, STOP - never proceed on remembered rules). ONE builder for every
/// delivery channel - the hook preamble endpoints and Pi's preamble file - so no agent family can
/// receive a differently-worded seat.
///
/// The workflow id is validated against the catalog's slug shape before it is interpolated. The
/// Director stamps whatever the create request carried (the Gateway is the source of truth and the
/// honest path always sends a real slug), so a value that is NOT a slug is a forged or corrupted
/// seat - it renders NO paragraph and logs loudly rather than letting authored bytes ride into an
/// agent's context as extra preamble lines.
/// </summary>
public static class WorkflowSeatParagraph
{
    /// <summary>The catalog's workflow id shape (parity with the Gateway's WorkflowValidation).</summary>
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    /// <summary>The paragraph for one session, or null when the session is unseated (or the seat is
    /// malformed - logged, never rendered).</summary>
    public static string? Build(Guid? workflowRunId, string? workflowId, int? workflowVersion, string? role)
    {
        if (workflowRunId is not Guid runId || string.IsNullOrWhiteSpace(workflowId))
            return null;
        if (!IdPattern.IsMatch(workflowId))
        {
            FileLog.Write($"[WorkflowSeatParagraph] REFUSED: workflow id {Compact(workflowId)} is not a " +
                          "catalog slug - a forged or corrupted seat renders no paragraph.");
            return null;
        }

        var seatRole = string.IsNullOrWhiteSpace(role) ? "a participant" : role.Trim();
        var versionArg = workflowVersion is int v ? $" --version {v}" : "";
        return
            $"[Workflow seat] You are seated as {seatRole} on the '{workflowId}' workflow" +
            (workflowVersion is int pv ? $" (pinned v{pv})" : "") +
            $", run {runId}. Before doing anything else, fetch your conduct and FOLLOW it:\n" +
            $"  cc-devthrottle workflow instructions {workflowId}{versionArg}\n" +
            "If that command fails, STOP and report the failure - never proceed on remembered or " +
            "reconstructed rules.";
    }

    private static string Compact(string value)
    {
        var printable = new string(value.Where(ch => !char.IsControl(ch)).ToArray());
        return printable.Length > 40 ? "'" + printable[..40] + "...'" : "'" + printable + "'";
    }
}
