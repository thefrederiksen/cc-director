using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The model's judgment of what a session's live screen needs from its owner, produced alongside the
/// per-turn spoken summary (the SCREEN line of the translation contract). Three answers, one story:
/// <c>menu</c> - an interactive picker owns the screen and typed text cannot answer it; <c>answer</c> -
/// the agent is waiting on words or a decision the owner types or speaks; <c>nothing</c> - the turn is
/// informational, the agent reported and is not waiting on the owner. Null/absent means the model gave
/// no verdict (no live screen was supplied, or the line did not parse) - callers treat that as unknown
/// and fall back to their fail-safe default, never to a block.
/// </summary>
public sealed class WingmanScreenVerdict
{
    /// <summary>"menu" | "answer" | "nothing" - normalized lowercase; anything else never leaves the parser.</summary>
    public string Needs { get; set; } = "";

    /// <summary>For a menu: the choice being asked, in plain words. Empty otherwise.</summary>
    public string Question { get; set; } = "";

    /// <summary>For a menu: the visible option labels as shown on screen. Empty otherwise.</summary>
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// Remembers the model's latest screen verdict per session, keyed by a FINGERPRINT of the grid rows it
/// judged. The point: the menu question is asked at two different moments - once per turn (when the
/// narration call sees the screen anyway) and again at send time (the prompt menu guard, a voice
/// reply) - and the screen usually has not changed between them. A fingerprint match serves the model's
/// verdict instantly, so the send path gets model-grade judgment at regex cost; a mismatch means the
/// screen moved and the verdict is stale, so the caller re-judges. One entry per session (newest wins).
/// </summary>
public static class WingmanScreenVerdictCache
{
    private static readonly ConcurrentDictionary<string, (string Hash, string Needs)> Entries = new();

    /// <summary>The fingerprint of a live grid: SHA-256 over the rows joined with newlines. Any repaint -
    /// even a spinner glyph - changes it, which is the conservative direction: a changed screen is
    /// re-judged rather than served a stale verdict.</summary>
    public static string HashRows(IReadOnlyList<string> rows)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Record the model's verdict for the screen with this fingerprint.</summary>
    public static void Store(string sessionKey, string hash, string needs)
        => Entries[sessionKey] = (hash, needs);

    /// <summary>The cached verdict, but ONLY when the fingerprint still matches the live screen.</summary>
    public static bool TryGet(string sessionKey, string hash, out string needs)
    {
        needs = "";
        if (!Entries.TryGetValue(sessionKey, out var e) || e.Hash != hash) return false;
        needs = e.Needs;
        return true;
    }

    /// <summary>TEST-ONLY: drop every cached verdict so one test's screens never leak into another's.</summary>
    public static void Clear() => Entries.Clear();
}
