using Avalonia.Controls;
using Avalonia.Media;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Settings tab that shows the user exactly what DevThrottle injects into their agents, and lets
/// them replace it with their own.
///
/// The point of this screen is CONSENT, so its first duty is that a user is never wrong about whose
/// text is live. That is why the source is a full-width coloured banner rather than a checkbox: a
/// user running their own text must never be able to believe they are on ours, and the reverse.
/// </summary>
public partial class InjectedTextView : UserControl
{
    // The colours of the two states. Ours is the calm, expected one; theirs is deliberately warmer,
    // because "this text is not the one DevThrottle ships" is the fact this screen exists to convey.
    private static readonly SolidColorBrush OursBannerBackground = new(Color.Parse("#173A2F"));
    private static readonly SolidColorBrush OursBannerForeground = new(Color.Parse("#7BD1A8"));
    private static readonly SolidColorBrush YoursBannerBackground = new(Color.Parse("#3A2E17"));
    private static readonly SolidColorBrush YoursBannerForeground = new(Color.Parse("#E0B761"));
    // The third state: chosen theirs, but it cannot be read, so agents are getting nothing at all.
    private static readonly SolidColorBrush UnavailableBannerBackground = new(Color.Parse("#3A1D19"));
    private static readonly SolidColorBrush UnavailableBannerForeground = new(Color.Parse("#E88E7D"));

    private readonly InjectedTextStore _store;

    /// <summary>The text as it is saved on disk, so "discard changes" and the dirty check are exact.</summary>
    private string _savedText = "";

    /// <summary>True while the code is setting the editor's text, so the change handler stays quiet.</summary>
    private bool _loading;

    /// <summary>True when the editor holds a draft of the user's own version, saved or not.</summary>
    private bool _editingOwn;

    /// <summary>
    /// Set while their chosen text cannot be delivered, so agents are getting nothing.
    ///
    /// It is sticky on purpose. Refresh() re-validates whatever is in the editor and clears the error
    /// when it is fine - which is right while typing, and wrong here: an unreadable file leaves an EMPTY
    /// editor, empty is a perfectly valid template, so Refresh would cheerfully wipe the "your agents are
    /// getting nothing" banner and leave the screen looking healthy while agents got nothing. The state
    /// is about the file on disk, not about the text in the box, so only saving clears it.
    /// </summary>
    private bool _unavailable;

    /// <summary>
    /// The first load. Exposed so a test can await it instead of guessing when the text has arrived -
    /// a test that races the load would go green on an empty screen, which is worse than no test.
    /// </summary>
    public Task Ready { get; }

    /// <summary>The tab as the application builds it, over this machine's real injected text.</summary>
    public InjectedTextView() : this(new InjectedTextStore()) { }

    /// <summary>
    /// Testable constructor over an explicit store.
    ///
    /// Without this, a test of the "your agents are getting nothing" banner could only pass by luck:
    /// it would assert against whatever the developer's own machine happens to be set to, go green
    /// having never entered the state it names, and stay green if that state broke. A test that cannot
    /// reach the case it guards is decoration.
    /// </summary>
    public InjectedTextView(InjectedTextStore store)
    {
        _store = store;

        InitializeComponent();

        // The dialog must respond immediately (project rule 1), so the panel appears at once and the
        // text arrives when it has been read.
        EditorBox.Text = "Loading...";
        EditorBox.IsEnabled = false;

        // The banner must NEVER be blank or wrong, not even for the moment before the text arrives.
        // "Whose text is live" is the one thing this screen exists to be right about, so it starts by
        // admitting it does not know yet rather than showing a colour that means something.
        SourceTitle.Text = "Checking which text your agents are getting...";
        SourceDetail.Text = "";
        SourceBanner.Background = new SolidColorBrush(Color.Parse("#2D2D2D"));
        SourceTitle.Foreground = new SolidColorBrush(Color.Parse("#888888"));

        Ready = InitialLoadAsync();
    }

