using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Tests;

/// <summary>
/// Minimal stub implementation of ISessionBackend for testing code paths
/// that need a backend instance but never actually start a process.
/// </summary>
internal sealed class StubSessionBackend : ISessionBackend
{
    public int ProcessId => 0;
    public string Status => "Stub";
    public bool IsRunning => false;
    public bool HasExited => true;
    public CircularTerminalBuffer? Buffer => null;

    // Stub never starts a process, so these interface-required events are never raised.
#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows)
        => throw new NotSupportedException("StubSessionBackend does not support Start.");

    public void Write(byte[] data)
        => throw new NotSupportedException("StubSessionBackend does not support Write.");

    public Task SendTextAsync(string text) => Task.CompletedTask;

    public Task SendEnterAsync() => Task.CompletedTask;

    public void Resize(short cols, short rows) { }

    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

    public void Dispose() { }
}
