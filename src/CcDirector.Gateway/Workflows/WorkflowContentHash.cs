using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Workflows;

/// <summary>
/// The canonical content hash of a workflow version: SHA-256 over one canonical JSON document holding
/// the COMPLETE bundle - metadata, steps, outcome criteria, the instruction markdown, and the ordered
/// (fileName, fileHash) list. One hash covers everything a version is, so "the content a run pinned",
/// "has the user customized this built-in", and "did anything change" are all the same comparison, and
/// nothing can drift between the pieces (a file edit changes the bundle hash even though the
/// instructions did not move).
///
/// The canonical form is deliberately explicit anonymous objects (fixed property order, camelCase to
/// match the wire) rather than serializing the DTOs, so a later DTO addition cannot silently change
/// every stored hash.
/// </summary>
public static class WorkflowContentHash
{
    /// <summary>SHA-256 (lowercase hex) of one file's content.</summary>
    public static string ForFile(string content)
    {
        if (content is null)
            throw new ArgumentNullException(nameof(content));
        return Sha256Hex(content);
    }

    /// <summary>The canonical bundle hash of a complete version snapshot.</summary>
    public static string ForBundle(
        string name,
        string summary,
        string whenToUse,
        string humanCheckpoint,
        IEnumerable<WorkflowStepDto> steps,
        IEnumerable<WorkflowOutcomeCriterionDto> outcomeCriteria,
        string instructionsMarkdown,
        IEnumerable<(string FileName, string FileHash)> files)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            name,
            summary,
            whenToUse,
            humanCheckpoint,
            steps = steps.Select(s => new { s.Name, s.Description, s.Doer, s.Reviewer, s.Done }).ToArray(),
            outcomeCriteria = outcomeCriteria
                .Select(c => new { c.CriterionId, c.Description, c.ProofHint }).ToArray(),
            instructionsMarkdown,
            files = files
                .OrderBy(f => f.FileName, StringComparer.Ordinal)
                .Select(f => new { f.FileName, f.FileHash }).ToArray(),
        });
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
