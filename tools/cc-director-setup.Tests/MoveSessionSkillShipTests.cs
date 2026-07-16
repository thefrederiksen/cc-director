using System.IO;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Deployment guard: the move-session skill must be in the installer's shipped list so it reaches
/// every machine, and its SKILL.md must exist in the repo (the installer fetches it by name from the
/// default branch). Together these fail if the skill is half-wired - listed but missing, or present
/// but not shipped. Mirrors <see cref="FleetCommsSkillShipTests"/> for the move-session skill.
/// </summary>
public sealed class MoveSessionSkillShipTests
{
    [Fact]
    public void SkillNames_IncludesMoveSession()
    {
        Assert.Contains("move-session", SkillInstaller.SkillNames);
    }

    [Fact]
    public void MoveSession_HasASkillMdInTheRepo()
    {
        var skillsDir = FindRepoDir(Path.Combine(".claude", "skills"));
        var path = Path.Combine(skillsDir, "move-session", "SKILL.md");
        Assert.True(File.Exists(path), $"move-session: SKILL.md is missing at {path}; the installer fetches it by name.");
    }

    private static string FindRepoDir(string relativePath)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate {relativePath} walking up from {System.AppContext.BaseDirectory}");
    }
}
