using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// This installer installs the Director. It says NOTHING to the user about a gateway or a cockpit -
/// those are a separate, do-it-yourself install run from the repository.
///
/// The Windows wizard used to carry a "Gateway &amp; Cockpit" card ("Always-on tray app + fleet
/// dashboard") on its install screen and streamed the gateway phase's log lines over the heading line,
/// so an already-current machine read "Gateway: Launcher tray app installed and running on 7900."
/// where its version belonged. macOS never had either, which is how the same screen came to say two
/// different things about one product.
///
/// These guards cover BOTH wizards, in the style of <see cref="InstallerNoSkillDeploymentTests"/>: the
/// Windows one by reflection (this assembly references it) and the markup of both by reading source,
/// because referencing the Avalonia project here would pull in Avalonia and collide on the shared
/// CcDirectorSetup.Services namespace.
///
/// SCOPE, stated plainly: the markup scan reads user-visible Text/Content literals only. It does not
/// read code-behind, and one deliberate mention survives there - the read-only "Install type" line on
/// the Welcome screen, which names the Gateway when describing what an existing machine ALREADY is,
/// not something this installer offers to do.
///
/// Revert-proof: re-adding a Gateway card to either install screen, or a Gateway status method to the
/// Windows install step, reds these.
/// </summary>
public sealed class InstallerNoGatewaySurfaceTests
{
    private static readonly string[] ForbiddenWords = ["Gateway", "Cockpit"];

    [Fact]
    public void NeitherWizardsMarkupMentionsAGatewayOrCockpit()
    {
        var root = FindRepoRoot();
        var wizards = new[]
        {
            Path.Combine(root, "tools", "cc-director-setup"),
            Path.Combine(root, "tools", "cc-director-setup-avalonia"),
        };

        var checked_ = 0;
        foreach (var wizard in wizards)
        {
            Assert.True(Directory.Exists(wizard), $"A wizard project is missing at {wizard}");

            foreach (var file in MarkupFiles(wizard))
            {
                checked_++;
                foreach (var literal in UserVisibleLiterals(File.ReadAllText(file)))
                {
                    foreach (var word in ForbiddenWords)
                    {
                        Assert.False(
                            literal.Contains(word, StringComparison.OrdinalIgnoreCase),
                            $"{file} shows the user \"{literal}\". This installer installs the Director; "
                            + "the gateway and the cockpit are a separate do-it-yourself install and are "
                            + "never named on these screens.");
                    }
                }
            }
        }

        // A scan that found no files would pass vacuously and prove nothing.
        Assert.True(checked_ >= 8, $"Expected the markup of both wizards, found only {checked_} files.");
    }

    [Fact]
    public void WindowsInstallStep_HasNoGatewayCardSurface()
    {
        var members = typeof(InstallStep).GetMembers()
            .Select(m => m.Name)
            .Where(n => n.Contains("Gateway", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(members);
    }

    // The wizard is three steps: Welcome, Install, Complete. The Prerequisites step is gone - the
    // Windows executables now carry their own .NET, so nothing this installer places needs anything
    // already on the machine, and there is nothing left for that screen to gate on.
    [Fact]
    public void WizardIsThreeSteps()
    {
        Assert.Equal([1, 7, 8], WizardStepFlow.VisibleSteps());
    }

    private static IEnumerable<string> MarkupFiles(string project) =>
        Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>The Text= and Content= values a person actually reads on screen. Attribute values only,
    /// so an explanatory comment in the markup is not mistaken for something the user sees.</summary>
    private static IEnumerable<string> UserVisibleLiterals(string markup) =>
        Regex.Matches(markup, "(?:Text|Content)=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.StartsWith('{'));   // a binding, not a literal

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
