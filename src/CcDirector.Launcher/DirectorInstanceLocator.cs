using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Instances;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>How the search for the supervised Director ended.</summary>
public enum DirectorResolution
{
    /// <summary>Exactly one live Director owns the supervised instance. It is named in the lookup.</summary>
    Running,

    /// <summary>No live Director owns the supervised instance.</summary>
    NotRunning,

    /// <summary>
    /// More than one live process claims the supervised instance. NOTHING may be done to any of them:
    /// see the class comment on <see cref="DirectorInstanceLocator"/>.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// One live Director, identified well enough to be acted on.
/// </summary>
/// <param name="DirectorId">Its identifier - the only string that names ONE Director process.</param>
/// <param name="Pid">Its process id, taken from the registration it wrote and verified against the
/// running process, not guessed from a scan.</param>
/// <param name="InstanceHome">The storage home it registered in, and therefore where its crash journal,
/// its updater state and its secret live.</param>
/// <param name="Version">The version it reported when it started. Written BY the running process, which
/// is what makes it usable as proof that a swapped build actually came up.</param>
/// <param name="ProcessStartedAtUtc">When the operating system started that process.</param>
/// <param name="ExecutablePath">
/// The image this process is running, or an empty string when it would not say. Used ONLY to tell an
/// INSTALL from a development build when more than one process claims one instance - never to identify a
/// Director. See <see cref="DirectorInstanceLocator.BreakTheTie"/> for why that distinction is not a
/// re-run of the defect this class exists to remove.
/// </param>
public sealed record SupervisedDirector(
    string DirectorId, int Pid, string InstanceHome, string Version, DateTime ProcessStartedAtUtc,
    string ExecutablePath);

/// <summary>The answer to "which Director am I supervising", with the evidence behind it.</summary>
/// <param name="Outcome">What the search concluded.</param>
/// <param name="Director">The resolved Director when <paramref name="Outcome"/> is
/// <see cref="DirectorResolution.Running"/>; null otherwise.</param>
/// <param name="Candidates">Every live claimant, described. One entry when resolved, several when
/// ambiguous, none when nothing is running - so a log line can always say what was actually seen.</param>
/// <param name="Conflict">
/// Set when more than one live process claimed this instance - INCLUDING when the tie-break resolved it.
/// A resolved conflict is still a machine in a wrong state, and a tie-break that quietly does the right
/// thing is how the underlying defect survives unseen; this one survived long enough to be found by a
/// mission that was looking at something else. Callers must carry it somewhere a person will meet it, not
/// only into a log file.
/// </param>
public sealed record DirectorLookup(
    DirectorResolution Outcome, SupervisedDirector? Director, IReadOnlyList<string> Candidates,
    string? Conflict = null);

/// <summary>
/// Answers "is THIS Director running, and which process is it" - the question the launcher has to get
/// right before it stops anything or installs anything over it.
///
/// WHY THIS CLASS EXISTS. The launcher used to answer it with
/// <c>Process.GetProcessesByName("cc-director")</c>, keeping the first process whose image path matched
/// the installed executable. That is wrong on exactly the machine it matters on. A named instance is
/// the SAME executable with a different data home - <c>cc-director.exe</c> and
/// <c>cc-director.exe --instance work</c> have the same process name and the same image path - so a
/// machine running two of them produced two identical matches and the launcher kept whichever the
/// operating system listed first. That is not a wrong answer some of the time; it is an arbitrary
/// answer every time, and its consequences are a graceful shutdown sent to somebody else's Director and
/// a session count read from the wrong one, which is the number that decides whether an update may
/// interrupt live work.
///
/// HOW IT ANSWERS INSTEAD. Every Director writes a registration file into ITS OWN instance home naming
/// its identifier, its process id and the moment it registered. The launcher supervises exactly one
/// instance - the default one, because <see cref="DirectorSupervisor.Start"/> launches the installed
/// application with no instance flag - so it reads only that home's registrations and takes the process
/// id from the file. Neither the process name nor the image path is consulted at any point.
///
/// THE STALE-FILE PROBLEM, AND THE ONE CHECK THAT SOLVES IT. A registration outlives a Director that
/// was killed, and an operating system reuses process ids, so "the file says 34032 and 34032 exists" is
/// not proof. The check is the process's own START TIME: a Director always registers a moment AFTER it
/// starts, so the true owner's start time sits just before the registration's timestamp, while a
/// recycled id belongs to a process that started LATER - after the original died, which is necessarily
/// after the file was written. That single comparison rejects every recycled id and every registration
/// left by a process that is gone, using only fields the file already carries.
///
/// AMBIGUITY IS AN ANSWER WHEREVER IT IS REAL. When more than one live process claims the supervised
/// instance, this decides it ONLY when it is decidable and otherwise reports
/// <see cref="DirectorResolution.Ambiguous"/>, names every claimant, and callers refuse to act. The line
/// between the two is drawn in <see cref="BreakTheTie"/>, and that is the paragraph to read before
/// changing anything here: two processes running the SAME image are the real defect and stay refused,
/// because an arbitrary choice among equals is indistinguishable from a correct one right up until it
/// stops the wrong Director; a development build sitting in the installed application's instance home is
/// a different question with an answer, and refusing to update the real Director because somebody is
/// testing a slot build helps nobody.
///
/// The condition is real and not theoretical. It happens whenever a development build is started without
/// an instance flag, because the single-instance guard is keyed by executable slot rather than by
/// instance and does not prevent two different executables from claiming one home. It was found on the
/// machine this was written on, with two live Directors in one instance home. A RESOLVED CONFLICT IS
/// STILL A MACHINE IN A WRONG STATE and is reported as one, on <see cref="DirectorLookup.Conflict"/> as
/// well as in the log - a tie-break that silently does the right thing is how this defect stayed unseen.
/// </summary>
public sealed class DirectorInstanceLocator
{
    /// <summary>
    /// How long before its registration a process may have started and still be its author. Generous:
    /// a cold Director walks a splash screen, the engine and the control interface before it registers,
    /// and on a loaded machine that is seconds, not milliseconds. It only has to be shorter than the
    /// time between a process dying and its id being handed to something else, which is not seconds.
    /// </summary>
    public static readonly TimeSpan RegistrationLag = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far AFTER its registration a process may have started and still be its author: essentially
    /// not at all. The margin exists only because the two timestamps are taken from different sources
    /// on the same clock, not because a real Director ever registers before it starts.
    /// </summary>
    public static readonly TimeSpan RegistrationSkew = TimeSpan.FromSeconds(2);

