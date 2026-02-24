using System.Runtime.InteropServices;

namespace CcDirector.Core.Pipes;

/// <summary>
/// Interface for IPC server that receives hook messages from Claude Code.
/// Windows uses named pipes, Unix uses domain sockets.
/// </summary>
public interface IDirectorServer : IDisposable
{
    /// <summary>Raised when a complete message is received and deserialized.</summary>
    event Action<PipeMessage>? OnMessageReceived;

    /// <summary>Start accepting connections.</summary>
    void Start();

    /// <summary>Stop accepting connections.</summary>
    void Stop();
}

/// <summary>
/// Factory for creating platform-appropriate IDirectorServer.
/// </summary>
public static class DirectorServerFactory
{
    /// <summary>
    /// Create a director server appropriate for the current platform.
    /// Windows: Named pipe server.
    /// Unix: Unix domain socket server.
    /// </summary>
    public static IDirectorServer Create(Action<string>? log = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DirectorPipeServer(log);
        }
        else
        {
            return new UnixSocketServer(log);
        }
    }
}
