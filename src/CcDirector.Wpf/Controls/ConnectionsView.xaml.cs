using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Utilities;
using CcDirector.Core.Storage;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Connection item for display in the connections list.
/// </summary>
public class ConnectionItem : INotifyPropertyChanged
{
    private string _status = "disconnected";

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Url { get; set; }
    public string? ToolBinding { get; set; }
    public string? Browser { get; set; }
    public string? CreatedAt { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            NotifyAllVisualProperties();
        }
    }

    public bool Connected { get; set; }
    public bool Busy { get; set; }
    public int? ChromePid { get; set; }

    private static readonly Brush BrushAmber = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)));
    private static readonly Brush BrushGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush BrushGray = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
    private static readonly Brush BrushGrayText = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Brush BrushDisabledBg = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Brush BrushCloseBg = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));
    private static readonly Brush BrushOpenBg = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));
    private static readonly Brush BrushLightText = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));

    private static Brush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    public Brush StatusBrush => Busy ? BrushAmber : Connected ? BrushGreen : BrushGray;

    public string StatusDisplay => Busy
        ? (Connected ? "Closing..." : "Opening...")
        : Connected ? "Connected" : "Disconnected";

    public Brush StatusTextBrush => Busy ? BrushAmber : Connected ? BrushGreen : BrushGrayText;

    public string ActionLabel => Busy
        ? (Connected ? "Closing..." : "Opening...")
        : Connected ? "Close" : "Open";

    public bool ActionEnabled => !Busy;

    public Brush ActionBackground => Busy ? BrushDisabledBg : Connected ? BrushCloseBg : BrushOpenBg;

    public Brush ActionForeground => Busy ? BrushGrayText : Connected ? BrushLightText : Brushes.White;

    public string ToolDisplay => string.IsNullOrEmpty(ToolBinding) ? "" : $"tool: {ToolBinding}";

    public string CreatedAtDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(CreatedAt)) return "";
            if (DateTime.TryParse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return $"Added {dt:MMM d, yyyy}";
            return "";
        }
    }

    public string MetaDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Url)) parts.Add(Url);
            if (!string.IsNullOrEmpty(ToolBinding)) parts.Add($"tool: {ToolBinding}");
            var dateStr = CreatedAtDisplay;
            if (!string.IsNullOrEmpty(dateStr)) parts.Add(dateStr);
            return string.Join("  |  ", parts);
        }
    }

    public void SetBusy(bool busy)
    {
        Busy = busy;
        NotifyAllVisualProperties();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyAllVisualProperties()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTextBrush));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionEnabled));
        OnPropertyChanged(nameof(ActionBackground));
        OnPropertyChanged(nameof(ActionForeground));
    }
}

