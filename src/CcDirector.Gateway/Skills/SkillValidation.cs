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
/// WHAT A SKILL IS, AND WHY THESE RULES LOOK LIKE THIS. A skill is a DIRECTORY in the Agent Skills
/// open standard (agentskills.io, stewarded by the Agentic AI Foundation): a required SKILL.md at its
/// root plus, in the specification's own words, "any additional files or directories". Every agent
/// this product supervises - Claude Code, Codex, Gemini, Grok, pi, Copilot, Cursor, opencode - reads
/// that same directory, byte for byte. So these rules exist to accept a CONFORMING skill and refuse a
/// DANGEROUS one, and for no other purpose. They deliberately do NOT define a DevThrottle-shaped
/// skill: a shape of our own would mean every skill in the world needs converting to come in, and
/// every skill of ours needs converting before any agent can use it.
///
/// That is why the old extension allow-list is gone. An allow-list cannot be completed when the
/// standard permits any file, and each extension we failed to guess was a skill that could not be
/// stored at all - including two in this very repository. What remains is a short deny-list of files
/// that are dangerous to write onto a machine unasked, plus path validation strict enough that a
/// bundle can always be materialized safely.
///
/// THE SUMMARY AND TRIGGER CAPS ARE THE FEATURE, NOT HOUSEKEEPING. Every published skill's summary
/// and triggers ride EVERY session's launch briefing on every machine, so a long summary is not one
/// author's problem - it is a tax on every agent in the fleet forever. They are capped hard and
/// deliberately tighter than the body, which only a session that actually uses the skill ever pays
/// for (devthrottle_internal issue 995). Codex caps its own initial skills listing at 8000 characters
/// or two percent of the context window, which is independent confirmation that these caps are right.
/// </summary>
public static class SkillValidation
{
    /// <summary>Lowercase slug ids, matching the shipped built-in ids ("move-session", "fleet-comms").
    /// This is also the skill's DIRECTORY NAME on disk and the standard's frontmatter <c>name</c>,
    /// which is why it is the standard's name rule and not a looser one.</summary>
    public static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    /// <summary>One path segment: the same safe-character set the old bare-name rule enforced, now
    /// applied per segment instead of by forbidding the separator outright.</summary>
    private static readonly Regex SegmentPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Extensions refused outright. Short on purpose: scripts and command-line programs are the POINT
    /// of supporting files, so .py, .sh, .ps1, .bat, .cmd, .js and .exe are all legitimate skill
    /// content. What is denied here cannot be legitimate skill content and is actively hostile on a
    /// Windows machine - a shortcut resolves to an arbitrary target the reviewer never sees, and an
    /// Explorer command file is a long-known credential-theft vector.
    /// </summary>
    private static readonly string[] DeniedFileExtensions = { ".lnk", ".url", ".scf" };

    /// <summary>
    /// Paths refused because they would silently PROMOTE a skill from instructions into code that runs
    /// without anyone asking for it. Claude Code documents that a skill folder containing
    /// <c>.claude-plugin/plugin.json</c> loads as a PLUGIN and may then bundle hooks and tool servers;
    /// an <c>.mcp.json</c> at a skill root is the same escalation by another route. A skill in this
    /// library is fetched by every machine in a fleet, so that promotion must not be something a skill
    /// can grant itself by including a file. Denied by exact path, not by extension - these are
    /// ordinary JSON file names everywhere else.
    /// </summary>
    private static readonly string[] DeniedPaths = { ".claude-plugin/plugin.json", ".mcp.json" };

    /// <summary>Windows device names, reserved in EVERY directory and with any extension. A file
    /// called "nul" is unopenable and genuinely hard to delete on Windows, and this repository has
    /// been bitten by stray ones before - so a skill can never introduce one.</summary>
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public const int MaxBodyBytes = 200 * 1024;

    /// <summary>Per-file cap, over the DECODED bytes - so a base64 payload is measured as the file it
    /// will become on disk, not as the larger string that carried it.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Whole-version cap over the decoded bytes of every file. A per-file cap alone bounds
    /// nothing when the file count runs to the hundreds.</summary>
    public const int MaxVersionBytes = 25 * 1024 * 1024;

    /// <summary>Ten times the largest real skill measured across this repository and this machine
    /// (eleven files). A limit meant to catch a runaway, not to shape authoring.</summary>
    public const int MaxFilesPerVersion = 200;

    public const int MaxFilePathChars = 200;

    /// <summary>The standard advises keeping file references one level deep from SKILL.md. Five
    /// segments is generous against that advice while still bounding what we materialize.</summary>
    public const int MaxFilePathSegments = 5;

