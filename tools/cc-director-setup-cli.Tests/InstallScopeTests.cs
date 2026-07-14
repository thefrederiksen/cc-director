using CcDirector.Setup.Cli;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

public class InstallScopeTests
{
    // The headline regression guard: a Gateway install is a true superset of a Workstation install,
    // so it MUST also install the per-user Python tools bundle. (A stale workstation-only gate once
    // dropped the cc-* tools on a Gateway install; this locks that it never returns.)
    [Fact]
    public void InstallsPythonTools_GatewayInstall_True()
    {
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: false));
    }

    [Fact]
    public void InstallsPythonTools_WorkstationInstall_True()
    {
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false));
    }

    [Theory]
    [InlineData(InstallRole.Gateway)]
    [InlineData(InstallRole.Workstation)]
    public void InstallsPythonTools_BothRolesAgree(InstallRole role)
    {
        // Role-independent by design: whatever Workstation does, Gateway does too.
        var gateway = InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: false);
        var workstation = InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false);
        Assert.Equal(workstation, gateway);
        // The parameterized role still resolves to the same decision.
        Assert.True(InstallScope.InstallsPythonTools(role, installMode: true, dryRun: false));
    }

    [Fact]
    public void InstallsPythonTools_DryRun_False()
    {
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: true));
    }

    [Fact]
    public void InstallsPythonTools_UpdateMode_False()
    {
        // A plain `update` pass (installMode: false) refreshes apps/tools already present; it does not
        // run the full bundle install step.
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: false, dryRun: false));
    }

    [Fact]
    public void InstallsPythonTools_PlatformIndependent_TrueOnInstall()
    {
        // The decision is platform-independent: PythonToolsInstaller selects the right bundle
        // assets per platform itself, and the release ships macOS bundles. The old Windows-only
        // gate silently skipped the cc-* tools on a macOS install (issue #1445).
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false));
    }
}
