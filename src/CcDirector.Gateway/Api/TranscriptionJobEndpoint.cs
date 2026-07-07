using System.Collections.Concurrent;
using CcDirector.Core;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The unified Gateway transcription job protocol. Clients upload audio here and then either receive
/// a transcript directly or poll the job status/result. This is the target replacement for the older
/// per-surface upload protocols such as /wingman/utterance and Director-owned /dictate.
/// </summary>
internal static class TranscriptionJobEndpoint
{
    public const string ProtocolVersion = "gateway-transcription-job-v1";

    private const int ProviderAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly ConcurrentDictionary<string, TranscriptionJobRecord> Jobs = new();

    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        var uploads = new VoiceUploadStore(CcStorage.TranscriptionUploads());

        app.MapPost("/transcription/upload", (TranscriptionJobUploadRequest? body, HttpContext ctx) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var requested = !string.IsNullOrWhiteSpace(body?.JobId) ? body!.JobId : key;
            var jobId = uploads.Register(string.IsNullOrWhiteSpace(requested) ? null : requested);
            var now = DateTime.UtcNow;
            var record = Jobs.AddOrUpdate(jobId,
                _ => new TranscriptionJobRecord
                {
                    JobId = jobId,
                    ProtocolVersion = ProtocolVersion,
                    Action = string.IsNullOrWhiteSpace(body?.Action) ? "return_transcript" : body!.Action!,
                    Status = "uploading",
                    ContentType = string.IsNullOrWhiteSpace(body?.ContentType) ? "audio/webm" : body!.ContentType!,
                    Extension = string.IsNullOrWhiteSpace(body?.Extension) ? null : body!.Extension,
                    ApplyCorrection = body?.ApplyCorrection ?? true,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Message = "upload started",
                },
                (_, existing) =>
                {
                    existing.Status = IsTerminal(existing.Status) ? existing.Status : "uploading";
                    existing.UpdatedUtc = now;
                    existing.Message = "upload reopened";
                    return existing;
                });

            FileLog.Write($"[TranscriptionJobEndpoint] upload registered job={jobId} action={record.Action}");
            return Results.Json(new { jobId, upload_id = jobId, protocolVersion = ProtocolVersion });
        });

        app.MapPut("/transcription/{jobId}/chunk/{index:int}", async (string jobId, int index, HttpContext ctx) =>
        {
            if (!uploads.Exists(jobId))
                return Results.Json(new { error = "unknown job id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            try
            {
                await uploads.StoreChunkAsync(jobId, index, ms.ToArray(), string.IsNullOrWhiteSpace(sha) ? null : sha, ctx.RequestAborted);
                var record = EnsureJob(jobId);
                record.Status = "uploading";
                record.UploadReceivedChunks = Math.Max(record.UploadReceivedChunks, index + 1);
                record.Percent = EstimatePercent(record);
                record.UpdatedUtc = DateTime.UtcNow;
                record.Message = $"uploaded chunk {index}";
                return Results.Json(new { ok = true, jobId, index, received = record.UploadReceivedChunks });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TranscriptionJobEndpoint] chunk job={jobId} index={index} FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/transcription/{jobId}/complete", async (string jobId, TranscriptionJobCompleteRequest req, HttpContext ctx) =>
        {
            var record = EnsureJob(jobId);
            record.UploadTotalChunks = req.TotalChunks;
            record.ContentType = string.IsNullOrWhiteSpace(req.Mime) ? record.ContentType : req.Mime!;
            record.Extension = string.IsNullOrWhiteSpace(req.Ext) ? record.Extension : req.Ext;
            record.ApplyCorrection = req.ApplyCorrection ?? record.ApplyCorrection;
            record.UpdatedUtc = DateTime.UtcNow;

            var assembled = await uploads.AssembleAsync(jobId, req.TotalChunks, ctx.RequestAborted);
            if (assembled.Status == "unknown_upload")
            {
                MarkFailed(record, "failed_final", "unknown upload id", StatusCodes.Status404NotFound);
                return Results.Json(new { error = record.Error }, statusCode: StatusCodes.Status404NotFound);
            }
            if (assembled.Status == "incomplete")
            {
                record.Status = "uploading";
                record.Message = "missing chunks";
                record.UpdatedUtc = DateTime.UtcNow;
                return Results.Json(new { status = "incomplete", jobId, missing = assembled.Missing }, statusCode: StatusCodes.Status409Conflict);
            }

            var audio = assembled.Audio ?? Array.Empty<byte>();
            record.Status = "transcribing";
            record.AudioBytes = audio.Length;
            record.UploadReceivedChunks = req.TotalChunks;
            record.Percent = 60;
            record.Message = "transcribing";
            record.StartedUtc ??= DateTime.UtcNow;
            record.UpdatedUtc = DateTime.UtcNow;

            var service = new GatewayTranscriptionService(vault);
            GatewayTranscriptionResult result = GatewayTranscriptionResult.ProviderError("devthrottle", null, "not attempted");
            for (var attempt = 1; attempt <= ProviderAttempts; attempt++)
            {
                record.RetryCount = attempt - 1;
                result = await service.TranscribeAsync(
                    audio,
                    FileNameFor(record),
                    record.ContentType,
                    record.ApplyCorrection,
                    ctx.RequestAborted);
                if (result.Outcome != TranscriptionOutcome.ProviderError) break;
                if (attempt < ProviderAttempts)
                {
                    record.Status = "failed_retryable";
                    record.Message = $"provider failed; retrying attempt {attempt + 1} of {ProviderAttempts}";
                    record.Error = result.Error;
                    record.UpdatedUtc = DateTime.UtcNow;
                    await Task.Delay(RetryDelay * attempt, ctx.RequestAborted);
                    record.Status = "transcribing";
                }
            }

            return result.Outcome switch
            {
                TranscriptionOutcome.Ok => CompleteOk(record, uploads, jobId, result),
                TranscriptionOutcome.NoAudio => CompleteFailure(record, "failed_final", result.Error, StatusCodes.Status400BadRequest),
                TranscriptionOutcome.NoKey => CompleteFailure(record, "failed_final", result.Error, StatusCodes.Status409Conflict),
                TranscriptionOutcome.OutOfCredits => CompleteCreditsFailure(record, result),
                TranscriptionOutcome.ProviderError => CompleteFailure(record, "failed_retryable", result.Error, StatusCodes.Status502BadGateway),
                _ => CompleteFailure(record, "failed_final", "unknown transcription outcome", StatusCodes.Status500InternalServerError),
            };
        });

        app.MapGet("/transcription/{jobId}/status", (string jobId) =>
        {
            if (!Jobs.TryGetValue(Normalize(jobId), out var record))
                return Results.Json(new { error = "unknown job id" }, statusCode: StatusCodes.Status404NotFound);
            return Results.Json(ToStatus(record));
        });

        app.MapGet("/transcription/{jobId}/result", (string jobId) =>
        {
            if (!Jobs.TryGetValue(Normalize(jobId), out var record))
                return Results.Json(new { error = "unknown job id" }, statusCode: StatusCodes.Status404NotFound);
            if (!string.Equals(record.Status, "complete", StringComparison.Ordinal))
                return Results.Json(new { error = "job is not complete", status = record.Status }, statusCode: StatusCodes.Status409Conflict);
            return Results.Json(ToResult(record));
        });
    }

    private static IResult CompleteOk(TranscriptionJobRecord record, VoiceUploadStore uploads, string jobId, GatewayTranscriptionResult result)
    {
        record.Status = "complete";
        record.Percent = 100;
        record.RawTranscript = result.Text ?? "";
        record.CleanedTranscript = result.Text ?? "";
        record.Mode = result.Mode;
        record.Model = result.Model;
        record.Provider = "gateway";
        record.CompletedUtc = DateTime.UtcNow;
        record.UpdatedUtc = record.CompletedUtc.Value;
        record.Message = "complete";
        uploads.Delete(jobId);
        return Results.Json(ToResult(record));
    }

    private static IResult CompleteFailure(TranscriptionJobRecord record, string status, string? error, int statusCode)
    {
        MarkFailed(record, status, error ?? "transcription failed", statusCode);
        return Results.Json(new { jobId = record.JobId, status = record.Status, error = record.Error }, statusCode: statusCode);
    }

    private static IResult CompleteCreditsFailure(TranscriptionJobRecord record, GatewayTranscriptionResult result)
    {
        record.Code = result.Code;
        return CompleteFailure(record, "failed_final", result.Error ?? "out of transcription credits", StatusCodes.Status402PaymentRequired);
    }

    private static void MarkFailed(TranscriptionJobRecord record, string status, string error, int statusCode)
    {
        record.Status = status;
        record.Error = error;
        record.Message = error;
        record.StatusCode = statusCode;
        record.UpdatedUtc = DateTime.UtcNow;
        record.Percent = status == "failed_retryable" ? Math.Max(record.Percent, 60) : record.Percent;
    }

    private static object ToStatus(TranscriptionJobRecord record) => new
    {
        jobId = record.JobId,
        protocolVersion = record.ProtocolVersion,
        status = record.Status,
        action = record.Action,
        percent = record.Percent,
        message = record.Message,
        error = record.Error,
        code = record.Code,
        uploadReceivedChunks = record.UploadReceivedChunks,
        uploadTotalChunks = record.UploadTotalChunks,
        transcribedParts = record.Status == "complete" ? 1 : 0,
        transcriptionTotalParts = record.Status is "transcribing" or "correcting" or "complete" ? 1 : 0,
        retryCount = record.RetryCount,
        startedUtc = record.StartedUtc,
        completedUtc = record.CompletedUtc,
        updatedUtc = record.UpdatedUtc,
    };

    private static object ToResult(TranscriptionJobRecord record) => new
    {
        jobId = record.JobId,
        protocolVersion = record.ProtocolVersion,
        status = record.Status,
        action = record.Action,
        rawTranscript = record.RawTranscript,
        cleanedTranscript = record.CleanedTranscript,
        transcript = record.CleanedTranscript,
        dictionaryApplied = record.ApplyCorrection,
        mode = record.Mode,
        provider = record.Provider,
        model = record.Model,
        startedUtc = record.StartedUtc,
        completedUtc = record.CompletedUtc,
    };

    private static TranscriptionJobRecord EnsureJob(string jobId)
    {
        var id = Normalize(jobId);
        return Jobs.GetOrAdd(id, _ =>
        {
            var now = DateTime.UtcNow;
            return new TranscriptionJobRecord
            {
                JobId = id,
                ProtocolVersion = ProtocolVersion,
                Action = "return_transcript",
                Status = "uploading",
                ContentType = "audio/webm",
                ApplyCorrection = true,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
        });
    }

    private static int EstimatePercent(TranscriptionJobRecord record)
    {
        if (record.UploadTotalChunks <= 0) return Math.Min(45, record.UploadReceivedChunks > 0 ? 10 : 0);
        return Math.Clamp((int)Math.Round(record.UploadReceivedChunks * 50.0 / record.UploadTotalChunks), 0, 50);
    }

    private static string FileNameFor(TranscriptionJobRecord record)
    {
        var ext = !string.IsNullOrWhiteSpace(record.Extension)
            ? record.Extension!.Trim().TrimStart('.')
            : GatewayTranscriptionService.ExtensionFor(record.ContentType);
        return "audio." + ext;
    }

    private static string Normalize(string id)
        => Guid.TryParse(id, out var g) ? g.ToString("N") : id;

    private static bool IsTerminal(string status)
        => status is "complete" or "failed_final" or "expired";
}

internal sealed class TranscriptionJobUploadRequest
{
    public string? JobId { get; set; }
    public string? Action { get; set; }
    public string? ContentType { get; set; }
    public string? Extension { get; set; }
    public bool? ApplyCorrection { get; set; }
}

internal sealed class TranscriptionJobCompleteRequest
{
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }
    public bool? ApplyCorrection { get; set; }
}

internal sealed class TranscriptionJobRecord
{
    public required string JobId { get; init; }
    public required string ProtocolVersion { get; init; }
    public required string Action { get; set; }
    public required string Status { get; set; }
    public required string ContentType { get; set; }
    public string? Extension { get; set; }
    public bool ApplyCorrection { get; set; }
    public int UploadReceivedChunks { get; set; }
    public int UploadTotalChunks { get; set; }
    public int Percent { get; set; }
    public int RetryCount { get; set; }
    public int StatusCode { get; set; }
    public int AudioBytes { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? Code { get; set; }
    public string? RawTranscript { get; set; }
    public string? CleanedTranscript { get; set; }
    public string? Mode { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
