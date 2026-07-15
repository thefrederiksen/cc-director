using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CcDirector.Core.Browsers;
using CcDirector.Terminal.Avalonia;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Covers the picker's structure and its choice-building. These run headless, so they assert what
/// the dialog is made of rather than driving a real click: the point is that every option is one
/// flat row and that the remember scope resolves to exactly one unambiguous intent.
/// </summary>
public class BrowserPickerDialogTests
{
    private static BrowserPickerDialog Make(string? repoPath = @"D:\repos\devthrottle")
        => new("https://example.com/page", repoPath);

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (var child in root.GetVisualChildren().OfType<Control>())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    [AvaloniaFact]
    public void Constructor_DoesNotBlockOnBrowserDetection()
    {
        // The window must be constructible without any disk read having happened: detection is
        // deferred to Opened. If this ever regresses to a synchronous detect, the constructor
        // starts touching Local State on the UI thread again.
        var dialog = Make();

        Assert.Null(dialog.Choice);
        Assert.Equal("Choose browser", dialog.Title);
    }

    [AvaloniaFact]
    public void Constructor_WithRepo_OffersBothRememberScopes()
    {
        var dialog = Make(@"D:\repos\devthrottle");
        dialog.Show();

        var radios = Descendants<RadioButton>(dialog).ToList();
        var scopeLabels = radios
            .Select(r => (r.Content as TextBlock)?.Text)
            .Where(t => t is not null)
            .ToList();

        Assert.Contains(scopeLabels, t => t!.Contains("For this repository (devthrottle)"));
        Assert.Contains(scopeLabels, t => t! == "For every repository");
    }

    [AvaloniaFact]
    public void Constructor_WithoutRepo_HidesTheScopeChoice()
    {
        // With no owning repository there is only one place a default can go, so a scope choice
        // would be a choice of one. The checkbox alone then carries the meaning.
        var dialog = Make(repoPath: null);
        dialog.Show();

        var scopeRadio = ScopeRadio(dialog, "For every repository");
        Assert.False(scopeRadio.IsEffectivelyVisible);
        Assert.DoesNotContain(Descendants<RadioButton>(dialog),
            r => ((r.Content as TextBlock)?.Text ?? "").StartsWith("For this repository"));
    }

    [AvaloniaFact]
    public void Constructor_WithRepo_ShowsTheScopeChoice()
    {
        var dialog = Make(@"D:\repos\devthrottle");
        dialog.Show();

        Assert.True(ScopeRadio(dialog, "For every repository").IsEffectivelyVisible);
        Assert.True(ScopeRadio(dialog, "For this repository (devthrottle)").IsEffectivelyVisible);
    }

    private static RadioButton ScopeRadio(BrowserPickerDialog dialog, string label)
        => Descendants<RadioButton>(dialog).Single(r => (r.Content as TextBlock)?.Text == label);

    [AvaloniaFact]
    public void Constructor_RememberUncheckedByDefault()
    {
        var dialog = Make();
        dialog.Show();

        var check = Descendants<CheckBox>(dialog).Single();
        Assert.False(check.IsChecked);
    }

    [AvaloniaTheory]
    [InlineData(BrowserRememberScope.None)]
    [InlineData(BrowserRememberScope.Repository)]
    [InlineData(BrowserRememberScope.Application)]
    public void BrowserChoice_CarriesScopeVerbatim(BrowserRememberScope scope)
    {
        var browser = new BrowserInfo(BrowserKind.Chrome, "Google Chrome", @"C:\chrome.exe", @"C:\UserData");
        var choice = new BrowserChoice(browser, "Profile 1", scope);

        Assert.Equal(scope, choice.Scope);
        Assert.Equal("Profile 1", choice.ProfileFolder);
        Assert.Same(browser, choice.Browser);
    }

    [AvaloniaFact]
    public void BrowserChoice_NullBrowserMeansSystemDefault()
    {
        var choice = new BrowserChoice(null, null, BrowserRememberScope.Application);

        Assert.Null(choice.Browser);
        Assert.Null(choice.ProfileFolder);
    }
}
