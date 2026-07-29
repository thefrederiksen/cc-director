using CcDirector.Core.Instances;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Starting, stopping and restarting a NAMED Director instance, rather than only the machine's default one.
///
/// WHY THIS EXISTS. The lifecycle verbs could act on exactly one Director per machine - the default - which
/// on any machine in use is the one carrying everybody's sessions. So a remote restart could not be tested
/// without interrupting real work, and there was no way to bring up a spare Director to exercise it against.
/// Naming an instance solves both: a spare can be created, restarted and thrown away while the default keeps
/// serving.
///
/// THE PROPERTY THAT MATTERS MOST is not that a named instance can be started - it is that acting on one
/// cannot touch another. Every instance runs the SAME executable, so the image-path match that identifies
/// "the installed Director" cannot tell two of them apart; it would return whichever process it met first.
/// The registration directory is the only evidence that says which instance a process belongs to, and these
/// tests are written against that.
/// </summary>
[Collection(DirectorRootCollection.Name)]
public sealed class DirectorSupervisorNamedInstanceTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public DirectorSupervisorNamedInstanceTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-named-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", _previousInstancesDir);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static void WriteRegistration(string directory, string directorId, int port, int? pid = null)
    {
        Directory.CreateDirectory(directory);
        var json = $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{pid ?? Environment.ProcessId}},
          "ControlEndpoint": "http://127.0.0.1:{{port}}",
          "Version": "1.8.4"
        }
        """;
        File.WriteAllText(Path.Combine(directory, directorId + ".json"), json);
    }

    // =====================================================================================================
    // Naming
    // =====================================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSlug_NothingNamed_IsTheDefaultInstance(string? given)
    {
        Assert.Equal(InstanceContext.DefaultSlug, DirectorSupervisor.NormalizeSlug(given));
        Assert.True(DirectorSupervisor.IsDefaultSlug(given));
    }

    /// <summary>
    /// A name is normalised the same way the Director normalises its own, or the launcher would look in
    /// "Test" while the Director wrote to "test" and neither would ever find the other.
    /// </summary>
    [Theory]
    [InlineData("Test", "test")]
    [InlineData("  SPARE  ", "spare")]
    public void NormalizeSlug_MatchesTheDirectorsOwnRule(string given, string expected)
    {
        Assert.Equal(expected, DirectorSupervisor.NormalizeSlug(given));
        Assert.False(DirectorSupervisor.IsDefaultSlug(given));
    }

    [Fact]
    public void RegistrationDirectoryFor_IsThatInstancesOwnHome()
    {
        var directory = DirectorSupervisor.RegistrationDirectoryFor("spare");

        Assert.Equal(Path.Combine(_root, "instances", "spare", "config", "director", "instances"), directory);
    }

    // =====================================================================================================
    // Isolation - the property that keeps a restart from hitting the wrong Director
    // =====================================================================================================

    /// <summary>
    /// Asking about one instance must not return another's registration. If this regressed, a remote restart
    /// aimed at a spare would find the default Director's port and shut down everybody's sessions.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_ForOneInstance_IgnoresEveryOther()
    {
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("default"), "d0000000-0000-0000-0000-000000000001", port: 7879);
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("spare"), "d0000000-0000-0000-0000-000000000002", port: 7999);

        var spare = DirectorSupervisor.ReadInstanceRegistrations("spare");

        Assert.Equal(7999, Assert.Single(spare).Port);
    }

    [Fact]
    public void ReadInstanceRegistrations_ForTheDefault_IgnoresNamedInstances()
    {
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("default"), "d0000000-0000-0000-0000-000000000003", port: 7879);
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("spare"), "d0000000-0000-0000-0000-000000000004", port: 7999);

        var byName = DirectorSupervisor.ReadInstanceRegistrations("default").Select(r => r.Port).ToList();
        var byNull = DirectorSupervisor.ReadInstanceRegistrations(null).Select(r => r.Port).ToList();

        Assert.Equal(new[] { 7879 }, byName);
        Assert.Equal(new[] { 7879 }, byNull);
    }

    /// <summary>
    /// The default instance ALSO answers from the pre-1.8 flat directory, because a machine upgraded but not
    /// yet restarted still has its registration there. Dropping that would make the launcher unable to stop a
    /// Director it can plainly see.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_ForTheDefault_AlsoReadsThePre18FlatLocation()
    {
        var flat = Path.Combine(_root, "config", "director", "instances");
        WriteRegistration(flat, "d0000000-0000-0000-0000-000000000005", port: 7878);

        var ports = DirectorSupervisor.ReadInstanceRegistrations(null).Select(r => r.Port).ToList();

        Assert.Equal(new[] { 7878 }, ports);
    }

    /// <summary>A named instance does NOT inherit the flat location - that belongs to the default alone.</summary>
    [Fact]
    public void ReadInstanceRegistrations_ForANamedInstance_DoesNotReadThePre18FlatLocation()
    {
        var flat = Path.Combine(_root, "config", "director", "instances");
        WriteRegistration(flat, "d0000000-0000-0000-0000-000000000006", port: 7878);

        Assert.Empty(DirectorSupervisor.ReadInstanceRegistrations("spare"));
    }

    [Fact]
    public void ReadInstanceRegistrations_UnknownInstance_IsEmptyRatherThanThrowing()
    {
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("default"), "d0000000-0000-0000-0000-000000000007", port: 7879);

        Assert.Empty(DirectorSupervisor.ReadInstanceRegistrations("never-started"));
    }

    /// <summary>
    /// The whole-machine scan still sees everything. The instance-scoped read narrows a caller's view; it does
    /// not narrow what the launcher can discover when it is asked about the machine as a whole.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_WithNoInstanceNamed_StillSeesEveryInstance()
    {
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("default"), "d0000000-0000-0000-0000-000000000008", port: 7879);
        WriteRegistration(DirectorSupervisor.RegistrationDirectoryFor("spare"), "d0000000-0000-0000-0000-000000000009", port: 7999);

        var ports = DirectorSupervisor.ReadInstanceRegistrations().Select(r => r.Port).OrderBy(p => p).ToList();

        Assert.Equal(new[] { 7879, 7999 }, ports);
    }
}
