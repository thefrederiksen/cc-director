using CcDirector.ControlApi;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression guard for issue #322: the whole test assembly must resolve the Director
/// instance-discovery directory to a throwaway per-process temp directory, so no test - even one that
/// spins a ControlApiHost / InstanceRegistration without passing its own isolated instancesDirectory -
/// can write an instance file into the REAL %LOCALAPPDATA%\cc-director\config\director\instances\
/// directory and surface a phantom "unreachable" Director in the live Cockpit. The pin is applied by
/// <see cref="TestEnvironment"/>'s module initializer via the CC_DIRECTOR_INSTANCES_DIR override that
/// <c>CcStorage.DirectorInstances</c> honors.
///
/// The assertions read the path-caching statics (resolved once, at process start, under the temp
/// directory). Nothing swaps CC_DIRECTOR_INSTANCES_DIR per-test, so the pinned value is stable for the
/// whole run.
/// </summary>
public sealed class InstancesDirectoryIsolationTests
{
    private static string RealInstancesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "config", "director", "instances");

    [Fact]
    public void PinnedInstancesDir_IsUnderTemp_AndNotTheRealDirectory()
    {
        Assert.StartsWith(Path.GetTempPath(), TestEnvironment.InstancesDir, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            Path.GetFullPath(RealInstancesDirectory),
            Path.GetFullPath(TestEnvironment.InstancesDir),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectorRegistry_InstancesDirectory_ResolvesToTheTestDirectory()
    {
        Assert.Equal(
            Path.GetFullPath(TestEnvironment.InstancesDir),
            Path.GetFullPath(DirectorRegistry.InstancesDirectory),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstanceRegistration_InstancesDirectory_ResolvesToTheTestDirectory()
    {
        Assert.Equal(
            Path.GetFullPath(TestEnvironment.InstancesDir),
            Path.GetFullPath(InstanceRegistration.InstancesDirectory),
            StringComparer.OrdinalIgnoreCase);
    }
}
