using System.Text;

namespace CcDirector.Gateway.Supervision;

/// <summary>
/// Step 3 of the funnel (issue #915): the rare model fallback. It is reached ONLY when the turn ended on
/// something that announces itself as an error and step 2's table did not recognize it - never on a Working
/// session, never on a cleanly finished one, and never on the majority of idle transitions. It is also the
/// only tier that sends terminal text off the machine, which is why it is independently switchable.
///
/// The question is tight and the answer set is fixed, so the engine acts on a verdict rather than on prose.
/// An unparsable or absent answer is NOT a verdict: it stays unclassified and escalates. A model that
/// mumbles must never be read as permission to type into somebody's session.
/// </summary>
public static class SupervisorVerdict
{
    /// <summary>The fixed answer set. Anything else is no verdict at all.</summary>
    public const string TransientRecoverable = "transient_recoverable";
    public const string NeedsHuman = "needs_human";
    public const string HealthyDone = "healthy_done";
    public const string ContextFull = "context_full";

    /// <summary>How many lines of the screen tail the question carries.</summary>
    public const int PromptTailLines = 20;

    /// <summary>
    /// Map a verdict word onto the engine's vocabulary. An unrecognized or missing verdict maps to
    /// <see cref="SessionFaultClass.Unclassified"/>, which escalates - the fail-safe direction, because the
    /// alternative is acting on a session on the strength of an answer nobody understood.
    /// </summary>
    public static SessionFaultClass Map(string? verdict)
    {
        var word = Normalize(verdict);
        return word switch
        {
            TransientRecoverable => SessionFaultClass.TransientTransport,
            NeedsHuman => SessionFaultClass.NonRecoverable,
            HealthyDone => SessionFaultClass.None,
            ContextFull => SessionFaultClass.ContextFull,
            _ => SessionFaultClass.Unclassified,
        };
    }

    /// <summary>
    /// Pull the verdict word out of a model reply. Tolerant of the wrapping a chat model adds (quotes,
    /// backticks, a trailing full stop, a leading "verdict:") but NOT of a reply that names two verdicts -
    /// that is an undecided answer, and it returns null rather than picking the first one.
    /// </summary>
    public static string? Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var lower = reply.ToLowerInvariant();
        string? found = null;
        foreach (var candidate in All)
        {
            if (!lower.Contains(candidate, StringComparison.Ordinal)) continue;
            if (found is not null) return null;      // two verdicts named - undecided
            found = candidate;
        }
        return found;
    }

    /// <summary>Every legal verdict word.</summary>
    public static readonly string[] All = { TransientRecoverable, NeedsHuman, HealthyDone, ContextFull };

    /// <summary>
    /// Build the one question the fallback asks. It states the closed answer set, forbids prose, and gives
    /// the model only the tail of the screen - the same window the deterministic classifier looked at, plus
    /// a little more context.
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<string>? rows, int tailLines = PromptTailLines)
    {
        var tail = Tail(rows, tailLines);
        var sb = new StringBuilder();
        sb.AppendLine("A coding-agent session in a terminal has stopped and is waiting. Below is the tail of its");
        sb.AppendLine("screen. Decide why it stopped and answer with EXACTLY ONE of these words and nothing else:");
        sb.AppendLine();
        sb.AppendLine($"  {TransientRecoverable}  - it died on a temporary network or transport failure that will clear by itself");
        sb.AppendLine($"  {NeedsHuman}            - it stopped on something a person must fix (no allowance or credit left, a sign-in failure, a question it is waiting on)");
        sb.AppendLine($"  {HealthyDone}           - it finished its work normally and is waiting for the next instruction");
        sb.AppendLine($"  {ContextFull}           - it ran out of context window");
        sb.AppendLine();
        sb.AppendLine("If you are not sure, answer needs_human. Answer with one word only.");
        sb.AppendLine();
        sb.AppendLine("--- screen tail ---");
        foreach (var line in tail) sb.AppendLine(line);
        sb.AppendLine("--- end ---");
        return sb.ToString();
    }

    private static IReadOnlyList<string> Tail(IReadOnlyList<string>? rows, int tailLines)
    {
        if (rows is null || rows.Count == 0 || tailLines <= 0) return Array.Empty<string>();
        var content = rows.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd()).ToList();
        if (content.Count <= tailLines) return content;
        return content.GetRange(content.Count - tailLines, tailLines);
    }

    private static string Normalize(string? verdict)
        => (verdict ?? "").Trim().Trim('"', '\'', '`', '.', ' ').ToLowerInvariant();
}
