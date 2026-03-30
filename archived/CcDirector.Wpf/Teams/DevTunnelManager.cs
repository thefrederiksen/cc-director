using System.Diagnostics;
using System.Net.Http.Headers;
using Azure.Identity;
using CcDirector.Wpf.Teams.Models;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Manages an Azure Dev Tunnel using the SDK (no CLI dependency).
/// On first run, creates a persistent tunnel with anonymous access.
/// On subsequent runs, reuses the existing tunnel by stored ID.
/// </summary>
public sealed class DevTunnelManager : IAsyncDisposable, IDisposable
{
    private const string DevTunnelsScope = "https://global.rel.tunnels.api.visualstudio.com/.default";
    private static readonly ProductInfoHeaderValue UserAgent = new("CcDirector", "1.0");

    private readonly TeamsBotConfig _config;
    private readonly Action<string> _log;
    private readonly TunnelStateStore _stateStore;

    private TunnelManagementClient? _managementClient;
    private TunnelRelayTunnelHost? _tunnelHost;
    private Tunnel? _tunnel;
    private bool _disposed;

    /// <summary>Public URL assigned by Dev Tunnel.</summary>
    public string? PublicUrl { get; private set; }

    /// <summary>Whether the tunnel host is connected and forwarding traffic.</summary>
    public bool IsRunning => _tunnelHost?.ConnectionStatus == ConnectionStatus.Connected;

    /// <summary>Fires when the public URL becomes available.</summary>
    public event Action<string>? OnUrlAvailable;

    /// <summary>Fires when the tunnel connection drops unexpectedly.</summary>
    public event Action<int>? OnProcessExited;

    public DevTunnelManager(TeamsBotConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
        _stateStore = new TunnelStateStore(config.ExpandedTunnelStatePath, log);
    }

    /// <summary>
    /// Create or reuse a Dev Tunnel and start hosting it in-process.
    /// </summary>
    public async Task StartAsync()
    {
        _log($"[DevTunnel] Starting SDK-based tunnel: name={_config.TunnelName}, port={_config.Port}");

        var credential = new DefaultAzureCredential();
        Func<Task<AuthenticationHeaderValue?>> tokenCallback = async () =>
        {
            var token = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext([DevTunnelsScope]));
            return new AuthenticationHeaderValue("Bearer", token.Token);
        };
        _managementClient = new TunnelManagementClient(
            UserAgent,
            tokenCallback,
            tunnelServiceUri: null);

        _tunnel = await ResolveOrCreateTunnelAsync();

        var traceSource = new TraceSource("DevTunnel", SourceLevels.Warning);
        traceSource.Listeners.Add(new DevTunnelTraceListener(_log));

        _tunnelHost = new TunnelRelayTunnelHost(_managementClient, traceSource);
        _tunnelHost.ConnectionStatusChanged += OnConnectionStatusChanged;

        _log("[DevTunnel] Connecting tunnel host...");
        await _tunnelHost.ConnectAsync(_tunnel);