    private readonly string _instanceHome;
    private readonly string? _legacyFlatDirectory;
    private readonly string _installedDirectorPath;

    /// <summary>The default instance of the installed application - what the launcher starts and stops.</summary>
    public DirectorInstanceLocator()
        : this(Path.Combine(CcStorage.Root(), "instances", InstanceContext.DefaultSlug),
               CcStorage.DirectorInstances(),
               InstallLayout.Default().PathFor(ComponentRegistry.Director)) { }

    /// <param name="instanceHome">The storage home of the instance to resolve. Tests aim this at a
    /// throwaway directory; production uses the default constructor.</param>
    /// <param name="legacyFlatDirectory">
    /// The pre-1.8 registration directory at the storage ROOT, or null to ignore it.
    ///
    /// It is read as PART OF the supervised instance rather than as a second place to look, because
    /// that is what it is: before 1.8 the installed Director had no instance folder and registered
    /// here, and from 1.8 the same Director registers one level in as instance "default". Two layouts,
    /// one Director. Dropping it would have quietly stopped a machine that has not been through an
    /// upgrade from being supervised at all, and named instances are still excluded either way - which
    /// is the precision this class exists for.
    /// </param>
    /// <param name="installedDirectorPath">
    /// The installed application's image. Used ONLY by the tie-break, and only to tell an install from a
    /// development build when more than one process claims this instance - never to identify a Director.
    /// Empty means no tie-break is possible, so every conflict is refused.
    /// </param>
    public DirectorInstanceLocator(string instanceHome, string? legacyFlatDirectory = null,
        string? installedDirectorPath = null)
    {
        _instanceHome = instanceHome ?? throw new ArgumentNullException(nameof(instanceHome));
        _legacyFlatDirectory = legacyFlatDirectory;
        _installedDirectorPath = installedDirectorPath ?? "";
    }

    /// <summary>The instance home being resolved.</summary>
    public string InstanceHome => _instanceHome;

    /// <summary>Every directory the supervised Director could have registered in, current layout first.</summary>
    public IEnumerable<string> RegistrationDirectories
    {
        get
        {
            yield return Path.Combine(_instanceHome, "config", "director", "instances");
            if (_legacyFlatDirectory is { Length: > 0 } flat) yield return flat;
        }
    }

