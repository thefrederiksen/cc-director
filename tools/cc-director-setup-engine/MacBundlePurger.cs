using System.Text.RegularExpressions;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Removes stale Director .app bundles so a reinstall converges on one canonical bundle
/// (issue #1821). Deletion is gated on PROOF OF IDENTITY: a bundle is only removed when its
/// Info.plist carries the product's own CFBundleIdentifier. Name alone is not enough - the
/// developer slot wrappers ("CC Director 2.app", bundle id com.devthrottle.ccdirector.slot2)
/// live in /Applications on a development machine, and "Director" is a generic enough app
/// name that an unrelated vendor's "Director.app" could exist. Deleting either would destroy
/// something we do not own, so anything whose identity cannot be confirmed is skipped and
/// logged, never deleted.
/// </summary>
public static class MacBundlePurger
{
    /// <summary>The shipped Director bundle's CFBundleIdentifier - the ONLY identity we ever delete.
    /// Developer slot wrappers use suffixed ids ("com.devthrottle.ccdirector.slot2") which do NOT
    /// match, by design.</summary>
    public const string ProductBundleIdentifier = "com.devthrottle.ccdirector";

    /// <summary>Bundle base names the product has shipped under: the current "Director" and the
    /// pre-rename "CC Director" (issue #1821).</summary>
    public static readonly IReadOnlyList<string> BaseNames = ["Director", "CC Director"];

    /// <summary>
    /// Delete every stale product bundle in <paramref name="directories"/>: bundles named
    /// "&lt;base&gt;.app" or Finder's "&lt;base&gt; N.app" duplicate form whose CFBundleIdentifier is
    /// <see cref="ProductBundleIdentifier"/>. The <paramref name="keep"/> path is never deleted -
    /// the caller replaces that one in place. Best-effort: a bundle that cannot be deleted (for
    /// example a copy in /Applications that needs admin rights) is logged and skipped, not fatal.
    /// </summary>
    /// <param name="directories">The directories to scan (non-existent ones are skipped).</param>
    /// <param name="keep">The canonical bundle path that must survive.</param>
    /// <param name="log">Receives one line per removal or skip.</param>
    /// <param name="readBundleIdentifier">
    /// Reads a bundle's CFBundleIdentifier, or null when it has none / cannot be read. Null uses
    /// the real Info.plist reader; tests inject a fake.
    /// </param>
    public static void Purge(IReadOnlyList<string> directories, string keep, Action<string> log,
        Func<string, string?>? readBundleIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(log);
        var readId = readBundleIdentifier ?? ReadBundleIdentifier;

        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var baseName in BaseNames)
            {
                foreach (var bundle in Directory.EnumerateDirectories(dir, $"{baseName}*.app"))
                {
                    var name = Path.GetFileName(bundle);
                    if (!IsBundleName(name, baseName)) continue;
                    if (string.Equals(Path.GetFullPath(bundle), Path.GetFullPath(keep), StringComparison.Ordinal))
                        continue; // the caller replaces this one in place.

                    var id = readId(bundle);
                    if (!string.Equals(id, ProductBundleIdentifier, StringComparison.Ordinal))
                    {
                        log($"leaving {bundle}: bundle id '{id ?? "unreadable"}' is not the Director's own; not ours to delete");
                        continue;
                    }

                    try
                    {
                        Directory.Delete(bundle, recursive: true);
                        log($"removed stale bundle {bundle}");
                    }
                    catch (Exception ex)
                    {
                        log($"could not remove {bundle} (needs admin?); skipping: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>True for "&lt;base&gt;.app" or Finder's "&lt;base&gt; N.app" duplicate form, and nothing else -
    /// so "Director.app"/"Director 2.app" match "Director" but an unrelated "Directory.app" does not.</summary>
    public static bool IsBundleName(string name, string baseName)
    {
        if (string.Equals(name, $"{baseName}.app", StringComparison.Ordinal)) return true;
        if (!name.StartsWith($"{baseName} ", StringComparison.Ordinal) ||
            !name.EndsWith(".app", StringComparison.Ordinal)) return false;
        var middle = name.Substring(baseName.Length + 1, name.Length - baseName.Length - 1 - ".app".Length);
        return middle.Length > 0 && middle.All(char.IsDigit);
    }

    /// <summary>
    /// Read CFBundleIdentifier from the bundle's Contents/Info.plist, or null when absent or
    /// unreadable. Handles the XML plist form our packaging writes; a binary plist (which our
    /// bundles never use) reads as null and the bundle is therefore left alone - the safe default.
    /// </summary>
    public static string? ReadBundleIdentifier(string bundlePath)
    {
        var plist = Path.Combine(bundlePath, "Contents", "Info.plist");
        if (!File.Exists(plist)) return null;
        try
        {
            var text = File.ReadAllText(plist);
            var match = Regex.Match(text,
                @"<key>\s*CFBundleIdentifier\s*</key>\s*<string>\s*([^<]+?)\s*</string>",
                RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