    public const int MaxNameChars = 100;

    /// <summary>The register line's budget. One sentence, because every session pays for it.</summary>
    public const int MaxSummaryChars = 200;

    /// <summary>A trigger is a short phrase, and a skill needs a handful, not a thesaurus.</summary>
    public const int MaxTriggerChars = 60;
    public const int MaxTriggersPerSkill = 12;

    public const int MaxShortFieldChars = 200;
    public const int MaxTextFieldChars = 4000;

    // ---- the standard's optional frontmatter -------------------------------------------------------
    // Held so a skill authored anywhere else survives a round trip through this library unchanged, and
    // so SKILL.md can be written back out as its author wrote it. The caps are the specification's own.

    public const int MaxLicenseChars = 200;
    public const int MaxCompatibilityChars = 500;
    public const int MaxAllowedToolsChars = 500;
    public const int MaxMetadataEntries = 32;
    public const int MaxMetadataKeyChars = 64;
    public const int MaxMetadataValueChars = 500;

    /// <summary>The two legal values of <see cref="SkillFileDto.Encoding"/>.</summary>
    public const string EncodingUtf8 = "utf8";
    public const string EncodingBase64 = "base64";

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
        CapLength("license", content.License, MaxLicenseChars);
        CapLength("compatibility", content.Compatibility, MaxCompatibilityChars);
        CapLength("allowedTools", content.AllowedTools, MaxAllowedToolsChars);

        // A summary that spans lines would render as extra lines in every agent's briefing, which is
        // how authored text turns into unearned preamble. One line means one line.
        if (content.Summary!.Contains('\n') || content.Summary.Contains('\r'))
            throw new SkillValidationException(
                "The summary must be a single line - it is rendered as one line of every session's " +
                "briefing.");

        ValidateTriggers(content.Triggers);
        ValidateMetadata(content.Metadata);

        var body = content.BodyMarkdown ?? "";
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            throw new SkillValidationException($"The skill body is too large (limit {MaxBodyBytes / 1024} KB).");

