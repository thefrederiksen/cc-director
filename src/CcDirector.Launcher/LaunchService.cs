using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Launcher;

/// <summary>
/// Launches arbitrary executables with clean process parentage.
///
/// The launcher itself runs outside any ConPty (started by the HKCU Run key or
/// Start Menu on Windows, by launchd on macOS), so a child it starts has clean
/// parentage - the rule-0b fix.
///
/// Windows parentage modes:
///   - GUI apps (UseShellExecute = true): launched with shell association, no ConPty.
///   - Headless/silent apps (UseShellExecute = false, CreateNoWindow = true): no console
///     handle inherited.
///   - .cmd / .bat files: routed through cmd.exe (Windows limitation: shell scripts
///     cannot be launched directly with UseShellExecute = false in all contexts).
///
/// macOS parentage modes:
///   - Application bundles (a directory ending in .app): launched through /usr/bin/open,
///     so the application is a child of launchd, not of this launcher - the cleanest
///     parentage macOS offers. /usr/bin/open exits immediately; the returned PID is
///     open's, so the audit records the bundle path, not a supervised PID.
///   - Shell scripts (.sh): routed through /bin/bash (works whether or not the script
///     carries an execute bit).
///   - Plain executables: spawned directly (UseShellExecute = false). The launcher runs
///     as a launch agent with no controlling terminal, so the child starts with clean
///     standard input/output and launchd-session parentage.
///   - .cmd / .bat files are refused explicitly - they cannot run on macOS.
///
/// Every launch is FileLog-audited with the resolved path and the caller description.
/// </summary>
public sealed class LaunchService
{
    private readonly ConcurrentDictionary<int, string> _launched = new();

    /// <summary>
    /// Build a ProcessStartInfo for the given launch request. No real spawn - pure,
    /// unit-testable seam.
    /// </summary>
    public ProcessStartInfo BuildStartInfo(LaunchRequest request) =>
        BuildStartInfoFor(request, CurrentPlatform);

    /// <summary>The platform this launcher is running on, as the launch rules see it.</summary>
    internal static LaunchPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? LaunchPlatform.Windows
        : OperatingSystem.IsMacOS() ? LaunchPlatform.MacOs
        : LaunchPlatform.Linux;

