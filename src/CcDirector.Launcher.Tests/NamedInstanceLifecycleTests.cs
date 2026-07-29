using CcDirector.Core.Instances;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Creating and deleting a named Director instance, which is the half that makes the lifecycle verbs worth
/// having.
///
/// TWO THINGS THIS PINS, BOTH OF WHICH WERE MISSING.
///
/// First, a created instance must be USABLE. Letting the Director build a bare home for itself produces an
/// instance that is unregistered, has no port, and is connected to no gateway - it can be started and
/// stopped and then never actually talked to. Creation goes through the registry instead, which assigns a
/// port, records it where the launcher and every other instance can see it, and scaffolds its configuration
/// with the gateway THIS MACHINE uses. The instance inherits the gateway that created it, because that is
/// the only one we can know is right.
///
/// Second, an instance must be REMOVABLE. Create without delete leaves every throwaway instance on disk
/// forever, which is the wrong half to be missing for a feature whose main use is throwaway instances.
/// </summary>
[Collection(DirectorRootCollection.Name)]
public sealed class NamedInstanceLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly string? _previousInstancesDir;

    private const string GatewayUrl = "https://gateway.example.test";
    private const string GatewayToken = "machine-gateway-token";

    public NamedInstanceLifecycleTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _previousInstancesDir = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", null);

        _root = Path.Combine(Path.GetTempPath(), "cc-instance-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // SharedRoot is captured once at static initialisation, which happened before this test set the
        // root. Initialize re-reads it from CcStorage, which honours CC_DIRECTOR_ROOT - so this points the
        // cross-instance registry at the temporary root rather than the real machine's.
        InstanceContext.Initialize(null, wasExplicit: false);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", _previousInstancesDir);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static DirectorSupervisor SupervisorWithGateway(string url = GatewayUrl) =>
        new(InstallLayout.Default(), () => (url, GatewayToken));

    // =====================================================================================================
    // A created instance is usable
    // =====================================================================================================

    /// <summary>
    /// The point of creating through the registry: the instance gets a port, an identity, and the machine's
    /// gateway written into its own configuration. Without this it would come up connected to nothing.
    /// </summary>
    [Fact]
    public void Create_GivesTheInstanceAPortAndTheCreatingGateway()
    {
        var created = NamedInstanceRegistry.Create("spare", GatewayUrl, GatewayToken);

        Assert.Equal("spare", created.Name);
        Assert.InRange(created.Port, 7880, 7898);
        Assert.Equal(GatewayUrl, created.GatewayUrl);

        var configPath = Path.Combine(NamedInstanceRegistry.HomeFor("spare"), "config", "config.json");
        Assert.True(File.Exists(configPath), "the instance home must be scaffolded with its own configuration");
        var config = File.ReadAllText(configPath);
        Assert.Contains(GatewayUrl, config);
        Assert.Contains(GatewayToken, config);
    }

    [Fact]
    public void Create_ThenGet_FindsItByName()
    {
        NamedInstanceRegistry.Create("spare", GatewayUrl, GatewayToken);

        Assert.NotNull(NamedInstanceRegistry.Get("spare"));
    }

    // =====================================================================================================
    // Deleting
    // =====================================================================================================

    [Fact]
    public void Delete_RemovesTheRegistrationAndTheDataHome()
    {
        NamedInstanceRegistry.Create("spare", GatewayUrl, GatewayToken);
        var home = NamedInstanceRegistry.HomeFor("spare");
        Assert.True(Directory.Exists(home));

        var removed = NamedInstanceRegistry.Delete("spare");

        Assert.True(removed);
        Assert.Null(NamedInstanceRegistry.Get("spare"));
        Assert.False(Directory.Exists(home), "the data home must go with the registration");
    }

    /// <summary>
    /// The default is the machine's real Director, with its actual sessions and settings. Deleting it would
    /// take those with it, and it is re-created the moment anything asks for the list - so the request is
    /// refused rather than quietly ignored, which would leave a caller believing it had worked.
    /// </summary>
    [Fact]
    public void Delete_TheDefaultInstance_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => NamedInstanceRegistry.Delete(InstanceContext.DefaultSlug));
    }

    [Fact]
    public void Delete_UnknownInstance_ReportsThatNothingWasRemoved()
    {
        Assert.False(NamedInstanceRegistry.Delete("never-existed"));
    }

    [Fact]
    public void Delete_LeavesOtherInstancesAlone()
    {
        NamedInstanceRegistry.Create("spare", GatewayUrl, GatewayToken);
        NamedInstanceRegistry.Create("keeper", GatewayUrl, GatewayToken);

        NamedInstanceRegistry.Delete("spare");

        Assert.Null(NamedInstanceRegistry.Get("spare"));
        Assert.NotNull(NamedInstanceRegistry.Get("keeper"));
        Assert.True(Directory.Exists(NamedInstanceRegistry.HomeFor("keeper")));
    }

    // =====================================================================================================
    // Through the supervisor, which is what the launcher and the Gateway relay actually call
    // =====================================================================================================

    [Fact]
    public async Task DeleteAsync_RemovesARegisteredInstance()
    {
        NamedInstanceRegistry.Create("spare", GatewayUrl, GatewayToken);

        var removed = await SupervisorWithGateway().DeleteAsync("spare");

        Assert.True(removed);
        Assert.Null(NamedInstanceRegistry.Get("spare"));
    }

    [Fact]
    public async Task DeleteAsync_TheDefault_IsRefusedBeforeAnythingIsStopped()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SupervisorWithGateway().DeleteAsync(InstanceContext.DefaultSlug));
    }

    [Fact]
    public async Task DeleteAsync_UnknownInstance_ReportsFalseRatherThanFailing()
    {
        Assert.False(await SupervisorWithGateway().DeleteAsync("never-existed"));
    }

    /// <summary>
    /// A machine with no gateway cannot produce a usable instance, so starting one there is refused with the
    /// reason rather than half-made. Creating it anyway would produce exactly the unreachable instance that
    /// registry-backed creation exists to prevent.
    /// </summary>
    [Fact]
    public void Start_NamedInstance_WithNoMachineGateway_IsRefusedWithTheReason()
    {
        var supervisor = new DirectorSupervisor(InstallLayout.Default(), () => ("", ""));

        // The installed Director is checked before anything else - correctly, since nothing can start
        // without the binary. This test is about the gateway refusal that comes after it, so a stand-in is
        // placed at the expected path; the test root redirect means there is no real installation here.
        PlaceStandInDirectorBinary(supervisor.DirectorExePath);

        var error = Record.Exception(() => supervisor.Start("spare"));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("no gateway configured", error!.Message);

        // And nothing half-made was left behind: the instance must not be registered when its creation was
        // refused, or the next start would find a registration with no usable gateway in it.
        Assert.Null(NamedInstanceRegistry.Get("spare"));
    }

    /// <summary>A file on Windows, an application bundle directory on macOS - whichever this platform's
    /// installed-Director check looks for.</summary>
    private static void PlaceStandInDirectorBinary(string path)
    {
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}
