using System;
using System.IO;
using System.Linq;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// The installer places NO skills on anyone's machine (issue 995). Skills are held centrally on the
/// Gateway and announced to every session in its launch briefing, so an agent fetches a skill's body
/// only when it is about to use one. Nothing is written to disk, which is why the fixed list of three
/// skill names, the downloader that wrote them into the user's own skills folder, and the manifest
/// that existed purely so uninstall could take them away again are all gone.
///
/// These guards cover BOTH installer paths, because they used to ship skills from separate lists:
/// the Windows wizard (tools/cc-director-setup, referenced by this test assembly, so it is checked by
/// reflection) and the macOS wizard (tools/cc-director-setup-avalonia, checked by reading its source -
/// referencing that project would pull Avalonia packages into this test and collide on the shared
/// CcDirectorSetup.Services namespace).
///
/// Revert-proof: reintroducing a skill installer to either wizard - a type named for skills on
/// Windows, or a SkillNames list / InstallSkillsAsync downloader on macOS - reds these.
/// </summary>
public sealed class InstallerNoSkillDeploymentTests
{
    [Fact]
    public void WindowsWizard_HasNoSkillInstallingSurface()
    {
        var wizard = typeof(WizardStepFlow).Assembly;

        Assert.DoesNotContain(wizard.GetTypes(), t => t.Name.Contains("Skill", StringComparison.Ordinal));
        Assert.DoesNotContain(
            wizard.GetTypes().SelectMany(t => t.GetMethods()),
            m => m.Name.Contains("Skill", StringComparison.Ordinal));
    }

    [Fact]
    public void MacWizard_HasNoSkillInstallingSurface()
    {
        var avalonia = Path.Combine(FindRepoRoot(), "tools", "cc-director-setup-avalonia");
        Assert.True(Directory.Exists(avalonia), $"The macOS wizard project is missing at {avalonia}");

        foreach (var file in Directory.EnumerateFiles(avalonia, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("SkillNames", StringComparison.Ordinal),
                $"{file} declares a list of skills to install; the installer ships no skills.");
            Assert.False(source.Contains("InstallSkillsAsync", StringComparison.Ordinal),
                $"{file} installs skill files; skills are fetched from the Gateway, never written here.");
            Assert.False(source.Contains("DownloadSkillFileAsync", StringComparison.Ordinal),
                $"{file} downloads a skill file; skills are fetched from the Gateway, never written here.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "cc-director-setup-avalonia")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo root (tools/cc-director-setup-avalonia) walking up from " + AppContext.BaseDirectory);
    }
}
