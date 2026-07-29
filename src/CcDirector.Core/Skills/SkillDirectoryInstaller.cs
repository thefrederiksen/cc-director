using CcDirector.Core.Agents;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>One supporting file of a skill, ready to write.</summary>
/// <param name="RelativePath">Path inside the skill's directory, forward slashes.</param>
/// <param name="Bytes">The file's bytes, already decoded.</param>
/// <param name="Executable">Whether the file gets the executable bit (ignored on Windows).</param>
public sealed record SkillFileBytes(string RelativePath, byte[] Bytes, bool Executable);

/// <summary>A complete skill, ready to become a directory on disk.</summary>
public sealed record SkillBundle(
    string Id,
    int Version,
    string ContentHash,
    string Summary,
    IReadOnlyList<string> Triggers,
    string BodyMarkdown,
    IReadOnlyList<SkillFileBytes> Files,
    string? License = null,
    string? Compatibility = null,
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Puts the fleet's skills where the launching agent looks for them, so every agent family discovers
/// them through its OWN skills machinery instead of needing a DevThrottle command.
///
/// WHY THIS IS SAFE TO DO NOW. The standing rule was that nothing is deployed to a machine, and its
/// stated reason was that a deployed file would only ever be read by Claude Code while reaching every
/// agent family is the point. That reason no longer holds: all eight families read the same Agent
/// Skills directory, and six share one path. The other reason - that a file on disk goes stale, and a
/// stale skill that looks current is exactly what the central library exists to prevent - is answered
/// by WHEN this runs. The install is refreshed at session launch and RECONCILED, never added to, so a
/// skill switched off or deleted on the Gateway is gone from disk the next time a session starts.
///
/// THREE RULES THIS CLASS EXISTS TO KEEP:
///
///  1. A skill we did not write is never touched. The library is an ADDITIONAL source of skills, and
///     a machine's own skills win a name clash. Every directory we install carries a marker file, and
///     only marked directories are ever overwritten or removed. A name already taken by one of the
///     owner's own skills is left exactly as it is - and logged, because silently declining to
///     install is the kind of thing that has to be findable later.
///  2. Reconcile, never add. What is installed equals what the Gateway currently serves.
///  3. An unreachable Gateway does not stop a session launching. It launches with whatever the last
///     refresh materialized, and a refresh that fails says so rather than pretending.
/// </summary>
public static class SkillDirectoryInstaller
{
    /// <summary>The marker that makes a directory ours. Holds the skill id, version and content hash,
    /// so the record of what we put somewhere is in the place we put it.</summary>
    public const string MarkerFileName = ".devthrottle-skill";

    /// <summary>Where materialized skills are held before being installed anywhere: one canonical
    /// copy, written once per refresh, that every agent's directory is reconciled against.</summary>
    public static string StoreRoot() => Path.Combine(CcStorage.Root(), "skills", "installed");

    /// <summary>
    /// Write one skill's directory into <paramref name="parentDirectory"/> as a complete, standard
    /// skill: SKILL.md with the standard's frontmatter at the root, every supporting file at its own
    /// relative path, and our marker. The directory is rebuilt from empty, so a file removed upstream
    /// cannot survive inside it.
    /// </summary>
    public static string Materialize(string parentDirectory, SkillBundle bundle)
    {
        if (bundle is null)
            throw new ArgumentNullException(nameof(bundle));

        var skillDirectory = Path.Combine(parentDirectory, bundle.Id);
        if (Directory.Exists(skillDirectory))
            Directory.Delete(skillDirectory, recursive: true);
        Directory.CreateDirectory(skillDirectory);

        var skillMd = SkillMarkdown.Compose(
            bundle.Id, bundle.Summary, bundle.Triggers, bundle.BodyMarkdown,
            bundle.License, bundle.Compatibility, bundle.AllowedTools, bundle.Metadata);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), skillMd);

        foreach (var file in bundle.Files)
        {
            var target = ResolveInside(skillDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Bytes);
            if (file.Executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, ReadWriteExecute);
        }

        File.WriteAllText(
            Path.Combine(skillDirectory, MarkerFileName),
            $"{bundle.Id}\n{bundle.Version}\n{bundle.ContentHash}\n");
        return skillDirectory;
    }

    /// <summary>
    /// Reconcile every directory <paramref name="kind"/> reads against the materialized store: install
    /// or refresh each skill we hold, remove ours that the store no longer has, and leave everything
    /// we did not write alone. Synchronous and local - no network, so it is safe on the launch path.
    /// Returns how many skills the agent can now see from us.
    /// </summary>
    /// <param name="kind">The agent being launched, which decides where skills have to appear.</param>
    /// <param name="storeRoot">The materialized store to reconcile from; defaults to the real one.</param>
    /// <param name="targetsOverride">The directories to install into, INSTEAD of the ones
    /// <paramref name="kind"/> implies. Exists so tests exercise this method rather than a copy of it -
    /// the real targets live under the running user's home directory, and a test that wrote there
    /// would scatter skills through the developer's own agent configuration.</param>
    public static int InstallFor(
        AgentKind kind, string? storeRoot = null, IReadOnlyList<string>? targetsOverride = null)
    {
        var store = storeRoot ?? StoreRoot();
        var targets = targetsOverride ?? SkillInstallTargets.For(kind);
        if (targets.Count == 0)
        {
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind} has no skills directory - nothing installed");
            return 0;
        }
        if (!Directory.Exists(store))
        {
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, no materialized skills at {store} - " +
                          "nothing installed (the Gateway has not been reached yet)");
            return 0;
        }

        var held = Directory.GetDirectories(store)
            .Where(d => File.Exists(Path.Combine(d, MarkerFileName)))
            .ToList();

        var installed = 0;
        foreach (var target in targets)
        {
            Directory.CreateDirectory(target);
            var wanted = new HashSet<string>(held.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

            // Remove OURS that the store no longer holds. Anything without our marker is somebody
            // else's skill and is not ours to delete.
            foreach (var existing in Directory.GetDirectories(target))
            {
                var name = Path.GetFileName(existing);
                if (!File.Exists(Path.Combine(existing, MarkerFileName)))
                    continue;
                if (!wanted.Contains(name))
                {
                    Directory.Delete(existing, recursive: true);
                    FileLog.Write($"[SkillDirectoryInstaller] Removed withdrawn skill '{name}' from {target}");
                }
            }

            foreach (var source in held)
            {
                var name = Path.GetFileName(source)!;
                var destination = Path.Combine(target, name);
                if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, MarkerFileName)))
                {
                    // The owner's own skill of the same name. It wins, and the fact that it did is
                    // recorded - a skill quietly not installed is the kind of absence nobody finds.
                    FileLog.Write($"[SkillDirectoryInstaller] '{name}' already exists in {target} and was not " +
                                  "installed by DevThrottle - leaving it alone; the machine's own skill wins");
                    continue;
                }
                CopyTree(source, destination);
                installed++;
            }
        }

        FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, installed={installed}, " +
                      $"targets={string.Join(", ", targets)}");
        return installed;
    }

    private const UnixFileMode ReadWriteExecute =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Copy a materialized skill over a destination, rebuilding it so a file removed upstream
    /// cannot survive in the copy.</summary>
    private static void CopyTree(string source, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(target, ReadWriteExecute);
        }
    }

    /// <summary>Resolve a supporting file's relative path INSIDE the skill directory, refusing any
    /// path that would land outside it. The Gateway validates paths on write; this is the same rule
    /// enforced again at the point where bytes hit this disk, because a store that was ever wrong
    /// must not be able to write anywhere it likes.</summary>
    private static string ResolveInside(string skillDirectory, string relativePath)
    {
        var root = Path.GetFullPath(skillDirectory);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Skill file path '{relativePath}' resolves outside the skill's own directory. " +
                "Refusing to write it.");
        return combined;
    }
}
