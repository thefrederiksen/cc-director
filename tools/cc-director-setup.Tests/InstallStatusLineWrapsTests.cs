using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// The install screen's status line must WRAP, on both wizards.
///
/// This is the second half of issue #1152. When the launcher step failed, the screen said:
///
///     ERROR: Launcher tray app failed to start. Launcher tray app started but did not answer on
///     7900. Check C:
///
/// and stopped there - clipped at the window's right edge, exactly where the log path began. The log
/// path is the only actionable part of that sentence, so the message told the user to check a file
/// without saying which one. A failure message whose one instruction is truncated is worse than no
/// instruction: it looks like the product tried to help and could not finish the sentence.
///
/// The cause was layout, not text. The status TextBlock sat in a horizontal StackPanel, which measures
/// its children with infinite width, so wrapping can never happen and anything too long is simply cut
/// off at the window edge. Both wizards had the same markup, so both are checked here.
///
/// Revert-proof: put StatusText back in a horizontal StackPanel, or drop its TextWrapping, and this
/// goes red.
/// </summary>
public sealed class InstallStatusLineWrapsTests
{
    [Fact]
    public void BothWizards_StatusLineWraps()
    {
        var root = FindRepoRoot();
        var checkedFiles = 0;

        foreach (var markup in InstallStepMarkup(root))
        {
            checkedFiles++;
            var text = File.ReadAllText(markup);
            var element = ElementDeclaring(text, "StatusText");

            Assert.True(element is not null, $"{markup} has no StatusText element to check.");
            Assert.Contains("TextWrapping=\"Wrap\"", element!, StringComparison.Ordinal);
        }

        Assert.Equal(2, checkedFiles);
    }

    /// <summary>
    /// The layout, not just the attribute. TextWrapping is ignored inside a horizontal StackPanel -
    /// children are measured with infinite width there - so the wrapping attribute alone would be a
    /// guard that passes while the text still clips.
    /// </summary>
    [Fact]
    public void BothWizards_StatusLineIsNotInsideAHorizontalStackPanel()
    {
        var root = FindRepoRoot();

        foreach (var markup in InstallStepMarkup(root))
        {
            var text = File.ReadAllText(markup);
            var before = text[..text.IndexOf("x:Name=\"StatusText\"", StringComparison.Ordinal)];
            var openPanels = Regex.Matches(before, "<(StackPanel|Grid)\\b[^>]*>", RegexOptions.Singleline)
                .Select(m => m.Value)
                .Where(v => !v.EndsWith("/>", StringComparison.Ordinal))
                .ToList();
            var closedPanels = Regex.Matches(before, "</(StackPanel|Grid)>").Count;
            var enclosing = openPanels.Skip(closedPanels).LastOrDefault();

            Assert.True(enclosing is not null, $"{markup}: could not find the panel holding StatusText.");
            Assert.DoesNotContain("Orientation=\"Horizontal\"", enclosing!, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> InstallStepMarkup(string root)
    {
        yield return Path.Combine(root, "tools", "cc-director-setup", "Steps", "InstallStep.xaml");
        yield return Path.Combine(root, "tools", "cc-director-setup-avalonia", "Steps", "InstallStep.axaml");
    }

    /// <summary>The whole opening tag of the element that declares <paramref name="name"/>.</summary>
    private static string? ElementDeclaring(string markup, string name)
    {
        var at = markup.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = markup.LastIndexOf('<', at);
        var close = markup.IndexOf('>', at);
        return open < 0 || close < 0 ? null : markup[open..close];
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