        ValidateFiles(content.Files);
    }

    private static void ValidateTriggers(List<string>? triggersOrNull)
    {
        var triggers = triggersOrNull ?? new List<string>();
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
    }

    private static void ValidateMetadata(Dictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return;
        if (metadata.Count > MaxMetadataEntries)
            throw new SkillValidationException(
                $"A skill carries at most {MaxMetadataEntries} metadata entries.");
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new SkillValidationException("A metadata key cannot be empty.");
            CapLength($"metadata key '{key}'", key, MaxMetadataKeyChars);
            CapLength($"metadata value for '{key}'", value, MaxMetadataValueChars);
        }
    }

    /// <summary>Every supporting file: a safe relative path, a legal encoding, and size caps applied
    /// to the DECODED bytes both per file and across the whole version.</summary>
    private static void ValidateFiles(List<SkillFileDto>? filesOrNull)
    {
        var files = filesOrNull ?? new List<SkillFileDto>();
        if (files.Count > MaxFilesPerVersion)
            throw new SkillValidationException(
                $"A skill version carries at most {MaxFilesPerVersion} supporting files.");

        // Case-insensitive, because the same bundle has to materialize on Windows and on Linux, and a
        // pair differing only in case would collide on one of them.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files)
        {
            if (file is null)
                throw new SkillValidationException("The files list contains a null entry.");
            ValidateFilePath(file.FileName);

            var byteCount = DecodedByteCount(file);
            if (byteCount > MaxFileBytes)
                throw new SkillValidationException(
                    $"Supporting file '{file.FileName}' is too large " +
                    $"(limit {MaxFileBytes / (1024 * 1024)} MB).");
            totalBytes += byteCount;

            if (!paths.Add(file.FileName.Trim()))
                throw new SkillValidationException(
                    $"Supporting file '{file.FileName}' appears more than once.");
        }

        if (totalBytes > MaxVersionBytes)
            throw new SkillValidationException(
                $"The skill's files total {totalBytes / (1024 * 1024)} MB, over the " +
                $"{MaxVersionBytes / (1024 * 1024)} MB limit for one version.");
    }

    /// <summary>The size a file will occupy once written to disk. Base64 content is measured decoded,
    /// so the cap means what it says regardless of how the bytes travelled.</summary>
    /// <exception cref="SkillValidationException">The encoding is unknown, or base64 content will not
    /// decode - caught HERE rather than at materialization time, where it would already be a
    /// half-written bundle on someone's machine.</exception>
    public static int DecodedByteCount(SkillFileDto file)
    {
        var content = file.Content ?? "";
        switch (NormalizeEncoding(file.Encoding))
        {
            case EncodingUtf8:
                return Encoding.UTF8.GetByteCount(content);
            case EncodingBase64:
                try
                {
                    return Convert.FromBase64String(content).Length;
                }
                catch (FormatException)
                {
                    throw new SkillValidationException(
                        $"Supporting file '{file.FileName}' is declared base64 but its content is not " +
                        "valid base64.");
                }
            default:
                throw new SkillValidationException(
                    $"Supporting file '{file.FileName}' has an unknown encoding '{file.Encoding}'. " +
                    $"Use '{EncodingUtf8}' for text or '{EncodingBase64}' for binary content.");
        }
    }

    /// <summary>An absent or blank encoding means text: the field was added after the first release,
    /// so an older client that sends no encoding is sending exactly what it always sent.</summary>
    public static string NormalizeEncoding(string? encoding) =>
        string.IsNullOrWhiteSpace(encoding) ? EncodingUtf8 : encoding.Trim().ToLowerInvariant();

    /// <summary>
    /// Validate one supporting file's RELATIVE PATH. A skill is a directory tree - "references/tracing.md"
    /// and "scripts/build.sh" are what real skills look like - so a path separator is legal here, and
    /// everything that makes a path dangerous is refused instead:
    ///
    ///  - traversal ("..") and absolute paths, which would write outside the skill's directory,
    ///  - backslashes and drive letters, so one bundle means one thing on every platform,
    ///  - reserved Windows device names in any segment,
    ///  - files that promote a skill from instructions into automatically-running code.
    /// </summary>
    public static void ValidateFilePath(string? filePath)
    {
        var path = (filePath ?? "").Trim();
        if (string.IsNullOrEmpty(path))
            throw new SkillValidationException("A supporting file needs a path.");
        if (path.Length > MaxFilePathChars)
            throw new SkillValidationException(
                $"Supporting file path '{path}' is too long (limit {MaxFilePathChars} characters).");

        if (path.Contains('\\'))
            throw new SkillValidationException(
                $"Supporting file path '{path}' uses a backslash. Skill paths are always written with " +
                "forward slashes so one bundle means the same thing on every platform.");
        if (path.StartsWith('/'))
            throw new SkillValidationException(
                $"Supporting file path '{path}' is absolute. Paths are relative to the skill's own " +
                "directory.");
        if (path.EndsWith('/'))
            throw new SkillValidationException(
                $"Supporting file path '{path}' ends with a slash, so it names a directory rather than " +
                "a file. Directories are created from the paths of the files inside them.");
        if (path.Length >= 2 && path[1] == ':')
            throw new SkillValidationException(
                $"Supporting file path '{path}' looks like a drive-qualified path. Paths are relative " +
                "to the skill's own directory.");

        var segments = path.Split('/');
        if (segments.Length > MaxFilePathSegments)
            throw new SkillValidationException(
                $"Supporting file path '{path}' is {segments.Length} levels deep, over the limit of " +
                $"{MaxFilePathSegments}. Keep a skill's files close to its SKILL.md.");

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new SkillValidationException(
                    $"Supporting file path '{path}' has an empty segment (a doubled slash).");
            if (segment is "." or "..")
                throw new SkillValidationException(
                    $"Supporting file path '{path}' walks the directory tree. A skill's files must all " +
                    "sit inside the skill's own directory.");
            if (!SegmentPattern.IsMatch(segment))
                throw new SkillValidationException(
                    $"Supporting file path '{path}' has an invalid segment '{segment}': letters, " +
                    "digits, dot, dash and underscore only.");

            // Reserved on Windows with ANY extension, so compare the stem: "nul.txt" is as unopenable
            // as "nul".
            var stem = segment.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem))
                throw new SkillValidationException(
                    $"Supporting file path '{path}' uses '{stem}', a name Windows reserves for a " +
                    "device. A file with that name cannot be written or deleted normally.");
        }

        if (DeniedFileExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new SkillValidationException(
                $"Supporting file '{path}' has a denied extension. Scripts and programs are welcome in " +
                "a skill, but shortcut files are not: they resolve to a target nobody reviewing the " +
                "skill can see. Denied: " + string.Join(", ", DeniedFileExtensions) + ".");

        if (DeniedPaths.Any(denied => path.Equals(denied, StringComparison.OrdinalIgnoreCase)))
            throw new SkillValidationException(
                $"Supporting file '{path}' would turn this skill into a plugin that can load hooks and " +
                "tool servers automatically on every machine that fetches it. A skill in the library " +
                "carries instructions and the files they use, and cannot grant itself that.");
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
}
