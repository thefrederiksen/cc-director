using System.Windows;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class AccountsDialog : Window
{
    private readonly ClaudeAccountStore _store;

    public AccountsDialog(ClaudeAccountStore store)
    {
        InitializeComponent();
        _store = store;
        RefreshList();
    }

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnCapture_Click");
        try
        {
            var account = _store.CaptureFromCredentials();
            if (account == null)
            {
                MessageBox.Show(this,
                    "Could not read credentials. Make sure you have run 'claude login' first.",
                    "Capture Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If new account (no label), prompt for label
            if (string.IsNullOrEmpty(account.Label))
            {
                var label = PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):");
                if (string.IsNullOrWhiteSpace(label))
                {
                    // User cancelled - remove the account we just added
                    _store.Remove(account.Id);
                    return;
                }
                _store.UpdateLabel(account.Id, label.Trim());
            }
            else
            {
                MessageBox.Show(this,
                    $"Token updated for existing account '{account.Label}'.",
                    "Account Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            RefreshList();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnCapture_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to capture account:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnAddFromJson_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnAddFromJson_Click");
        try
        {
            var json = PromptForJson();
            if (string.IsNullOrWhiteSpace(json)) return;

            var creds = ClaudeAccountStore.ParseCredentialsJson(json);
            if (creds == null)
            {
                MessageBox.Show(this,
                    "Could not parse credentials JSON.\n\nExpected format: the full contents of ~/.claude/.credentials.json\n(must contain a \"claudeAiOauth\" object with an \"accessToken\" field).",
                    "Parse Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if token already exists
            var existing = _store.GetAll().FirstOrDefault(a => a.AccessToken == creds.AccessToken);
            if (existing != null)
            {
                // Update existing
                existing.RefreshToken = creds.RefreshToken;
                existing.ExpiresAt = creds.ExpiresAt;
                existing.SubscriptionType = creds.SubscriptionType;
                existing.RateLimitTier = creds.RateLimitTier;
                _store.UpdateToken(existing.Id, creds.AccessToken, creds.RefreshToken, creds.ExpiresAt);
                MessageBox.Show(this,
                    $"Token updated for existing account '{existing.Label}'.",
                    "Account Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshList();
                return;
            }

            // Prompt for label
            var label = PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):");
            if (string.IsNullOrWhiteSpace(label)) return;

            var account = new ClaudeAccount
            {
                Label = label.Trim(),
                AccessToken = creds.AccessToken,
                RefreshToken = creds.RefreshToken,
                ExpiresAt = creds.ExpiresAt,
                SubscriptionType = creds.SubscriptionType,
                RateLimitTier = creds.RateLimitTier,
                IsActive = false,
            };
            _store.Add(account);

            FileLog.Write($"[AccountsDialog] BtnAddFromJson_Click: added account '{label}' ({creds.SubscriptionType})");
            RefreshList();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnAddFromJson_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to add account:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptForJson()
    {
        var dlg = new Window
        {
            Title = "Add Account from JSON",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526")),
            Owner = this,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

        var instructions = new System.Windows.Controls.TextBlock
        {
            Text = "Paste the contents of ~/.claude/.credentials.json below.\n\nTo get this: open a terminal where you are logged in as the desired account, then run:\n  type %USERPROFILE%\\.claude\\.credentials.json",
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(instructions);

        var input = new System.Windows.Controls.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            CaretBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3C3C3C")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New"),
        };
        panel.Children.Add(input);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        string? result = null;

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3C3C3C")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(cancelBtn);

        var addBtn = new System.Windows.Controls.Button
        {
            Content = "Add Account",
            Width = 100, Height = 30,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        addBtn.Click += (_, _) => { result = input.Text; dlg.Close(); };
        buttons.Children.Add(addBtn);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        dlg.Loaded += (_, _) => input.Focus();
        dlg.ShowDialog();
        return result;
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnRename_Click: id={id}");
        var account = _store.GetAll().FirstOrDefault(a => a.Id == id);
        if (account == null) return;

        var newLabel = PromptForLabel($"Rename '{account.Label}' to:", account.Label);
        if (string.IsNullOrWhiteSpace(newLabel)) return;

        _store.UpdateLabel(id, newLabel.Trim());
        RefreshList();
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnRemove_Click: id={id}");
        var account = _store.GetAll().FirstOrDefault(a => a.Id == id);
        if (account == null) return;

        var result = MessageBox.Show(this,
            $"Remove account '{account.Label}'?",
            "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _store.Remove(id);
            RefreshList();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshList()
    {
        var accounts = _store.GetAll();
        var viewModels = accounts.Select(a => new AccountViewModel(a)).ToList();
        AccountList.ItemsSource = viewModels;
        EmptyMessage.Visibility = viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? PromptForLabel(string prompt, string defaultValue = "")
    {
        // Simple input dialog using a secondary window
        var dlg = new Window
        {
            Title = "Account Label",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526")),
            Owner = this,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(label);

        var input = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            CaretBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3C3C3C")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
        };
        panel.Children.Add(input);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        string? result = null;

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3C3C3C")),
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(cancelBtn);

        var okBtn = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80, Height = 30,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        okBtn.Click += (_, _) => { result = input.Text; dlg.Close(); };
        buttons.Children.Add(okBtn);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        dlg.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        dlg.ShowDialog();
        return result;
    }

    internal sealed class AccountViewModel
    {
        public string Id { get; }
        public string Label { get; }
        public string TierDisplay { get; }
        public string ExpiryDisplay { get; }
        public Visibility ActiveVisibility { get; }

        public AccountViewModel(ClaudeAccount account)
        {
            Id = account.Id;
            Label = string.IsNullOrEmpty(account.Label) ? "(unnamed)" : account.Label;
            ActiveVisibility = account.IsActive ? Visibility.Visible : Visibility.Collapsed;

            // Format tier display
            var sub = (account.SubscriptionType ?? "").ToUpperInvariant();
            var tier = account.RateLimitTier ?? "";
            if (tier.Contains("20x", StringComparison.OrdinalIgnoreCase))
                TierDisplay = $"{sub} 20x";
            else if (tier.Contains("5x", StringComparison.OrdinalIgnoreCase))
                TierDisplay = $"{sub} 5x";
            else
                TierDisplay = sub;

            // Format expiry
            if (account.ExpiresAt > 0)
            {
                var expiry = DateTimeOffset.FromUnixTimeMilliseconds(account.ExpiresAt);
                ExpiryDisplay = $"Token expires: {expiry:MMM d, yyyy h:mm tt}";
            }
            else
            {
                ExpiryDisplay = "Token expiry: unknown";
            }
        }
    }
}
