using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Workflows;

/// <summary>A workflow authoring request violates the content rules. Maps to HTTP 400.</summary>
public sealed class WorkflowValidationException : Exception
{
    public WorkflowValidationException(string message) : base(message) { }
}

/// <summary>A workflow authoring request lost a race (id already taken, If-Match hash stale). Maps to
/// HTTP 409 so the caller re-reads and retries deliberately instead of clobbering.</summary>
public sealed class WorkflowConflictException : Exception
{
    public WorkflowConflictException(string message) : base(message) { }
}

/// <summary>
/// The workflow content rules, enforced at the store boundary (the endpoints translate the exceptions
/// to status codes). Two tiers on purpose: a DRAFT may be skeletal (the Cockpit's add dialog supplies
/// only a name and summary and hands authoring to an agent), while PUBLISHING demands the pieces a
/// listed catalog entry cannot be without - the legacy read contract promises every listed workflow
/// has steps with a doer and a done, and the whole feature is pointless without instructions.
/// </summary>
public static class WorkflowValidation
{
    /// <summary>Lowercase slug ids, matching the shipped built-in ids ("mission", "standalone-with-review").</summary>
    public static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);

    private static readonly Regex FileNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly string[] AllowedFileExtensions = { ".py", ".md", ".txt", ".json" };

    public const int MaxInstructionsBytes = 200 * 1024;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFilesPerVersion = 20;
    // Every other field is bounded too - an authenticated caller within the host body limit must not
    // be able to persist megabytes into a name column or a ten-thousand-step list.
    public const int MaxShortFieldChars = 200;
    public const int MaxTextFieldChars = 4000;
    public const int MaxStepsPerVersion = 50;
    public const int MaxCriteriaPerVersion = 50;

    /// <summary>Validate a workflow id (slug).</summary>
    public static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            throw new WorkflowValidationException(
                "A workflow id must be a lowercase slug: letters, digits, and dashes, starting with a " +
                "letter or digit, 2 to 64 characters (like \"release-train\").");
    }

    /// <summary>The draft-tier rules: identity fields present, every size cap and file rule honored.</summary>
    public static void ValidateDraft(WorkflowContentRequest content)
    {
        if (content is null)
            throw new WorkflowValidationException("A workflow body is required.");
        if (string.IsNullOrWhiteSpace(content.Name))
            throw new WorkflowValidationException("A workflow needs a name.");
        if (string.IsNullOrWhiteSpace(content.Summary))
            throw new WorkflowValidationException("A workflow needs a one-line summary.");

        CapLength("name", content.Name, MaxShortFieldChars);
        CapLength("summary", content.Summary, MaxTextFieldChars);
        CapLength("whenToUse", content.WhenToUse, MaxTextFieldChars);
        CapLength("humanCheckpoint", content.HumanCheckpoint, MaxTextFieldChars);
        CapLength("authoredBy", content.AuthoredBy, MaxShortFieldChars);
        CapLength("changeNote", content.ChangeNote, MaxTextFieldChars);

        var instructions = content.InstructionsMarkdown ?? "";
        if (System.Text.Encoding.UTF8.GetByteCount(instructions) > MaxInstructionsBytes)
            throw new WorkflowValidationException(
                $"The instructions are too large (limit {MaxInstructionsBytes / 1024} KB).");

        var steps = content.Steps ?? new List<WorkflowStepDto>();
        if (steps.Count > MaxStepsPerVersion)
            throw new WorkflowValidationException(
                $"A workflow version carries at most {MaxStepsPerVersion} steps.");
        foreach (var step in steps)
        {
            // JSON like "steps":[null] deserializes to a null element - reject it as the bad input it
            // is rather than letting it surface later as an unhandled 500.
            if (step is null)
                throw new WorkflowValidationException("The steps list contains a null entry.");
            CapLength("step name", step.Name, MaxShortFieldChars);
            CapLength("step description", step.Description, MaxTextFieldChars);
            CapLength("step doer", step.Doer, MaxShortFieldChars);
            CapLength("step reviewer", step.Reviewer, MaxShortFieldChars);
            CapLength("step done", step.Done, MaxTextFieldChars);
        }

        var criteria = content.OutcomeCriteria ?? new List<WorkflowOutcomeCriterionDto>();
        if (criteria.Count > MaxCriteriaPerVersion)
            throw new WorkflowValidationException(
                $"A workflow version carries at most {MaxCriteriaPerVersion} outcome criteria.");
        var criterionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in criteria)
        {
            if (criterion is null)
                throw new WorkflowValidationException("The outcome criteria list contains a null entry.");
            if (string.IsNullOrWhiteSpace(criterion.CriterionId) || !IdPattern.IsMatch(criterion.CriterionId))
                throw new WorkflowValidationException(
                    $"Outcome criterion id '{criterion.CriterionId}' must be a lowercase slug.");
            if (string.IsNullOrWhiteSpace(criterion.Description))
                throw new WorkflowValidationException(
                    $"Outcome criterion '{criterion.CriterionId}' needs a description.");
            CapLength("criterion description", criterion.Description, MaxTextFieldChars);
            CapLength("criterion proofHint", criterion.ProofHint, MaxTextFieldChars);
            if (!criterionIds.Add(criterion.CriterionId))
                throw new WorkflowValidationException(
                    $"Outcome criterion id '{criterion.CriterionId}' appears more than once.");
        }

        var files = content.Files ?? new List<WorkflowFileDto>();
        if (files.Count > MaxFilesPerVersion)
            throw new WorkflowValidationException(
                $"A workflow version carries at most {MaxFilesPerVersion} helper files.");
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null)
                throw new WorkflowValidationException("The files list contains a null entry.");
            ValidateFileName(file.FileName);
            if (System.Text.Encoding.UTF8.GetByteCount(file.Content ?? "") > MaxFileBytes)
                throw new WorkflowValidationException(
                    $"Helper file '{file.FileName}' is too large (limit {MaxFileBytes / 1024} KB).");
            if (!fileNames.Add(file.FileName))
                throw new WorkflowValidationException(
                    $"Helper file name '{file.FileName}' appears more than once.");
        }
    }

    private static void CapLength(string field, string? value, int maxChars)
    {
        if (value is not null && value.Length > maxChars)
            throw new WorkflowValidationException(
                $"The {field} is too long (limit {maxChars} characters).");
    }

    /// <summary>The publish-tier rules on top of the draft tier: a listed workflow must actually say
    /// how to do the work.</summary>
    public static void ValidateForPublish(
        string instructionsMarkdown, IReadOnlyList<WorkflowStepDto> steps)
    {
        if (string.IsNullOrWhiteSpace(instructionsMarkdown))
            throw new WorkflowValidationException(
                "A workflow cannot publish without instructions - the instruction markdown is the " +
                "conduct the fleet follows.");
        if (steps.Count == 0)
            throw new WorkflowValidationException(
                "A workflow cannot publish without at least one step.");
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Doer) || string.IsNullOrWhiteSpace(step.Done))
                throw new WorkflowValidationException(
                    $"Step '{step.Name}' needs a doer and a definition of done - a step without them " +
                    "is a wish, not a workflow step.");
        }
    }

    private static void ValidateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !FileNamePattern.IsMatch(fileName))
            throw new WorkflowValidationException(
                $"Helper file name '{fileName}' is invalid: bare file names only (letters, digits, " +
                "dot, dash, underscore), never a path.");
        if (!AllowedFileExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new WorkflowValidationException(
                $"Helper file '{fileName}' has a disallowed extension. Allowed: " +
                string.Join(", ", AllowedFileExtensions) + ".");
    }
}
