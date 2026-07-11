using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// The two verdicts an automated (auto-dismiss) run can end on (issue #1200). An agent whose session was
/// launched with auto-dismiss ends its run by printing a <c>CC-DISMISS</c> block; the Director parses the
/// verdict and the Gateway acts on it: <see cref="Done"/> closes the session over the stream, so it never
/// lingers in the rail; <see cref="NeedsHuman"/> keeps it open exactly like a normal session.
/// </summary>
public enum DismissVerdict
{
    /// <summary>The run finished and nothing needs the human - safe to close the session.</summary>
    Done,

    /// <summary>The run finished but something needs the human (a decision, an approval, a flagged item) - keep the session open.</summary>
    NeedsHuman,
}

/// <summary>
/// The parsed <c>CC-DISMISS</c> sentinel block (issue #1200), modeled on the work-list runner's
/// <c>IMPL-LOOP-TERMINAL</c> sentinel. An auto-dismiss run prints exactly one such block as its final
/// message so a supervisor learns the outcome WITHOUT parsing prose:
///
/// <code>
/// CC-DISMISS
/// verdict: done | needs-human
/// reason: &lt;one line - why this verdict&gt;
/// </code>
///
/// The verdict string on <see cref="Session.DismissVerdict"/> uses the wire spellings <c>"done"</c> /
/// <c>"needs-human"</c> (see <see cref="Verdict"/> -&gt; <see cref="Wire"/>).
/// </summary>
public sealed class DismissVerdictSignal
{
    /// <summary>Which of the two verdicts the run ended on.</summary>
    public DismissVerdict Verdict { get; init; }

    /// <summary>The single human-readable line explaining the verdict (may be empty).</summary>
    public string Reason { get; init; } = "";

    private const string Marker = "CC-DISMISS";

    /// <summary>The wire spelling stored on the session / DTO for this verdict ("done" | "needs-human").</summary>
    public string Wire => Verdict == DismissVerdict.Done ? "done" : "needs-human";

    /// <summary>
    /// Find the LAST complete <c>CC-DISMISS</c> block in <paramref name="text"/> (typically an agent's final
    /// assistant message) and parse it. Returns null when no complete block is present - the conservative
    /// default that keeps a session open until the agent explicitly declares its verdict. Reading the LAST
    /// block makes the parse idempotent against text that is re-read as the session keeps producing output,
    /// and lets a later verdict supersede an earlier one within the same message.
    /// </summary>
    public static DismissVerdictSignal? ParseLatest(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        DismissVerdictSignal? latest = null;
        for (var i = 0; i < lines.Length; i++)
        {
            // The marker may be fenced or lightly decorated (```, bullets); accept a line that IS the marker
            // once stripped of surrounding backticks/space, so a markdown code fence around the block is fine.
            if (!IsMarkerLine(lines[i]))
                continue;

            var parsed = ParseBlock(lines, i);
            if (parsed is not null)
                latest = parsed;
        }

        return latest;
    }

    private static bool IsMarkerLine(string line) =>
        line.Trim().Trim('`').Trim().Equals(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Parse a single block whose marker line is at <paramref name="markerIndex"/>. Fields are the
    /// <c>key: value</c> lines immediately following the marker, in any order, up to the first line that is
    /// not a recognized field. A block missing a recognized <c>verdict</c> is incomplete and returns null.
    /// </summary>
    private static DismissVerdictSignal? ParseBlock(string[] lines, int markerIndex)
    {
        DismissVerdict? verdict = null;
        var reason = "";

        for (var i = markerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim().Trim('`').Trim();
            if (line.Length == 0)
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                break; // not a key: value field - the block has ended

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "verdict":
                    verdict = ParseVerdict(value);
                    break;
                case "reason":
                    reason = value;
                    break;
                default:
                    // A non-field line after the marker ends the block.
                    return Build(verdict, reason);
            }
        }

        return Build(verdict, reason);
    }

    private static DismissVerdictSignal? Build(DismissVerdict? verdict, string reason)
    {
        if (verdict is null)
        {
            FileLog.Write("[DismissVerdictSignal] incomplete block (missing or unrecognized verdict)");
            return null;
        }

        return new DismissVerdictSignal { Verdict = verdict.Value, Reason = reason };
    }

    private static DismissVerdict? ParseVerdict(string value) => value.ToLowerInvariant() switch
    {
        "done" => DismissVerdict.Done,
        "needs-human" => DismissVerdict.NeedsHuman,
        "needshuman" => DismissVerdict.NeedsHuman,
        _ => null,
    };
}
