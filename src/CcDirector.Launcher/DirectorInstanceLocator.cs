using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Instances;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

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
public sealed record SupervisedDirector(
    string DirectorId, int Pid, string InstanceHome, string Version, DateTime ProcessStartedAtUtc);

/// <summary>The answer to "which Director am I supervising", with the evidence behind it.</summary>
/// <param name="Outcome">What the search concluded.</param>
/// <param name="Director">The resolved Director when <paramref name="Outcome"/> is
/// <see cref="DirectorResolution.Running"/>; null otherwise.</param>
/// <param name="Candidates">Every live claimant, described. One entry when resolved, several when
/// ambiguous, none when nothing is running - so a log line can always say what was actually seen.</param>
public sealed record DirectorLookup(
    DirectorResolution Outcome, SupervisedDirector? Director, IReadOnlyList<string> Candidates);

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
/// AMBIGUITY IS AN ANSWER, NOT A TIE TO BREAK. When two live processes both claim the supervised
/// instance, this reports <see cref="DirectorResolution.Ambiguous"/> and names them all, and callers
/// must refuse to act. Picking one would be the original defect wearing a tidier interface: the whole
/// reason to stop scanning by name was that an arbitrary choice among equals is indistinguishable from
/// a correct one right up until it stops the wrong Director. The condition is real and not theoretical
/// - it happens whenever a development build is started without an instance flag, because the
/// single-instance guard is keyed by executable slot rather than by instance and does not prevent two
/// different executables from claiming one home.
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

    /// <summary>The default instance of the installed application - what the launcher starts and stops.</summary>
    public DirectorInstanceLocator()
        : this(Path.Combine(CcStorage.Root(), "instances", InstanceContext.DefaultSlug),
               CcStorage.DirectorInstances()) { }

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
    public DirectorInstanceLocator(string instanceHome, string? legacyFlatDirectory = null)
    {
        _instanceHome = instanceHome ?? throw new ArgumentNullException(nameof(instanceHome));
        _legacyFlatDirectory = legacyFlatDirectory;
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

                live.Add(new SupervisedDirector(dto.DirectorId, dto.Pid, home, dto.Version, startedAt));
                described.Add($"directorId={dto.DirectorId} pid={dto.Pid} version={dto.Version} "
                              + $"started={startedAt:o} registration={file}");
            }
        }

        if (live.Count == 0)
            return new DirectorLookup(DirectorResolution.NotRunning, null, described);

        if (live.Count > 1)
        {
            FileLog.Write($"[DirectorInstanceLocator] {live.Count} live processes all claim the instance at "
                          + $"{_instanceHome}, so which one the launcher supervises is UNDECIDABLE and nothing will "
                          + "be stopped, restarted or updated until it is not. Claimants: "
                          + string.Join(" | ", described));
            return new DirectorLookup(DirectorResolution.Ambiguous, null, described);
        }

        return new DirectorLookup(DirectorResolution.Running, live[0], described);
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