    /// <summary>
    /// <see cref="BuildStartInfo"/> with the platform passed in rather than read from the machine.
    ///
    /// This seam exists because of inspection finding M03-I2-02, and the finding was as much about
    /// the TESTS as about the code. Every non-Windows launch test began by returning early on a
    /// Windows host, so on the machine this repository is built and tested on they all passed
    /// without running - a silent skip that reads as coverage. Worse, the production code sent
    /// EVERY non-Windows path down the macOS arm, so there was no Linux behaviour to cover even if
    /// they had run. With the platform as an argument, all three arms are exercised on any one
    /// machine, and the Linux desktop-entry handoff is proven rather than assumed.
    /// </summary>
    internal ProcessStartInfo BuildStartInfoFor(LaunchRequest request, LaunchPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("Path must not be empty.", nameof(request));

        if (platform == LaunchPlatform.MacOs)
            return BuildStartInfoMac(request);
        if (platform == LaunchPlatform.Linux)
            return BuildStartInfoLinux(request);

        if (!File.Exists(request.Path))
            throw new FileNotFoundException($"Executable not found: {request.Path}", request.Path);

        var ext = Path.GetExtension(request.Path).ToUpperInvariant();
        var isBatchFile = ext is ".CMD" or ".BAT";

        ProcessStartInfo psi;
        if (isBatchFile)
        {
            // Batch files must be launched via cmd.exe; UseShellExecute=false with cmd lets
            // us control the window and keep parentage clean.
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"{request.Path}\"{(request.Args is { Length: > 0 } a ? " " + a : "")}",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else if (request.Headless)
        {
            psi = new ProcessStartInfo
            {
                FileName = request.Path,
                Arguments = request.Args ?? "",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }
        else
        {
            // GUI app: UseShellExecute = true -> shell association, no ConPty inheritance.
            // This is the clean-parentage recipe (rule-0b fix).
            psi = new ProcessStartInfo
            {
                FileName = request.Path,
                Arguments = request.Args ?? "",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = true,
            };
        }

        return psi;
    }

    /// <summary>
    /// The macOS half of <see cref="BuildStartInfo"/>. See the class summary for the
    /// parentage modes. Pure, unit-testable seam like the Windows half.
    /// </summary>
    private static ProcessStartInfo BuildStartInfoMac(LaunchRequest request)
    {
        var ext = Path.GetExtension(request.Path).ToUpperInvariant();
        if (ext is ".CMD" or ".BAT")
            throw new NotSupportedException($"Windows batch files cannot be launched on macOS: {request.Path}");

        var isAppBundle = ext == ".APP" && Directory.Exists(request.Path);
        if (!isAppBundle && !File.Exists(request.Path))
            throw new FileNotFoundException($"Executable not found: {request.Path}", request.Path);

        if (isAppBundle)
        {
            // /usr/bin/open hands the launch to launchd: the application is NOT a child of
            // this launcher. --args forwards arguments to the application itself.
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                WorkingDirectory = request.Cwd ?? "",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(request.Path);
            if (request.Args is { Length: > 0 })
            {
                psi.ArgumentList.Add("--args");
                foreach (var arg in SplitArguments(request.Args))
                    psi.ArgumentList.Add(arg);
            }
            return psi;
        }

        if (ext == ".SH")
        {
            // Route shell scripts through bash so a missing execute bit never fails the launch.
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(request.Path);
            foreach (var arg in SplitArguments(request.Args ?? ""))
                psi.ArgumentList.Add(arg);
            return psi;
        }

        // Plain executable: direct spawn. The launcher (a launch agent) has no controlling
        // terminal, so the child inherits clean standard input/output and session context.
        return new ProcessStartInfo
        {
            FileName = request.Path,
            Arguments = request.Args ?? "",
            WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
            UseShellExecute = false,
        };
    }

    /// <summary>
    /// The Linux half of <see cref="BuildStartInfo"/>.
    ///
    /// The case that matters is the desktop entry, because on Linux that IS what the application
    /// catalogue contains (inspection finding M03-I2-02). A ".desktop" file describes a program; it
    /// is not the program, and running it as an executable does nothing. So the entry is read and
    /// the command on its own Exec line is started instead.
    ///
    /// The security property Phase 1 established is preserved exactly: what runs is decided by the
    /// catalogued file ON THIS MACHINE and nothing else. The caller named a catalogue entry; the
    /// entry named its program. A hosted caller cannot supply arguments or a working directory at
    /// all - the Gateway refuses those before the request reaches any launcher - and the arguments a
    /// self-host caller may still pass are added as separate argument-list entries, never
    /// concatenated into a command string where they could be re-read as extra words.
    /// </summary>
    private static ProcessStartInfo BuildStartInfoLinux(LaunchRequest request)
    {
        var ext = Path.GetExtension(request.Path).ToUpperInvariant();
        if (ext is ".CMD" or ".BAT")
            throw new NotSupportedException($"Windows batch files cannot be launched on Linux: {request.Path}");

        if (!File.Exists(request.Path))
            throw new FileNotFoundException($"Executable not found: {request.Path}", request.Path);

        if (ext == ".DESKTOP")
            return BuildStartInfoLinuxDesktopEntry(request);

        if (ext == ".SH")
        {
            // Route shell scripts through bash so a missing execute bit never fails the launch -
            // the same rule the macOS arm uses, for the same reason.
            var script = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
                UseShellExecute = false,
            };
            script.ArgumentList.Add(request.Path);
            foreach (var arg in SplitArguments(request.Args ?? ""))
                script.ArgumentList.Add(arg);
            return script;
        }

        // A plain executable named directly. This is the form that worked before the catalogue
        // allowlist landed and it still works: the launcher runs with no controlling terminal, so
        // the child starts with clean standard input and output.
        var psi = new ProcessStartInfo
        {
            FileName = request.Path,
            WorkingDirectory = request.Cwd ?? Path.GetDirectoryName(request.Path) ?? "",
            UseShellExecute = false,
        };
        foreach (var arg in SplitArguments(request.Args ?? ""))
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Turn a catalogued Linux desktop entry into the process start it describes. See
    /// <see cref="DesktopEntry"/> for why this is the fix for a deleted capability rather than a
    /// widening of the allowlist.
    /// </summary>
    private static ProcessStartInfo BuildStartInfoLinuxDesktopEntry(LaunchRequest request)
    {
        var entry = DesktopEntry.Read(request.Path);

        if (!string.Equals(entry.Type, "Application", StringComparison.Ordinal))
            throw new NotSupportedException(
                $"Only a Type=Application desktop entry names a program to start. '{request.Path}' is " +
                $"Type={entry.Type}, which describes a link or a directory, so there is nothing to launch.");

        if (entry.Terminal)
            throw new NotSupportedException(
                $"'{request.Path}' is a Terminal=true desktop entry: it must be run inside a terminal " +
                "emulator, and which emulator this machine uses is not something the launcher can " +
                "determine. Trying a list of likely ones would start the program under whichever " +
                "happened to be installed, or silently start it with no terminal at all. Launch it " +
                "from a session instead.");

        if (entry.Exec is null)
            throw new InvalidOperationException(
                $"'{request.Path}' has no Exec line, so it names no program to start.");

        var argv = DesktopEntry.ParseExec(entry.Exec);

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            // Path= in the entry is the working directory its author chose for the program. A
            // self-host caller may still override it; a hosted caller cannot supply one at all.
            WorkingDirectory = request.Cwd ?? entry.WorkingDirectory ?? "",
            UseShellExecute = false,
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);
        foreach (var arg in SplitArguments(request.Args ?? ""))
            psi.ArgumentList.Add(arg);

        FileLog.Write($"[LaunchService] desktop entry {request.Path} starts {argv[0]} " +
                      $"with {psi.ArgumentList.Count} arguments");
        return psi;
    }

    /// <summary>
    /// Split a single argument string into argv entries: whitespace separates, double
    /// quotes group. Needed on macOS where /usr/bin/open and /bin/bash take an argument
    /// LIST (ProcessStartInfo.ArgumentList), while the request carries one string.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Launch the given path with clean parentage. Returns the started process PID.
    /// Throws if the path is missing or the process fails to start.
    /// </summary>
    public int Launch(LaunchRequest request, string caller = "api")
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = BuildStartInfo(request);

        FileLog.Write($"[LaunchService] Launch: path={request.Path} args={request.Args ?? "(none)"} " +
                      $"headless={request.Headless} cwd={psi.WorkingDirectory} caller={caller}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for: {request.Path}");

        _launched[proc.Id] = request.Path;
        FileLog.Write($"[LaunchService] Launched pid={proc.Id} path={request.Path}");
        return proc.Id;
    }

    /// <summary>PIDs of processes launched since this service instance was created.</summary>
    public IReadOnlyList<int> LaunchedPids => _launched.Keys.ToList();
}

/// <summary>
/// The operating system a launch decision is being made for. Passed in rather than read from the
/// machine so every arm is testable on any one build machine - see
/// <see cref="LaunchService.BuildStartInfoFor"/>.
/// </summary>
internal enum LaunchPlatform
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>A request to launch an executable.</summary>
public sealed class LaunchRequest
{
    /// <summary>Absolute path to the executable (or .cmd/.bat).</summary>
    public required string Path { get; init; }

    /// <summary>Optional command-line arguments.</summary>
    public string? Args { get; init; }

    /// <summary>Optional working directory (defaults to the executable's directory).</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// When true, use UseShellExecute=false + CreateNoWindow=true (headless/hidden mode).
    /// When false (default), use UseShellExecute=true (GUI mode, clean parentage via shell).
    /// </summary>
    public bool Headless { get; init; }
}
