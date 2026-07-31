using System.Net.Http;
using CcDirector.Core.Security;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The credential the launcher presents must be derived from the secret of the Director instance it
/// is addressing - which lives under THAT instance's home - not from the launcher's own shared root.
///
/// THE DEFECT THESE PIN (re-inspection P1). Every Director, the default included, keeps its whole
/// storage under <c>&lt;root&gt;/instances/&lt;slug&gt;</c>, so on a clean install the shared root
/// holds no secret at all and the flat-root read found nothing; on an upgraded machine it could find
/// a STALE flat file and only work when that file happened to match. Either way the launcher minted
/// from the wrong place, the Director refused, and the refusal fell through to force-kill - the
/// exact #960 harm the graceful shutdown exists to avoid.
///
/// The fix carries the instance home on the registration itself (the same file that supplies the
/// port), so the secret read and the Director called are the same instance by construction. These
/// tests drive the production <see cref="DirectorSupervisor.AttachDirectorCredential"/> and the
/// production registration scan - not copies of them.
/// </summary>
[Collection(StorageRootCollection.Name)]
public sealed class DirectorSupervisorCredentialTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    public DirectorSupervisorCredentialTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-supervisor-credential-" + Guid.NewGuid().ToString("N"));
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

    private static void WriteSecret(string home, string secret)
    {
        var dir = Path.Combine(home, "config", "director");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gateway-token.txt"), secret);
    }

    private static void WriteRegistration(string home, string directorId, int port)
    {
        var dir = Path.Combine(home, "config", "director", "instances");
        Directory.CreateDirectory(dir);
        var json = $$"""
        {
          "DirectorId": "{{directorId}}",
          "Pid": {{Environment.ProcessId}},
          "ControlEndpoint": "http://127.0.0.1:{{port}}",
          "Version": "1.8.3"
        }
        """;
        File.WriteAllText(Path.Combine(dir, directorId + ".json"), json);
    }

    private static string? BearerOf(HttpRequestMessage request) => request.Headers.Authorization?.Parameter;

    /// <summary>
    /// The independent reproduction from the re-inspection, kept as the regression: different
    /// secrets at the shared root and in the default instance's home, and the credential must be
    /// verified by the INSTANCE secret - the one its Director actually accepts.
    /// </summary>
    [Fact]
    public void AttachDirectorCredential_MintsFromTheMatchedInstance_NotTheLaunchersSharedRoot()
    {
        WriteSecret(_root, "the-shared-roots-stale-secret");
        WriteSecret(InstanceHome("default"), "the-default-instances-secret");
        WriteRegistration(InstanceHome("default"), "c6db060e-0000-0000-0000-000000000010", port: 7879);

        var registration = Assert.Single(DirectorSupervisor.ReadInstanceRegistrations());
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:7879/shutdown");
        DirectorSupervisor.AttachDirectorCredential(request, registration.Home);

        Assert.Equal(DirectorScopedToken.Mint("the-default-instances-secret", ScopeNames.Admin), BearerOf(request));
        Assert.NotEqual(DirectorScopedToken.Mint("the-shared-roots-stale-secret", ScopeNames.Admin), BearerOf(request));
    }

    /// <summary>
    /// The clean-install acceptance row: a named-default layout with NO flat files at all. The old
    /// resolver found nothing here - and worse, LoadOrCreate then WROTE a fresh flat-root token
    /// file, planting the stale stray that could mask this bug on the next run.
    /// </summary>
    [Fact]
    public void AttachDirectorCredential_CleanNamedDefaultInstall_MintsFromTheDefaultInstancesHome()
    {
        WriteSecret(InstanceHome("default"), "the-only-secret-on-this-machine");
        WriteRegistration(InstanceHome("default"), "c6db060e-0000-0000-0000-000000000011", port: 7879);

        var registration = Assert.Single(DirectorSupervisor.ReadInstanceRegistrations());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:7879/healthz");
        DirectorSupervisor.AttachDirectorCredential(request, registration.Home);

        Assert.Equal(DirectorScopedToken.Mint("the-only-secret-on-this-machine", ScopeNames.Admin), BearerOf(request));

        // Read-only by design: attaching a credential must not have minted a secret file into the
        // shared root - that is exactly the stray that made this defect invisible on upgraded machines.
        Assert.False(File.Exists(Path.Combine(_root, "config", "director", "gateway-token.txt")),
            "resolving the credential wrote a flat-root token file; the client must never create the server's secret");
    }

    /// <summary>
    /// An instance with no readable secret sends NO header - the Director's refusal is then a fact
    /// the caller reports, rather than a credential invented client-side that verifies nothing.
    /// </summary>
    [Fact]
    public void AttachDirectorCredential_NoReadableSecret_SendsNoHeader()
    {
        WriteRegistration(InstanceHome("default"), "c6db060e-0000-0000-0000-000000000012", port: 7879);

        var registration = Assert.Single(DirectorSupervisor.ReadInstanceRegistrations());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:7879/healthz");
        DirectorSupervisor.AttachDirectorCredential(request, registration.Home);

        Assert.Null(request.Headers.Authorization);
    }

    /// <summary>
    /// The pairing that makes the fix hold: each registration carries the home of the instance it
    /// was read from, for both layouts. Port alone was the old contract, and it is what let the
    /// port come from one place and the secret from another.
    /// </summary>
    [Fact]
    public void ReadInstanceRegistrations_CarriesTheHomeEachRegistrationWasReadFrom()
    {
        WriteRegistration(_root, "5edf0787-0000-0000-0000-000000000013", port: 7880);
        WriteRegistration(InstanceHome("work"), "c6db060e-0000-0000-0000-000000000014", port: 7881);

        var registrations = DirectorSupervisor.ReadInstanceRegistrations();

        Assert.Equal(Path.GetFullPath(_root),
            registrations.Single(r => r.Port == 7880).Home);
        Assert.Equal(Path.GetFullPath(InstanceHome("work")),
            registrations.Single(r => r.Port == 7881).Home);
    }
}
