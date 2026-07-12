using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the browser-terminal sink must translate each Director up-frame back into
/// the EXACT terminal wire protocol the browser has always spoken, so the browser contract is unchanged when
/// the frames start coming from the tunnel instead of a dialed Director WebSocket.
/// </summary>
public sealed class WebSocketStreamSinkTests
{
    // A stub WebSocket that records what was sent and whether the output was closed. Only the members the sink
    // uses are implemented.
    private sealed class StubWebSocket : WebSocket
    {
        public List<(WebSocketMessageType Type, byte[] Data)> Sent { get; } = new();
        public bool ClosedOutput { get; private set; }
        private WebSocketState _state = WebSocketState.Open;

        public void ForceState(WebSocketState s) => _state = s;

        public override WebSocketState State => _state;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Sent.Add((messageType, buffer.ToArray()));
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
        {
            ClosedOutput = true;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Size_frame_becomes_a_size_json_text_message()
    {
        var ws = new StubWebSocket();
        var sink = new WebSocketStreamSink(ws);

        await sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Size, Cols = 120, Rows = 40 }, default);

        var (type, data) = Assert.Single(ws.Sent);
        Assert.Equal(WebSocketMessageType.Text, type);
        var json = JsonDocument.Parse(data).RootElement;
        Assert.Equal("size", json.GetProperty("type").GetString());
        Assert.Equal(120, json.GetProperty("cols").GetInt32());
        Assert.Equal(40, json.GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task Binary_frame_becomes_a_binary_message_of_the_raw_bytes()
    {
        var ws = new StubWebSocket();
        var sink = new WebSocketStreamSink(ws);
        var payload = Encoding.UTF8.GetBytes("raw pty bytes");

        await sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Binary, Data = payload }, default);

        var (type, data) = Assert.Single(ws.Sent);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task Closed_frame_becomes_a_closed_json_text_message()
    {
        var ws = new StubWebSocket();
        var sink = new WebSocketStreamSink(ws);

        await sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Closed, Reason = "session exited" }, default);

        var (type, data) = Assert.Single(ws.Sent);
        Assert.Equal(WebSocketMessageType.Text, type);
        var json = JsonDocument.Parse(data).RootElement;
        Assert.Equal("closed", json.GetProperty("type").GetString());
        Assert.Equal("session exited", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CompleteAsync_closes_the_output_send_only()
    {
        var ws = new StubWebSocket();
        var sink = new WebSocketStreamSink(ws);

        await sink.CompleteAsync("eof");

        Assert.True(ws.ClosedOutput);
    }

    [Fact]
    public async Task WriteFrameAsync_throws_when_the_socket_is_not_open_so_the_registry_tears_down()
    {
        var ws = new StubWebSocket();
        ws.ForceState(WebSocketState.Closed);
        var sink = new WebSocketStreamSink(ws);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Binary, Data = new byte[] { 1 } }, default));
    }
}
