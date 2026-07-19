using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Guards the prerequisite re-classification. These call the REAL production checklist
/// (<see cref="PrerequisiteChecker.CreateChecklist"/>) and the REAL gate
/// (<see cref="PrerequisiteChecker.AllRequiredMet"/>) - the same two the wizard calls - so
/// putting <c>IsRequired = true</c> back on any of the three recommended rows turns these red.
/// </summary>
public class PrerequisiteClassificationTests
{
    [Fact]
    public void CreateChecklist_OnlyDotNetIsRequired()
    {
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation);

        var required = checklist.Where(p => p.IsRequired).Select(p => p.Name).ToList();

        // The Windows Director publishes framework-dependent, so the runtime is the one genuine
        // dependency. Claude Code, Python and Node.js must never gate the wizard again.
        Assert.Equal([".NET 10 Runtime"], required);
    }

    [Theory]
    [InlineData("Claude Code", "Anthropic.ClaudeCode")]
    [InlineData("Python", "Python.Python.3.12")]
    [InlineData("Node.js", "OpenJS.NodeJS.LTS")]
    public void CreateChecklist_RecommendedItemsCanBeInstalledInPlace(string name, string wingetId)
    {
        var item = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation)
            .Single(p => p.Name == name);

        Assert.True(item.CanAutoInstall);
        Assert.Equal(wingetId, item.WingetId);

        // The row's install action is what the user actually clicks; it must be offered while the
        // item is missing and hide itself once found.
        item.IsFound = false;
        Assert.True(item.ShowAutoInstall);
        item.IsFound = true;
        Assert.False(item.ShowAutoInstall);
    }

    [Fact]
    public void CreateChecklist_EveryItemKeepsAManualInstallLink()
    {
        // winget is absent on locked-down machines, so the link is the fallback path and must
        // survive on every row - including the ones that gained an auto-install.
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Gateway);

        Assert.All(checklist, p => Assert.False(string.IsNullOrWhiteSpace(p.InstallUrl)));
    }

    [Fact]
    public void AllRequiredMet_UserWithOnlyTheDotNetRuntime_CanContinue()
    {
        // The point of the change: a brand-new machine where Setup has installed the runtime and
        // nothing else is NOT stopped on screen two.
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation);
        foreach (var item in checklist)
            item.IsFound = item.Name == ".NET 10 Runtime";

        Assert.True(PrerequisiteChecker.AllRequiredMet(checklist));
    }

    [Fact]
    public void AllRequiredMet_MissingDotNetRuntime_StillBlocks()
    {
        // The gate still protects the one real dependency: warn-and-continue alone would walk the
        // user through to a Director that cannot start.
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation);
        foreach (var item in checklist)
            item.IsFound = item.Name != ".NET 10 Runtime";

        Assert.False(PrerequisiteChecker.AllRequiredMet(checklist));
    }

    [Fact]
    public void AllRequiredMet_NothingCheckedYet_IsTreatedAsMet()
    {
        Assert.True(PrerequisiteChecker.AllRequiredMet(new List<PrerequisiteInfo>()));
    }
}
