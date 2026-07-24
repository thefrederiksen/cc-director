using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Activity;

/// <summary>
/// Pure helpers for the BOUNDED terminal evidence an output-while-settled event carries. The July 24
/// incident could not be fully explained because nobody kept what the repaint changed; these keep exactly
/// enough to answer that next time - a normalized body hash on each side and the first few changed rows -
/// and never the raw byte stream. Pure and allocation-light so they can run on the PTY producer thread
/// and be unit tested directly.
/// </summary>
public static class ActivityEvidence
{
    /// <summary>At most this many changed rows are quoted in a diff.</summary>
    public const int MaxDiffRows = 8;

    /// <summary>The hard character cap on one bounded diff (inside the ledger's own 4000 cap).</summary>
    public const int MaxDiffChars = 2000;

    /// <summary>A normalized 128-bit content hash of a screen body (trailing whitespace per row ignored,
    /// so a repaint that only re-pads columns hashes identically). Empty input hashes too - "no body" and
    /// "blank body" must still compare stably across events.</summary>
    public static string BodyHash(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(body))))[..32].ToLowerInvariant();

    /// <summary>
    /// The first changed rows between two screen bodies, each quoted as "row N: text", bounded by
    /// <see cref="MaxDiffRows"/> and <see cref="MaxDiffChars"/>. A row present before and gone after is
    /// quoted as removed. When more rows changed than are quoted, the tail says how many were left out -
    /// a bounded diff that reads as complete would lie about coverage.
    /// </summary>
    public static string BoundedRowDiff(string beforeBody, string afterBody)
    {
        var before = Normalize(beforeBody).Split('\n');
        var after = Normalize(afterBody).Split('\n');
        var rows = Math.Max(before.Length, after.Length);

        var quoted = new StringBuilder();
        var changed = 0;
        var quotedRows = 0;
        for (var i = 0; i < rows; i++)
        {
            var b = i < before.Length ? before[i] : null;
            var a = i < after.Length ? after[i] : null;
            if (string.Equals(b, a, StringComparison.Ordinal)) continue;
            changed++;
            if (quotedRows >= MaxDiffRows || quoted.Length >= MaxDiffChars) continue;
            var line = a is null ? $"row {i}: <removed>" : $"row {i}: {a}";
            if (quoted.Length + line.Length + 1 > MaxDiffChars) continue;
            if (quoted.Length > 0) quoted.Append('\n');
            quoted.Append(line);
            quotedRows++;
        }

        if (changed == 0)
            return "";
        if (changed > quotedRows)
            quoted.Append('\n').Append($"({changed - quotedRows} more changed row(s) not quoted)");
        return quoted.ToString();
    }

    /// <summary>Trailing whitespace per row is presentation, not content - strip it before comparing.</summary>
    private static string Normalize(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var rows = body.Split('\n');
        for (var i = 0; i < rows.Length; i++)
            rows[i] = rows[i].TrimEnd();
        return string.Join('\n', rows);
    }
}
