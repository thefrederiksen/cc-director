using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Test microphone / Test transcription checks.
///
///   POST   /voice-test/clip     multipart: audio, kind, language?, expected?, quality?
///          -&gt; 200 { clipId, transcript? }   stored; transcript present for a transcription check
///          -&gt; 400 { error }                 no audio, unknown kind, or clip too large
///          -&gt; 403 { error }                 no tenant is bound to this request
///          -&gt; 402 { error, code }           the account is out of credits
///          -&gt; 502 { error }                 the provider rejected the request
///   GET    /voice-test/clips    -&gt; { clips: [...] }   this tenant's stored clips, newest first
///   DELETE /voice-test/clips    -&gt; { removed }        delete every clip this tenant stored
///
/// WHY THE CLIPS ARE KEPT. The microphone check tells one user about one headset. The point of
/// keeping the clips is the question no single run can answer: how well does transcription actually
/// work, per language, across real headsets and real rooms. That needs the audio, the passage the
/// user was reading and the text that came back, held together and compared later. See
/// <see cref="VoiceTestClipStore"/> for why keeping THIS audio is a different proposition from
/// keeping dictation, and for the retention that still applies.
///
/// WHY THE DICTIONARY CORRECTOR IS OFF for the transcription check: it would swap known terms after
/// the fact and mask the very thing being measured. The Settings "Test it" button already leaves it
/// off for exactly this reason - a test of transcription must test transcription.
///
/// TENANCY. This route stores speech at rest, so it uses
/// <see cref="GatewayDictationEndpoint.ResolveTenant"/>, which returns null - a refusal - when a
/// hosted request carries no bound tenant. It deliberately does NOT use the read-side helper, which
/// falls back to Local when no boundary was passed and would therefore write one account's audio into
/// the self-host partition.
/// </summary>
internal static class VoiceTestEndpoint
{
    private const string Prefix = "/voice-test";

    /// <summary>Largest metadata field accepted. The passages are a few hundred characters; a
    /// megabyte of "expected text" is not a passage, and storing it would be someone else's idea.</summary>
    private const int MaxTextFieldChars = 4000;

    public static void Map(
        IEndpointRouteBuilder app,
        GatewayTranscriptionService transcription,
        Tenancy.HostedTenantBoundary? tenantBoundary = null,
        VoiceTestClipStore? storeOverride = null)
    {
        var group = app.MapGroup(Prefix);

        group.MapPost("/clip", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();

            if (!ctx.Request.HasFormContentType)
                return BadRequest("send the clip as multipart form-data with an 'audio' file");

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return BadRequest("no audio in the upload");

            if (file.Length > VoiceUploadLimits.MaxOneShotFileBytes)
            {
                FileLog.Write($"[VoiceTest] clip rejected: {file.Length} bytes > {VoiceUploadLimits.MaxOneShotFileBytes} cap");
                return BadRequest($"audio is {file.Length} bytes; the limit for this endpoint is {VoiceUploadLimits.MaxOneShotFileBytes}");
            }

            var kind = Field(form, "kind");
            if (!VoiceTestKind.IsValid(kind))
                return BadRequest($"kind must be '{VoiceTestKind.Microphone}' or '{VoiceTestKind.Transcription}'");

            var language = Trim(Field(form, "language"), 32);
            var expected = Trim(Field(form, "expected"), MaxTextFieldChars);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "voice-test.wav" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "audio/wav" : file.ContentType;

            string? transcript = null;
            string? outcome = null;
            IResult? failure = null;

            if (kind == VoiceTestKind.Transcription)
            {
                var routing = transcription.Resolve();
                if (routing.Key is null)
                {
                    outcome = "no_key";
                    failure = Results.Json(
                        new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                else
                {
                    // Correction OFF on purpose - see the class remarks. The language hint is what makes
                    // a non-English run meaningful: auto-detection on a short clip is exactly where it
                    // goes wrong, and a wrong detection returns confident nonsense rather than an error.
                    var result = await transcription.TranscribeAsync(
                        bytes, fileName, contentType, applyCorrection: false, ct,
                        tenant: tenant, source: "voice-test", language: language);

                    outcome = result.Outcome.ToString();
                    if (result.Outcome == TranscriptionOutcome.Ok)
                    {
                        transcript = result.Text;
                    }
                    else if (result.Outcome == TranscriptionOutcome.OutOfCredits)
                    {
                        failure = HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.MapCode(result.Code));
                    }
                    else
                    {
                        failure = Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
                    }
                }
            }

            // Store whatever happened, INCLUDING a failure. A clip that could not be transcribed is the
            // most interesting clip there is - it is the one carrying the defect worth studying - so a
            // provider error must not also throw the evidence away.
            var clip = new VoiceTestClip
            {
                ClipId = Guid.NewGuid().ToString("N"),
                Kind = kind!,
                RecordedAtUtc = DateTime.UtcNow,
                Language = language,
                ExpectedText = expected,
                Transcript = transcript,
                Outcome = outcome,
                Quality = ParseQuality(Field(form, "quality")),
                AudioBytes = bytes.LongLength,
                ContentType = contentType,
            };
            var store = storeOverride ?? VoiceTestClipStore.ForTenant(tenant);
            var clipId = store.TrySave(clip, bytes, contentType);

            if (failure is not null) return failure;
            return Results.Json(new { clipId, transcript });
        });

        group.MapGet("/clips", (HttpContext ctx) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();
            var store = storeOverride ?? VoiceTestClipStore.ForTenant(tenant);
            return Results.Json(new { clips = store.List() });
        });

        group.MapDelete("/clips", (HttpContext ctx) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();
            var store = storeOverride ?? VoiceTestClipStore.ForTenant(tenant);
            return Results.Json(new { removed = store.Clear() });
        });
    }

    private static IResult BadRequest(string error)
        => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static string? Field(IFormCollection form, string name)
        => form.TryGetValue(name, out var value) ? value.ToString() : null;

    private static string? Trim(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }

    /// <summary>
    /// Parse the client's measurements, which are stored verbatim. Unparseable input is dropped rather
    /// than rejected: the measurements are a bonus for later analysis, and losing them must never cost
    /// the user the clip or the transcript they were actually asking for.
    /// </summary>
    private static JsonElement? ParseQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[VoiceTest] ignoring unparseable quality payload: {ex.Message}");
            return null;
        }
    }
}
