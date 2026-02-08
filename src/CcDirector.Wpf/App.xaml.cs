using System.IO;
using System.Text.Json;
using System.Windows;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;

namespace CcDirector.Wpf;

public partial class App : Application
{
    public SessionManager SessionManager { get; private set; } = null!;
    public AgentOptions Options { get; private set; } = null!;
    public List<RepositoryConfig> Repositories { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LoadConfiguration();

        SessionManager = new SessionManager(Options, msg =>
            System.Diagnostics.Debug.WriteLine($"[SessionManager] {msg}"));

        SessionManager.ScanForOrphans();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionManager.KillAllSessionsAsync().GetAwaiter().GetResult();
        SessionManager.Dispose();
        base.OnExit(e);
    }

    private void LoadConfiguration()
    {
        Options = new AgentOptions();
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
            return;

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
}
