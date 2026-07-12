using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): the browser-facing consumer of one up-stream's frames.
/// The Gateway opens a stream over the tunnel, registers a sink for it in <see cref="GatewayStreamRegistry"/>,
/// and the Director's frames flow up and out through this sink to the browser. Phase 2 provides the real
/// sinks - a WebSocket writer for the live terminal, a buffered HTTP response writer for a file or screenshot
/// read; Phase 0 exercises the registry with a test sink.
///
/// The backpressure invariant (Architect ruling 1) lives in the registry, not here: the registry awaits
/// <see cref="WriteFrameAsync"/> for one frame BEFORE it pulls the next frame from the Director's up-stream,
/// so a slow sink stalls the pull, which fills the small SignalR channel, which blocks the Director's
/// producer - end-to-end backpressure with bounded memory. A sink implementation therefore does NOT need to
/// buffer; it just writes the one frame it is handed and returns when that write has drained.
/// </summary>
public interface IStreamSink
{
    /// <summary>
    /// Write one frame to the browser-facing transport and return only when it has drained (so the registry
    /// does not pull the next frame until this one is delivered). Throwing signals the sink is broken; the
    /// registry tears the stream down.
    /// </summary>
    Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken);

    /// <summary>
    /// Called exactly once when the stream ends - a natural end / end-of-file, a browser disconnect, or a
    /// teardown - so the sink can close its transport. <paramref name="reason"/> is the Closed frame's reason
    /// when the stream ended naturally, or a teardown reason otherwise; null when not known.
    /// </summary>
    Task CompleteAsync(string? reason);
}
