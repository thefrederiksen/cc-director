using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Pins the RULE that decides what is test code, not the file list that happens to satisfy it today.
///
/// This distinction is the whole point of the file. A fact asserting "these twelve files are fine"
/// passes forever while the rule underneath it stays broken - and that is exactly what happened on
/// 2 August: four architecture guards shared a <c>.Contains(".Tests/")</c> decision, a suite split
/// moved 2,750 test files into <c>CcDirector.Gateway.UnitTests</c>, and every one of them became
/// "production" at a stroke. Two guards went red naming files nobody had touched; two stayed green
/// only because none of those files happened to match their patterns.
///
/// So these facts name SPELLINGS rather than paths, and the first of them is the one that was broken:
/// a project called <c>.UnitTests</c> is a test project. Restore the old substring in
/// <see cref="TestProjectPath.IsTestProject"/> and that fact goes red, which is the property that
/// makes this a guard rather than a decoration.
/// </summary>
public class TestProjectPathTests
{
    [Theory]
    // The spelling that broke: ".UnitTests/" does not contain ".Tests/", and this is what regressed.
    [InlineData("src/CcDirector.Gateway.UnitTests/RosterFoldTests.cs")]
    // The spellings that already worked, kept so a fix cannot narrow the rule while widening it.
    [InlineData("src/CcDirector.Gateway.Tests/HostedStatsServeTests.cs")]
    [InlineData("src/CcDirector.Core.Tests/StorageRootGuardTests.cs")]
    // Nested below the project directory - the decision is the PROJECT, not the immediate folder.
    [InlineData("src/CcDirector.Core.Tests/AgentPlugins/AgentPluginArchitectureGuardTests.cs")]
    // Any future suffix, so nobody has to come back here when the next suite is split out.
    [InlineData("src/CcDirector.Gateway.IntegrationTests/Whatever.cs")]
    [InlineData("tools/SomeTool.ContractTests/Whatever.cs")]
    public void ADirectoryWhoseNameEndsInTests_IsATestProject(string relativePath)
        => Assert.True(TestProjectPath.IsTestProject(relativePath), relativePath);

    [Theory]
    // Ordinary production projects, which is the half that must NOT widen: a guard that calls
    // everything a test stops guarding anything, and would pass this file's other half silently.
    [InlineData("src/CcDirector.Gateway/Api/GatewayEndpoints.cs")]
    [InlineData("src/CcDirector.Core/Sessions/SessionManager.cs")]
    [InlineData("src/CcDirector.ControlApi/ControlEndpoints.cs")]
    // A production FILE whose own name mentions tests. The file name is never consulted.
    [InlineData("src/CcDirector.Core/Diagnostics/SelfTests.cs")]
    // A directory ending in "Tests" NESTED INSIDE a production project. The first version of the
    // predicate matched any ancestor and called these test code - which would have made production
    // files invisible to all four guards while every one of them stayed green. A widening that
    // SILENCES a guard is worse than the narrowing it was written to fix.
    [InlineData("src/CcDirector.Core/DiagnosticsTests/Probe.cs")]
    [InlineData("src/CcDirector.Core/Tests/Probe.cs")]
    [InlineData("src/CcDirector.Gateway/Api/IntegrationTests/Helper.cs")]
    // An ABSOLUTE path whose ancestors OUTSIDE the repository end in "Tests". Anchoring on the source
    // root is what stops a checkout location silently disabling a guard.
    [InlineData("D:/SomeTests/checkout/src/CcDirector.Core/Sessions/SessionManager.cs")]
    public void AProductionProject_IsNotATestProject(string relativePath)
        => Assert.False(TestProjectPath.IsTestProject(relativePath), relativePath);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Program.cs")]
    public void AnEmptyOrBareName_IsNotATestProject(string relativePath)
        => Assert.False(TestProjectPath.IsTestProject(relativePath), $"'{relativePath}'");
}
