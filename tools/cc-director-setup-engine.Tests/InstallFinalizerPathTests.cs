using System;
using System.IO;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The user PATH is permanent machine state, and this is the component that writes to it. The write
/// itself is not exercised here - a test that edited the developer's real PATH would be a worse bug
/// than the one being fixed - so these pin the decision that comes before the write.
/// </summary>
public class InstallFinalizerPathTests
{
    [Fact]
    public void IsUnderTemp_AnInstallInTheTempDirectory_IsRecognised()
    {
        // FOUND BY DOING IT. Standing up a test Director whose root was under the temp directory put
        // that throwaway bin into the real user PATH, where it outlives the directory: the same
        // machine was already carrying ...\Temp\wizard-harness-home-29ef...\cc-director\bin from an
        // earlier harness, pointing at a directory that no longer exists.
        var rig = Path.Combine(Path.GetTempPath(), "some-harness-root", "instances", "default", "bin");

        Assert.True(InstallFinalizer.IsUnderTemp(rig));
    }

    [Fact]
    public void IsUnderTemp_AnOrdinaryInstall_IsNot()
    {
        // The guard must not fire on the real thing, or an ordinary install stops putting its tools on
        // PATH at all - a far worse failure than the leak it prevents.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "instances", "default", "bin");

        Assert.False(InstallFinalizer.IsUnderTemp(real));
    }

    [Fact]
    public void IsUnderTemp_ADirectoryMerelyNamedLikeTemp_IsNot()
    {
        // Compared on full paths, not by looking for the word. A project directory called "temp" is
        // somebody's real work, not scratch space.
        Assert.False(InstallFinalizer.IsUnderTemp(Path.Combine("C:", "work", "temp", "bin")));
    }

    [Fact]
    public void IsUnderTemp_NothingToJudge_IsNotTreatedAsTemp()
    {
        // A path that cannot be resolved cannot support the claim, and refusing to add it on a guess
        // would break an ordinary install. Absence of an answer is not a yes.
        Assert.False(InstallFinalizer.IsUnderTemp(""));
        Assert.False(InstallFinalizer.IsUnderTemp("   "));
    }
}
