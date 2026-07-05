using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #957: the Cockpit left menu must stay the same on every page. The menu is defined
/// twice - once in the Blazor <c>NavMenu.razor</c> (rendered on Blazor pages) and once in
/// <c>tool-nav.js</c> (rendered on the plain-HTML tool pages such as Settings). If the two
/// drift, navigating from a Blazor page to Settings visibly changes the menu. These two lists
/// had drifted (tool-nav.js was missing Learning, Account, and Telemetry). This test pins them
/// together: the ordered set of VISIBLE menu entries (and the separator) must be identical in
/// both files, so the menu can never silently change between pages again.
/// </summary>
public class NavMenuParityTests
{
    // "SEP" marks the divider; other tokens are "href|Label".
    private const string Separator = "SEP";

    [Fact]
    public void BlazorNavMenu_And_ToolNavJs_ExposeTheSameVisibleMenu()
    {
        var blazor = ParseBlazorNav(ReadSource("Components/Layout/NavMenu.razor"));
        var toolNav = ParseToolNav(ReadSource("wwwroot/pages/tool-nav.js"));

        // Guard against a broken parser silently returning empty lists (which would make the
        // equality check pass for the wrong reason): both must contain the known anchors.
        Assert.Contains("/|Home", blazor);
        Assert.Contains("/settings|Settings", blazor);
        Assert.Contains("/telemetry|Telemetry", blazor);
        Assert.Contains(Separator, blazor);
        Assert.True(blazor.Count >= 8, $"expected a full menu, parsed only {blazor.Count} items");

        Assert.Equal(blazor, toolNav);
    }

    private static string ReadSource(string relativeToCockpitProject, [CallerFilePath] string thisFile = "")
    {
        // thisFile: ...\src\CcDirector.Cockpit.Tests\NavMenuParityTests.cs
        var testsDir = Path.GetDirectoryName(thisFile)!;
        var srcDir = Path.GetDirectoryName(testsDir)!;
        var path = Path.Combine(srcDir, "CcDirector.Cockpit", relativeToCockpitProject.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"expected source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Ordered visible entries from NavMenu.razor: every &lt;NavLink&gt;/&lt;a&gt; with class
    /// "nv-item" that is NOT hidden with style="display:none", plus the nv-sep divider.
    /// </summary>
    private static List<string> ParseBlazorNav(string razor)
    {
        var tokens = new List<string>();
        // Match, in document order, either a nav item element (up to its nv-label text) or a separator.
        var rx = new Regex(
            "<(?:NavLink|a)\\b(?<attrs>[^>]*?)class=\"nv-item\"(?<rest>.*?)<span class=\"nv-label\">(?<label>[^<]*)</span>"
            + "|<div class=\"nv-sep\">",
            RegexOptions.Singleline);

        foreach (Match m in rx.Matches(razor))
        {
            if (!m.Groups["label"].Success)
            {
                tokens.Add(Separator);
                continue;
            }

            var openingTag = m.Groups["attrs"].Value + m.Groups["rest"].Value;
            if (openingTag.Contains("display:none"))
                continue; // hidden item - not part of the visible menu

            var href = Regex.Match(openingTag, "href=\"(?<h>[^\"]*)\"").Groups["h"].Value;
            var label = m.Groups["label"].Value.Trim();
            tokens.Add($"{href}|{label}");
        }

        return tokens;
    }

    /// <summary>
    /// Ordered visible entries from tool-nav.js: every ITEMS entry that is NOT marked
    /// alpha:true, plus the { sep:true } divider.
    /// </summary>
    private static List<string> ParseToolNav(string js)
    {
        var start = js.IndexOf("var ITEMS", StringComparison.Ordinal);
        var arrOpen = js.IndexOf('[', start);
        var arrClose = js.IndexOf("];", arrOpen, StringComparison.Ordinal);
        var body = js.Substring(arrOpen + 1, arrClose - arrOpen - 1);

        var tokens = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//"))
                continue;

            if (line.Contains("sep: true") || line.Contains("sep:true"))
            {
                tokens.Add(Separator);
                continue;
            }

            var hrefMatch = Regex.Match(line, "href:\\s*\"(?<h>[^\"]*)\"");
            if (!hrefMatch.Success)
                continue;
            if (line.Contains("alpha: true") || line.Contains("alpha:true"))
                continue; // hidden item - not part of the visible menu

            var label = Regex.Match(line, "label:\\s*\"(?<l>[^\"]*)\"").Groups["l"].Value.Trim();
            tokens.Add($"{hrefMatch.Groups["h"].Value}|{label}");
        }

        return tokens;
    }
}
