using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Parity guard: the two installer paths ship skills from separate, hand-maintained lists -
/// <c>SkillInstaller.SkillNames</c> (tools/cc-director-setup, referenced by this test assembly) and
/// <c>ToolInstaller.SkillNames</c> (tools/cc-director-setup-avalonia). They MUST carry the same set of
/// skills, or an install through one path silently ships something the other does not. They have
/// drifted before - the Avalonia list dropped fleet-comms until it was re-synced. This test reads the
/// Avalonia list from source (referencing that UI project's assembly would pull Avalonia packages into
/// the test and collide on the shared CcDirectorSetup.Services namespace) and set-compares the two.
/// </summary>
public sealed class InstallerSkillListParityTests
{
    [Fact]
    public void SkillInstaller_And_ToolInstaller_ShipTheSameSkills()
    {
        var skillInstaller = SkillInstaller.SkillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toolInstaller = ParseAvaloniaSkillNames();

        var onlyInSkillInstaller = skillInstaller.Except(toolInstaller, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyInToolInstaller = toolInstaller.Except(skillInstaller, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(
            onlyInSkillInstaller.Count == 0 && onlyInToolInstaller.Count == 0,
            "Installer skill lists have drifted - keep SkillInstaller.SkillNames and ToolInstaller.SkillNames in sync. " +
            $"In SkillInstaller only: [{string.Join(", ", onlyInSkillInstaller)}]. " +
            $"In ToolInstaller only: [{string.Join(", ", onlyInToolInstaller)}].");
    }

    private static HashSet<string> ParseAvaloniaSkillNames()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "tools", "cc-director-setup-avalonia", "Services", "ToolInstaller.cs");
        Assert.True(File.Exists(path), $"ToolInstaller.cs not found at {path}");

        // Isolate the `public static readonly string[] SkillNames = [ ... ];` initializer, strip any
        // // line comments inside it (so a quoted word in a comment cannot masquerade as a skill entry),
        // then pull out the quoted skill names.
        var source = File.ReadAllText(path);
        var block = Regex.Match(source, @"SkillNames\s*=\s*\[(?<body>.*?)\]\s*;", RegexOptions.Singleline);
        Assert.True(block.Success, $"Could not find the SkillNames array in {path}");

        var body = Regex.Replace(block.Groups["body"].Value, @"//[^\n]*", "");
        return Regex.Matches(body, "\"(?<name>[^\"]+)\"")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "cc-director-setup-avalonia")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repo root (tools/cc-director-setup-avalonia) walking up from " + System.AppContext.BaseDirectory);
    }
}
