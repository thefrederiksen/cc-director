using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Listens on a named pipe for JSON messages from Claude Code hook relays.
/// Each client writes one JSON line, then disconnects.
/// Windows only - use UnixSocketServer on macOS/Linux.
/// </summary>
public sealed class DirectorPipeServer : IDirectorServer
{
    public const string PipeName = "CC_ClaudeDirector";

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;
    private Task? _acceptLoop;

    /// <summary>Raised when a complete message is received and deserialized.</summary>
    public event Action<PipeMessage>? OnMessageReceived;

    public DirectorPipeServer(Action<string>? log = null)
    {
        _log = log;
    }

    public void Start()
    {
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Fire-and-forget client handling so we can accept next connection immediately
                var clientServer = server;
                server = null; // prevent disposal below
                _ = HandleClientAsync(clientServer);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Pipe accept error: {ex.Message}");
                server?.Dispose();
                await Task.Delay(100, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8);
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
            _log?.Invoke($"Pipe client handling error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
