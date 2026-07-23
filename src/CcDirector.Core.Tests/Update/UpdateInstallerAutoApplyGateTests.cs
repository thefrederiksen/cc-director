using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// The zero-sessions gate for auto-applying a staged update (issue #2047). The Director
/// restarts itself into a staged build with no human step, but ONLY when no sessions are
/// running, so a running session is never interrupted. These cover the pure decision; the
/// relaunch/shutdown side effects live in App.axaml.cs.
/// </summary>
public class UpdateInstallerAutoApplyGateTests
{
    [Fact]
    public void ShouldAutoApply_StagedAndNoSessions_True()
    {
        Assert.True(UpdateInstaller.ShouldAutoApplyWhenIdle(hasStagedUpdate: true, runningSessionCount: 0));
    }

    [Fact]
    public void ShouldAutoApply_StagedButSessionsRunning_False()
    {
        Assert.False(UpdateInstaller.ShouldAutoApplyWhenIdle(hasStagedUpdate: true, runningSessionCount: 1));
    }

    [Fact]
    public void ShouldAutoApply_ManySessionsRunning_False()
    {
        Assert.False(UpdateInstaller.ShouldAutoApplyWhenIdle(hasStagedUpdate: true, runningSessionCount: 7));
    }

    [Fact]
    public void ShouldAutoApply_NothingStaged_False()
    {
        // No staged update: nothing to apply even when the machine is completely idle.
        Assert.False(UpdateInstaller.ShouldAutoApplyWhenIdle(hasStagedUpdate: false, runningSessionCount: 0));
    }

    [Fact]
    public void ShouldAutoApply_NothingStagedAndSessionsRunning_False()
    {
        Assert.False(UpdateInstaller.ShouldAutoApplyWhenIdle(hasStagedUpdate: false, runningSessionCount: 3));
    }
}
