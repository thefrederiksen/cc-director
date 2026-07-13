namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Gateway Cleanup mission, Phase 0: the request for the <c>screenshots-list</c> unary byte verb (the
/// tunnel twin of <c>GET /screenshots</c>). The list is a directory read on THIS Director's machine, so
/// the only input is the newest-first cap. A null or non-positive <see cref="Count"/> falls back to the
/// Director's default cap, exactly as the query parameter did on the REST route.
/// </summary>
public sealed class ScreenshotListRequest
{
    /// <summary>Newest-first cap. Null or non-positive means "use the default cap".</summary>
    public int? Count { get; set; }
}

/// <summary>
/// Gateway Cleanup mission, Phase 0: one screenshot row in a <see cref="ScreenshotListResponse"/>. The
/// field set is byte-identical to the anonymous object the <c>GET /screenshots</c> route returned, so the
/// Cockpit gallery reads the same shape whether the list came over the REST route or the tunnel verb.
/// </summary>
public sealed class ScreenshotItem
{
    /// <summary>The bare file name, e.g. "shot-2026-07-12.png".</summary>
    public string FileName { get; set; } = "";

    /// <summary>The absolute on-disk path on THIS Director's machine (injected into the composer on tap).</summary>
    public string Path { get; set; } = "";

    /// <summary>A pre-formatted local time label ("MMM d, h:mm tt") so clients need not re-derive it.</summary>
    public string TimeLabel { get; set; } = "";

    /// <summary>Last write time in universal time, used for newest-first ordering on the client.</summary>
    public DateTime LastWriteUtc { get; set; }

    /// <summary>The file size in bytes.</summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// Gateway Cleanup mission, Phase 0: the response of the <c>screenshots-list</c> verb. Mirrors the
/// <c>GET /screenshots</c> body exactly: the screenshots directory, the FULL folder count (so a client can
/// say "newest N of total"), and the capped, newest-first <see cref="Items"/>.
/// </summary>
public sealed class ScreenshotListResponse
{
    /// <summary>The screenshots folder on THIS Director's machine.</summary>
    public string Directory { get; set; } = "";

    /// <summary>The full folder count (never capped), so clients can report "newest N of total".</summary>
    public int Total { get; set; }

    /// <summary>The capped, newest-first screenshot rows.</summary>
    public List<ScreenshotItem> Items { get; set; } = new();
}

/// <summary>
/// Gateway Cleanup mission, Phase 0: the request for the <c>upload-image</c> unary byte verb (the tunnel
/// twin of <c>POST /sessions/{sid}/upload-image</c>). The image bytes ride DOWN in the payload,
/// base64-encoded so they survive the JSON envelope intact; <see cref="FileName"/> carries the original
/// name so the saved file keeps the uploaded extension. The REST route reads the multipart form at the
/// HTTP boundary and fills this same request before dispatching, so the SAVE logic is shared, not
/// duplicated.
/// </summary>
public sealed class UploadImageRequest
{
    /// <summary>The original file name (only its extension is used to pick the saved file's extension).</summary>
    public string FileName { get; set; } = "";

    /// <summary>The raw image bytes, base64-encoded.</summary>
    public string BytesBase64 { get; set; } = "";
}

/// <summary>
/// Gateway Cleanup mission, Phase 0: the response of the <c>upload-image</c> verb. Byte-identical to the
/// <c>{ path, fileName }</c> object the REST route returned: the absolute saved path (which the owning
/// Claude session reads directly) and the saved file name.
/// </summary>
public sealed class UploadImageResponse
{
    /// <summary>The absolute path the image was saved to on THIS Director's machine.</summary>
    public string Path { get; set; } = "";

    /// <summary>The saved file name (an "upload-yyyyMMdd-HHmmss-fff" stamp plus the uploaded extension).</summary>
    public string FileName { get; set; } = "";
}

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR upload-image): an image is uploaded DOWN the tunnel in bounded
/// chunks across three unary commands - begin / chunk (repeated) / complete - reassembled on the Director by
/// <see cref="UploadId"/>. This honours the Architect's ruling 2 (no single message monopolizes the shared
/// tunnel): a whole photo would be a multi-hundred-KB unary message, so it is split into pieces each smaller
/// than the SignalR receive limit. This is the BEGIN command: it opens a reassembly buffer for a fresh
/// <see cref="UploadId"/> and carries the file name (for the saved extension). No bytes yet.
/// </summary>
public sealed class UploadImageBeginRequest
{
    /// <summary>A fresh Guid the Gateway mints for this upload; every chunk and the complete carry it.</summary>
    public string UploadId { get; set; } = "";

    /// <summary>The original file name (only its extension is used to pick the saved file's extension).</summary>
    public string FileName { get; set; } = "";

    /// <summary>The total raw byte length of the image, so the Director can reject a mismatch on complete.</summary>
    public long TotalBytes { get; set; }
}

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR upload-image): one CHUNK of an in-flight upload. The raw bytes are
/// base64-encoded (so they survive the JSON payload envelope) and kept small - see
/// <see cref="DirectorStreamLimits.UploadChunkRawBytes"/> - so the framed command stays well under the
/// SignalR receive limit and never monopolizes the tunnel. Chunks are sent in order; <see cref="Seq"/> is
/// zero-based and the Director rejects an out-of-order chunk (the Gateway awaits each chunk's reply before
/// sending the next, so in-order delivery is guaranteed on the happy path).
/// </summary>
public sealed class UploadImageChunkRequest
{
    /// <summary>The upload this chunk belongs to (matches the begin's UploadId).</summary>
    public string UploadId { get; set; } = "";

    /// <summary>The zero-based chunk index; must equal the number of chunks already appended.</summary>
    public int Seq { get; set; }

    /// <summary>This chunk's raw image bytes, base64-encoded.</summary>
    public string BytesBase64 { get; set; } = "";
}

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR upload-image): the COMPLETE command - all chunks are in, so the
/// Director assembles them, validates the extension and total length, writes the file into the screenshots
/// folder (the SAME save as the single-shot path), and returns the saved <see cref="UploadImageResponse"/>.
/// It also releases the reassembly buffer. Completing an unknown/expired upload is a fail-loud BadRequest.
/// </summary>
public sealed class UploadImageCompleteRequest
{
    /// <summary>The upload to finalize (matches the begin's UploadId).</summary>
    public string UploadId { get; set; } = "";
}
