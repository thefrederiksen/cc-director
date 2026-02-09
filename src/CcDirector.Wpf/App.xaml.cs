using System.IO;
using System.Text.Json;
using System.Windows;
using CcDirector.Core.Configuration;
using CcDirector.Core.Hooks;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf;

public partial class App : Application
{
    public SessionManager SessionManager { get; private set; } = null!;
    public AgentOptions Options { get; private set; } = null!;
    public List<RepositoryConfig> Repositories { get; private set; } = new();
    public RepositoryRegistry RepositoryRegistry { get; private set; } = null!;
    public DirectorPipeServer PipeServer { get; private set; } = null!;
    public EventRouter EventRouter { get; private set; } = null!;
    public SessionStateStore SessionStateStore { get; private set; } = null!;

    /// <summary>
    /// Persisted session data loaded on startup, consumed by MainWindow for HWND reattach.
    /// Cleared after MainWindow processes it.
    /// </summary>
    public List<PersistedSession>? RestoredPersistedData { get; set; }

    /// <summary>
    /// Set to true by MainWindow when the user chooses "Keep Sessions" on close.
    /// When true, OnExit detaches consoles instead of killing them.
    /// </summary>
    public bool KeepSessionsOnExit { get; set; }

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance enforcement
        _singleInstanceMutex = new Mutex(true, @"Global\CcDirector_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("CC Director is already running.", "CC Director",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        LoadConfiguration();

        // Initialize repository registry and seed from appsettings
        RepositoryRegistry = new RepositoryRegistry();
        RepositoryRegistry.Load();
        RepositoryRegistry.SeedFrom(Repositories);

        SessionStateStore = new SessionStateStore();

        Action<string> log = msg => System.Diagnostics.Debug.WriteLine($"[CcDirector] {msg}");

        SessionManager = new SessionManager(Options, log);
        SessionManager.ScanForOrphans();

        // Restore persisted sessions (before pipe server starts, so EventRouter can find them)
        var restored = SessionManager.LoadPersistedSessions(SessionStateStore);
        RestoredPersistedData = restored.Select(r => r.Persisted).ToList();
        SessionStateStore.Clear(); // prevent stale re-reads on crash

        // Start pipe server and event router
        PipeServer = new DirectorPipeServer(log);
        EventRouter = new EventRouter(SessionManager, log);
        PipeServer.OnMessageReceived += EventRouter.Route;
        PipeServer.Start();

        // Install hooks (fire-and-forget, non-blocking startup)
        _ = InstallHooksAsync(log);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (KeepSessionsOnExit)
        {
            // Sessions were already saved and detached by MainWindow.OnClosing
            EmbeddedConsoleHost.DetachAll();
        }
        else
        {
            SessionStateStore?.Clear();
            EmbeddedConsoleHost.DisposeAll();
            SessionManager?.KillAllSessionsAsync().GetAwaiter().GetResult();
        }

        PipeServer?.Dispose();
        SessionManager?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private async Task InstallHooksAsync(Action<string> log)
    {
        try
        {
            HookRelayScript.EnsureWritten();
            log($"Hook relay script written to {HookRelayScript.ScriptPath}");
            await HookInstaller.InstallAsync(HookRelayScript.ScriptPath, log);
        }
        catch (Exception ex)
        {
            log($"Failed to install hooks: {ex.Message}");
        }
    }

    private void LoadConfiguration()
    {
        Options = new AgentOptions();
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
        {
            WriteDefaultConfig(configPath);
        }

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Agent", out var agentSection))
            {
                if (agentSection.TryGetProperty("ClaudePath", out var cp))
                    Options.ClaudePath = cp.GetString() ?? "claude";
                if (agentSection.TryGetProperty("DefaultBufferSizeBytes", out var bs))
                    Options.DefaultBufferSizeBytes = bs.GetInt32();
                if (agentSection.TryGetProperty("GracefulShutdownTimeoutSeconds", out var gs))
                    Options.GracefulShutdownTimeoutSeconds = gs.GetInt32();
            }

            if (doc.RootElement.TryGetProperty("Repositories", out var reposSection))
            {
                Repositories = JsonSerializer.Deserialize<List<RepositoryConfig>>(
                    reposSection.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<RepositoryConfig>();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
        }
    }

    private static void WriteDefaultConfig(string configPath)
    {
        const string defaultConfig = """
            {
              "Agent": {
                "ClaudePath": "claude",
                "DefaultBufferSizeBytes": 2097152,
                "GracefulShutdownTimeoutSeconds": 5
              },
              "Repositories": []
            }
            """;

        try
        {
            File.WriteAllText(configPath, defaultConfig);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to write default config: {ex.Message}");
        }
    }
}