        ExtractPublicUrl();
        _log($"[DevTunnel] Tunnel host connected, IsRunning={IsRunning}");
    }

    /// <summary>
    /// Stop the tunnel host gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        _log("[DevTunnel] Stopping tunnel host");

        if (_tunnelHost is not null)
        {
            _tunnelHost.ConnectionStatusChanged -= OnConnectionStatusChanged;
            await _tunnelHost.DisposeAsync();
            _tunnelHost = null;
        }

        PublicUrl = null;
        _log("[DevTunnel] Tunnel host stopped");
    }

    private async Task<Tunnel> ResolveOrCreateTunnelAsync()
    {
        if (_managementClient is null)
            throw new InvalidOperationException("Management client not initialized");

        var state = _stateStore.Load();
        if (state is not null)
        {
            _log($"[DevTunnel] Found saved tunnel state: id={state.TunnelId}, cluster={state.ClusterId}");

            var lookup = new Tunnel
            {
                TunnelId = state.TunnelId,
                ClusterId = state.ClusterId,
            };

            var options = new TunnelRequestOptions { IncludePorts = true };

            Tunnel? fetched;
            try
            {
                fetched = await _managementClient.GetTunnelAsync(lookup, options, CancellationToken.None);
            }
            catch (Exception ex) when (ex.Message.Contains("404") || ex.Message.Contains("Not Found") || ex.Message.Contains("not found"))
            {
                // Tunnel was deleted externally - clear saved state and fall through to create a new one
                _log("[DevTunnel] Saved tunnel no longer exists (deleted externally), creating new one");
                _stateStore.Delete();
                fetched = null;
            }

            if (fetched is not null)
            {
                _log($"[DevTunnel] Reusing existing tunnel: id={fetched.TunnelId}, name={fetched.Name}");
                await EnsurePortExistsAsync(fetched);
                return fetched;
            }

            _log("[DevTunnel] Saved tunnel returned null, creating new one");
            _stateStore.Delete();
        }

        return await CreateTunnelAsync();
    }

    private async Task<Tunnel> CreateTunnelAsync()
    {
        if (_managementClient is null)
            throw new InvalidOperationException("Management client not initialized");

        _log($"[DevTunnel] Creating new tunnel: name={_config.TunnelName}");

        var tunnel = new Tunnel
        {
            Name = _config.TunnelName,
            AccessControl = new TunnelAccessControl(
            [
                new TunnelAccessControlEntry
                {
                    Type = TunnelAccessControlEntryType.Anonymous,
                    Subjects = [],
                    Scopes = [TunnelAccessScopes.Connect],
                },
            ]),
        };

        var options = new TunnelRequestOptions
        {
            IncludePorts = true,
            TokenScopes = [TunnelAccessScopes.Host],
        };

        var created = await _managementClient.CreateTunnelAsync(tunnel, options, CancellationToken.None);
        _log($"[DevTunnel] Tunnel created: id={created.TunnelId}, cluster={created.ClusterId}");

        _stateStore.Save(new TunnelStateStore.TunnelState(created.TunnelId!, created.ClusterId!));

        await EnsurePortExistsAsync(created);
        return created;
    }

    private async Task EnsurePortExistsAsync(Tunnel tunnel)
    {
        if (_managementClient is null)
            throw new InvalidOperationException("Management client not initialized");

        var portNumber = (ushort)_config.Port;

        if (tunnel.Ports is not null)
        {
            foreach (var p in tunnel.Ports)
            {
                if (p.PortNumber == portNumber)
                {
                    _log($"[DevTunnel] Port {portNumber} already exists on tunnel");
                    return;
                }
            }
        }

        _log($"[DevTunnel] Adding port {portNumber} to tunnel");

        var port = new TunnelPort
        {
            PortNumber = portNumber,
            Protocol = TunnelProtocol.Http,
            AccessControl = new TunnelAccessControl(
            [
                new TunnelAccessControlEntry
                {
                    Type = TunnelAccessControlEntryType.Anonymous,
                    Subjects = [],
                    Scopes = [TunnelAccessScopes.Connect],
                },
            ]),
        };

        await _managementClient.CreateTunnelPortAsync(tunnel, port, null, CancellationToken.None);
        _log($"[DevTunnel] Port {portNumber} added");
    }

    private void ExtractPublicUrl()
    {
        if (_tunnel?.Endpoints is null || _tunnel.Endpoints.Length == 0)
        {
            _log("[DevTunnel] WARNING: No endpoints available after connect");
            return;
        }

        foreach (var endpoint in _tunnel.Endpoints)
        {
            var uri = TunnelEndpoint.GetPortUri(endpoint, _config.Port);
            if (uri is not null)
            {
                PublicUrl = uri.ToString().TrimEnd('/');
                break;
            }

            if (endpoint.PortUriFormat is not null)
            {
                PublicUrl = endpoint.PortUriFormat
                    .Replace(TunnelEndpoint.PortToken, _config.Port.ToString())
                    .TrimEnd('/');
                break;
            }

            if (endpoint.TunnelUri is not null)
            {
                PublicUrl = endpoint.TunnelUri.TrimEnd('/');
                break;
            }
        }

        if (string.IsNullOrEmpty(PublicUrl))
        {
            _log("[DevTunnel] WARNING: Could not extract public URL from tunnel endpoints");
            return;
        }

        _log($"[DevTunnel] Public URL: {PublicUrl}");
        OnUrlAvailable?.Invoke(PublicUrl);
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChangedEventArgs e)
    {
        _log($"[DevTunnel] Connection status: {e.PreviousStatus} -> {e.Status}");

        if (e.Status == ConnectionStatus.Disconnected && e.DisconnectException is not null)
        {
            _log($"[DevTunnel] Disconnected with error: {e.DisconnectException.Message}");
            OnProcessExited?.Invoke(-1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_tunnelHost is not null)
        {
            _tunnelHost.ConnectionStatusChanged -= OnConnectionStatusChanged;
            await _tunnelHost.DisposeAsync();
            _tunnelHost = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_tunnelHost is not null)
        {
            _tunnelHost.ConnectionStatusChanged -= OnConnectionStatusChanged;
            // Fire-and-forget async dispose; callers should prefer DisposeAsync()
            _ = _tunnelHost.DisposeAsync();
            _tunnelHost = null;
        }
    }

    /// <summary>
    /// Forwards TraceSource messages from the SDK to FileLog.
    /// </summary>
    private sealed class DevTunnelTraceListener : TraceListener
    {
        private readonly Action<string> _log;

        public DevTunnelTraceListener(Action<string> log) => _log = log;

        public override void Write(string? message)
        {
            if (message is not null)
                _log($"[DevTunnel-SDK] {message}");
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
                _log($"[DevTunnel-SDK] {message}");
        }
    }
}
