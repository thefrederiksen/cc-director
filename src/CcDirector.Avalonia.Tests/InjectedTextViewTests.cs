using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CcDirector.Avalonia.Tests.InjectedTextViewTestApp))]

namespace CcDirector.Avalonia.Tests;

/// <summary>A bare Avalonia application so controls can be built with no window.</summary>
public class InjectedTextViewTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<InjectedTextViewTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// The Injected text tab, rendered for real with no window.
///
/// XAML is parsed at RUNTIME, so a build that succeeds says nothing about whether this view opens: a
/// mistyped name or a handler that does not exist is a crash the moment the user clicks the tab. These
/// tests exist so that lands here instead.
///
/// The banner assertions are the ones that matter. The owner's requirement is that a user running
/// their own text must never be able to believe they are on ours, so "whose text is live" being wrong
/// is not a cosmetic bug - it is the feature failing.
/// </summary>
public class InjectedTextViewTests
{
    [AvaloniaFact]
    public async Task TheTabOpens()
    {
        var view = await ShownAsync();

        // Reaching here means the XAML parsed, every x:Name resolved, and every Click handler named in
        // the markup exists on the class.
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public async Task TheTabNamesTheMechanismAndDoesNotClaimWeTypeIntoTheTerminal()
    {
        var view = await ShownAsync();

        var text = AllTextOf(view);

        // Users assume the worse thing. The tab has to say plainly that this is a startup extension
        // point and not keystrokes, because that is the distinction they cannot check for themselves.
        Assert.Contains("not typed into your terminal", text);
    }

    [AvaloniaFact]
    public async Task TheBannerStatesWhoseTextIsLive_AndTheTwoStatesCannotBeConfused()
    {
        var view = await ShownAsync();

        var title = view.GetControl<TextBlock>("SourceTitle");
        var banner = view.GetControl<Border>("SourceBanner");

        // Whichever way this machine is configured, the banner must commit to one of the two answers
        // in words, and it must not be blank or ambiguous.
        Assert.False(string.IsNullOrWhiteSpace(title.Text));
        Assert.True(
            title.Text!.Contains("YOUR text", StringComparison.Ordinal) ||
            title.Text.Contains("the DevThrottle text", StringComparison.Ordinal),
            $"The banner must say whose text is live, but said: '{title.Text}'");

        // The two states must not look alike: the colour carries the meaning for someone who glances.
        Assert.NotNull(banner.Background);
    }

    [AvaloniaFact]
    public async Task ThePlaceholdersAreListedWhereTheyAreTyped()
    {
        var view = await ShownAsync();

        var hint = view.GetControl<TextBlock>("PlaceholderHint").Text ?? "";

        // Documentation nobody opens is not a control. Every placeholder is named on the screen where
        // the user writes them.
        Assert.Contains("[SESSION_ID]", hint);
        Assert.Contains("[USER_EMAIL]", hint);
        Assert.Contains("[IF_SIGNED_IN]", hint);
    }

    // OURS is live: the banner says so, and the editor is not editable, because it is not their text.
    [AvaloniaFact]
    public async Task WhenOurTextIsLive_TheBannerSaysSo()
    {
        var dir = NewDir();
        try
        {
            var view = await ShownAsync(InjectedTextStore.AlwaysOurs(dir));

            Assert.Contains("the DevThrottle text", view.GetControl<TextBlock>("SourceTitle").Text!);
            Assert.Contains("this is what your agents get", view.GetControl<TextBlock>("EditorLabel").Text!);
            Assert.Contains("NEVER SIGN IT", view.GetControl<TextBox>("EditorBox").Text!);
        }
        finally { Cleanup(dir); }
    }

    // THEIRS is live: the banner must commit to that, and their words - not ours - must be on screen.
    [AvaloniaFact]
    public async Task WhenTheirTextIsLive_TheBannerSaysItIsTheirs_AndOurTextIsNotShownAsLive()
    {
        var dir = NewDir();
        try
        {
            var store = TheirsStore(dir);
            store.SaveYours("only my words. you are [SESSION_SHORT_ID].");

            var view = await ShownAsync(store);

            Assert.Contains("YOUR text", view.GetControl<TextBlock>("SourceTitle").Text!);
            var editor = view.GetControl<TextBox>("EditorBox").Text!;
            Assert.Equal("only my words. you are [SESSION_SHORT_ID].", editor);
            // Our policy text must not be sitting in the live editor claiming to be theirs.
            Assert.DoesNotContain("NEVER SIGN IT", editor);
        }
        finally { Cleanup(dir); }
    }

    // THE STATE THAT MATTERS MOST, and the one a config-dependent test could never reach. Their text is
    // chosen but cannot be DELIVERED, so agents are getting nothing. The tab must say exactly that
    // rather than labelling the editor "what your agents get".
    //
    // Two ways to get here, and they must look identical to the user because they are identical to the
    // agent: the file is gone, or the file is there but was hand-edited into something unrenderable.
    // The second is the one that slipped through: the file reads fine, so nothing failed until launch.
    [AvaloniaTheory]
    [InlineData(null)]                       // the file is gone
    [InlineData("[IF_SIGNED_IN]\nhello")]    // hand-edited into a template that cannot render
    public async Task WhenTheirTextCannotBeDelivered_TheTabSaysAgentsAreGettingNothing(string? onDisk)
    {
        var dir = NewDir();
        try
        {
            var store = TheirsStore(dir);
            if (onDisk is null)
            {
                store.SaveYours("my own text");
                File.Delete(store.YoursPath);
            }
            else
            {
                // Bypass SaveYours, which validates - this is a file edited behind the product's back.
                Directory.CreateDirectory(dir);
                File.WriteAllText(store.YoursPath, onDisk);
            }

            var view = await ShownAsync(store);

            var title = view.GetControl<TextBlock>("SourceTitle").Text!;
            var label = view.GetControl<TextBlock>("EditorLabel").Text!;

            Assert.Contains("NO injected text", title);
            Assert.Contains("NOT live", label);
            Assert.DoesNotContain("this is what your agents get", label);

            // The error must be shown, and must NOT be our text quietly reappearing.
            var error = view.GetControl<TextBlock>("ErrorText");
            Assert.True(error.IsVisible);
            Assert.DoesNotContain("NEVER SIGN IT", view.GetControl<TextBox>("EditorBox").Text ?? "");
        }
        finally { Cleanup(dir); }
    }

    // The same lie, through a BUTTON instead of a load. Ours is live, a stale custom file is sitting on
    // disk hand-edited into something unrenderable, and the user clicks "Switch back to my version".
    // The config flips to theirs and every launch path injects nothing - so the banner must say so
    // rather than cheerfully announcing their text is live.
    //
    // This is why every transition now mutates the store and RELOADS through the one path: the button
    // used to reassemble the screen by hand and had no idea the file was undeliverable.
    [AvaloniaFact]
    public async Task SwitchingBackToAStaleInvalidVersion_DoesNotAnnounceItAsLive()
    {
        var dir = NewDir();
        try
        {
            // Ours is live, but a custom file exists and was edited outside the product into a template
            // that cannot render.
            var useYours = false;
            var store = new InjectedTextStore(dir, () => new InjectedTextConfig(useYours), v => useYours = v);
            Directory.CreateDirectory(dir);
            File.WriteAllText(store.YoursPath, "[IF_SIGNED_IN]\nhello");

            var view = await ShownAsync(store);
            Assert.Contains("the DevThrottle text", view.GetControl<TextBlock>("SourceTitle").Text!);

            // The user switches back to their version.
            await ClickAsync(view, "WriteMyOwnButton");

            // The config really did change - so the tab must not claim their text is reaching agents.
            Assert.True(useYours);
            var title = view.GetControl<TextBlock>("SourceTitle").Text!;
            var label = view.GetControl<TextBlock>("EditorLabel").Text!;
            Assert.Contains("NO injected text", title);
            Assert.Contains("NOT live", label);
            Assert.DoesNotContain("this is what your agents get", label);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>Raise a button's Click and let the handler's async work finish.</summary>
    private static async Task ClickAsync(InjectedTextView view, string name)
    {
        var button = view.GetControl<Button>(name);
        button.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // The handlers are async void, so there is no task to await. Pump until the reload settles -
        // bounded, so a hang fails the test rather than wedging the suite.
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(10);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static InjectedTextStore TheirsStore(string dir)
    {
        var useYours = true;
        return new InjectedTextStore(dir, () => new InjectedTextConfig(useYours), v => useYours = v);
    }

    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "injected-text-view-test-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Build the view, show it, and WAIT for its first load to finish.</summary>
    private static async Task<InjectedTextView> ShownAsync(InjectedTextStore? store = null)
    {
        var view = store is null ? new InjectedTextView() : new InjectedTextView(store);
        var window = new Window { Content = view };
        window.Show();
        await view.Ready;
        return view;
    }

    private static string AllTextOf(Visual root)
    {
        var parts = new List<string>();
        Collect(root, parts);
        return string.Join(" ", parts);

        static void Collect(Visual v, List<string> into)
        {
            if (v is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                into.Add(tb.Text);
            foreach (var child in v.GetVisualChildren())
                Collect(child, into);
        }
    }
}
