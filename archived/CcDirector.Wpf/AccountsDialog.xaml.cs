using System.Diagnostics;
using System.IO;
using System.Windows;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

public partial class AccountsDialog : Window
{
    private readonly ClaudeAccountStore _store;
    private readonly string _credentialsPath;

    public AccountsDialog(ClaudeAccountStore store)
    {
        InitializeComponent();
        _store = store;
        _credentialsPath = ClaudeAccountStore.GetDefaultCredentialsPath();
        CredPathText.Text = _credentialsPath;
        RefreshList();
        RefreshCredentialStatus();
    }

    private void RefreshCredentialStatus()
    {
        FileLog.Write("[AccountsDialog] RefreshCredentialStatus");
        var (matched, creds) = _store.ReadCredentialStatus();

        if (creds == null)
        {
            CredentialStatusText.Text = "No credentials file found. Click 'Login New Account' to get started.";
            CredentialStatusText.Foreground = BrushFromHex("#D97706");
            BtnCaptureUnknown.Visibility = Visibility.Collapsed;
            return;
        }

        var tierLabel = FormatTier(creds.SubscriptionType, creds.RateLimitTier);

        if (matched != null)
        {
            CredentialStatusText.Text = $"Current login: {matched.Label} ({tierLabel})";
            CredentialStatusText.Foreground = BrushFromHex("#CCCCCC");
            BtnCaptureUnknown.Visibility = Visibility.Collapsed;
        }
        else
        {
            CredentialStatusText.Text = $"Current login: Unknown account ({tierLabel})";
            CredentialStatusText.Foreground = BrushFromHex("#D97706");
            BtnCaptureUnknown.Visibility = Visibility.Visible;
        }
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnLogin_Click");

        // Disable buttons and show progress
        BtnLogin.IsEnabled = false;
        BtnBrowse.IsEnabled = false;
        LoginProgressBar.Visibility = Visibility.Visible;

        try
        {
            // Snapshot current credentials before login so we can detect what changed
            var (_, beforeCreds) = _store.ReadCredentialStatus();
            var beforeToken = beforeCreds?.AccessToken ?? "";

            // Run claude login as a process
            var exitCode = await Task.Run(() =>
            {
                FileLog.Write("[AccountsDialog] BtnLogin_Click: starting claude login process");
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "login",
                    UseShellExecute = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    FileLog.Write("[AccountsDialog] BtnLogin_Click: failed to start process");
                    return -1;
                }

                process.WaitForExit();
                FileLog.Write($"[AccountsDialog] BtnLogin_Click: process exited with code {process.ExitCode}");
                return process.ExitCode;
            });

            if (exitCode != 0)
            {
                MessageBox.Show(this,
                    $"'claude login' exited with code {exitCode}.\nCheck that Claude CLI is installed and working.",
                    "Login Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Read the new credentials
            var (matched, creds) = _store.ReadCredentialStatus();
            if (creds == null)
            {
                MessageBox.Show(this,
                    "Login completed but no credentials file found.\nThis is unexpected - check ~/.claude/.credentials.json.",
                    "Capture Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if the token actually changed
            if (creds.AccessToken == beforeToken)
            {
                MessageBox.Show(this,
                    "Login completed but the credentials did not change.\nYou may have logged into the same account.",
                    "No Change", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshCredentialStatus();
                return;
            }

            // If it matches an existing stored account, just update it
            if (matched != null)
            {
                MessageBox.Show(this,
                    $"Logged in and updated tokens for existing account '{matched.Label}'.",
                    "Account Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshList();
                RefreshCredentialStatus();
                return;
            }

            // New account - capture it
            var account = _store.CaptureFromCredentials();
            if (account == null)
            {
                MessageBox.Show(this,
                    "Could not capture the new credentials.",
                    "Capture Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(account.Label))
            {
                // Try profile API for auto-label
                string? autoLabel = null;
                try
                {
                    autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(account.AccessToken);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AccountsDialog] BtnLogin_Click: auto-label fetch failed: {ex.Message}");
                }

                var label = PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
                if (string.IsNullOrWhiteSpace(label))
                {
                    _store.Remove(account.Id);
                    return;
                }
                _store.UpdateLabel(account.Id, label.Trim());
            }

            RefreshList();
            RefreshCredentialStatus();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnLogin_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Login failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnLogin.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
            LoginProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnCapture_Click(object sender, RoutedEventArgs e)
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

            if (string.IsNullOrEmpty(account.Label))
            {
                string? autoLabel = null;
                try
                {
                    autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(account.AccessToken);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AccountsDialog] BtnCapture_Click: auto-label fetch failed: {ex.Message}");
                }

                var label = PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
                if (string.IsNullOrWhiteSpace(label))
                {
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
            RefreshCredentialStatus();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnCapture_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to capture account:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnBrowseJson_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnBrowseJson_Click");
        try
        {
            var claudeDir = Path.GetDirectoryName(_credentialsPath) ?? "";
            var dlg = new OpenFileDialog
            {
                Title = "Select credentials file",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(claudeDir) ? claudeDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            if (dlg.ShowDialog(this) != true) return;

            FileLog.Write($"[AccountsDialog] BtnBrowseJson_Click: selected={dlg.FileName}");
            var json = File.ReadAllText(dlg.FileName);
            await AddAccountFromJson(json);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnBrowseJson_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to read file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnAddFromJson_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnAddFromJson_Click");
        try
        {
            var json = PromptForJson();
            if (string.IsNullOrWhiteSpace(json)) return;
            await AddAccountFromJson(json);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] BtnAddFromJson_Click FAILED: {ex.Message}");
            MessageBox.Show(this, $"Failed to add account:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AddAccountFromJson(string json)
    {
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
            existing.RefreshToken = creds.RefreshToken;
            existing.ExpiresAt = creds.ExpiresAt;
            existing.SubscriptionType = creds.SubscriptionType;
            existing.RateLimitTier = creds.RateLimitTier;
            _store.UpdateToken(existing.Id, creds.AccessToken, creds.RefreshToken, creds.ExpiresAt);
            MessageBox.Show(this,
                $"Token updated for existing account '{existing.Label}'.",
                "Account Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshList();
            RefreshCredentialStatus();
            return;
        }

        // Try profile API for auto-label
        string? autoLabel = null;
        try
        {
            autoLabel = await ClaudeAccountStore.TryFetchProfileLabelAsync(creds.AccessToken);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountsDialog] AddAccountFromJson: auto-label fetch failed: {ex.Message}");
        }

        var label = PromptForLabel("Enter a label for this account (e.g. 'Personal', 'Work'):", autoLabel ?? "");
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

        FileLog.Write($"[AccountsDialog] AddAccountFromJson: added account '{label}' ({creds.SubscriptionType})");
        RefreshList();
        RefreshCredentialStatus();
    }

    private void BtnSetActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id) return;

        FileLog.Write($"[AccountsDialog] BtnSetActive_Click: id={id}");
        _store.SetActiveAccount(id);
        RefreshList();
        RefreshCredentialStatus();
    }

    private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnCopyPath_Click");
        Clipboard.SetText(_credentialsPath);
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[AccountsDialog] BtnOpenFolder_Click");
        var dir = Path.GetDirectoryName(_credentialsPath);
        if (dir != null && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
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
            Background = BrushFromHex("#252526"),
            Owner = this,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

        var instructions = new System.Windows.Controls.TextBlock
        {
            Text = "Paste the contents of ~/.claude/.credentials.json below.\n\nTo get this: open a terminal where you are logged in as the desired account, then run:\n  type %USERPROFILE%\\.claude\\.credentials.json",
            Foreground = BrushFromHex("#CCCCCC"),
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
            Background = BrushFromHex("#1E1E1E"),
            Foreground = BrushFromHex("#CCCCCC"),
            CaretBrush = BrushFromHex("#CCCCCC"),
            BorderBrush = BrushFromHex("#3C3C3C"),
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
            Background = BrushFromHex("#3C3C3C"),
            Foreground = BrushFromHex("#CCCCCC"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(cancelBtn);

        var addBtn = new System.Windows.Controls.Button
        {
            Content = "Add Account",
            Width = 100, Height = 30,
            Background = BrushFromHex("#007ACC"),
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
        RefreshCredentialStatus();
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
            RefreshCredentialStatus();
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
        var dlg = new Window
        {
            Title = "Account Label",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = BrushFromHex("#252526"),
            Owner = this,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Foreground = BrushFromHex("#CCCCCC"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(label);

        var input = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Background = BrushFromHex("#1E1E1E"),
            Foreground = BrushFromHex("#CCCCCC"),
            CaretBrush = BrushFromHex("#CCCCCC"),
            BorderBrush = BrushFromHex("#3C3C3C"),
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
            Background = BrushFromHex("#3C3C3C"),
            Foreground = BrushFromHex("#CCCCCC"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(cancelBtn);

        var okBtn = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80, Height = 30,
            Background = BrushFromHex("#007ACC"),
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

    private static string FormatTier(string? subscriptionType, string? rateLimitTier)
    {
        var sub = (subscriptionType ?? "").ToUpperInvariant();
        var tier = rateLimitTier ?? "";
        if (tier.Contains("20x", StringComparison.OrdinalIgnoreCase))
            return $"{sub} 20x";
        if (tier.Contains("5x", StringComparison.OrdinalIgnoreCase))
            return $"{sub} 5x";
        return sub;
    }

    private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal sealed class AccountViewModel
    {
        public string Id { get; }
        public string Label { get; }
        public string TierDisplay { get; }
        public string ExpiryDisplay { get; }
        public Visibility ActiveVisibility { get; }
        public Visibility SetActiveVisibility { get; }

        public AccountViewModel(ClaudeAccount account)
        {
            Id = account.Id;
            Label = string.IsNullOrEmpty(account.Label) ? "(unnamed)" : account.Label;
            ActiveVisibility = account.IsActive ? Visibility.Visible : Visibility.Collapsed;
            SetActiveVisibility = account.IsActive ? Visibility.Collapsed : Visibility.Visible;
            TierDisplay = FormatTier(account.SubscriptionType, account.RateLimitTier);

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
