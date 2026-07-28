using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The launcher has to find a running Director's instance registration, and from 1.8 that registration is not
/// where it used to be.
///
/// THE DEFECT THESE PIN. The launcher runs at the storage ROOT, so it read
/// <c>&lt;root&gt;/config/director/instances</c>. A Director started for a named instance keeps its whole
/// storage under <c>&lt;root&gt;/instances/&lt;slug&gt;/</c> and registers THERE instead, and from 1.8 the
/// installed Director boots as instance "default". So on a normal machine the directory the launcher read was
/// empty and every live registration sat one level in.
///
/// It failed differently on each platform, which is how it survived. On macOS the running check walks these
/// files, so the launcher reported the Director as not running while it was up. On Windows the running check
/// enumerates processes and was CORRECT - the damage was one level down, where the Control API port is looked
/// up: no registration meant port 0, the graceful shutdown was skipped, and every remote stop and restart
/// force-killed while appearing to succeed.
///
/// These tests are written against the ROOT rather than against either symptom, because one lookup feeds both.
/// </summary>
public sealed class DirectorSupervisorInstanceDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public DirectorSupervisorInstanceDiscoveryTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        // This override pins the flat directory on its own. Left set, it would mask exactly what these tests
        // are about, so it is cleared for the duration and restored afterwards.
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-supervisor-discovery-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The flat directory a pre-1.8 Director registers in.</summary>
    private string FlatDirectory => Path.Combine(_root, "config", "director", "instances");

    /// <summary>The directory a Director running as <paramref name="slug"/> registers in.</summary>
    private string InstanceDirectory(string slug) =>
        Path.Combine(_root, "instances", slug, "config", "director", "instances");

    /// <summary>
    /// Write a registration exactly as a Director writes one. The pid is this test process by default, so the
    /// entry describes a process that is genuinely alive - a registration for a dead pid is meaningless to
    /// every caller of this code.
    /// </summary>
    private static void WriteRegistration(string directory, string directorId, int port, int? pid = null)
    {
        Directory.CreateDirectory(directory);
        var json = $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{pid ?? Environment.ProcessId}},
          "ControlEndpoint": "http://127.0.0.1:{{port}}",
          "Version": "1.8.3"
        }
        """;
        File.WriteAllText(Path.Combine(directory, directorId + ".json"), json);
    }

    [Fact]
    public void InstanceRegistrationDirectories_IncludesTheFlatPath()
    {
        Directory.CreateDirectory(FlatDirectory);

        var directories = DirectorSupervisor.InstanceRegistrationDirectories().ToList();

        Assert.Contains(FlatDirectory, directories);
    }

    /// <summary>The regression itself: the per-instance home must be scanned, not only the root's own.</summary>
    [Fact]
    public void InstanceRegistrationDirectories_IncludesEveryPerInstanceHome()
    {
        Directory.CreateDirectory(InstanceDirectory("default"));
        Directory.CreateDirectory(InstanceDirectory("work"));

        var directories = DirectorSupervisor.InstanceRegistrationDirectories().ToList();

        Assert.Contains(InstanceDirectory("default"), directories);
        Assert.Contains(InstanceDirectory("work"), directories);
    }

    [Fact]
    public void InstanceRegistrationDirectories_WithNoInstancesRoot_StillYieldsTheFlatPath()
    {
        // A machine that has never run a named instance has no instances/ directory at all. Enumerating must
        // not throw or come back empty - the pre-1.8 layout is still a supported machine.
        var directories = DirectorSupervisor.InstanceRegistrationDirectories().ToList();

        Assert.Contains(FlatDirectory, directories);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A 1.8 Director's registration lives in the per-instance home; the launcher must
    /// read it. Before the fix this returned nothing, which is what made the port lookup fail and the stop
    /// force-kill.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_FindsARegistrationInAPerInstanceHome()
    {
        WriteRegistration(InstanceDirectory("default"), "c6db060e-0000-0000-0000-000000000001", port: 7879);

        var registrations = DirectorSupervisor.ReadInstanceRegistrations();

        var found = Assert.Single(registrations);
        Assert.Equal(Environment.ProcessId, found.Pid);
        Assert.Equal(7879, found.Port);
    }

    /// <summary>
    /// A pre-1.8 Director still registers flat, and must still be found. Replacing the old path rather than
    /// adding to it would have broken those machines the way the old launcher breaks 1.8 ones.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_StillFindsAFlatRegistration()
    {
        WriteRegistration(FlatDirectory, "5edf0787-0000-0000-0000-000000000002", port: 7880);

        var registrations = DirectorSupervisor.ReadInstanceRegistrations();

        var found = Assert.Single(registrations);
        Assert.Equal(7880, found.Port);
    }

    [Fact]
    public void ReadInstanceRegistrations_FindsBothLayoutsAtOnce()
    {
        WriteRegistration(FlatDirectory, "5edf0787-0000-0000-0000-000000000003", port: 7880);
        WriteRegistration(InstanceDirectory("default"), "c6db060e-0000-0000-0000-000000000004", port: 7879);

        var ports = DirectorSupervisor.ReadInstanceRegistrations().Select(r => r.Port).OrderBy(p => p).ToList();

        Assert.Equal(new[] { 7879, 7880 }, ports);
    }

    [Fact]
    public void ReadInstanceRegistrations_FindsRegistrationsAcrossSeveralInstances()
    {
        WriteRegistration(InstanceDirectory("default"), "c6db060e-0000-0000-0000-000000000005", port: 7879);
        WriteRegistration(InstanceDirectory("work"), "c6db060e-0000-0000-0000-000000000006", port: 7881);

        var ports = DirectorSupervisor.ReadInstanceRegistrations().Select(r => r.Port).OrderBy(p => p).ToList();

        Assert.Equal(new[] { 7879, 7881 }, ports);
    }

    [Fact]
    public void ReadInstanceRegistrations_MalformedFile_IsSkippedRatherThanFailingTheScan()
    {
        Directory.CreateDirectory(InstanceDirectory("default"));
        File.WriteAllText(Path.Combine(InstanceDirectory("default"), "broken.json"), "{ this is not json");
        WriteRegistration(InstanceDirectory("default"), "c6db060e-0000-0000-0000-000000000007", port: 7879);

        var registrations = DirectorSupervisor.ReadInstanceRegistrations();

        Assert.Equal(7879, Assert.Single(registrations).Port);
    }

    [Fact]
    public void ReadInstanceRegistrations_NothingRegistered_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(DirectorSupervisor.ReadInstanceRegistrations());
    }
}