    /// <summary>
    /// Resolve the supervised Director. Never throws: an unreadable directory is reported as nothing
    /// running, because a launcher that throws while working out whether to stop something is worse
    /// than one that declines to.
    /// </summary>
    public DirectorLookup Resolve()
    {
        var live = new List<SupervisedDirector>();
        var described = new List<string>();

        foreach (var (file, home, dto) in ReadRegistrations())
        {
            var process = TryGetLiveProcess(dto.Pid);
            if (process is null) continue;

            using (process)
            {
                DateTime startedAt;
                try
                {
                    startedAt = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex)
                {
                    // A process that cannot be interrogated cannot be certified as the registration's
                    // author, and an uncertified process must not be stopped or updated over.
                    FileLog.Write($"[DirectorInstanceLocator] pid={dto.Pid} from {file} could not be asked when it "
                                  + $"started ({ex.Message}), so it cannot be certified as the Director that wrote "
                                  + "that registration. Ignoring it.");
                    continue;
                }

                var earliest = dto.StartedAt - RegistrationLag;
                var latest = dto.StartedAt + RegistrationSkew;
                if (startedAt < earliest || startedAt > latest)
                {
                    FileLog.Write($"[DirectorInstanceLocator] pid={dto.Pid} is alive but started {startedAt:o}, "
                                  + $"outside the window {earliest:o}..{latest:o} implied by the registration in "
                                  + $"{file} (written {dto.StartedAt:o}). This is a process that INHERITED the id of "
                                  + "a dead Director, or a registration left behind by one. Ignoring it.");
                    continue;
                }

                var executable = ExecutablePathOf(process);
                live.Add(new SupervisedDirector(dto.DirectorId, dto.Pid, home, dto.Version, startedAt, executable));
                described.Add($"directorId={dto.DirectorId} pid={dto.Pid} version={dto.Version} "
                              + $"started={startedAt:o} exe={(executable.Length == 0 ? "UNREADABLE" : executable)} "
                              + $"registration={file}");
            }
        }

        if (live.Count == 0)
            return new DirectorLookup(DirectorResolution.NotRunning, null, described);

        // ONE claimant is resolved without the image ever being consulted. The tie-break exists only for
        // a conflict, and keeping it there is what stops a preference quietly becoming an identity check.
        if (live.Count == 1)
            return new DirectorLookup(DirectorResolution.Running, live[0], described);

        return BreakTheTie(live, described);
    }

    /// <summary>
    /// More than one live process claims this instance. Decide it when it is decidable, refuse when it is
    /// not.
    ///
    /// WHY AN EXECUTABLE PATH IS ADMISSIBLE HERE AND INADMISSIBLE ABOVE - READ THIS BEFORE CONCLUDING THE
    /// DEFECT WAS REINTRODUCED. The rule this class enforces is that a Director is never IDENTIFIED by its
    /// image path, and that rule is untouched: <see cref="Resolve"/> never consults the path, and a single
    /// claimant is resolved without looking at it. The reason is that every named instance of one install
    /// runs the SAME executable - <c>cc-director.exe</c> and <c>cc-director.exe --instance work</c> are
    /// one image - so a path cannot tell two instances apart. That case is the defect, and it is still
    /// REFUSED below.
    ///
    /// What a path CAN tell apart is an INSTALL from a development build, because those are different
    /// images in different places. That is a different question from "which Director is this", and it has
    /// an answer the launcher is entitled to act on: it supervises the installed application, a slot build
    /// somebody is testing is not the machine's Director of record, and refusing to update or stop the
    /// real one because a test build sits in the same instance home helps nobody. So the tie-break narrows
    /// the refusal to the case that is genuinely undecidable rather than refusing on every conflict.
    ///
    /// THIS IS NOT A FALLBACK. That rule forbids two PATHS to one capability - try one thing, fall back to
    /// another - because the second path is a door the mission exists to close. This is one path with a
    /// tie-break on an ambiguous input: no second mechanism, nothing retried, and the undecidable case
    /// still ends in a refusal rather than a guess.
    ///
    /// A CLAIMANT WHOSE IMAGE CANNOT BE READ POISONS THE TIE-BREAK. An elevated process will not say what
    /// it is running, and "I could not check" is not "it is not the install" - treating it as the latter
    /// would let the guard fail open in exactly the case where something unusual is going on. Unknown
    /// means refuse.
    /// </summary>
    private DirectorLookup BreakTheTie(List<SupervisedDirector> live, List<string> described)
    {
        var conflict = $"{live.Count} live processes claim the instance at {_instanceHome}: "
                       + string.Join(" | ", described);

        if (live.Any(d => d.ExecutablePath.Length == 0))
        {
            FileLog.Write($"[DirectorInstanceLocator] REFUSING to choose: {conflict}. At least one claimant would "
                          + "not say what image it is running, so it cannot be ruled out as a second copy of the "
                          + "installed Director. Nothing will be stopped, restarted or updated until this is fixed.");
            return new DirectorLookup(DirectorResolution.Ambiguous, null, described, conflict);
        }

        var installed = live.Where(d => IsInstalledDirector(d.ExecutablePath)).ToList();
        if (installed.Count != 1)
        {
            FileLog.Write($"[DirectorInstanceLocator] REFUSING to choose: {conflict}. "
                          + (installed.Count == 0
                              ? "None of them is the installed Director, so there is nothing here this launcher "
                                + "supervises."
                              : $"{installed.Count} of them run the SAME installed executable, which is the case a "
                                + "path cannot decide - two named instances of one install look identical. This is "
                                + "the defect this launcher refuses to guess about.")
                          + " Nothing will be stopped, restarted or updated until this is fixed.");
            return new DirectorLookup(DirectorResolution.Ambiguous, null, described, conflict);
        }

        // Decided - and the machine is STILL WRONG. The whole reason this defect went unnoticed is that
        // everything carried on working, so it is said at every pass and handed to the caller to put
        // somewhere other than this log.
        FileLog.Write($"[DirectorInstanceLocator] CONFLICT - {conflict}. Resolved to the INSTALLED Director "
                      + $"(directorId={installed[0].DirectorId} pid={installed[0].Pid} exe={installed[0].ExecutablePath}) "
                      + "because the others are not the installed application. THIS MACHINE IS STILL IN A WRONG "
                      + "STATE: two processes should never share one instance home, and the single-instance guard "
                      + "does not prevent it because it is keyed by executable slot rather than by instance.");
        return new DirectorLookup(DirectorResolution.Running, installed[0], described, conflict);
    }