    private async Task InitialLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] InitialLoadAsync FAILED: {ex.Message}");
            ShowError($"The injected text could not be loaded: {ex.Message}");
        }
    }

    private async Task LoadAsync()
    {
        FileLog.Write("[InjectedTextView] LoadAsync");

        // Writing our current text to disk is part of the promise that our updates are always there
        // to read, even for a user running their own.
        var (source, text, problem) = await Task.Run(() =>
        {
            _store.EnsureOursWritten();
            var src = _store.ActiveSource();

            try
            {
                // Ask the ACTUAL LAUNCH PATH what a session would be given, rather than re-deciding it
                // here. This is the only way the tab and the agents cannot drift apart: any reason a
                // session would get nothing - unreadable file, a template hand-edited into something
                // that cannot render - surfaces here through the same code, automatically, including
                // reasons added later that nobody remembers to mirror into this screen.
                //
                // The rendered result is thrown away; only whether it THROWS matters. What the user
                // edits is the template, not the rendering.
                FleetPreamble.BuildForSession(
                    Guid.Empty.ToString(), "sample", Environment.MachineName, "sample", null, _store);

                return (src, _store.ActiveTemplate(), (string?)null);
            }
            catch (Exception ex) when (ex is InjectedTextUnavailableException or FleetPreambleTemplateException)
            {
                // Their text is chosen but cannot be delivered. Show that plainly - we have NOT swapped
                // ours in, and sessions are launching with no injected text at all until it is fixed.
                string? theirs = null;
                try { theirs = _store.ReadYours(); } catch { /* unreadable is the whole problem */ }
                return (src, theirs ?? "", ex.Message);
            }
        });

        _savedText = text;
        _editingOwn = source == InjectedTextSource.Yours;

        _loading = true;
        EditorBox.Text = text;
        _loading = false;
        EditorBox.IsEnabled = true;
        EditorBox.IsReadOnly = false;

        PlaceholderHint.Text =
            "DevThrottle fills these in for each session: " +
            string.Join(", ", FleetPreamblePlaceholders.All) +
            ". Write them anywhere in your text and they are replaced when a session starts. " +
            "The lines between [IF_SIGNED_IN] and [END_IF] are used only when you are signed in.";

        if (problem is not null)
        {
            // THE THIRD STATE, and it is not a detail. Their text is selected but cannot be delivered,
            // so agents are getting NOTHING right now. Painting the ordinary "your text is live" banner
            // over it would be a lie of exactly the kind this tab exists to stop: the label would say
            // "this is what your agents get" about text they are not getting.
            ApplyUnavailable(problem);
        }
        else
        {
            // LoadAsync is the ONLY thing that decides this state, in either direction. Every button
            // that changes whose text is live now mutates the store and re-enters here rather than
            // reassembling the screen by hand - the last defect in this tab was a button that set the
            // ordinary "your text is live" banner without ever asking whether that text could be
            // delivered, so a stale invalid file on disk was announced as live while agents got nothing.
            _unavailable = false;
            ApplySource(source);
            Refresh();
        }

        FileLog.Write($"[InjectedTextView] LoadAsync: source={source}, {text.Length} characters, " +
                      $"deliverable={problem is null}");
    }

    /// <summary>Paint the banner and set what the editor is allowed to do for this source.</summary>
    private void ApplySource(InjectedTextSource source)
    {
        if (source == InjectedTextSource.Yours)
        {
            SourceBanner.Background = YoursBannerBackground;
            SourceTitle.Foreground = YoursBannerForeground;
            SourceDetail.Foreground = YoursBannerForeground;
            SourceTitle.Text = "Your agents are getting YOUR text, not DevThrottle's.";
            SourceDetail.Text =
                "You are running a version you wrote. DevThrottle's updates to this text are still " +
                "downloaded and can be read at any time, but they are not applied to yours.";
            EditorLabel.Text = "Your text (this is what your agents get)";
            EditorBox.IsReadOnly = false;
            UseOursButton.IsVisible = true;
            WriteMyOwnButton.IsVisible = false;
        }
        else
        {
            SourceBanner.Background = OursBannerBackground;
            SourceTitle.Foreground = OursBannerForeground;
            SourceDetail.Foreground = OursBannerForeground;
            SourceTitle.Text = "Your agents are getting the DevThrottle text.";
            SourceDetail.Text =
                "This is the version we ship. It updates when DevThrottle updates. " +
                "You can replace it with your own at any time.";
            EditorLabel.Text = "The DevThrottle text (this is what your agents get)";
            EditorBox.IsReadOnly = true;
            UseOursButton.IsVisible = false;
            WriteMyOwnButton.IsVisible = true;

            // If they have a version of their own put aside, offer it back by name rather than making
            // them retype it - and never word it as "write my own", which would suggest starting over
            // and losing what they wrote.
            WriteMyOwnButton.Content = _store.HasYours
                ? "Switch back to my version"
                : "Write my own version";
        }
    }

    /// <summary>
    /// The state where the user's text is chosen but cannot be read or rendered, so their agents are
    /// currently getting NOTHING. It is called out in its own colour because it is neither of the two
    /// normal answers, and because the honest sentence - "your agents have no injected text right now"
    /// - is not one the user could work out from a banner that just says theirs is live.
    /// </summary>
    private void ApplyUnavailable(string problem)
    {
        _unavailable = true;
        SourceBanner.Background = UnavailableBannerBackground;
        SourceTitle.Foreground = UnavailableBannerForeground;
        SourceDetail.Foreground = UnavailableBannerForeground;
        SourceTitle.Text = "Your agents are getting NO injected text.";
        SourceDetail.Text =
            "You chose to run your own text, but it cannot be read. DevThrottle has NOT put its own " +
            "text back in its place, because you turned that off. Save a version below, or switch to " +
            "the DevThrottle text.";

        EditorLabel.Text = "Your text (NOT live - it could not be read)";
        EditorBox.IsReadOnly = false;
        UseOursButton.IsVisible = true;
        WriteMyOwnButton.IsVisible = false;

        ShowError(problem);

        // Saving is the way out, so the save controls are offered even though nothing has been typed.
        SaveButton.IsVisible = true;
        SaveButton.IsEnabled = true;
        RevertButton.IsVisible = false;
        SaveStatus.Text = "";
    }

    /// <summary>Recompute everything that depends on what is currently in the editor.</summary>
    private void Refresh()
    {
        // While their text cannot be delivered, the screen keeps saying so. Saving is the way out, and
        // BtnSave_Click is what clears this - see the field's note for why re-validating the editor is
        // the wrong question to ask here.
        if (_unavailable)
            return;

        var text = EditorBox.Text ?? "";
        var dirty = _editingOwn && text != _savedText;

        SaveButton.IsVisible = dirty;
        RevertButton.IsVisible = dirty;
        SaveStatus.Text = dirty ? "Not saved - your agents are still getting the previous text." : "";

        ErrorText.IsVisible = false;

        // An unrenderable template cannot be saved. Say so while they type, not at a session launch.
        var problem = FleetPreambleRenderer.Validate(text);
        if (problem is not null)
        {
            ShowError(problem);
            SaveButton.IsEnabled = false;
            return;
        }

        SaveButton.IsEnabled = true;
        ShowWarningsFor(text);
    }

    /// <summary>
    /// Tell the user what their own text will cost them, WITHOUT refusing it. Removing the fleet
    /// commands is their right; discovering months later that their agents cannot find each other is
    /// not a consequence they agreed to.
    /// </summary>
    private void ShowWarningsFor(string text)
    {
        var warnings = new List<string>();

        if (!text.Contains("cc-devthrottle", StringComparison.Ordinal))
            warnings.Add(
                "Your text does not mention the cc-devthrottle command. Your agents will not be told " +
                "how to reach each other, so they will not message, list, or coordinate with the rest " +
                "of your fleet unless they work it out another way.");

        if (!text.Contains(FleetPreamblePlaceholders.SessionId, StringComparison.Ordinal) &&
            !text.Contains(FleetPreamblePlaceholders.SessionShortId, StringComparison.Ordinal))
            warnings.Add(
                "Your text does not include [SESSION_ID] or [SESSION_SHORT_ID], so an agent will not " +
                "know which session it is.");

        // The likeliest silent mistake: a placeholder-shaped word that is not one of ours reaches the
        // agent verbatim. Catch it here, where the person who typed it is looking.
        var unknown = FindUnknownPlaceholders(text);
        if (unknown.Count > 0)
            warnings.Add(
                "This looks like a placeholder but is not one DevThrottle knows, so it will be sent to " +
                "the agent exactly as written: " + string.Join(", ", unknown) + ".");

        WarningText.Text = string.Join("\n\n", warnings);
        WarningText.IsVisible = warnings.Count > 0;
    }

    /// <summary>
    /// Find bracket-shaped words that look like a placeholder typo - all-capitals with underscores,
    /// which is the shape of ours and not the shape of ordinary prose. Deliberately narrow: our own
    /// text opens with the literal "[CC Director fleet]", and a user's "[see the docs]" is prose, not
    /// a mistake. Warn about the near-misses only.
    /// </summary>
    private static List<string> FindUnknownPlaceholders(string text)
    {
        var found = new List<string>();
        var known = new HashSet<string>(FleetPreamblePlaceholders.All, StringComparer.Ordinal)
        {
            FleetPreamblePlaceholders.IfSignedIn,
            FleetPreamblePlaceholders.EndIf,
        };

        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('[', i);
            if (open < 0) break;
            var close = text.IndexOf(']', open);
            if (close < 0) break;

            var token = text[open..(close + 1)];
            var inner = token[1..^1];

            var placeholderShaped =
                inner.Length > 0 &&
                inner.All(c => char.IsAsciiLetterUpper(c) || c == '_' || c == '-' || char.IsAsciiDigit(c));

            if (placeholderShaped && !known.Contains(token) && !found.Contains(token))
                found.Add(token);

            i = close + 1;
        }

        return found;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        WarningText.IsVisible = false;
    }

    // -- Event handlers. Try-catch lives here and nowhere below (project rule 4). --

    private void Editor_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] Editor_TextChanged FAILED: {ex.Message}");
        }
    }

    private async void BtnWriteMyOwn_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[InjectedTextView] BtnWriteMyOwn_Click");

            if (_store.HasYours)
            {
                // They already have one put aside: bring it back rather than overwrite it. Note this
                // file may be stale or hand-edited into something that cannot render - which is exactly
                // why the reload below decides how it looks, instead of this method assuming.
                _store.UseYours();
            }
            else
            {
                // Start from a copy of ours, which is the only sensible blank page: it is what their
                // agents get today, and editing it is easier than writing from nothing.
                _store.SaveYours(InjectedTextStore.Ours);
            }

            await LoadAsync();
            SaveStatus.Text = "Your version is now live. Edit it and save.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] BtnWriteMyOwn_Click FAILED: {ex.Message}");
            ShowError($"Could not switch to your own version: {ex.Message}");
        }
    }

    private async void BtnUseOurs_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[InjectedTextView] BtnUseOurs_Click");

            _store.UseOurs();

            await LoadAsync();
            SaveStatus.Text = "Your version is kept - you can switch back to it at any time.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] BtnUseOurs_Click FAILED: {ex.Message}");
            ShowError($"Could not switch to the DevThrottle version: {ex.Message}");
        }
    }

    private void BtnShowOurs_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var showing = !OursBox.IsVisible;
            OursBox.IsVisible = showing;
            OursLabel.IsVisible = showing;
            PaneSplitter.IsVisible = showing;
            OursBox.Text = InjectedTextStore.Ours;
            ShowOursButton.Content = showing
                ? "Hide the DevThrottle text"
                : "Show the current DevThrottle text";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] BtnShowOurs_Click FAILED: {ex.Message}");
        }
    }

    private void BtnRevert_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _loading = true;
            EditorBox.Text = _savedText;
            _loading = false;
            Refresh();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] BtnRevert_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSave_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var text = EditorBox.Text ?? "";
            FileLog.Write($"[InjectedTextView] BtnSave_Click: {text.Length} characters");

            _store.SaveYours(text);

            await LoadAsync();
            SaveStatus.Text = "Saved. New sessions get this text.";
        }
        catch (FleetPreambleTemplateException ex)
        {
            FileLog.Write($"[InjectedTextView] BtnSave_Click rejected: {ex.Message}");
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InjectedTextView] BtnSave_Click FAILED: {ex.Message}");
            ShowError($"Could not save your version: {ex.Message}");
        }
    }
}
