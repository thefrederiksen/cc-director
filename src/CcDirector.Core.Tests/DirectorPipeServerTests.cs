using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Pipes;
using Xunit;

namespace CcDirector.Core.Tests;

public class DirectorPipeServerTests : IDisposable
{
    private readonly string _testPipeName;
    private readonly DirectorPipeServer _server;

    public DirectorPipeServerTests()
    {
        _testPipeName = $"CC_Test_{Guid.NewGuid():N}";
        _server = new DirectorPipeServer(_testPipeName, msg => System.Diagnostics.Debug.WriteLine($"[Test] {msg}"));
    }

    [Fact]
    public async Task Server_ReceivesJsonMessage()
    {
        _server.Start();

        PipeMessage? received = null;
        var tcs = new TaskCompletionSource<PipeMessage>();

        _server.OnMessageReceived += msg => tcs.TrySetResult(msg);

        // Connect as a client and send JSON
        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "test-session-123",
            Cwd = @"C:\test"
        };

        var json = JsonSerializer.Serialize(msg);

        using (var client = new NamedPipeClientStream(".", _testPipeName, PipeDirection.Out))
        {
            await client.ConnectAsync(2000);
            var writer = new StreamWriter(client, Encoding.UTF8);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
        }

        // Wait for the server to process
        var result = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(result == tcs.Task, "Timed out waiting for pipe message");

        received = await tcs.Task;
        Assert.Equal("Stop", received.HookEventName);
        Assert.Equal("test-session-123", received.SessionId);
        Assert.Equal(@"C:\test", received.Cwd);
    }

    [Fact]
    public async Task Server_HandlesMultipleClients()
    {
        _server.Start();

        var receivedMessages = new System.Collections.Concurrent.ConcurrentBag<PipeMessage>();
        var countdownEvent = new CountdownEvent(3);

        _server.OnMessageReceived += msg =>
        {
            receivedMessages.Add(msg);
            countdownEvent.Signal();
        };

        // Send 3 messages sequentially (pipe server handles one connection at a time per instance)
        for (int i = 0; i < 3; i++)
        {
            var msg = new PipeMessage
            {
                HookEventName = $"Event{i}",
                SessionId = $"session-{i}"
            };

            var json = JsonSerializer.Serialize(msg);

            using (var client = new NamedPipeClientStream(".", _testPipeName, PipeDirection.Out))
            {
                await client.ConnectAsync(2000);
                var writer = new StreamWriter(client, Encoding.UTF8);
                await writer.WriteLineAsync(json);
                await writer.FlushAsync();
            }

            // Small delay to let the server process
            await Task.Delay(100);
        }

        var signaled = await Task.Run(() => countdownEvent.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(signaled, $"Expected 3 messages, got {receivedMessages.Count}");
        Assert.Equal(3, receivedMessages.Count);
    }

    public void Dispose()
    {
        _server.Dispose();
    }
}
