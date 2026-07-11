namespace CcDirector.Setup.Engine;

/// <summary>
/// Version parsing and comparison shared across the engine. Mirrors the
/// normalization the Director's UpdateService already uses: collapse to
/// (Major, Minor, Build) so 4-part assembly versions and 3-part tags compare
/// cleanly, and tolerate a leading 'v' plus a "-prerelease" suffix.
/// </summary>
public static class VersionUtil
{
    /// <summary>Parse "v0.3.3", "0.3.3", "0.3.3-rc1", or "1.2.0.4" into a normalized Version, or null.</summary>
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>Collapse to (Major, Minor, Build); a negative Build becomes 0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// True when the version carries a pre-release suffix (for example "1.1.0-rc4").
    /// After dropping a leading 'v'/'V' and any "+build" metadata, a '-' segment remains.
    /// A plain "1.1.0" (or "v1.1.0+abc123") is NOT a pre-release.
    /// </summary>
    public static bool HasPreReleaseSuffix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return t.IndexOf('-') >= 0;
    }

    /// <summary>
    /// Canonical form for comparing a version to a release tag: drop a leading 'v'/'V'
    /// and any "+build" metadata, then trim and lowercase. The "-rc4" pre-release suffix
    /// is KEPT (unlike <see cref="TryParse"/>), so "v1.1.0-rc4" and "1.1.0-rc4" compare
    /// equal while "1.1.0" does not. An empty or whitespace input yields "".
    /// </summary>
    public static string CanonicalTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return t.ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="installed"/>. Either side being unparseable returns false
    /// (we never "update" on the basis of a version we cannot read).
    /// </summary>
    public static bool IsNewer(string? candidate, string? installed)
    {
        var c = TryParse(candidate);
        var i = TryParse(installed);
        if (c is null || i is null) return false;
        return c > i;
    }
}
