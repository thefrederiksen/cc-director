using System.Diagnostics;
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
/// ONE COPY, LINKED. Every skill is written exactly once, into the shared <c>~/.agents/skills</c> that
/// six of the eight agent families read natively. The two that do not - Claude Code and Cursor - get
/// one LINK PER SKILL inside their own directory pointing at that copy. Three copies is three things
/// that can drift; one copy cannot disagree with itself. On Windows the link is a directory JUNCTION,
/// not a symlink: a directory symlink needs administrator rights or Developer Mode and a junction
/// needs neither. On Linux and macOS it is an ordinary unprivileged symlink.
///
/// THREE RULES THIS CLASS EXISTS TO KEEP:
///
///  1. A skill we did not write is never touched, and neither is the agent's own skills DIRECTORY -
///     we only ever create, replace or remove one named entry inside it. The library is an ADDITIONAL
///     source of skills, and a machine's own skills win a name clash. Every directory we install
///     carries a marker file, and only marked entries are ever overwritten or removed. A name already
///     taken by one of the owner's own skills is left exactly as it is - and logged, because silently
///     declining to install is the kind of thing that has to be findable later.
///  2. Reconcile, never add. What is installed equals what the Gateway currently serves.
///  3. An unreachable Gateway does not stop a session launching. It launches with whatever the last
///     refresh materialized, and a refresh that fails says so rather than pretending.
/// </summary>
public static class SkillDirectoryInstaller
{
    /// <summary>The marker that makes a directory ours. Holds the skill id, version and content hash,
    /// so the record of what we put somewhere is in the place we put it.</summary>
    public const string MarkerFileName = ".devthrottle-skill";

