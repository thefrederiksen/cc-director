using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the browser-facing sink for a FINITE byte read (a session file or a
/// screenshot) served over the tunnel up-stream. Wraps the browser <see cref="HttpResponse"/>: each Binary
/// up-frame is written to the response body and flushed; the finishing Closed/eof frame just ends the stream.
///
/// Header ordering (Architect ruling 4): the open command's Ok reply carries the total size and content type
/// (<see cref="OpenReadResponse"/>) so the Gateway can set Content-Type and Content-Length BEFORE the first
/// body byte. But the Director's first Binary up-frame can race the open reply on the shared connection, so
/// this sink GATES the first write on <see cref="ApplyMetadata"/>: <see cref="WriteFrameAsync"/> awaits the
/// metadata signal before it writes, and the endpoint calls <see cref="ApplyMetadata"/> exactly once when the
/// open returns Ok (or <see cref="Fail"/> when the open failed with nothing written yet). No frame can reach
/// the response before its headers, and the backpressure invariant holds the producer at the gate meanwhile.
/// </summary>
public sealed class HttpResponseStreamSink : IStreamSink
{
    private readonly HttpResponse _response;
    private readonly TaskCompletionSource _metadataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _failed;

    public HttpResponseStreamSink(HttpResponse response) => _response = response ?? throw new ArgumentNullException(nameof(response));

    /// <summary>
    /// Apply the finite read's headers from the open command's Ok body and OPEN the write gate. Called exactly
    /// once, before any body byte can be written (WriteFrameAsync awaits this). Safe to call with a null body
    /// (headers stay unset; the response is then chunked).
    /// </summary>
    public void ApplyMetadata(OpenReadResponse? meta)
    {
        if (meta?.ContentType is { Length: > 0 } ctype)
            _response.ContentType = ctype;
        if (meta?.TotalBytes is { } total && total >= 0)
            _response.ContentLength = total;
        _metadataReady.TrySetResult();
    }

    /// <summary>The open failed (or there was no stream); release the gate so a racing WriteFrameAsync unwinds
    /// WITHOUT writing to the body (the endpoint writes the error status/body instead).</summary>
    public void Fail()
    {
        _failed = true;
        _metadataReady.TrySetResult();
    }

    public async Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
    {
        // Gate the FIRST byte on the headers (ruling 4). Once the gate is open this returns immediately, so
        // later frames pass straight through. A slow gate holds the producer (backpressure), never buffers.
        await _metadataReady.Task.WaitAsync(cancellationToken);
        if (_failed)
            throw new InvalidOperationException("finite read open failed; no body to write");

        if (frame.Kind == DirectorStreamFrameType.Binary)
        {
            var data = frame.Data ?? Array.Empty<byte>();
            if (data.Length > 0)
            {
                await _response.Body.WriteAsync(data, cancellationToken);
                await _response.Body.FlushAsync(cancellationToken);
            }
        }
        // A finite read never emits Size frames; a Closed/eof frame just ends the stream (CompleteAsync).
    }

    public Task CompleteAsync(string? reason)
    {
        // The response body is completed by the request pipeline when the endpoint handler returns; there is
        // nothing to close here. Log an abnormal end so a truncated download is visible, never silent.
        if (!string.IsNullOrEmpty(reason) && reason != "eof" && reason != "closed")
            FileLog.Write($"[HttpResponseStreamSink] finite read ended early: {reason}");
        return Task.CompletedTask;
    }
}
