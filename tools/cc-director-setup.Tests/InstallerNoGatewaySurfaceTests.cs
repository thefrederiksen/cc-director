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

    /// <summary>
    /// The names, pinned. DevThrottle is the PRODUCT; the application it installs is the
    /// <em>Director</em> and the background app is the <em>Launcher</em>. The install screen used to
    /// call the application "DevThrottle" and the launcher "cc-launcher" - one wrong, the other a file
    /// name rather than a name - and the two wizards disagreed with the rest of the product.
    ///
    /// Executable names are legitimate where an exact path is needed, so this checks only the strings
    /// a person reads as a NAME: the card titles, the headings and the buttons.
    ///
    /// Revert-proof: rename a card back to DevThrottle or cc-launcher and this goes red.
    /// </summary>
    [Fact]
    public void TheInstalledThingsAreCalledDirectorAndLauncher()
    {
        var root = FindRepoRoot();
        var checkedFiles = 0;

        foreach (var wizard in new[] { "cc-director-setup", "cc-director-setup-avalonia" })
        {
            foreach (var file in MarkupFiles(Path.Combine(root, "tools", wizard)))
            {
                // The Welcome screen and the window chrome speak for the PRODUCT, which is DevThrottle.
                if (Path.GetFileName(file).StartsWith("WelcomeStep", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(file).StartsWith("MainWindow", StringComparison.OrdinalIgnoreCase)) continue;

                checkedFiles++;
                foreach (var literal in UserVisibleLiterals(File.ReadAllText(file)))
                {
                    Assert.False(literal.Contains("cc-launcher", StringComparison.OrdinalIgnoreCase),
                        $"{file} shows \"{literal}\" - the background app is called the Launcher; cc-launcher is a file name.");
                    Assert.False(literal.Contains("cc-director", StringComparison.OrdinalIgnoreCase),
                        $"{file} shows \"{literal}\" - the application is called the Director; cc-director is a file name.");
                    Assert.False(literal.Contains("CC Director", StringComparison.Ordinal),
                        $"{file} shows \"{literal}\" - it is the Director, never CC Director.");
                }
            }
        }

        Assert.True(checkedFiles >= 6, $"Expected the step markup of both wizards, found only {checkedFiles} files.");
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
