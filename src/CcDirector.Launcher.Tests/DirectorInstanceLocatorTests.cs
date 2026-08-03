using System.Diagnostics;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher has to answer "is THIS Director running, and which process is it" before it stops
/// anything or installs anything over it. These pin that answer.
///
/// THE DEFECT THEY PIN. The launcher used to scan by process name and keep the first process whose
/// image path matched the installed executable. A named instance is the SAME executable with a
/// different data home - <c>cc-director.exe</c> and <c>cc-director.exe --instance work</c> have the
/// same name and the same image path - so a machine running two of them produced two identical matches
/// and the launcher kept whichever the operating system happened to list first. That is not a wrong
/// answer some of the time; it is an arbitrary answer every time, and its consequences are a shutdown
/// sent to somebody else's Director and a session count read from the wrong one - the number that
/// decides whether an update may interrupt live work.
///
/// The old tests could not have caught it. They asserted that BOTH the flat root and every named
/// instance home were scanned, which is exactly the behaviour that made two instances indistinguishable.
/// The scan was correct as a way to find registrations and wrong as a way to identify ONE Director.
///
/// THE OTHER DEFECT THEY PIN. A registration outlives a Director that was killed, and an operating
/// system reuses process ids, so "the file says 34032 and 34032 exists" is not proof. The identity
/// check is the process start time against the registration's timestamp - <see
/// cref="ProcessStartTime_AfterTheRegistration_IsRejectedAsARecycledProcessId"/> is the one that
/// fails without it.
///
/// These drive the production <see cref="DirectorInstanceLocator"/>, not a copy of it.
/// </summary>
[Collection(StorageRootCollection.Name)]
public sealed class DirectorInstanceLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public DirectorInstanceLocatorTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", _previousInstancesDir);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the run is not a failure */ }
    }

    private string InstanceHome(string slug) => Path.Combine(_root, "instances", slug);
    private string FlatDirectory => Path.Combine(_root, "config", "director", "instances");
    private string InstanceDirectory(string slug) =>
        Path.Combine(InstanceHome(slug), "config", "director", "instances");

    /// <summary>The locator as production builds it: the default instance, plus the pre-1.8 flat path.</summary>
    private DirectorInstanceLocator Locator() =>
        new(InstanceHome("default"), FlatDirectory);

    /// <summary>
    /// Write a registration exactly as a Director writes one.
    ///
    /// The pid defaults to this test process, so the entry describes something genuinely alive, and
    /// StartedAt defaults to a moment AFTER this process started - which is the truthful shape, because
    /// a Director always registers after it starts. A test that wants to exercise the identity check
    /// passes a StartedAt that breaks that ordering.
    /// </summary>
    private static void WriteRegistration(
        string directory, string directorId, int port = 7879, int? pid = null,
        DateTime? startedAt = null, string version = "1.9.7")
    {
        Directory.CreateDirectory(directory);
        var stamp = startedAt ?? DateTime.UtcNow;
        var json = $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{pid ?? Environment.ProcessId}},
          "StartedAt": "{{stamp:o}}",
          "ControlEndpoint": "http://127.0.0.1:{{port}}",
          "Version": "{{version}}"
        }
        """;
        File.WriteAllText(Path.Combine(directory, directorId + ".json"), json);
    }

    private static void WriteRoster(string home, string directorId, int sessions)
    {
        var dir = Path.Combine(home, "config", "director", "crash-journal");
        Directory.CreateDirectory(dir);
        var entries = string.Join(",", Enumerable.Range(0, sessions).Select(i =>
            $$"""{"SessionId":"session-{{i}}","RepoPath":"D:/repo","Agent":"ClaudeCode"}"""));
        File.WriteAllText(Path.Combine(dir, directorId + ".json"),
            $$"""{"DirectorId":"{{directorId}}","Pid":{{Environment.ProcessId}},"Sessions":[{{entries}}]}""");
    }

    [Fact]
    public void NothingRegistered_IsNotRunning()
    {
        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.NotRunning, lookup.Outcome);
        Assert.Null(lookup.Director);
    }

    [Fact]
    public void ALiveRegistrationInTheDefaultInstance_ResolvesToThatDirector()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000001", version: "1.9.7");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-000000000001", lookup.Director!.DirectorId);
        Assert.Equal(Environment.ProcessId, lookup.Director.Pid);
        Assert.Equal("1.9.7", lookup.Director.Version);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A second Director running as a NAMED instance is the same executable with
    /// the same process name, so the old scan matched it just as well as the installed default and
    /// could return either. The launcher supervises the default instance, and only that one may be
    /// resolved.
    /// </summary>
    [Fact]
    public void ANamedInstanceRunningAlongside_IsNotTheSupervisedDirector()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-00000000000d");
        WriteRegistration(InstanceDirectory("work"), "aaaa0001-0000-0000-0000-00000000000e",
            port: 7881);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-00000000000d", lookup.Director!.DirectorId);
        Assert.Single(lookup.Candidates);
    }

    /// <summary>
    /// Two live processes claiming the SAME instance is undecidable, and the launcher must say so
    /// rather than pick. This is not hypothetical: it is what a development build started without an
    /// instance flag does, because the single-instance guard is keyed by executable slot rather than by
    /// instance and does not prevent two executables from claiming one home.
    /// </summary>
    [Fact]
    public void TwoLiveProcessesClaimingTheSameInstance_IsAmbiguousAndNamesBoth()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000010");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000011", port: 7880);

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Ambiguous, lookup.Outcome);
        Assert.Null(lookup.Director);
        Assert.Equal(2, lookup.Candidates.Count);
    }

    /// <summary>
    /// A registration whose process id is long gone is not evidence of anything, and this is the exact
    /// shape a force-killed Director leaves behind.
    /// </summary>
    [Fact]
    public void ARegistrationForADeadProcessId_IsIgnored()
    {
        // A pid that cannot be running: process ids are positive, and this range is not handed out to
        // a live user process on either platform in a way that could collide with a real Director.
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000020",
            pid: 999_999_996);

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// THE PROCESS-ID REUSE CHECK. The registration names a pid that IS alive, but that process started
    /// well after the registration was written - so it inherited the id from the Director that wrote
    /// the file rather than being it. Without the start-time comparison this resolves to a live
    /// process and the launcher goes on to shut down something it does not own.
    /// </summary>
    [Fact]
    public void ProcessStartTime_AfterTheRegistration_IsRejectedAsARecycledProcessId()
    {
        // Registered an hour before this process started, which no true author ever is.
        var beforeThisProcessStarted =
            Process.GetCurrentProcess().StartTime.ToUniversalTime() - TimeSpan.FromHours(1);
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000030",
            startedAt: beforeThisProcessStarted);

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// The other direction of the same check: a registration written far in the future cannot have been
    /// written by a process that started long before it. A guard has two failure directions and only
    /// asserting one of them leaves half of it untested.
    /// </summary>
    [Fact]
    public void ProcessStartTime_LongBeforeTheRegistration_IsAlsoRejected()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000031",
            startedAt: DateTime.UtcNow + TimeSpan.FromDays(1));

        Assert.Equal(DirectorResolution.NotRunning, Locator().Resolve().Outcome);
    }

    /// <summary>
    /// A pre-1.8 Director registers at the storage root rather than in an instance folder, and it is
    /// still the Director this launcher supervises. Two layouts, one Director.
    /// </summary>
    [Fact]
    public void APreInstanceLayoutRegistration_IsStillTheSupervisedDirector()
    {
        WriteRegistration(FlatDirectory, "aaaa0001-0000-0000-0000-000000000040", version: "1.7.2");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("1.7.2", lookup.Director!.Version);
    }

    [Fact]
    public void AMalformedRegistration_IsSkippedRatherThanFailingTheLookup()
    {
        Directory.CreateDirectory(InstanceDirectory("default"));
        File.WriteAllText(Path.Combine(InstanceDirectory("default"), "broken.json"), "{ this is not json");
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000050");

        var lookup = Locator().Resolve();

        Assert.Equal(DirectorResolution.Running, lookup.Outcome);
        Assert.Equal("aaaa0001-0000-0000-0000-000000000050", lookup.Director!.DirectorId);
    }

    [Fact]
    public void TheSessionCount_ComesFromTheRostersTheResolvedDirectorMaintains()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000060");
        WriteRoster(InstanceHome("default"), "aaaa0001-0000-0000-0000-000000000060", sessions: 3);

        var locator = Locator();
        var lookup = locator.Resolve();

        Assert.Equal(3, locator.ReadSessionCount(lookup.Director!));
    }

    /// <summary>
    /// An idle Director maintains a roster with nothing in it, which is a genuine zero and must read as
    /// one. The distinction from the case below is the whole reason the count is nullable.
    /// </summary>
    [Fact]
    public void AnEmptyRoster_ReadsAsZeroSessions()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000061");
        WriteRoster(InstanceHome("default"), "aaaa0001-0000-0000-0000-000000000061", sessions: 0);

        var locator = Locator();
        Assert.Equal(0, locator.ReadSessionCount(locator.Resolve().Director!));
    }

    /// <summary>
    /// NO ROSTER IS NOT NO SESSIONS. A missing file means the answer is unknown, and an unknown answer
    /// must never let an update proceed - the update owner turns this null into HeldBecauseUnknown. A
    /// zero here would silently restart a Director holding live work.
    /// </summary>
    [Fact]
    public void AMissingRoster_ReadsAsUnknownRatherThanZero()
    {
        WriteRegistration(InstanceDirectory("default"), "aaaa0001-0000-0000-0000-000000000062");

        var locator = Locator();
        Assert.Null(locator.ReadSessionCount(locator.Resolve().Director!));
    }

    /// <summary>
    /// The roster of a PRE-1.8 Director lives under the storage root, not under an instance folder, and
    /// the count has to be read from wherever that Director actually keeps it. This is why the home
    /// travels on the resolved Director rather than being assumed from the locator's own instance.
    /// </summary>
    [Fact]
    public void TheSessionCountOfAPreInstanceLayoutDirector_IsReadFromTheRoot()
    {
        WriteRegistration(FlatDirectory, "aaaa0001-0000-0000-0000-000000000070");
        WriteRoster(_root, "aaaa0001-0000-0000-0000-000000000070", sessions: 2);

        var locator = Locator();
        Assert.Equal(2, locator.ReadSessionCount(locator.Resolve().Director!));
    }
}
