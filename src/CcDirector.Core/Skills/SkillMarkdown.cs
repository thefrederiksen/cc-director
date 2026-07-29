using System.Text;

namespace CcDirector.Core.Skills;

/// <summary>
/// Composes the <c>SKILL.md</c> an agent actually reads: the Agent Skills standard's YAML frontmatter
/// followed by the body the library holds.
///
/// The Gateway stores the body WITHOUT frontmatter, because the register already owns the identity
/// fields - a skill's id, its one-line summary, its triggers - and duplicating them into the body
/// would let the two disagree. The frontmatter is therefore composed here, at the moment a skill is
/// written to disk for an agent, from the single source that already exists.
///
/// The mapping to the standard, which is not quite one-to-one and is worth stating:
///  - <c>name</c> is the skill's ID, not its display name. The standard requires a lowercase slug
///    that MATCHES THE DIRECTORY NAME, and the directory is named by the id. A display name like
///    "Move a session" would fail every agent's validation.
///  - <c>description</c> is our summary. The standard uses the description for the same job we use
///    the summary for: it is the only thing an agent sees before deciding whether to use the skill.
///  - the triggers ride the description, because the standard has nowhere else to put them and the
///    phrases that should bring a skill to mind are exactly what makes a description matchable.
/// </summary>
public static class SkillMarkdown
{
    /// <summary>Compose the complete SKILL.md text for one skill.</summary>
    public static string Compose(
        string id,
        string summary,
        IReadOnlyList<string>? triggers,
        string bodyMarkdown,
        string? license = null,
        string? compatibility = null,
        string? allowedTools = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var description = ComposeDescription(summary, triggers);

        var text = new StringBuilder();
        text.Append("---\n");
        text.Append("name: ").Append(id).Append('\n');
        text.Append("description: ").Append(Quote(description)).Append('\n');
        if (!string.IsNullOrWhiteSpace(license))
            text.Append("license: ").Append(Quote(license!)).Append('\n');
        if (!string.IsNullOrWhiteSpace(compatibility))
            text.Append("compatibility: ").Append(Quote(compatibility!)).Append('\n');
        if (!string.IsNullOrWhiteSpace(allowedTools))
            text.Append("allowed-tools: ").Append(Quote(allowedTools!)).Append('\n');
        if (metadata is { Count: > 0 })
        {
            text.Append("metadata:\n");
            foreach (var (key, value) in metadata.OrderBy(e => e.Key, StringComparer.Ordinal))
                text.Append("  ").Append(key).Append(": ").Append(Quote(value ?? "")).Append('\n');
        }
        text.Append("---\n\n");
        text.Append(bodyMarkdown ?? "");
        return text.ToString();
    }

    /// <summary>The description an agent matches against: the summary, with the trigger phrases
    /// appended as the "when to use it" half the standard asks for.</summary>
    public static string ComposeDescription(string summary, IReadOnlyList<string>? triggers)
    {
        var text = (summary ?? "").Trim();
        var phrases = (triggers ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
        if (phrases.Count > 0)
            text += $" Use when the task involves: {string.Join(", ", phrases)}.";
        // The standard caps a description at 1024 characters and agents validate it. Our own summary
        // and trigger caps make this unreachable in practice; the guard is here so a cap raised
        // upstream can never silently produce a SKILL.md that every agent rejects.
        return text.Length <= 1024 ? text : text[..1024].TrimEnd();
    }

    /// <summary>A double-quoted YAML scalar. Quoting ALWAYS rather than only when it looks necessary:
    /// a summary containing a colon, a leading percent, or a trailing colon is ordinary English and
    /// would otherwise change the meaning of the document or fail to parse.</summary>
    private static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");
        return $"\"{escaped}\"";
    }
}
