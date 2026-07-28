using System.Collections.Generic;
using System.Text;
using CcDirector.Core.Account;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The placeholder tokens a fleet-preamble template may use. These are the ONLY tokens the renderer
/// substitutes; every other run of square brackets in the text is left exactly as written.
/// </summary>
public static class FleetPreamblePlaceholders
{
    /// <summary>The session's full identifier.</summary>
    public const string SessionId = "[SESSION_ID]";

    /// <summary>The first eight characters of the session id - what the fleet commands take.</summary>
    public const string SessionShortId = "[SESSION_SHORT_ID]";

    /// <summary>The session's display name, or "(unnamed)".</summary>
    public const string SessionName = "[SESSION_NAME]";

    /// <summary>The machine the session runs on.</summary>
    public const string Machine = "[MACHINE]";

    /// <summary>The session's repository / working directory.</summary>
    public const string RepoPath = "[REPO_PATH]";

    /// <summary>The signed-in user's display name. Only meaningful inside an [IF_SIGNED_IN] block.</summary>
    public const string UserName = "[USER_NAME]";

    /// <summary>The signed-in user's email. Only meaningful inside an [IF_SIGNED_IN] block.</summary>
    public const string UserEmail = "[USER_EMAIL]";

    /// <summary>The workflow catalog index block (Workflows mission, phase 5): one line per published
    /// fleet workflow plus how to fetch its conduct. Empty when the Director has never reached a
    /// Gateway or the catalog is empty.</summary>
    public const string WorkflowIndex = "[WORKFLOW_INDEX]";

    /// <summary>The skill register index block (the central skill library): one line per available
    /// skill plus how to fetch one in full. Empty when the Director has never reached a Gateway or the
    /// register is empty. This block is what replaces installing skill files on the machine.</summary>
    public const string SkillIndex = "[SKILL_INDEX]";

    /// <summary>Opens a block kept only when a user is signed in.</summary>
    public const string IfSignedIn = "[IF_SIGNED_IN]";

    /// <summary>Closes an [IF_SIGNED_IN] block.</summary>
    public const string EndIf = "[END_IF]";

    /// <summary>Every substitution token, for documentation and for the Settings tab to list.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        SessionId, SessionShortId, SessionName, Machine, RepoPath, UserName, UserEmail, WorkflowIndex,
        SkillIndex,
    };
}