    /// <summary>
    /// The Director's own staging area, filled by the network half. It is deliberately NOT a place any
    /// agent reads: the Gateway fetch rebuilds a skill directory whole, and a half-written skill must
    /// never be visible to an agent mid-write. The launch half reflects this into the one place agents
    /// do read.
    /// </summary>
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
    /// Put the store's skills where <paramref name="kind"/> looks: one real copy in the shared
    /// directory, plus a link per skill in the agent's own directory when the agent does not read the
    /// shared one. Ours are installed or refreshed, ours the store no longer holds are removed, and
    /// everything we did not write is left alone. Synchronous and local - no network, so it is safe on
    /// the launch path. Returns how many skills the agent can now see from us.
    /// </summary>
    /// <param name="kind">The agent being launched, which decides where skills have to appear.</param>
    /// <param name="storeRoot">The materialized store to reconcile from; defaults to the real one.</param>
    /// <param name="pathsOverride">The directories to install into, INSTEAD of the ones
    /// <paramref name="kind"/> implies. Exists so tests exercise this method rather than a copy of it -
    /// the real paths live under the running user's home directory, and a test that wrote there would
    /// scatter skills through the developer's own agent configuration.</param>
    public static int InstallFor(
        AgentKind kind, string? storeRoot = null, SkillInstallPaths? pathsOverride = null)
    {
        var store = storeRoot ?? StoreRoot();
        var paths = pathsOverride ?? SkillInstallTargets.For(kind);
        if (paths is null)
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

        // The copy is reconciled BEFORE the links, and the order is load-bearing: a link is created
        // only for a skill that is already present in the shared directory, so no link is ever made
        // pointing at something that is not there.
        var materialized = ReconcileCopies(held, paths.SharedRoot);
        if (paths.LinkRoot is null)
        {
            FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind} reads the shared path, " +
                          $"installed={materialized.Count} at {paths.SharedRoot}");
            return materialized.Count;
        }

        var linked = ReconcileLinks(paths.SharedRoot, materialized, paths.LinkRoot);
        FileLog.Write($"[SkillDirectoryInstaller] InstallFor: kind={kind}, materialized={materialized.Count} " +
                      $"at {paths.SharedRoot}, linked={linked} into {paths.LinkRoot}");
        return linked;
    }

    /// <summary>
    /// Reflect the store into the shared directory - the ONE real copy every agent reads, directly or
    /// through a link. Returns the skill names that are actually ours in there afterwards, which is
    /// what may be linked: a name the owner already used is not ours to link either.
    /// </summary>
    private static List<string> ReconcileCopies(IReadOnlyList<string> held, string sharedRoot)
    {
        Directory.CreateDirectory(sharedRoot);
        var wanted = new HashSet<string>(held.Select(d => Path.GetFileName(d)!), StringComparer.OrdinalIgnoreCase);

        // Remove OURS that the store no longer holds. Anything without our marker is somebody else's
        // skill and is not ours to delete.
        foreach (var existing in Directory.GetDirectories(sharedRoot))
        {
            var name = Path.GetFileName(existing)!;
            if (!File.Exists(Path.Combine(existing, MarkerFileName)))
                continue;
            if (!wanted.Contains(name))
            {
                Directory.Delete(existing, recursive: true);
                FileLog.Write($"[SkillDirectoryInstaller] Removed withdrawn skill '{name}' from {sharedRoot}");
            }
        }

        var ours = new List<string>();
        foreach (var source in held)
        {
            var name = Path.GetFileName(source)!;
            var destination = Path.Combine(sharedRoot, name);
            if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, MarkerFileName)))
            {
                // The owner's own skill of the same name. It wins, and the fact that it did is
                // recorded - a skill quietly not installed is the kind of absence nobody finds.
                FileLog.Write($"[SkillDirectoryInstaller] '{name}' already exists in {sharedRoot} and was not " +
                              "installed by DevThrottle - leaving it alone; the machine's own skill wins");
                continue;
            }
            CopyTree(source, destination);
            ours.Add(name);
        }
        return ours;
    }

    /// <summary>
    /// Give an agent that does not read the shared path one link per skill into it. Never touches
    /// <paramref name="linkRoot"/> itself - that folder is the owner's and holds skills we did not
    /// write - only named entries inside it. Returns how many links the agent can now follow.
    /// </summary>
    private static int ReconcileLinks(string sharedRoot, IReadOnlyList<string> names, string linkRoot)
    {
        Directory.CreateDirectory(linkRoot);
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Directory.GetDirectories(linkRoot))
        {
            var name = Path.GetFileName(existing)!;
            if (wanted.Contains(name) || !IsOurs(existing, sharedRoot))
                continue;
            RemoveOurs(existing);
            FileLog.Write($"[SkillDirectoryInstaller] Removed withdrawn skill '{name}' from {linkRoot}");
        }

        var linked = 0;
        foreach (var name in names)
        {
            var destination = Path.Combine(linkRoot, name);
            if (Directory.Exists(destination))
            {
                if (!IsOurs(destination, sharedRoot))
                {
                    FileLog.Write($"[SkillDirectoryInstaller] '{name}' already exists in {linkRoot} and was not " +
                                  "installed by DevThrottle - leaving it alone; the machine's own skill wins");
                    continue;
                }
                // Ours: either a link from a previous launch or a full copy from the scheme that
                // preceded links. Rebuilt rather than inspected, because a link is cheap to make and a
                // link believed to point somewhere it does not is the failure that has no symptom.
                RemoveOurs(destination);
            }

            CreateDirectoryLink(destination, Path.Combine(sharedRoot, name));
            linked++;
        }
        return linked;
    }

    /// <summary>An entry is ours if our marker is reachable through it - which holds for a live link
    /// and for a copy left by the previous scheme - or if it is a link pointing into our shared
    /// directory, which is how a link whose target has already gone is still recognised as ours.</summary>
    private static bool IsOurs(string path, string sharedRoot)
    {
        if (File.Exists(Path.Combine(path, MarkerFileName)))
            return true;
        var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        return target is not null
               && target.StartsWith(Path.GetFullPath(sharedRoot) + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Delete one of our entries. A link is removed as a link, so what it points at survives -
    /// a recursive delete through a link would empty the one real copy every other agent reads.</summary>
    private static void RemoveOurs(string path)
    {
        var isLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        Directory.Delete(path, recursive: !isLink);
    }

    /// <summary>
    /// Point <paramref name="linkPath"/> at <paramref name="targetPath"/>.
    ///
    /// On Windows this is a directory JUNCTION and not a symlink. Creating a directory symlink needs
    /// administrator rights or Developer Mode, and the Director runs as the ordinary signed-in user; a
    /// junction needs neither. There is no managed API that creates a junction, so it is made with the
    /// command Windows ships for it. A failure throws rather than silently degrading to a copy: a copy
    /// that looks like a link is exactly the drift this design removed.
    /// </summary>
    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start cmd.exe to create a skill junction.");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
            throw new InvalidOperationException(
                $"Could not create the skill junction '{linkPath}' -> '{targetPath}': {output.Trim()}");
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
