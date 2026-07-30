using System.Text.Json;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Pi;

/// <summary>
/// Computes how full a pi session's context window is, from its session JSONL
/// (<c>~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;ts&gt;_&lt;uuid&gt;.jsonl</c>). Each assistant message line
/// carries the usage and the model:
///
///   {"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.5",
///       "usage":{"input":3838,"output":33,"cacheRead":0,"cacheWrite":0,"totalTokens":3871, ...}}}
///
/// UsedTokens = the latest assistant message's <c>usage.input</c> (the conversation the model last
/// ingested = context fullness). pi does NOT record the window anywhere in this file, and since issue
/// #1100 it is no longer inferred from the model id - the window is reported as unknown and the gauge
/// shows the raw token count with no percentage. See <see cref="Drivers.ContextWindowSource"/>.
/// The session file for a repo is located by reading each session line's <c>cwd</c> (pi has no
/// preassigned id the Director can use), newest matching file wins.
/// </summary>
public static class PiContextUsage
{
    /// <summary>Context usage for the newest pi session matching <paramref name="repoPath"/>, or null
    /// when none matches or it has no assistant usage yet.</summary>
    public static ContextUsageDto? ReadForRepo(string repoPath)
    {
        var file = LocateNewestForRepo(repoPath);
        if (file is null)
            return null;
        return ReadFromFile(file);
    }

    /// <summary>Context usage from one pi session file. Reads with FileShare.ReadWrite so the live pi
    /// session can keep writing. Null when the file is missing or has no assistant usage line.</summary>
    public static ContextUsageDto? ReadFromFile(string sessionPath)
    {
        if (!File.Exists(sessionPath))
            return null;
        FileLog.Write($"[PiContextUsage] ReadFromFile: {sessionPath}");
        using var fs = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw session lines. Returns the LAST assistant usage.</summary>
    public static ContextUsageDto? Compute(IEnumerable<string> sessionLines)
    {
        ArgumentNullException.ThrowIfNull(sessionLines);
        ContextUsageDto? latest = null;

        foreach (var line in sessionLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while pi writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.GetString() == "message")) continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!(msg.TryGetProperty("role", out var role) && role.GetString() == "assistant")) continue;
                if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                var used = Long(usage, "input");
                if (used <= 0)
                    continue;

                // NO WINDOW, for the same reason as Claude (issue #1100): pi does not record one in its
                // session file, so the old code inferred it from the model id - and inherited the Claude
                // bug wholesale by delegating Claude models into that same table. It carried a second copy
                // of the pattern of its own: a hardcoded 272,000 for gpt-5.5, with a comment noting it
                // disagreed with the 258,400 the Codex backend reports. Two numbers for one window, and no
                // way for the screen to say which one it was showing.
                //
                // pi does have a route - an extension call that answers directly - and wiring it is tracked
                // separately. Until then this reports the used tokens, which are honestly measured, and no
                // denominator.
                var asOf = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                           && DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : (DateTime?)null;

                latest = new ContextUsageDto
                {
                    UsedTokens = used,
                    WindowTokens = null,
                    PercentUsed = null,
                    AsOfUtc = asOf,
                    WindowSource = nameof(ContextWindowSource.Unknown),
                };
            }
        }

        if (latest is not null)
            FileLog.Write($"[PiContextUsage] used={latest.UsedTokens}, window={latest.WindowTokens}, pct={latest.PercentUsed}");
        return latest;
    }

    /// <summary>The newest pi session file whose <c>session</c> line cwd matches the repo, or null.
    /// Scans <c>~/.pi/agent/sessions</c> recursively; skips pi's <c>_archived_*</c> folders.</summary>
    public static string? LocateNewestForRepo(string repoPath)
    {
        var dir = SessionsDirectory();
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(dir))
            return null;
        var target = NormalizePath(repoPath);

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains("_archived", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return null;
        }

        foreach (var file in files)
            if (NormalizePath(ReadSessionCwd(file.FullName)) == target)
                return file.FullName;
        return null;
    }

    /// <summary>Read the <c>cwd</c> from a pi session file's first <c>session</c> line, or null.</summary>
    private static string? ReadSessionCwd(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var first = reader.ReadLine();
            if (string.IsNullOrEmpty(first))
                return null;
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!(root.TryGetProperty("type", out var t) && t.GetString() == "session")) return null;
            return root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SessionsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "sessions");

    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    private static long Long(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
