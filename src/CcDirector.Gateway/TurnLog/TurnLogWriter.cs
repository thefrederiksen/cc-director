using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// Where a turn record goes: one compressed bundle per day, per account, per machine.
///
/// WHY BUNDLES RATHER THAN A FILE PER RECORD. Four hundred loose files a day is a repository nobody wants
/// to open, and uncompressed the corpus runs to several gigabytes a year. A record inside a bundle is still
/// complete on its own - the bundle is a container, not a join - so extracting one line still gives you
/// everything needed to replay that turn.
///
/// WHY EACH RECORD IS ITS OWN GZIP MEMBER. A bundle is written by appending; a stream we had to close
/// cleanly would lose the day's tail every time the process was stopped, which on a hosted deploy is twice
/// a week. Concatenated gzip members are valid gzip - <c>zcat</c> and Python's <c>gzip</c> read the whole
/// file - so every record is flushed complete the moment it is written and a killed process loses nothing
/// but the record it was mid-way through.
///
/// WHY THE WRITER HAS ITS OWN FILE. Two Gateway processes exist at once for a few seconds on every deploy
/// swap, and an append larger than a page is not atomic, so two processes sharing one file could interleave
/// two half-records into a bundle that then fails to decompress from that point on - losing the rest of the
/// day. Each process writes its own file inside the day's directory instead, and whatever reads the corpus
/// reads them all. Nothing is lost and nothing has to be coordinated.
///
/// IT NEVER THROWS AT ITS CALLER. A failure to log is not a failure of a turn. But it is logged loudly
/// rather than swallowed, because a silent writer produces a corpus with holes exactly where the
/// interesting turns were, and a hole is indistinguishable from a quiet day.
/// </summary>
public sealed class TurnLogWriter
{
    /// <summary>The serializer for the corpus. Web defaults are deliberately NOT used: every wire name on
    /// the record is spelled out, so the only thing asked of the serializer is that it leaves them alone.
    /// Indentation is off - one record is one line, which is what makes a bundle greppable.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;
    private readonly string _writerId;
    private readonly object _gate = new();

    /// <param name="root">The turn-log directory. Created on first write, never at construction - a
    /// Gateway that never has the log switched on leaves no trace on disk.</param>
    /// <param name="writerId">This process's discriminator. Defaults to a short random id, which is what
    /// keeps two Gateway processes off each other's file during a deploy swap.</param>
    public TurnLogWriter(string root, string? writerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _writerId = string.IsNullOrWhiteSpace(writerId)
            ? Guid.NewGuid().ToString("N")[..8]
            : Sanitize(writerId!);
    }

    /// <summary>The bundle this record belongs in. Public so a test can name the file it expects rather
    /// than searching for whatever appeared.</summary>
    public string BundlePathFor(DateTime capturedAtUtc, string account, string machine)
        => Path.Combine(
            _root,
            capturedAtUtc.ToString("yyyy-MM-dd"),
            Sanitize(account),
            Sanitize(machine),
            _writerId + ".jsonl.gz");

    /// <summary>
    /// Append one record to its bundle. Answers the path written, or null when nothing was written -
    /// which is a real outcome the caller may want to count, never an exception it has to handle.
    /// </summary>
    public string? Append(TurnLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = BundlePathFor(record.CapturedAtUtc, record.Glance.Account, record.Glance.Computer ?? "unknown-machine");
        try
        {
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            var member = Compress(Encoding.UTF8.GetBytes(line));

            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(member, 0, member.Length);
                stream.Flush();
            }
            return path;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogWriter] Append FAILED: record={record.RecordId} path={path}: {ex.Message}");
            return null;
        }
    }

    private static byte[] Compress(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return buffer.ToArray();
    }

    /// <summary>
    /// A path segment safe on both operating systems the Gateway runs on. An account or machine name is
    /// caller-supplied text that reaches the file system, so it is reduced to a known-safe alphabet rather
    /// than merely having the obvious separators removed - "..", a drive letter and a leading separator all
    /// stop being expressible.
    /// </summary>
    internal static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var cleaned = builder.ToString().Trim('-');
        if (cleaned.Length == 0) return "unknown";
        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }
}
