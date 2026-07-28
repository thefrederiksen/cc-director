using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Skills;

/// <summary>
/// The canonical content hash of a skill version: SHA-256 over one canonical JSON document holding the
/// COMPLETE bundle - name, summary, triggers, the body markdown, and the ordered (fileName, fileHash)
/// list. One hash covers everything a version is, so "is what I hold current", "has this changed", and
/// the optimistic-concurrency token for draft edits are all the same comparison, and nothing can drift
/// between the pieces: editing a supporting file changes the bundle hash even though the body did not
/// move.
///
/// This hash is what makes the register listing cheap to act on. A client holding a skill compares one
/// short string against the listing and knows whether to re-fetch, without pulling a single body.
///
/// The canonical form is a deliberately explicit anonymous object (fixed property order, camelCase to
/// match the wire) rather than a serialized DTO, so a later DTO addition cannot silently change every
/// stored hash.
/// </summary>
public static class SkillContentHash
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
        IEnumerable<string> triggers,
        string bodyMarkdown,
        IEnumerable<(string FileName, string FileHash)> files)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            name,
            summary,
            // Triggers are hashed in AUTHORED ORDER, not sorted: the order is what an agent reads, so
            // reordering them is a real content change and must mint a new version.
            triggers = triggers.ToArray(),
            bodyMarkdown,
            files = files
                .OrderBy(f => f.FileName, StringComparer.Ordinal)
                .Select(f => new { f.FileName, f.FileHash }).ToArray(),
        });
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
