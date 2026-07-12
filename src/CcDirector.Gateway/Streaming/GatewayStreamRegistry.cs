using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): the Gateway's registry of live up-streams, keyed by the
/// fresh stream id the Gateway minted when it opened the stream over the tunnel. It maps a stream id to the
/// browser-facing <see cref="IStreamSink"/>, and its <see cref="ConsumeAsync"/> is what the hub's StreamUp
/// method calls to pump the Director's frames into that sink.
///
/// Three of the Architect's four binding refinements live here (the fourth, bounded frames, lives in the
/// producer and the contract):
///  1. Backpressure (ruling 1): <see cref="ConsumeAsync"/> is pull-then-forward - it awaits the sink write of
///     one frame BEFORE pulling the next frame from the Director's up-stream. A slow sink therefore stalls the
///     pull, which (with a small SignalR StreamBufferCapacity) blocks the Director's producer. Bounded memory
///     end to end; this is the whole reason for native streaming, not an optimization.
///  3. close-stream / lifecycle races (ruling 3): a StreamUp that arrives after its sink is already gone is a
///     safe no-op (nothing to pump into); a StreamUp that never arrives after an open is torn down by a
///     timeout so a sink is never left waiting forever. Stream ids are fresh Guids and never reused, so no
///     late frame can alias a later stream.
/// </summary>
public sealed class GatewayStreamRegistry
{
    private sealed class Entry
    {
        public required IStreamSink Sink { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Timer? OpenTimeout { get; set; }
        public int Claimed;
    }

    private readonly ConcurrentDictionary<string, Entry> _streams = new();

    /// <summary>How long a registered sink waits for its StreamUp before it is torn down (ruling 3).</summary>
    private readonly TimeSpan _openTimeout;

    /// <param name="openTimeout">
    /// The StreamUp-never-arrives teardown window. Null uses ten seconds. A test seam; production passes null.
    /// </param>
    public GatewayStreamRegistry(TimeSpan? openTimeout = null)
    {
        _openTimeout = openTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The number of live (registered, not yet torn down) streams. For diagnostics and tests.</summary>
    public int LiveStreamCount => _streams.Count;

    /// <summary>
    /// Register the sink for a stream the Gateway just opened over the tunnel (Phase 2 callers). Arms the
    /// StreamUp-never-arrives timeout. Returns a token that fires when the stream is torn down (a browser
    /// disconnect via <see cref="Close"/>, the timeout, or natural completion) so the caller can stop waiting.
    /// A duplicate stream id is a fail-loud error (ids are fresh Guids and must be unique).
    /// </summary>
    public CancellationToken Register(string streamId, IStreamSink sink)
    {
        if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required", nameof(streamId));
        if (sink is null) throw new ArgumentNullException(nameof(sink));

        var entry = new Entry { Sink = sink, Cts = new CancellationTokenSource() };
        if (!_streams.TryAdd(streamId, entry))
            throw new InvalidOperationException($"stream id already registered (ids must be fresh and unique): {streamId}");

        entry.OpenTimeout = new Timer(_ => TimeoutUnclaimed(streamId), null, _openTimeout, Timeout.InfiniteTimeSpan);
        FileLog.Write($"[GatewayStreamRegistry] registered stream {streamId} (live={_streams.Count})");
        return entry.Cts.Token;
    }

    // StreamUp-never-arrives (ruling 3): if nothing claimed this stream within the window, tear the sink down
    // so it is not left waiting forever (the Director died mid-open, or the open never produced).
    private void TimeoutUnclaimed(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var entry)) return;
        if (Interlocked.CompareExchange(ref entry.Claimed, 0, 0) == 1) return; // already claimed - fine
        FileLog.Write($"[GatewayStreamRegistry] stream {streamId} never received its StreamUp within the open window; tearing down");
        Teardown(streamId, "stream did not start");
    }

    /// <summary>
    /// Consume a Director up-stream into its registered sink (called by the hub's StreamUp method). Pull-then-
    /// forward: awaits the sink write of each frame before pulling the next (the backpressure invariant). Ends
    /// on the Closed frame, natural completion, cancellation (a browser disconnect via <see cref="Close"/>), or
    /// the hub connection aborting. If no sink is registered for this id the frames are dropped and the method
    /// returns at once (StreamUp-after-sink-gone: the browser is already gone).
    /// </summary>
    public async Task ConsumeAsync(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames, CancellationToken hubCancellation)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));

        if (!_streams.TryGetValue(streamId, out var entry))
        {
            // StreamUp-after-sink-gone (ruling 3): the browser disconnected before the frames arrived. There
            // is nothing to pump into; return immediately so the Director's producer is not consumed further.
            FileLog.Write($"[GatewayStreamRegistry] StreamUp for unknown/closed stream {streamId}; dropping (sink already gone)");
            return;
        }

        Interlocked.Exchange(ref entry.Claimed, 1);
        entry.OpenTimeout?.Dispose();
        entry.OpenTimeout = null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token, hubCancellation);
        string? reason = null;
        try
        {
            await foreach (var frame in frames.WithCancellation(linked.Token))
            {
                // Pull-then-forward (ruling 1): await this frame's delivery BEFORE the loop pulls the next one.
                await entry.Sink.WriteFrameAsync(frame, linked.Token);
                if (frame.Kind == DirectorStreamFrameType.Closed)
                {
                    reason = frame.Reason;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected teardown: the browser disconnected (Close) or the hub connection aborted. Not a fault -
            // the finally tears the stream down normally.
            reason ??= "cancelled";
        }
        finally
        {
            Teardown(streamId, reason);
        }
    }

    /// <summary>Tear a stream down from the browser-facing side (the browser disconnected). Idempotent.</summary>
    public void Close(string streamId) => Teardown(streamId, "closed");

    private void Teardown(string streamId, string? reason)
    {
        if (!_streams.TryRemove(streamId, out var entry)) return;
        entry.OpenTimeout?.Dispose();
        try { entry.Cts.Cancel(); } catch { /* already disposed */ }
        _ = SafeCompleteAsync(entry.Sink, reason);
        entry.Cts.Dispose();
        FileLog.Write($"[GatewayStreamRegistry] torn down stream {streamId} (reason={reason ?? "none"}, live={_streams.Count})");
    }

    private static async Task SafeCompleteAsync(IStreamSink sink, string? reason)
    {
        try { await sink.CompleteAsync(reason); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamRegistry] sink CompleteAsync failed: {ex.Message}"); }
    }
}