/// <summary>
/// Renders a fleet-preamble template - ours or the user's - into the exact text one session receives.
///
/// The whole design is SUBSTITUTION, not evaluation: there is no expression language, no loops, and
/// exactly ONE conditional ([IF_SIGNED_IN]), which exists only because the signed-in-user line has to
/// vanish completely when nobody is signed in and a half-rendered "The user of this session is  ()."
/// would be worse than no line. Anything more would be a language the user has to learn in order to
/// edit a paragraph.
///
/// SUBSTITUTION IS EXACT-TOKEN ONLY. The default text itself opens with the literal "[CC Director
/// fleet]", and a user may write anything bracket-shaped in their own prose. So the renderer replaces
/// only the known tokens above and never pattern-matches brackets. Bracketed text that is not a known
/// token is ordinary text and survives verbatim.
///
/// THE RULE, STATED EXACTLY, because a vaguer version of it was wrong: a known token is replaced
/// WHEREVER it appears, including when it sits inside other brackets - "[[SESSION_ID]]" renders as
/// "[" + the id + "]". There is deliberately no escape syntax for writing a literal "[SESSION_ID]"
/// that does not expand. That is a real if rare limitation, accepted because the alternative is an
/// escaping language the user must learn in order to edit a paragraph, and because the tokens are
/// all-capital names nobody writes by accident. It is pinned by test rather than left to be
/// rediscovered.
/// </summary>
public static class FleetPreambleRenderer
{
    /// <summary>
    /// Render <paramref name="template"/> for one session. <paramref name="name"/> may be null/empty
    /// (an unnamed session). <paramref name="user"/> is the signed-in DevThrottle user; when null, or
    /// when they have no email, every [IF_SIGNED_IN] block is dropped whole - no blank line, no "null".
    /// </summary>
    /// <exception cref="FleetPreambleTemplateException">
    /// The template's conditional markers are unbalanced. This is thrown rather than papered over: a
    /// malformed template means the text reaching the agent is not the text the author intended, and
    /// silently guessing which half they meant is how the wrong instructions reach seven agents.
    /// Callers that accept user-authored templates validate with <see cref="Validate"/> at the point
    /// the user saves, so this surfaces at the edit, not at a session launch.
    /// </exception>
    public static string Render(
        string template,
        string sessionId,
        string? name,
        string machine,
        string repoPath,
        SignedInUser? user = null,
        string workflowIndex = "",
        string skillIndex = "")
    {
        var signedIn = user is not null && !string.IsNullOrWhiteSpace(user.Email);
        var kept = ApplyConditionals(template, signedIn);

        var shortId = sessionId.Length >= 8 ? sessionId.Substring(0, 8) : sessionId;
        var displayName = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FleetPreamblePlaceholders.SessionId] = sessionId,
            [FleetPreamblePlaceholders.SessionShortId] = shortId,
            [FleetPreamblePlaceholders.SessionName] = displayName,
            [FleetPreamblePlaceholders.Machine] = machine,
            [FleetPreamblePlaceholders.RepoPath] = repoPath,
            // Outside an [IF_SIGNED_IN] block these can appear with nobody signed in. They render
            // empty rather than leaking the token text to the agent.
            [FleetPreamblePlaceholders.UserName] = signedIn ? user!.DisplayName : "",
            [FleetPreamblePlaceholders.UserEmail] = signedIn ? user!.Email : "",
            // The workflow index renders whatever block the caller supplies - possibly empty. A user
            // running their own template gets the index only where they wrote the token, the same
            // contract as every other placeholder.
            [FleetPreamblePlaceholders.WorkflowIndex] = workflowIndex,
            // The skill index follows the same contract: whatever block the caller supplies, possibly
            // empty, rendered only where the token appears.
            [FleetPreamblePlaceholders.SkillIndex] = skillIndex,
        };

        return Substitute(kept, values);
    }

    /// <summary>
    /// Check a template's conditional markers are balanced. Returns null when the template is fine,
    /// or a plain-English description of the problem to show the user.
    /// </summary>
    public static string? Validate(string template)
    {
        var depth = 0;
        var lineNumber = 0;

        foreach (var line in SplitLines(template))
        {
            lineNumber++;
            var trimmed = line.Trim();

            if (trimmed == FleetPreamblePlaceholders.IfSignedIn)
            {
                if (depth > 0)
                    return $"Line {lineNumber} opens another {FleetPreamblePlaceholders.IfSignedIn} " +
                           $"before the previous one was closed with {FleetPreamblePlaceholders.EndIf}. " +
                           "These blocks cannot be nested.";
                depth++;
            }
            else if (trimmed == FleetPreamblePlaceholders.EndIf)
            {
                if (depth == 0)
                    return $"Line {lineNumber} has an {FleetPreamblePlaceholders.EndIf} with no " +
                           $"matching {FleetPreamblePlaceholders.IfSignedIn} above it.";
                depth--;
            }
        }

        return depth > 0
            ? $"An {FleetPreamblePlaceholders.IfSignedIn} block was never closed with " +
              $"{FleetPreamblePlaceholders.EndIf}."
            : null;
    }

    /// <summary>
    /// Replace known tokens in ONE left-to-right pass over the template.
    ///
    /// A single pass is the whole correctness argument, and it is not a micro-optimisation. Chained
    /// String.Replace calls run over their own output, so a value substituted early is still visible
    /// to every later replacement: a session named "[MACHINE]" would come out as the machine name.
    /// Scanning once means a substituted VALUE is never re-examined, so what the user's data says can
    /// never be mistaken for what their template says.
    /// </summary>
    private static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        var output = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var end = text.IndexOf(']', i);
                if (end > i)
                {
                    var token = text[i..(end + 1)];
                    if (values.TryGetValue(token, out var value))
                    {
                        output.Append(value);
                        i = end + 1;
                        continue;
                    }
                }
            }

            // Not a known token: ordinary text, including any bracket-shaped prose.
            output.Append(text[i]);
            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Drop the marker lines, and - when nobody is signed in - the lines they wrap. Whole lines are
    /// removed, so a dropped block leaves no blank line behind.
    /// </summary>
    private static string ApplyConditionals(string template, bool signedIn)
    {
        var problem = Validate(template);
        if (problem is not null)
            throw new FleetPreambleTemplateException(problem);

        var output = new StringBuilder();
        var inBlock = false;
        var first = true;

        foreach (var line in SplitLines(template))
        {
            var trimmed = line.Trim();

            if (trimmed == FleetPreamblePlaceholders.IfSignedIn)
            {
                inBlock = true;
                continue;
            }

            if (trimmed == FleetPreamblePlaceholders.EndIf)
            {
                inBlock = false;
                continue;
            }

            if (inBlock && !signedIn)
                continue;

            if (!first)
                output.Append('\n');
            output.Append(line);
            first = false;
        }

        return output.ToString();
    }

    /// <summary>
    /// Split on newlines, tolerating Windows line endings. A user pasting text into the Settings tab
    /// will produce \r\n; the agents want \n, and a stray \r renders as a control character in a
    /// terminal, so the \r is dropped here rather than shipped to seven agents.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (var line in text.Split('\n'))
            yield return line.EndsWith('\r') ? line[..^1] : line;
    }
}

/// <summary>Thrown when a fleet-preamble template cannot be rendered as written.</summary>
public class FleetPreambleTemplateException : Exception
{
    public FleetPreambleTemplateException(string message) : base(message) { }
}
