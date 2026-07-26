using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Diagnostics;

/// <summary>
/// Reads the build stamp a served web bundle ships with: <c>build.json</c>, emitted by that app's Vite
/// build and staged into the Gateway's wwwroot by the release-gated MSBuild targets on
/// CcDirector.Gateway.csproj.
///
/// This is the ONLY way the Gateway process can name the Cockpit and mobile builds it is serving. Each
/// app's commit is also compiled into its own minified bundle (the <c>__COCKPIT_COMMIT__</c> /
/// <c>__MOBILE_COMMIT__</c> defines), but the server never executes that JavaScript, so before this
/// stamp existed the About page could report only the build of the bundle the browser was already
/// running - never the other surface's, and never from a server-side call at all.
///
/// Read on EVERY request, deliberately not cached: a Cockpit-only redeploy replaces wwwroot/c under a
/// live Gateway without restarting it, so a cached stamp would keep naming the previous build - which
/// is precisely the staleness this stamp exists to expose. The file is a few dozen bytes and About is
/// not a hot path.
/// </summary>
public static class BundleStamp
{
    /// <summary>The file each app's build emits into its bundle root.</summary>
    public const string FileName = "build.json";

    /// <summary>
    /// The stamp for the bundle served out of <paramref name="webRoot"/>, or null when that bundle
    /// carries no stamp. Null is a real, reportable state, not a substituted value: a routine Debug
    /// build does not build the web apps at all, so wwwroot is absent and the caller says so plainly
    /// rather than inventing a commit. A stamp that is PRESENT but unreadable is a broken deploy and
    /// is logged as a failure - it is never quietly reported as "no bundle".
    /// </summary>
    public static BundleStampDto? Read(string webRoot)
    {
        var path = Path.Combine(webRoot, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            var stamp = JsonSerializer.Deserialize<StampFile>(File.ReadAllText(path));
            if (stamp is null || string.IsNullOrWhiteSpace(stamp.Commit))
            {
                FileLog.Write($"[BundleStamp] Read FAILED: {path} carries no commit - the bundle staged here was built without a stamp");
                return null;
            }
            return new BundleStampDto { Commit = stamp.Commit.Trim(), BuildTime = stamp.BuildTime };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BundleStamp] Read FAILED: {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The on-disk shape written by the apps' Vite build-stamp plugin.</summary>
    private sealed class StampFile
    {
        [JsonPropertyName("commit")]
        public string? Commit { get; set; }

        [JsonPropertyName("buildTime")]
        public DateTime? BuildTime { get; set; }
    }
}