    /// <summary>True when this image is the installed Director (inside the bundle on macOS).</summary>
    private bool IsInstalledDirector(string executablePath)
    {
        if (executablePath.Length == 0 || _installedDirectorPath.Length == 0) return false;
        return OperatingSystem.IsWindows()
            ? string.Equals(executablePath, _installedDirectorPath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(executablePath, _installedDirectorPath, StringComparison.Ordinal)
              || executablePath.StartsWith(_installedDirectorPath + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The image a process is running, or "" when it will not say. Windows reads the main module; macOS
    /// and Linux ask /bin/ps, because MainModule is unreliable there.
    /// </summary>
    private static string ExecutablePathOf(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return process.MainModule?.FileName ?? "";
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorInstanceLocator] pid={process.Id} would not say what image it is "
                              + $"running: {ex.Message}");
                return "";
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("comm=");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(process.Id.ToString());

            using var ps = Process.Start(psi);
            if (ps is null) return "";
            var output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return ps.ExitCode == 0 ? output : "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorInstanceLocator] pid={process.Id} image path could not be read: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// How many sessions the resolved Director is holding, read from the roster it maintains on disk.
    ///
    /// THIS REPLACES ASKING IT OVER A SOCKET, AND IT IS NOT A WEAKER ANSWER. The objection to a file is
    /// that it says what was true at some earlier moment - but this file is rewritten on every change to
    /// the session set, atomically, and it is DELETED on a clean shutdown. So while the Director is
    /// alive the file is exactly as current as the roster itself, and its absence beside a live Director
    /// is not "zero sessions", it is "no answer" - which is why this returns null there rather than a
    /// number that would read as idle and let an update proceed.
    /// </summary>
    public int? ReadSessionCount(SupervisedDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);
        var directory = Path.Combine(director.InstanceHome, "config", "director", "crash-journal");
        var roster = DirectorCrashJournal.ReadLiveRoster(director.DirectorId, directory);
        if (roster is null)
        {
            FileLog.Write($"[DirectorInstanceLocator] the Director {director.DirectorId} is running but no live "
                          + $"session roster was readable under {directory}, so how busy it is is UNKNOWN. It is not "
                          + "reported as idle: an update must never proceed on a missing answer.");
            return null;
        }

        return roster.Sessions.Count;
    }

    private IEnumerable<(string File, string Home, Gateway.Contracts.DirectorDto Dto)> ReadRegistrations()
    {
        foreach (var dir in RegistrationDirectories)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorInstanceLocator] cannot list {dir}: {ex.Message}");
                continue;
            }

            // The storage home this registration belongs to: three levels up from
            // <home>/config/director/instances. Carried per registration rather than assumed, because
            // the crash journal that says how busy that Director is lives under ITS home, and the two
            // layouts put it in different places.
            var home = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));

            foreach (var file in files)
            {
                Gateway.Contracts.DirectorDto? dto = null;
                try
                {
                    dto = JsonSerializer.Deserialize<Gateway.Contracts.DirectorDto>(File.ReadAllText(file), JsonOptions);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[DirectorInstanceLocator] cannot read the registration {file}: {ex.Message}");
                }

                if (dto is null || dto.Pid <= 0 || string.IsNullOrWhiteSpace(dto.DirectorId)) continue;
                yield return (file, home, dto);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static Process? TryGetLiveProcess(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException)
        {
            return null; // no such process
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorInstanceLocator] cannot inspect pid={pid}: {ex.Message}");
            return null;
        }
    }
}
