using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
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
    private readonly object _connectionsLock = new();
    private DispatcherTimer? _pollTimer;

    public ConnectionsView()
    {
        InitializeComponent();

        ConnectionList.ItemsSource = _connections;
        BindingOperations.EnableCollectionSynchronization(_connections, _connectionsLock);

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

        FileLog.Write($"[ConnectionsView] BtnConnectionAction_Click: name={name}, connected={item.Connected}, busy={item.Busy}, pid={item.ChromePid}");

        if (item.Busy)
        {
            FileLog.Write($"[ConnectionsView] BtnConnectionAction_Click: ignored, item is busy");
            return;
        }

        if (item.Connected)
        {
            await CloseConnection(item);
        }
        else
        {
            await OpenConnection(item);
        }
    }

    private async Task OpenConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] OpenConnection: name={item.Name}, connected={item.Connected}, pid={item.ChromePid}");

        if (item.Connected)
        {
            FileLog.Write($"[ConnectionsView] OpenConnection: already connected, ignoring double-open");
            return;
        }

        item.SetBusy(true);

        try
        {
            var port = GetDaemonPort();
            var payload = JsonSerializer.Serialize(new { name = item.Name });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{port}/connections/open", content);
            var json = await response.Content.ReadAsStringAsync();

            FileLog.Write($"[ConnectionsView] OpenConnection: daemon response status={response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[ConnectionsView] OpenConnection FAILED: {json}");
                item.SetBusy(false);
                MessageBox.Show(
                    $"Failed to open connection \"{item.Name}\".\n\n{json}",
                    "Open Connection Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(json);
            if (result.TryGetProperty("pid", out var pidEl))
                item.ChromePid = pidEl.GetInt32();

            item.Connected = true;
            item.SetBusy(false);
            FileLog.Write($"[ConnectionsView] OpenConnection: opened pid={item.ChromePid}");
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnection FAILED: daemon not reachable: {ex.Message}");
            item.SetBusy(false);
            MessageBox.Show(
                "cc-browser daemon is not running.\n\nStart it with: cc-browser daemon",
                "Daemon Not Running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnection FAILED: {ex.Message}");
            item.SetBusy(false);
            MessageBox.Show(
                $"Failed to open connection \"{item.Name}\".\n\n{ex.Message}",
                "Open Connection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task CloseConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] CloseConnection: name={item.Name}, pid={item.ChromePid}");
        item.SetBusy(true);

        try
        {
            var port = GetDaemonPort();
            var payload = JsonSerializer.Serialize(new { name = item.Name });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{port}/connections/close", content);
            FileLog.Write($"[ConnectionsView] CloseConnection: daemon response status={response.StatusCode}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] CloseConnection: daemon call failed: {ex.Message}");
        }

        item.ChromePid = null;
        item.Connected = false;
        item.SetBusy(false);
    }

    private async void BtnWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;
        var item = _connections.FirstOrDefault(c => c.Name == name);
        if (item == null) return;

        FileLog.Write($"[ConnectionsView] BtnWorkflow_Click: name={name}");

        // Step 1: Show confirmation dialog
        var confirm = new WorkflowConfirmDialog { Owner = Window.GetWindow(this) };
        if (confirm.ShowDialog() != true)
        {
            FileLog.Write("[ConnectionsView] BtnWorkflow_Click: user cancelled");
            return;
        }

        // Step 2: Close existing connection if open
        if (item.Connected)
        {
            FileLog.Write($"[ConnectionsView] BtnWorkflow_Click: closing existing connection for {name}");
            await CloseConnection(item);
            await Task.Delay(500); // allow process cleanup
        }

        // Step 3: Calculate screen layout -- recorder left 20%, browser right 80%
        var screenW = (int)SystemParameters.PrimaryScreenWidth;
        var screenH = (int)SystemParameters.PrimaryScreenHeight;
        var recorderWidth = (int)(screenW * 0.2);
        var browserX = recorderWidth;
        var browserWidth = screenW - recorderWidth;

        // Step 4: Open recorder window on left 20%
        var recorder = new WorkflowRecorderWindow(name, GetDaemonPort());
        recorder.Left = 0;
        recorder.Top = 0;
        recorder.Width = recorderWidth;
        recorder.Height = screenH;
        recorder.Show();

        // Step 5: Launch browser positioned on right 80%
        await OpenConnectionPositioned(item, browserX, 0, browserWidth, screenH);

        // Step 6: Ensure browser is positioned correctly (Chromium flags are best-effort)
        await Task.Delay(2000);
        RepositionBrowserWindow(item, browserX, 0, browserWidth, screenH);
    }

    private async Task OpenConnectionPositioned(ConnectionItem item, int x, int y, int width, int height)
    {
        FileLog.Write($"[ConnectionsView] OpenConnectionPositioned: name={item.Name}, x={x}, y={y}, w={width}, h={height}");

        item.SetBusy(true);

        try
        {
            var port = GetDaemonPort();
            var payload = JsonSerializer.Serialize(new
            {
                name = item.Name,
                windowPosition = new { x, y },
                windowSize = new { width, height },
            });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{port}/connections/open", content);
            var json = await response.Content.ReadAsStringAsync();

            FileLog.Write($"[ConnectionsView] OpenConnectionPositioned: daemon response status={response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[ConnectionsView] OpenConnectionPositioned FAILED: {json}");
                item.SetBusy(false);
                MessageBox.Show(
                    $"Failed to open connection \"{item.Name}\".\n\n{json}",
                    "Open Connection Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(json);
            if (result.TryGetProperty("pid", out var pidEl))
                item.ChromePid = pidEl.GetInt32();

            item.Connected = true;
            item.SetBusy(false);
            FileLog.Write($"[ConnectionsView] OpenConnectionPositioned: opened pid={item.ChromePid}");
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnectionPositioned FAILED: daemon not reachable: {ex.Message}");
            item.SetBusy(false);
            MessageBox.Show(
                "cc-browser daemon is not running.\n\nStart it with: cc-browser daemon",
                "Daemon Not Running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnectionPositioned FAILED: {ex.Message}");
            item.SetBusy(false);
            MessageBox.Show(
                $"Failed to open connection \"{item.Name}\".\n\n{ex.Message}",
                "Open Connection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RepositionBrowserWindow(ConnectionItem item, int x, int y, int width, int height)
    {
        FileLog.Write($"[ConnectionsView] RepositionBrowserWindow: name={item.Name}, pid={item.ChromePid}");
        if (item.ChromePid is not int pid) return;

        try
        {
            var proc = Process.GetProcessById(pid);
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                FileLog.Write("[ConnectionsView] RepositionBrowserWindow: no main window handle yet");
                proc.Dispose();
                return;
            }

            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE);
            FileLog.Write($"[ConnectionsView] RepositionBrowserWindow: moved to x={x}, w={width}, h={height}");
            proc.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] RepositionBrowserWindow FAILED: {ex.Message}");
        }
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

    // -----------------------------------------------------------------------
    // Win32 Interop
    // -----------------------------------------------------------------------

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
