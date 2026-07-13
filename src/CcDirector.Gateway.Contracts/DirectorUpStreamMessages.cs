namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Gateway Cleanup mission, Phase 0: the up-stream primitive. This is the ONE frame type that carries every
/// Director-to-Gateway byte stream over the tunnel - both the open-ended live terminal output AND a finite
/// file/screenshot byte read - keyed by <see cref="StreamId"/>.
///
/// The tunnel is the existing two-way SignalR channel (the Director dials the Gateway's DirectorHub and binds
/// its id with <see cref="DirectorStreamHello"/>). The Director is the SignalR CLIENT, so an up-stream is
/// native client-to-server streaming: the Gateway sends a unary "open" command carrying a fresh
/// <see cref="StreamId"/>, the Director starts a producer and streams these frames UP under that id via the
/// hub's StreamUp method until the Gateway sends a "close-stream" command (the load-bearing stop signal - see
/// the Architect ruling in docs/architecture/gateway-cleanup-phase0-tunnel-protocol.md) or the producer
/// reaches its end.
///
/// The browser-facing contract is UNCHANGED: the browser still opens the same WebSocket at
/// <c>/sessions/{sid}/stream</c> and the same GET for a file; the Gateway translates each frame back into the
/// browser wire form it always sent (a Size frame -> the {"type":"size"} json, a Binary frame -> a binary
/// WebSocket message or an HTTP body chunk, a Closed frame -> {"type":"closed"} then close).
/// </summary>
public sealed class DirectorStreamFrame
{
    /// <summary>Correlates this frame to the browser request the Gateway is serving. A fresh Guid per open, never reused.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>What this frame carries. See <see cref="DirectorStreamFrameType"/>.</summary>
    public DirectorStreamFrameType Kind { get; set; }

    /// <summary>
    /// The binary payload for a <see cref="DirectorStreamFrameType.Binary"/> frame: raw PTY bytes for the
    /// terminal, or a file chunk for a finite read. Null for Size/Closed frames. BOUNDED: a producer never
    /// emits more than <see cref="DirectorStreamLimits.MaxBinaryFrameBytes"/> in one frame, so no single frame
    /// can monopolize the one shared tunnel connection (Architect ruling 2).
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>Terminal grid columns; meaningful only on a <see cref="DirectorStreamFrameType.Size"/> frame.</summary>
    public int Cols { get; set; }

    /// <summary>Terminal grid rows; meaningful only on a <see cref="DirectorStreamFrameType.Size"/> frame.</summary>
    public int Rows { get; set; }

    /// <summary>
    /// Why the stream ended; meaningful only on a <see cref="DirectorStreamFrameType.Closed"/> frame
    /// (for example "session exited", "session not found", or "eof" for the natural end of a finite read).
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>The three frame kinds that serve both the terminal stream and a finite byte read.</summary>
public enum DirectorStreamFrameType
{
    /// <summary>A terminal grid size (Cols x Rows). Sent first on a terminal stream and again on every PTY resize.</summary>
    Size = 0,

    /// <summary>A chunk of bytes (<see cref="DirectorStreamFrame.Data"/>): live PTY output, or a file chunk.</summary>
    Binary = 1,

    /// <summary>The stream has ended (<see cref="DirectorStreamFrame.Reason"/> says why). The last frame of any stream.</summary>
    Closed = 2,
}

/// <summary>
/// Shared numeric bounds for the up-stream (Architect ruling 2). The frame cap and the hub's
/// MaximumReceiveMessageSize are set from the SAME constant so they can never drift apart: a producer that
/// chunks at the cap can never emit a frame the hub would reject, and a bounded frame keeps the one shared
/// tunnel connection interleaving terminal output, file chunks, and unary commands instead of stalling on a
/// single large message.
/// </summary>
public static class DirectorStreamLimits
{
    /// <summary>
    /// The maximum bytes in a single <see cref="DirectorStreamFrame.Data"/> payload (48 KB - inside the
    /// 32-to-64 KB band the Architect fixed). The file producer chunks at this bound; the terminal producer's
    /// tail frames are already smaller. The hub's MaximumReceiveMessageSize is set to this plus a small
    /// envelope allowance so the framed message (with its StreamId and metadata) still fits.
    /// </summary>
    public const int MaxBinaryFrameBytes = 48 * 1024;

    /// <summary>
    /// SignalR StreamBufferCapacity for the up-stream (Architect ruling 1): small, single-digit, so a slow
    /// browser sink fills this channel quickly and pushes back onto the Director producer's <c>yield return</c>,
    /// giving end-to-end backpressure with bounded memory. NOT an optimization - the backpressure invariant.
    /// </summary>
    public const int StreamBufferCapacity = 4;

    /// <summary>
    /// The extra envelope room (over <see cref="MaxBinaryFrameBytes"/>) allowed for a framed message's own
    /// fields (StreamId, kind, size ints, reason) plus the SignalR/MessagePack framing, used to set the hub's
    /// MaximumReceiveMessageSize = MaxBinaryFrameBytes + this.
    /// </summary>
    public const int FrameEnvelopeAllowanceBytes = 4 * 1024;

    /// <summary>
    /// Gateway Cleanup mission, Phase 2 (upload-image): the RAW byte size of one DOWN-stream image chunk. An
    /// image uploaded to the Gateway is split into pieces this large and sent to the Director across unary
    /// commands (begin / chunk / complete), so a whole photo never rides as one large unary message that would
    /// monopolize the shared tunnel (Architect ruling 2). Deliberately small: each chunk is base64-encoded in
    /// the command payload (+33%), so 20 KB raw is ~27 KB on the wire - comfortably under the SignalR default
    /// receive limit even with the command envelope, whatever a client's limit is. Images are small and
    /// infrequent, so the extra chunks are free; the invariant that matters is bounded, no-monopoly framing.
    /// </summary>
    public const int UploadChunkRawBytes = 20 * 1024;
}

/// <summary>
/// The payload of the unary "open" command that starts an up-stream (verbs open-terminal-stream / read-file /
/// screenshot-file). Rides in <see cref="DirectorCommand.PayloadJson"/>; the target session is
/// <see cref="DirectorCommand.SessionId"/> on the command itself.
/// </summary>
public sealed class OpenStreamRequest
{
    /// <summary>The fresh Guid the Gateway minted for this stream. The Director tags every up-frame with it.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>For a file read (read-file): the file path to serve. Null/absent for a terminal stream.</summary>
    public string? Path { get; set; }

    /// <summary>For a screenshot read (screenshot-file): the screenshot id/name to serve. Null/absent otherwise.</summary>
    public string? ScreenshotId { get; set; }
}

/// <summary>
/// The success body of a finite read's "open" command (Architect ruling 4). When the Director can cheaply
/// stat the resource up front, it returns the total byte length and content type in the open command's
/// <see cref="DirectorCommandResult.BodyJson"/>, so the Gateway can set <c>Content-Length</c> on the browser
/// response instead of falling back to chunked transfer. A terminal open returns no body (open-ended).
/// </summary>
public sealed class OpenReadResponse
{
    /// <summary>Total bytes the finite read will deliver, when known up front; null when not cheaply knowable.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>The resource's content type (for the browser response), when known.</summary>
    public string? ContentType { get; set; }
}

/// <summary>The payload of the unary "close-stream" command (Architect ruling 3): the id of the stream to stop.
/// Idempotent on the Director - closing a stream that already ended or never started is a safe no-op.</summary>
public sealed class CloseStreamRequest
{
    /// <summary>The stream to stop. Cancels the Director producer's CancellationToken for this id.</summary>
    public string StreamId { get; set; } = "";
}
