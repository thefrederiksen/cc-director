using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Skills;

/// <summary>A skill authoring request violates the content rules. Maps to HTTP 400.</summary>
public sealed class SkillValidationException : Exception
{
    public SkillValidationException(string message) : base(message) { }
}

/// <summary>A skill authoring request lost a race (id already taken, If-Match hash stale). Maps to
/// HTTP 409 so the caller re-reads and retries deliberately instead of clobbering.</summary>
public sealed class SkillConflictException : Exception
{
    public SkillConflictException(string message) : base(message) { }
}

/// <summary>
/// The skill content rules, enforced at the store boundary (the endpoints translate the exceptions to
/// status codes). Two tiers, like workflows: a DRAFT may be skeletal, while PUBLISHING demands what a
/// listed skill cannot be without - a one-line summary an agent can choose from, and a body that
/// actually says how to do the thing.
///
/// THE SUMMARY AND TRIGGER CAPS ARE THE FEATURE, NOT HOUSEKEEPING. Every published skill's summary
/// and triggers ride EVERY session's launch briefing on every machine, so a long summary is not one
/// author's problem - it is a tax on every agent in the fleet forever. They are capped hard and
/// deliberately tighter than the body, which only a session that actually uses the skill ever pays
/// for (devthrottle_internal issue 995).
/// </summary>
public static class SkillValidation
{
    /// <summary>Lowercase slug ids, matching the shipped built-in ids ("move-session", "fleet-comms").</summary>
    public static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    private static readonly Regex FileNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly string[] AllowedFileExtensions = { ".py", ".md", ".txt", ".json" };

    public const int MaxBodyBytes = 200 * 1024;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFilesPerVersion = 20;
    public const int MaxNameChars = 100;

    /// <summary>The register line's budget. One sentence, because every session pays for it.</summary>
    public const int MaxSummaryChars = 200;

    /// <summary>A trigger is a short phrase, and a skill needs a handful, not a thesaurus.</summary>
    public const int MaxTriggerChars = 60;
    public const int MaxTriggersPerSkill = 12;

    public const int MaxShortFieldChars = 200;
    public const int MaxTextFieldChars = 4000;

    /// <summary>Validate a skill id (slug).</summary>
    public static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            throw new SkillValidationException(
                "A skill id must be a lowercase slug: letters, digits, and dashes, starting with a " +
                "letter or digit, 2 to 64 characters (like \"move-session\").");
    }

    /// <summary>The draft-tier rules: identity fields present, every size cap and file rule honored.</summary>
    public static void ValidateDraft(SkillContentRequest content)
    {
        if (content is null)
            throw new SkillValidationException("A skill body is required.");
        if (string.IsNullOrWhiteSpace(content.Name))
            throw new SkillValidationException("A skill needs a name.");
        if (string.IsNullOrWhiteSpace(content.Summary))
            throw new SkillValidationException(
                "A skill needs a one-line summary - it is the only thing an agent sees before " +
                "deciding whether to fetch the skill.");

        CapLength("name", content.Name, MaxNameChars);
        CapLength("summary", content.Summary, MaxSummaryChars);
        CapLength("authoredBy", content.AuthoredBy, MaxShortFieldChars);
        CapLength("changeNote", content.ChangeNote, MaxTextFieldChars);

        // A summary that spans lines would render as extra lines in every agent's briefing, which is
        // how authored text turns into unearned preamble. One line means one line.
        if (content.Summary!.Contains('\n') || content.Summary.Contains('\r'))
            throw new SkillValidationException(
                "The summary must be a single line - it is rendered as one line of every session's " +
                "briefing.");

        var triggers = content.Triggers ?? new List<string>();
        if (triggers.Count > MaxTriggersPerSkill)
            throw new SkillValidationException(
                $"A skill carries at most {MaxTriggersPerSkill} triggers. Pick the phrases that " +
                "actually bring this skill to mind.");
        var seenTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                throw new SkillValidationException("The triggers list contains an empty entry.");
            CapLength("trigger", trigger, MaxTriggerChars);
            if (trigger.Contains('\n') || trigger.Contains('\r'))
                throw new SkillValidationException("A trigger must be a single short phrase.");
            if (!seenTriggers.Add(trigger.Trim()))
                throw new SkillValidationException($"Trigger '{trigger.Trim()}' appears more than once.");
        }

        var body = content.BodyMarkdown ?? "";
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            throw new SkillValidationException($"The skill body is too large (limit {MaxBodyBytes / 1024} KB).");

        var files = content.Files ?? new List<SkillFileDto>();
        if (files.Count > MaxFilesPerVersion)
            throw new SkillValidationException(
                $"A skill version carries at most {MaxFilesPerVersion} supporting files.");
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null)
                throw new SkillValidationException("The files list contains a null entry.");
            ValidateFileName(file.FileName);
            if (Encoding.UTF8.GetByteCount(file.Content ?? "") > MaxFileBytes)
                throw new SkillValidationException(
                    $"Supporting file '{file.FileName}' is too large (limit {MaxFileBytes / 1024} KB).");
            if (!fileNames.Add(file.FileName))
                throw new SkillValidationException(
                    $"Supporting file name '{file.FileName}' appears more than once.");
        }
    }

    /// <summary>The publish-tier rule on top of the draft tier: a listed skill must actually say how
    /// to do the thing. A skill with no body is a line in every briefing that leads nowhere.</summary>
    public static void ValidateForPublish(string bodyMarkdown)
    {
        if (string.IsNullOrWhiteSpace(bodyMarkdown))
            throw new SkillValidationException(
                "A skill cannot publish without a body - the body is what an agent fetches when it " +
                "reaches for the skill, and a skill without one costs every session a briefing line " +
                "that leads nowhere.");
    }

    private static void CapLength(string field, string? value, int maxChars)
    {
        if (value is not null && value.Length > maxChars)
            throw new SkillValidationException($"The {field} is too long (limit {maxChars} characters).");
    }

    private static void ValidateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !FileNamePattern.IsMatch(fileName))
            throw new SkillValidationException(
                $"Supporting file name '{fileName}' is invalid: bare file names only (letters, digits, " +
                "dot, dash, underscore), never a path.");
        if (!AllowedFileExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new SkillValidationException(
                $"Supporting file '{fileName}' has a disallowed extension. Allowed: " +
                string.Join(", ", AllowedFileExtensions) + ".");
    }
}
