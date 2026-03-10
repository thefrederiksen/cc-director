namespace CcDirector.Core.Pipes;

/// <summary>
/// Interface for IPC server that receives hook messages from Claude Code.
/// Implemented by DirectorFileEventWatcher (Windows) and UnixSocketServer (macOS/Linux).
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
