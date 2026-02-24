using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Listens on a Unix domain socket for JSON messages from Claude Code hook relays.
/// Each client writes one JSON line, then disconnects.
/// Used on macOS and Linux only.
/// </summary>
public sealed class UnixSocketServer : IDirectorServer
{
    /// <summary>
    /// Path to the Unix domain socket.
    /// Located in ~/.cc_director/director.sock
    /// </summary>
    public static readonly string SocketPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cc_director",
        "director.sock");

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;
    private Socket? _listener;
    private Task? _acceptLoop;

    /// <summary>Raised when a complete message is received and deserialized.</summary>
    public event Action<PipeMessage>? OnMessageReceived;

    public UnixSocketServer(Action<string>? log = null)
    {
        _log = log;
    }

    public void Start()
    {
        // Ensure directory exists
        var socketDir = Path.GetDirectoryName(SocketPath)!;
        Directory.CreateDirectory(socketDir);

        // Remove stale socket file if it exists
        if (File.Exists(SocketPath))
        {
            try
            {
                File.Delete(SocketPath);
                _log?.Invoke($"Removed stale socket file: {SocketPath}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Failed to remove stale socket file: {ex.Message}");
                throw;
            }
        }

        // Create Unix domain socket
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(10);

        _log?.Invoke($"Unix socket server listening on {SocketPath}");

        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts.Cancel();

        try
        {
            _listener?.Close();
        }
        catch { }

        // Clean up socket file
        try
        {
            if (File.Exists(SocketPath))
                File.Delete(SocketPath);
        }
        catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            Socket? client = null;
            try
            {
                client = await _listener.AcceptAsync(ct);

                // Fire-and-forget client handling so we can accept next connection immediately
                var clientSocket = client;
                client = null; // prevent disposal below
                _ = HandleClientAsync(clientSocket);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                // Socket was closed during shutdown
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Socket accept error: {ex.Message}");
                client?.Dispose();
                await Task.Delay(100, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        try
        {
            using (client)
            using (var stream = new NetworkStream(client, ownsSocket: false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var line = await reader.ReadLineAsync();

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var msg = JsonSerializer.Deserialize<PipeMessage>(line);
                    if (msg != null)
                    {
                        msg.ReceivedAt = DateTimeOffset.UtcNow;
                        OnMessageReceived?.Invoke(msg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Socket client handling error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
