using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Reads the COMPACTION mark out of a claude transcript (issue #2150).
///
/// When claude compacts a conversation - whether a person typed <c>/compact</c>, the Director
/// commanded it, or the tool auto-compacted on its own - it appends one entry carrying
/// <c>isCompactSummary: true</c>: the summary the next turn is continued from. Crucially it appends
/// that entry to the SAME transcript file under the SAME session id, so compaction is observable
/// without re-linking anything. Verified against live transcripts written by claude 2.1.195 through
/// 2.1.218 (the summary line, the file's first line and its last line all carry one session id).
///
/// This is the completion signal for compact-and-continue: the Director submits the compaction
/// command, then watches here for a mark stamped after the moment it submitted, and only then sends
/// the follow-up prompt. Terminal quiet would be a guess; this is the tool's own record.
/// </summary>
public static class CompactionMarker
{
    /// <summary>The transcript field that marks a compaction summary entry.</summary>
    private const string MarkerField = "isCompactSummary";

    /// <summary>
    /// The timestamp of the NEWEST compaction mark in this transcript, or null when the file does not
    /// exist, has no compaction mark, or carries a mark with no readable timestamp. Reads the file line
    /// by line with a cheap substring pre-filter, so an unrelated multi-megabyte transcript costs a scan
    /// and no JSON parsing.
    /// </summary>
    public static DateTime? ReadLastCompactionUtc(string jsonlPath)
    {
        if (string.IsNullOrWhiteSpace(jsonlPath) || !File.Exists(jsonlPath))
            return null;

        DateTime? newest = null;
        using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || !line.Contains(MarkerField, StringComparison.Ordinal))
                continue;

            var stamp = ReadMarkedTimestamp(line);
            if (stamp is not null && (newest is null || stamp > newest))
                newest = stamp;
        }

        return newest;
    }

    /// <summary>
    /// The entry's timestamp when the line IS a compaction mark, else null. A line mentioning the field
    /// inside message text is not a mark: the value must be the boolean true at the top level.
    /// </summary>
    private static DateTime? ReadMarkedTimestamp(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A partially-written last line while claude is mid-append. The next poll reads it whole.
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty(MarkerField, out var marker) || marker.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String)
            {
                FileLog.Write("[CompactionMarker] compaction mark carries no readable timestamp; ignoring it");
                return null;
            }
            return timestamp.TryGetDateTime(out var parsed) ? parsed.ToUniversalTime() : null;
        }
    }
}