/// <summary>
/// Full-page overlay view for managing browser connections.
/// Polls daemon for status updates; saves directly to connections.json for CRUD.
/// </summary>
public partial class ConnectionsView : UserControl
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ObservableCollection<ConnectionItem> _connections = new();
    private DispatcherTimer? _pollTimer;

    public ConnectionsView()
    {
        InitializeComponent();

        ConnectionList.ItemsSource = _connections;

        Loaded += async (_, _) =>
        {
            FileLog.Write("[ConnectionsView] Loaded");
            LoadingText.Visibility = Visibility.Visible;

            await Task.Run(() => LoadConnectionsFromFile());

            await Dispatcher.BeginInvoke(() =>
            {
                LoadingText.Visibility = Visibility.Collapsed;
                UpdateEmptyState();
            });
        };
    }

    // -----------------------------------------------------------------------
    // Public Polling Control (called by MainWindow)
    // -----------------------------------------------------------------------

    public void StartPolling()
    {
        FileLog.Write("[ConnectionsView] StartPolling");
        if (_pollTimer != null) return;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollDaemonStatus();
        _pollTimer.Start();

        // Immediate poll
        _ = PollDaemonStatus();
    }

    public void StopPolling()
    {
        FileLog.Write("[ConnectionsView] StopPolling");
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    // -----------------------------------------------------------------------
    // Connections File (direct read/write, no daemon needed)
    // -----------------------------------------------------------------------

    private void LoadConnectionsFromFile()
    {
        FileLog.Write("[ConnectionsView] LoadConnectionsFromFile");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            if (!File.Exists(registryPath))
            {
                FileLog.Write("[ConnectionsView] No connections.json found");
                return;
            }

            var json = File.ReadAllText(registryPath);
            var items = JsonSerializer.Deserialize<JsonElement>(json);

            if (items.ValueKind != JsonValueKind.Array) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                _connections.Clear();
                foreach (var item in items.EnumerateArray())
                {
                    _connections.Add(new ConnectionItem
                    {
                        Name = item.GetProperty("name").GetString() ?? "",
                        Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() : null,
                        ToolBinding = item.TryGetProperty("toolBinding", out var t) ? t.GetString() : null,
                        Browser = item.TryGetProperty("browser", out var b) ? b.GetString() : "chrome",
                        CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetString() : null,
                        Status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "disconnected" : "disconnected",
                    });
                }
                FileLog.Write($"[ConnectionsView] Loaded {_connections.Count} connections");
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] LoadConnectionsFromFile FAILED: {ex.Message}");
        }
    }

    private void SaveConnectionToFile(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] SaveConnectionToFile: {item.Name}");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            var dir = Path.GetDirectoryName(registryPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<Dictionary<string, object?>> existing = new();
            if (File.Exists(registryPath))
            {
                var json = File.ReadAllText(registryPath);
                var parsed = JsonSerializer.Deserialize<JsonElement>(json);
                if (parsed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in parsed.EnumerateArray())
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText());
                        if (dict != null) existing.Add(dict);
                    }
                }
            }

            var newEntry = new Dictionary<string, object?>
            {
                ["name"] = item.Name,
                ["description"] = item.Description,
                ["url"] = item.Url,
                ["toolBinding"] = item.ToolBinding,
                ["browser"] = item.Browser ?? "chrome",
                ["createdAt"] = item.CreatedAt,
                ["status"] = "disconnected",
            };

            existing.Add(newEntry);
            File.WriteAllText(registryPath, JsonSerializer.Serialize(existing,
                new JsonSerializerOptions { WriteIndented = true }));

            FileLog.Write($"[ConnectionsView] Saved connection to file: {item.Name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] SaveConnectionToFile FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Daemon Polling (status updates)
    // -----------------------------------------------------------------------

    private async Task PollDaemonStatus()
    {
        try
        {
            var port = GetDaemonPort();
            var response = await _http.GetAsync($"http://127.0.0.1:{port}/connections");
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (!data.TryGetProperty("connections", out var conns)) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                foreach (var item in conns.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString();
                    var connected = item.TryGetProperty("connected", out var c) && c.GetBoolean();

                    var existing = _connections.FirstOrDefault(ci => ci.Name == name);
                    if (existing != null)
                    {
                        existing.Connected = connected;
                        existing.Status = connected ? "connected" : "disconnected";
                    }
                    else
                    {
                        _connections.Add(new ConnectionItem
                        {
                            Name = name ?? "",
                            Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            Url = item.TryGetProperty("url", out var u) ? u.GetString() : null,
                            ToolBinding = item.TryGetProperty("toolBinding", out var t) ? t.GetString() : null,
                            CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetString() : null,
                            Connected = connected,
                            Status = connected ? "connected" : "disconnected",
                        });
                    }
                }
                UpdateEmptyState();
            });
        }
        catch
        {
            // Daemon not running - set all to disconnected
            _ = Dispatcher.BeginInvoke(() =>
            {
                foreach (var item in _connections)
                {
                    item.Connected = false;
                    item.Status = "disconnected";
                }
            });
        }
    }

    private int GetDaemonPort()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var lockPath = Path.Combine(localAppData, "cc-browser", "daemon.lock");

        if (File.Exists(lockPath))
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(lockPath));
                if (data.TryGetProperty("port", out var port)) return port.GetInt32();
            }
            catch { }
        }
        return 9280;
    }

    // -----------------------------------------------------------------------
    // UI Helpers
    // -----------------------------------------------------------------------

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // Event Handlers
    // -----------------------------------------------------------------------

    private void BtnAddConnection_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectionsView] BtnAddConnection_Click");
        var dialog = new AddConnectionDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var item = new ConnectionItem
            {
                Name = dialog.ConnectionName,
                Description = dialog.ConnectionDescription,
                Url = dialog.ConnectionUrl,
                ToolBinding = dialog.ConnectionTool,
                Browser = "chrome",
                CreatedAt = now,
            };

            _connections.Add(item);
            UpdateEmptyState();

            // Save directly to file (no daemon needed)
            Task.Run(() => SaveConnectionToFile(item));

            FileLog.Write($"[ConnectionsView] Connection added: {dialog.ConnectionName}");
        }
    }

    private async void BtnConnectionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;

        var item = _connections.FirstOrDefault(c => c.Name == name);
        if (item == null) return;

        FileLog.Write($"[ConnectionsView] BtnConnectionAction_Click: {name}, connected={item.Connected}");

        if (item.Connected)
        {
            // Close: try daemon, but don't fail hard
            await CloseConnection(item);
        }
        else
        {
            // Open: launch Chrome directly (no daemon dependency)
            await OpenConnection(item);
        }
    }

    private async Task OpenConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] OpenConnection: {item.Name}");
        item.SetBusy(true);

        var chromePath = FindChromePath();
        if (chromePath == null)
        {
            FileLog.Write("[ConnectionsView] OpenConnection FAILED: Chrome not found");
            item.SetBusy(false);
            MessageBox.Show(
                "Chrome not found. Install Google Chrome or Microsoft Edge.",
                "Browser Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var profileDir = CcStorage.ConnectionProfile(item.Name);
        if (!Directory.Exists(profileDir))
            Directory.CreateDirectory(profileDir);

        // Extension directory (relative to cc-browser tool source)
        var extensionDir = FindExtensionDir();

        var args = $"--user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check --disable-features=TranslateUI --disable-sync";

        if (extensionDir != null)
            args += $" --load-extension=\"{extensionDir}\"";

        if (!string.IsNullOrEmpty(item.Url))
            args += $" \"{item.Url}\"";

        FileLog.Write($"[ConnectionsView] Launching: {chromePath} {args}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = args,
                UseShellExecute = false,
            };

            var process = Process.Start(psi);
            FileLog.Write($"[ConnectionsView] Chrome launched: pid={process?.Id}");

            item.ChromePid = process?.Id;
            item.Connected = true;
            item.SetBusy(false);

            // Also notify daemon if it's running (fire-and-forget)
            _ = NotifyDaemonAsync("open", item.Name);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnection FAILED: {ex.Message}");
            item.SetBusy(false);
            MessageBox.Show(
                $"Failed to launch browser for \"{item.Name}\".\n\n{ex.Message}",
                "Launch Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task CloseConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] CloseConnection: {item.Name}, pid={item.ChromePid}");
        item.SetBusy(true);

        if (item.ChromePid is int pid)
        {
            var killed = await Task.Run(() => KillProcessTree(pid));
            FileLog.Write($"[ConnectionsView] KillProcessTree pid={pid}: killed={killed}");
        }
        else
        {
            FileLog.Write("[ConnectionsView] No tracked PID, skipping kill");
        }

        // Also notify daemon if running
        _ = NotifyDaemonAsync("close", item.Name);

        item.ChromePid = null;
        item.Connected = false;
        item.SetBusy(false);
    }

    private async Task NotifyDaemonAsync(string endpoint, string name)
    {
        try
        {
            var port = GetDaemonPort();
            var body = new StringContent(
                JsonSerializer.Serialize(new { name }),
                System.Text.Encoding.UTF8,
                "application/json");
            await _http.PostAsync($"http://127.0.0.1:{port}/connections/{endpoint}", body);
        }
        catch
        {
            // Daemon not running - that's OK, Chrome is open directly
        }
    }

    // -----------------------------------------------------------------------
    // Chrome Detection
    // -----------------------------------------------------------------------

    private static string? FindChromePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                FileLog.Write($"[ConnectionsView] Found browser: {path}");
                return path;
            }
        }

        return null;
    }

    private static bool KillProcessTree(int pid)
    {
        FileLog.Write($"[ConnectionsView] KillProcessTree: pid={pid}");
        try
        {
            var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            proc.Dispose();
            FileLog.Write($"[ConnectionsView] Killed process tree: pid={pid}");
            return true;
        }
        catch (ArgumentException)
        {
            // Process already exited
            FileLog.Write($"[ConnectionsView] Process already exited: pid={pid}");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] KillProcessTree FAILED pid={pid}: {ex.Message}");
            return false;
        }
    }

    private static string? FindExtensionDir()
    {
        // Check cc-browser tool source directory
        var repoExtension = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "cc-browser", "extension"));
        if (Directory.Exists(repoExtension) && File.Exists(Path.Combine(repoExtension, "manifest.json")))
        {
            FileLog.Write($"[ConnectionsView] Found extension (repo): {repoExtension}");
            return repoExtension;
        }

        // Check installed location
        var installedExtension = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "extension");
        if (Directory.Exists(installedExtension) && File.Exists(Path.Combine(installedExtension, "manifest.json")))
        {
            FileLog.Write($"[ConnectionsView] Found extension (installed): {installedExtension}");
            return installedExtension;
        }

        FileLog.Write("[ConnectionsView] Extension directory not found (non-critical)");
        return null;
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string name) return;

        var item = _connections.FirstOrDefault(c => c.Name == name);
        if (item == null) return;

        FileLog.Write($"[ConnectionsView] ContextDelete_Click: {item.Name}");

        var result = MessageBox.Show(
            $"Delete connection \"{item.Name}\"?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // Remove from file directly
        Task.Run(() => DeleteConnectionFromFile(item.Name));

        _connections.Remove(item);
        UpdateEmptyState();
        FileLog.Write($"[ConnectionsView] Connection deleted: {item.Name}");
    }

    private void DeleteConnectionFromFile(string name)
    {
        FileLog.Write($"[ConnectionsView] DeleteConnectionFromFile: {name}");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            if (!File.Exists(registryPath)) return;

            var json = File.ReadAllText(registryPath);
            var items = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();
            var filtered = items.Where(i =>
                i.TryGetProperty("name", out var n) && n.GetString() != name).ToList();
            File.WriteAllText(registryPath, JsonSerializer.Serialize(filtered,
                new JsonSerializerOptions { WriteIndented = true }));

            FileLog.Write($"[ConnectionsView] Deleted from file: {name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] DeleteConnectionFromFile FAILED: {ex.Message}");
        }
    }
}
