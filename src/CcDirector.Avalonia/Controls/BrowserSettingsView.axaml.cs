using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Settings > Browsers: create, sign-in-once, rename, stop/start, and remove drivable automation
/// browsers. Every action calls the same <see cref="AutomationBrowserService"/> engine the Control API
/// exposes to agents, and every verdict shown is the Core fold rendered verbatim - the tab is a layout
/// over the one source of truth, never a second implementation of it.
/// </summary>
public partial class BrowserSettingsView : UserControl
{
    /// <summary>Raised after any successful change (create/rename/remove/start/stop/sign-in) so the
    /// host can refresh other Browsers surfaces (the rail group).</summary>
    public event EventHandler? Changed;

    private IReadOnlyList<AutomationBrowserView> _views = Array.Empty<AutomationBrowserView>();
    private readonly HashSet<string> _renamingIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;

    public BrowserSettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Open the inline create panel (used by the Browsers menu's "New Browser...").</summary>
    public void OpenCreatePanel()
    {
        CreatePanel.IsVisible = true;
        CreateNameBox.Focus();
    }

    /// <summary>Re-read the registry, probe live status, and repaint the whole tab.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            StatusText.Text = "Checking browsers...";

            var harnessInstalled = false;
            IReadOnlyList<AutomationBrowserView> views = Array.Empty<AutomationBrowserView>();
            IReadOnlyList<BrowserInfo> installed = Array.Empty<BrowserInfo>();
            await Task.Run(async () =>
            {
                harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled();
                installed = BrowserLauncher.DetectBrowsers();
                views = await AutomationBrowserViewFold.ListAsync().ConfigureAwait(false);
            });

            _views = views;
            HarnessBanner.IsVisible = !harnessInstalled;
            EmptyText.IsVisible = views.Count == 0;
            StatusText.Text = "";

            // The create panel offers only the browsers actually installed on this machine.
            var kinds = installed.Select(b => b.Kind.ToString()).ToList();
            var selected = CreateKindCombo.SelectedItem as string;
            CreateKindCombo.ItemsSource = kinds;
            CreateKindCombo.SelectedItem = kinds.Contains(selected ?? "") ? selected : kinds.FirstOrDefault();
            NewBrowserButton.IsEnabled = kinds.Count > 0;
            if (kinds.Count == 0)
                StatusText.Text = "No Chrome or Edge installation was found on this machine, so no browser can be created.";

            BrowserCards.ItemsSource = views.Select(v => new BrowserCardViewModel(v)
            {
                IsRenaming = _renamingIds.Contains(v.Id),
            }).ToList();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] RefreshAsync FAILED: {ex.Message}");
            StatusText.Text = $"Could not read the browsers list: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshAndNotifyAsync()
    {
        await RefreshAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Window OwnerWindow()
        => TopLevel.GetTopLevel(this) as Window
           ?? throw new InvalidOperationException("BrowserSettingsView has no owner window.");

    private BrowserCardViewModel? CardFor(object? sender)
    {
        var id = (sender as Button)?.Tag as string;
        return (BrowserCards.ItemsSource as IEnumerable<BrowserCardViewModel>)?.FirstOrDefault(c => c.Id == id);
    }

    // ---- header actions ----

    private void BtnNewBrowser_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowserSettingsView] BtnNewBrowser_Click");
        CreateError.IsVisible = false;
        OpenCreatePanel();
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowserSettingsView] BtnRefresh_Click");
        await RefreshAsync();
    }

    private void BtnInstallHarness_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnInstallHarness_Click -> {AutomationBrowserViewFold.HarnessInstallUrl}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AutomationBrowserViewFold.HarnessInstallUrl)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnInstallHarness_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not open the install guide: {ex.Message}";
        }
    }

    // ---- create ----

    private async void BtnCreate_Click(object? sender, RoutedEventArgs e)
    {
        var name = CreateNameBox.Text?.Trim() ?? "";
        var kindText = CreateKindCombo.SelectedItem as string;
        FileLog.Write($"[BrowserSettingsView] BtnCreate_Click: name={name}, kind={kindText}");

        try
        {
            if (name.Length == 0)
                throw new ArgumentException("Give the browser a name first.");
            if (!Enum.TryParse<BrowserKind>(kindText, ignoreCase: true, out var kind))
                throw new ArgumentException("Pick which browser to use.");

            CreateError.IsVisible = false;
            await Task.Run(() => AutomationBrowserService.Create(name, kind));

            CreateNameBox.Text = "";
            CreatePanel.IsVisible = false;
            StatusText.Text = $"Created \"{name}\". Next: Sign in once, so it holds a login your agents can use.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnCreate_Click FAILED: {ex.Message}");
            CreateError.Text = ex.Message;
            CreateError.IsVisible = true;
        }
    }

    private void BtnCreateCancel_Click(object? sender, RoutedEventArgs e)
    {
        CreatePanel.IsVisible = false;
        CreateError.IsVisible = false;
    }

    // ---- per-card actions ----

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnStart_Click: id={card.Id}");
            card.IsBusy = true;
            StatusText.Text = $"Starting \"{card.Name}\"...";
            await Task.Run(() => AutomationBrowserService.LaunchAsync(card.Id));
            StatusText.Text = $"\"{card.Name}\" is up.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnStart_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnStop_Click: id={card.Id}");
            card.IsBusy = true;
            StatusText.Text = $"Stopping \"{card.Name}\"...";
            await Task.Run(() => AutomationBrowserService.StopAsync(card.Id));
            StatusText.Text = $"Stopped \"{card.Name}\". Its login is kept - start it again any time.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnStop_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnSignIn_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnSignIn_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);
            card.IsBusy = true;
            if (await BrowserSignInFlow.RunAsync(OwnerWindow(), view))
                StatusText.Text = $"\"{card.Name}\" is signed in and ready to drive.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnSignIn_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnAttach_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnAttach_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("The clipboard is not available.");
            await clipboard.SetTextAsync(view.AttachCommand);
            StatusText.Text = $"Copied: {view.AttachCommand}";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnAttach_Click FAILED: id={card.Id}, {ex.Message}");
            StatusText.Text = ex.Message;
        }
    }

    // ---- rename ----

    private void BtnRenameStart_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;
        FileLog.Write($"[BrowserSettingsView] BtnRenameStart_Click: id={card.Id}");
        _renamingIds.Add(card.Id);
        card.EditName = card.Name;
        card.IsRenaming = true;
    }

    private void BtnRenameCancel_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;
        _renamingIds.Remove(card.Id);
        card.IsRenaming = false;
    }

    private async void BtnRenameSave_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnRenameSave_Click: id={card.Id}, newName={card.EditName}");
            await Task.Run(() => AutomationBrowserService.Rename(card.Id, card.EditName));
            _renamingIds.Remove(card.Id);
            StatusText.Text = $"Renamed to \"{card.EditName.Trim()}\".";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnRenameSave_Click FAILED: id={card.Id}, {ex.Message}");
            StatusText.Text = ex.Message;
        }
    }

    // ---- remove ----

    private async void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnRemove_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);

            var signedInClause = view.LastSignedInUtc is null
                ? "It has never been signed in."
                : $"It was signed in on {view.LastSignedInUtc.Value.ToLocalTime():yyyy-MM-dd}, and that login is deleted with it.";
            var dialog = new ConfirmDialog(
                "Remove browser",
                $"This closes \"{view.Name}\" and permanently deletes its profile folder. {signedInClause} " +
                "To use this identity again you would have to create a new browser and sign in again.",
                confirmLabel: "Delete browser",
                cancelLabel: "Cancel");
            if (await dialog.ShowDialog<bool?>(OwnerWindow()) != true) return;

            card.IsBusy = true;
            StatusText.Text = $"Removing \"{view.Name}\"...";
            await Task.Run(() => AutomationBrowserService.RemoveAsync(view.Id));
            StatusText.Text = $"Removed \"{view.Name}\".";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnRemove_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>
    /// One rendered browser card: the fold's strings verbatim, plus the brushes this surface maps the
    /// fold's names to, plus the two bits of transient UI state (renaming, busy).
    /// </summary>
    public sealed class BrowserCardViewModel : INotifyPropertyChanged
    {
        private static readonly IBrush ChromeIcon = Brush.Parse("#0F6CBD");
        private static readonly IBrush EdgeIcon = Brush.Parse("#1AA1B8");
        private static readonly IBrush PillGreyBg = Brush.Parse("#3C3C3C");
        private static readonly IBrush PillDarkText = Brush.Parse("#141413");
        private static readonly IBrush PillLightText = Brush.Parse("#CCCCCC");

        private bool _isRenaming;
        private bool _isBusy;
        private string _editName = "";

        public BrowserCardViewModel(AutomationBrowserView view)
        {
            Id = view.Id;
            Name = view.Name;
            Subtitle = $"{view.Subtitle}  (port {view.Port})";
            StatusLabel = view.StatusLabel;
            AttachToolTip = $"Copy to the clipboard: {view.AttachCommand}";
            IconLetter = view.Browser.Length > 0 ? view.Browser[..1] : "?";
            IconBackground = string.Equals(view.Browser, "Edge", StringComparison.OrdinalIgnoreCase) ? EdgeIcon : ChromeIcon;

            ShowStart = view.Status == AutomationBrowserStatus.Stopped;
            ShowSignIn = view.Status == AutomationBrowserStatus.NeedsSignIn;
            ShowAttach = view.Status == AutomationBrowserStatus.Ready;
            ShowStop = view.Status != AutomationBrowserStatus.Stopped;

            // The pill's color IS the fold's dot color; only the text contrast is chosen here.
            PillBackground = view.Status == AutomationBrowserStatus.Stopped
                ? PillGreyBg
                : StatusPalette.BrushFor(view.DotColor);
            PillForeground = view.Status == AutomationBrowserStatus.Stopped ? PillLightText : PillDarkText;
        }

        public string Id { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public string StatusLabel { get; }
        public string AttachToolTip { get; }
        public string IconLetter { get; }
        public IBrush IconBackground { get; }
        public IBrush PillBackground { get; }
        public IBrush PillForeground { get; }
        public bool ShowStart { get; }
        public bool ShowSignIn { get; }
        public bool ShowAttach { get; }
        public bool ShowStop { get; }

        public bool IsRenaming
        {
            get => _isRenaming;
            set { _isRenaming = value; Raise(nameof(IsRenaming)); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(NotBusy)); }
        }

        public bool NotBusy => !_isBusy;

        public string EditName
        {
            get => _editName;
            set { _editName = value; Raise(nameof(EditName)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
