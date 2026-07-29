using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Skills;

/// <summary>
/// The canonical content hash of a skill version: SHA-256 over one canonical JSON document holding the
/// COMPLETE bundle - name, summary, triggers, the body markdown, the standard's frontmatter fields, and
/// the ordered file list with each file's path, byte hash, encoding and executable bit. One hash covers
/// everything a version is, so "is what I hold current", "has this changed", and
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
    /// <summary>SHA-256 (lowercase hex) of one file's DECODED BYTES - what the file will be on disk,
    /// not the string that carried it. So the same file hashes identically whether it travelled as
    /// text or as base64, and a client can verify what it wrote rather than what it received.</summary>
    public static string ForFileBytes(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>SHA-256 (lowercase hex) of a text file's content, encoded UTF-8.</summary>
    public static string ForFile(string content)
    {
        if (content is null)
            throw new ArgumentNullException(nameof(content));
        return ForFileBytes(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>One file's identity inside the bundle hash: its path, its bytes, and the two
    /// properties that change what lands on disk without changing the bytes.</summary>
    public readonly record struct HashedFile(
        string FileName, string FileHash, string Encoding, bool Executable);

    /// <summary>The canonical bundle hash of a complete version snapshot.</summary>
    public static string ForBundle(
        string name,
        string summary,
        IEnumerable<string> triggers,
        string bodyMarkdown,
        IEnumerable<HashedFile> files,
        string? license = null,
        string? compatibility = null,
        string? allowedTools = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            name,
            summary,
            // Triggers are hashed in AUTHORED ORDER, not sorted: the order is what an agent reads, so
            // reordering them is a real content change and must mint a new version.
            triggers = triggers.ToArray(),
            bodyMarkdown,
            // The standard's frontmatter is part of the skill, so changing a licence or a tool grant
            // mints a version like any other edit. Null and empty are folded together so a client that
            // omits a field and one that sends it blank agree on the hash.
            license = Blank(license),
            compatibility = Blank(compatibility),
            allowedTools = Blank(allowedTools),
            // Metadata is an unordered map, so it is hashed in sorted key order - unlike triggers,
            // whose order an agent actually reads.
            metadata = (metadata ?? new Dictionary<string, string>())
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new { e.Key, e.Value }).ToArray(),
            // The executable bit and the encoding ride the hash because they change what materializes
            // on disk: the same bytes marked executable are a different file to an agent that runs it.
            files = files
                .OrderBy(f => f.FileName, StringComparer.Ordinal)
                .Select(f => new { f.FileName, f.FileHash, f.Encoding, f.Executable }).ToArray(),
        });
        return Sha256Hex(canonical);
    }

    private static string Blank(string? value) => value ?? "";

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
