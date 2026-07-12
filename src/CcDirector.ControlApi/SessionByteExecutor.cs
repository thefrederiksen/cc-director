using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the UNARY BYTE area of the tunnel command surface. It owns the
/// byte verbs that are plain unary commands (the small-payload ones), NOT the up-stream byte streams: the
/// screenshots list (a directory read) and upload-image (bytes DOWN in the payload, chunked if large). The
/// three connection-bound byte STREAMS - read-file, screenshot-file, and the terminal stream - are NOT here:
/// they are handled at the connection layer in GatewayStreamClient with the up-stream primitive, because
/// only they need the live connection (Architect ruling A). Worker S1 fills it once the spine's up-stream
/// producers are in place. Each verb is declared in <see cref="Verbs"/> and handled in
/// <see cref="ExecuteAsync"/>, touching only this file.
/// </summary>
internal sealed class SessionByteExecutor : ISessionCommandArea
{
    // Gateway Cleanup Phase 0 (Wave 4a): screenshot-delete is the third unary byte verb - a plain file delete
    // keyed by a traversal-safe bare name, the tunnel twin of DELETE /screenshots/file. Its core reproduces
    // that route verbatim, so the REST route and the tunnel verb share one core and cannot drift.
    public IReadOnlyCollection<string> Verbs { get; } = new[] { "screenshots-list", "upload-image", "screenshot-delete" };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var result = command.Verb switch
        {
            "screenshots-list" => ScreenshotsList(command),
            "upload-image" => UploadImage(context.SessionManager, command),
            "screenshot-delete" => ScreenshotDelete(command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the byte area"),
        };
        return Task.FromResult(result);
    }

    /// <summary>
    /// The <c>screenshots-list</c> verb: list the screenshots on THIS Director's machine, newest first, with
    /// a pre-formatted local time label. Mirrors the Director's <c>GET /screenshots</c> lambda exactly - a
    /// null or non-positive count falls back to <see cref="ControlEndpoints.DefaultScreenshotCount"/>, the
    /// items are capped to that cap while <see cref="ScreenshotListResponse.Total"/> always reports the full
    /// folder count, and older files past the cap are deliberately dropped. This is a plain unary read: no
    /// target session and no error branches (an empty or missing folder simply lists nothing).
    /// </summary>
    internal static DirectorCommandResult ScreenshotsList(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<ScreenshotListRequest>(command.PayloadJson);
        var cap = request?.Count is > 0 ? request.Count.Value : ControlEndpoints.DefaultScreenshotCount;
        FileLog.Write($"[SessionByteExecutor] screenshots-list cap={cap}");

        var dir = CcStorage.Screenshots();
        var all = ControlEndpoints.ScreenshotFiles(dir);
        var items = all
            .Take(cap)
            .Select(info => new ScreenshotItem
            {
                FileName = info.Name,
                // Absolute on-disk path on THIS Director's machine. The Cockpit injects it into the composer
                // at the cursor (desktop parity: drop the path into the prompt) - the owning Claude session
                // reads it directly, no upload needed.
                Path = info.FullName,
                TimeLabel = info.LastWriteTime.ToString("MMM d, h:mm tt"),
                LastWriteUtc = info.LastWriteTimeUtc,
                SizeBytes = info.Length,
            })
            .ToList();

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new ScreenshotListResponse
        {
            Directory = dir,
            Total = all.Count,
            Items = items,
        }));
    }

    /// <summary>
    /// The image extensions <c>upload-image</c> accepts, byte-identical to the set the REST route enforced.
    /// A phone can offer HEIC, so it is included; the on-disk file simply keeps whatever extension was sent.
    /// </summary>
    private static readonly string[] UploadAllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp" };

    /// <summary>
    /// The <c>upload-image</c> verb: file an uploaded image into the user's screenshots folder on THIS
    /// Director's machine, where the owning Claude session can read it by absolute path. Mirrors the
    /// Director's <c>POST /sessions/{sid}/upload-image</c> lambda - invalid id -&gt; BadRequest, missing
    /// session -&gt; NotFound - then hands the already-read bytes and file name to the shared save core.
    /// The REST route reads the multipart form at the HTTP boundary and dispatches this SAME verb, so the
    /// save logic lives in exactly one place (<see cref="SaveUploadedImage"/>).
    /// </summary>
    internal static DirectorCommandResult UploadImage(SessionManager sessionManager, DirectorCommand command)
    {
        FileLog.Write($"[SessionByteExecutor] upload-image: sid={command.SessionId}");

        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<UploadImageRequest>(command.PayloadJson);
        return SaveUploadedImage(request);
    }

    /// <summary>
    /// The shared SAVE core, past the id/session guards: validate the image, decode the base64 bytes, and
    /// write the file into the screenshots folder under a timestamped name that keeps the uploaded
    /// extension. Called by the <c>upload-image</c> verb over the tunnel AND directly by the REST route
    /// after it reads the multipart form, so the two paths file bytes identically. Empty bytes -&gt;
    /// BadRequest (matching the REST route's empty-file guard), an unsupported extension -&gt; BadRequest,
    /// and undecodable base64 -&gt; a fail-loud BadRequest naming the cause. Returns the saved absolute path.
    /// </summary>
    internal static DirectorCommandResult SaveUploadedImage(UploadImageRequest? request)
    {
        if (request is null || string.IsNullOrEmpty(request.BytesBase64))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "no image uploaded; use form field 'file'");

        var ext = Path.GetExtension(request.FileName).ToLowerInvariant();
        if (!UploadAllowedExtensions.Contains(ext))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unsupported image type '{ext}'. Allowed: {string.Join(", ", UploadAllowedExtensions)}");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.BytesBase64);
        }
        catch (FormatException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "image bytes must be base64");
        }

        if (bytes.Length == 0)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "no image uploaded; use form field 'file'");

        var dir = CcStorage.Screenshots();
        var name = $"upload-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}";
        var fullPath = Path.Combine(dir, name);
        File.WriteAllBytes(fullPath, bytes);

        FileLog.Write($"[SessionByteExecutor] upload-image saved: {fullPath} ({bytes.Length} bytes)");
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new UploadImageResponse
        {
            Path = fullPath,
            FileName = name,
        }));
    }

    /// <summary>
    /// The <c>screenshot-delete</c> verb: delete one screenshot from disk (the per-card "Del" action). Mirrors
    /// the Director's <c>DELETE /screenshots/file</c> lambda - the bare name is resolved traversal-safe by the
    /// SAME <see cref="ControlEndpoints.ResolveScreenshot"/> helper the file GET/read used, so a name that
    /// escapes the screenshots folder, is not a bare image file, or does not exist -&gt; NotFound (the route's
    /// 404) - and returns <c>{ deleted = true, fileName }</c> on success. The name rides in the payload. This
    /// is a plain unary write.
    /// </summary>
    internal static DirectorCommandResult ScreenshotDelete(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<ScreenshotDeleteRequest>(command.PayloadJson);
        var name = request?.Name;
        FileLog.Write($"[SessionByteExecutor] screenshot-delete: name={name}");

        var full = ControlEndpoints.ResolveScreenshot(name);
        if (full is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "screenshot not found");

        File.Delete(full);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new ScreenshotDeleteResponse
        {
            Deleted = true,
            FileName = Path.GetFileName(full),
        }));
    }
}
