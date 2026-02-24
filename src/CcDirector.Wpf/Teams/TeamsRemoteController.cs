using System.IO;
using System.Windows.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;
using CcDirector.Wpf.Teams.Models;
using CcDirector.Wpf.Teams.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Main controller for Teams bot integration.
/// Manages WebApplication, Dev Tunnel, and session state.
/// </summary>
public sealed class TeamsRemoteController : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly TeamsBotConfig _config;
    private readonly TeamsWhitelist _whitelist;
    private readonly IReadOnlyList<RepositoryConfig> _repositories;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Session, TerminalControl?> _getTerminalControl;
    private readonly Action<string> _log;

    private WebApplication? _webApp;
    private DevTunnelManager? _tunnelManager;
    private IBotFrameworkHttpAdapter? _adapter;
    private IBot? _bot;
    private bool _disposed;

    // State
    private Session? _activeSession;
    private TeamsUserState? _userState;
    private OutputQuiescenceMonitor? _quiescenceMonitor;
    private readonly object _stateLock = new();

    /// <summary>The active session for remote commands.</summary>
    public Session? ActiveSession
    {
        get { lock (_stateLock) return _activeSession; }
    }

    /// <summary>Public URL from Dev Tunnel.</summary>
    public string? PublicUrl => _tunnelManager?.PublicUrl;

    /// <summary>Whether the controller is running.</summary>
    public bool IsRunning => _webApp != null;

    public TeamsRemoteController(
        SessionManager sessionManager,
        TeamsBotConfig config,
        IReadOnlyList<RepositoryConfig> repositories,
        Dispatcher dispatcher,
        Func<Session, TerminalControl?> getTerminalControl,
        Action<string> log)
    {
        _sessionManager = sessionManager;
        _config = config;
        _repositories = repositories;
        _dispatcher = dispatcher;
        _getTerminalControl = getTerminalControl;
        _log = log;

        _whitelist = new TeamsWhitelist(config, log);
    }

    /// <summary>
    /// Start the Teams bot server and Dev Tunnel.
    /// </summary>
    public async Task StartAsync()
    {
        _log("[TeamsRemote] Starting...");

        // Load whitelist
        _whitelist.Load();

        // Start Dev Tunnel
        _tunnelManager = new DevTunnelManager(_config, _log);
        _tunnelManager.OnProcessExited += OnTunnelExited;

        try
        {
            await _tunnelManager.StartAsync();
        }
        catch (Exception ex)
        {
            _log($"[TeamsRemote] Dev Tunnel start FAILED: {ex.Message}");
            _log("[TeamsRemote] Continuing without tunnel - bot will only be accessible locally");
        }

        // Build and start web application
        var builder = WebApplication.CreateSlimBuilder();

        // Configure Bot Framework authentication
        builder.Services.AddSingleton<BotFrameworkAuthentication>(sp =>
        {
            if (string.IsNullOrEmpty(_config.MicrosoftAppId))
            {
                // No credentials - local testing only
                return new AllowAllAuth();
            }

            return new PasswordServiceClientCredentialFactory(
                _config.MicrosoftAppId,
                _config.MicrosoftAppPassword);
        });

        builder.Services.AddSingleton<IBotFrameworkHttpAdapter>(sp =>
        {
            var auth = sp.GetRequiredService<BotFrameworkAuthentication>();
            return new CloudAdapter(auth);
        });

        builder.Services.AddSingleton<IBot>(sp =>
        {
            return new TeamsBotHandler(
                _sessionManager,
                _whitelist,
                _repositories,
                _dispatcher,
                _getTerminalControl,
                () => ActiveSession,
                SetActiveSession,
                ClearActiveSession,
                UpdateUserState,
                StartQuiescenceMonitoring,
                _log);
        });

        _webApp = builder.Build();

        _webApp.MapPost("/api/messages", async (HttpContext context, IBotFrameworkHttpAdapter adapter, IBot bot) =>
        {
            await adapter.ProcessAsync(context.Request, context.Response, bot);
        });

        // Health check endpoint
        _webApp.MapGet("/health", () => "OK");

        // Start on configured port
        _webApp.Urls.Add($"http://localhost:{_config.Port}");

        _adapter = _webApp.Services.GetService<IBotFrameworkHttpAdapter>();
        _bot = _webApp.Services.GetService<IBot>();

        await _webApp.StartAsync();

        var url = _tunnelManager?.PublicUrl ?? $"http://localhost:{_config.Port}";
        _log($"[TeamsRemote] Bot started, URL: {url}/api/messages");
    }

    /// <summary>
    /// Stop the Teams bot server and Dev Tunnel.
    /// </summary>
    public async Task StopAsync()
    {
        _log("[TeamsRemote] Stopping...");

        if (_webApp != null)
        {
            await _webApp.StopAsync();
            await _webApp.DisposeAsync();
            _webApp = null;
        }

        if (_tunnelManager != null)
        {
            await _tunnelManager.StopAsync();
            _tunnelManager.Dispose();
            _tunnelManager = null;
        }

        _quiescenceMonitor?.Dispose();
        _quiescenceMonitor = null;

        _log("[TeamsRemote] Stopped");
    }

    /// <summary>
    /// Set the active session for remote commands.
    /// </summary>
    public void SetActiveSession(Session session)
    {
        lock (_stateLock)
        {
            _activeSession = session;
        }

        var repoName = Path.GetFileName(session.RepoPath);
        _log($"[TeamsRemote] Active session: {session.CustomName ?? repoName} ({session.Id.ToString().Substring(0, 8)})");
    }

    /// <summary>
    /// Clear the active session.
    /// </summary>
    public void ClearActiveSession()
    {
        lock (_stateLock)
        {
            _activeSession = null;
            _quiescenceMonitor?.StopMonitoring();
        }
        _log("[TeamsRemote] Active session cleared");
    }

    private void UpdateUserState(TeamsUserState state)
    {
        lock (_stateLock)
        {
            _userState = state;
        }
    }

    private void StartQuiescenceMonitoring()
    {
        var session = ActiveSession;
        if (session == null)
            return;

        _quiescenceMonitor?.Dispose();
        _quiescenceMonitor = new OutputQuiescenceMonitor(
            session,
            _config.NotificationQuiescenceMs,
            OnSessionQuiescent,
            _log);
        _quiescenceMonitor.StartMonitoring();
    }

    private void OnSessionQuiescent(Session session)
    {
        _log($"[TeamsRemote] Session {session.Id} quiescent, sending notification");

        // Send proactive message if we have a conversation reference
        TeamsUserState? userState;
        lock (_stateLock)
        {
            userState = _userState;
        }

        if (userState?.ConversationReference == null || _adapter == null || _bot == null)
        {
            _log("[TeamsRemote] Cannot send proactive message - no conversation reference or adapter");
            return;
        }

        var repoName = Path.GetFileName(session.RepoPath);
        var displayName = session.CustomName ?? repoName;
        var message = $"[OK] Task complete in {displayName}";

        _ = SendProactiveMessageAsync(userState.ConversationReference, message);
    }

    private async Task SendProactiveMessageAsync(ConversationReference reference, string message)
    {
        try
        {
            if (_adapter is CloudAdapter cloudAdapter)
            {
                await cloudAdapter.ContinueConversationAsync(
                    _config.MicrosoftAppId,
                    reference,
                    async (turnContext, ct) =>
                    {
                        await turnContext.SendActivityAsync(MessageFactory.Text(message), ct);
                    },
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log($"[TeamsRemote] Proactive message FAILED: {ex.Message}");
        }
    }

    private void OnTunnelExited(int exitCode)
    {
        _log($"[TeamsRemote] Dev Tunnel exited with code {exitCode}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _quiescenceMonitor?.Dispose();

        if (_webApp != null)
        {
            // Fire-and-forget async cleanup - avoid blocking Dispose with sync-over-async
            try
            {
                var stopTask = _webApp.StopAsync();
                if (!stopTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    _log("[TeamsRemote] WebApp stop timed out");
                }
            }
            catch (Exception ex)
            {
                _log($"[TeamsRemote] WebApp stop error: {ex.Message}");
            }
        }

        _tunnelManager?.Dispose();
    }

    /// <summary>
    /// Simple auth that allows all requests (for local testing without Azure credentials).
    /// </summary>
    private sealed class AllowAllAuth : BotFrameworkAuthentication
    {
        public override Task<AuthenticateRequestResult> AuthenticateRequestAsync(
            Activity activity, string authHeader, CancellationToken ct)
        {
            return Task.FromResult(new AuthenticateRequestResult
            {
                ClaimsIdentity = new System.Security.Claims.ClaimsIdentity(),
                ConnectorFactory = new SimpleConnectorFactory()
            });
        }

        public override Task<AuthenticateRequestResult> AuthenticateStreamingRequestAsync(
            string authHeader, string channelIdHeader, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public override ConnectorFactory CreateConnectorFactory(
            System.Security.Claims.ClaimsIdentity claimsIdentity)
        {
            return new SimpleConnectorFactory();
        }

        public override Task<UserTokenClient> CreateUserTokenClientAsync(
            System.Security.Claims.ClaimsIdentity claimsIdentity, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Simple connector factory for local testing.
    /// </summary>
    private sealed class SimpleConnectorFactory : ConnectorFactory
    {
        public override Task<IConnectorClient> CreateAsync(string serviceUrl, string audience, CancellationToken ct)
        {
            throw new NotImplementedException("Connector not available in local mode");
        }
    }

    /// <summary>
    /// Credential factory using app ID and password.
    /// Note: Full authentication implementation pending Azure Bot registration.
    /// Currently returns allow-all for local development.
    /// </summary>
    private sealed class PasswordServiceClientCredentialFactory : BotFrameworkAuthentication
    {
        // Stored for future Azure Bot authentication implementation
        private readonly string _appId;
#pragma warning disable IDE0052 // Remove unread private member - reserved for future Azure auth
        private readonly string _password;
#pragma warning restore IDE0052

        public PasswordServiceClientCredentialFactory(string appId, string password)
        {
            _appId = appId;
            _password = password;
        }

        public override Task<AuthenticateRequestResult> AuthenticateRequestAsync(
            Activity activity, string authHeader, CancellationToken ct)
        {
            return Task.FromResult(new AuthenticateRequestResult
            {
                ClaimsIdentity = new System.Security.Claims.ClaimsIdentity(),
                ConnectorFactory = new SimpleConnectorFactory()
            });
        }

        public override Task<AuthenticateRequestResult> AuthenticateStreamingRequestAsync(
            string authHeader, string channelIdHeader, CancellationToken ct)
        {
            throw new NotImplementedException();
        }

        public override ConnectorFactory CreateConnectorFactory(
            System.Security.Claims.ClaimsIdentity claimsIdentity)
        {
            return new SimpleConnectorFactory();
        }

        public override Task<UserTokenClient> CreateUserTokenClientAsync(
            System.Security.Claims.ClaimsIdentity claimsIdentity, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }
}
