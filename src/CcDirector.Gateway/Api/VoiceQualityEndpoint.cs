using System.Text.Json.Serialization;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Background microphone-quality monitoring for ordinary dictation.
///
///   POST   /voice-quality/sample   one measurement from a finished dictation -> 204
///   GET    /voice-quality/summary  the folded verdict the Cockpit renders    -> 200
///   GET    /voice-quality/detail   per-device measurements + quality over time -> 200
///   DELETE /voice-quality/history  forget every measurement for this tenant  -> 200 { removed }
///
/// The client measures (it is where the decoded audio already is) and posts a handful of numbers plus
/// the microphone's name. No audio and no transcript are involved, so this route carries nothing that
/// could identify what was said - only how it sounded.
///
/// THE POST ANSWERS 204 AND MEANS IT. A dictation has already succeeded by the time this is called, so
/// there is no failure here worth telling the client about: a rejected sample is dropped and logged,
/// never turned into an error the user could see. The client sends fire-and-forget for the same
/// reason (qualityReport.ts).
///
/// The verdict on GET is FOLDED ON THE GATEWAY (MicrophoneQualityFold), not assembled in the browser -
/// the standing rule that a client renders a decision and never re-derives one.
/// </summary>
internal static class VoiceQualityEndpoint
{
    private const string Prefix = "/voice-quality";

    /// <summary>Longest device name stored. Real names are well under this; the cap stops a client
    /// from turning a per-dictation record into a place to park arbitrary text.</summary>
    private const int MaxDeviceChars = 200;

    /// <summary>Longest device id stored. Browser deviceIds are 64-character hashes; the headroom
    /// covers other engines without opening a text-parking hole.</summary>
    private const int MaxDeviceIdChars = 128;

    /// <summary>Longest raw platform evidence stored - one capped line of navigator hints.</summary>
    private const int MaxPlatformRawChars = 160;

    public static void Map(
        IEndpointRouteBuilder app,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary,
        MicrophoneQualityLog? logOverride = null)
    {
        var group = app.MapGroup(Prefix);

        group.MapPost("/sample", async (HttpContext ctx, CancellationToken ct) =>
        {
            // Stores a per-account record, so it uses the refusing resolver: on hosted, a request with
            // no bound tenant is denied rather than silently written into the self-host partition.
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();

            SampleRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<SampleRequest>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.NoContent();
            }
            if (body is null) return Results.NoContent();

            var log = logOverride ?? MicrophoneQualityLog.ForTenant(tenant);
            log.Record(new MicrophoneQualityRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Device = Trim(body.Device, MaxDeviceChars),
                DeviceId = Trim(body.DeviceId, MaxDeviceIdChars),
                // The bucket is validated at FOLD time (anything unrecognised reads as unknown), so
                // storing what the client sent loses nothing and keeps the record honest.
                Platform = Trim(body.Platform, 16),
                PlatformRaw = Trim(body.PlatformRaw, MaxPlatformRawChars),
                Source = Trim(body.Source, 40),
                DurationSeconds = body.DurationSeconds,
                SampleRate = body.SampleRate,
                SpeechLevelDb = body.SpeechLevelDb,
                NoiseFloorDb = body.NoiseFloorDb,
                SignalToNoiseDb = body.SignalToNoiseDb,
                ClippedFraction = body.ClippedFraction,
                HighBandRatioDb = body.HighBandRatioDb,
                Narrowband = body.Narrowband,
                Rating = Trim(body.Rating, 16),
                Issues = Trim(body.Issues, 120),
            });

            return Results.NoContent();
        });

        group.MapGet("/summary", (HttpContext ctx) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();

            var days = ParseDays(ctx.Request.Query["days"]);
            var since = days is null ? (DateTime?)null : DateTime.UtcNow.AddDays(-days.Value);
            var log = logOverride ?? MicrophoneQualityLog.ForTenant(tenant);
            return Results.Json(MicrophoneQualityFold.Summarize(log.Load(since)));
        });

        group.MapGet("/detail", (HttpContext ctx) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();

            var days = ParseDays(ctx.Request.Query["days"]);
            var since = days is null ? (DateTime?)null : DateTime.UtcNow.AddDays(-days.Value);
            var log = logOverride ?? MicrophoneQualityLog.ForTenant(tenant);
            return Results.Json(MicrophoneQualityFold.Detail(log.Load(since)));
        });

        group.MapDelete("/history", (HttpContext ctx) =>
        {
            if (GatewayDictationEndpoint.ResolveTenant(ctx, tenantBoundary) is not { } tenant)
                return GatewayDictationEndpoint.NoTenantResult();
            var log = logOverride ?? MicrophoneQualityLog.ForTenant(tenant);
            return Results.Json(new { removed = log.Clear() });
        });
    }

    private static int? ParseDays(string? raw)
        => int.TryParse(raw, out var days) && days > 0 && days <= 365 ? days : null;

    private static string Trim(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }

    /// <summary>The measurement as the browser sends it. Mirrors DictationQualitySample in
    /// qualityReport.ts; the two are one contract and change together.</summary>
    private sealed record SampleRequest
    {
        [JsonPropertyName("source")] public string? Source { get; init; }
        [JsonPropertyName("device")] public string? Device { get; init; }
        [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
        [JsonPropertyName("platform")] public string? Platform { get; init; }
        [JsonPropertyName("platformRaw")] public string? PlatformRaw { get; init; }
        [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
        [JsonPropertyName("sampleRate")] public int SampleRate { get; init; }
        [JsonPropertyName("speechLevelDb")] public double SpeechLevelDb { get; init; }
        [JsonPropertyName("noiseFloorDb")] public double NoiseFloorDb { get; init; }
        [JsonPropertyName("signalToNoiseDb")] public double SignalToNoiseDb { get; init; }
        [JsonPropertyName("clippedFraction")] public double ClippedFraction { get; init; }
        [JsonPropertyName("highBandRatioDb")] public double HighBandRatioDb { get; init; }
        [JsonPropertyName("narrowband")] public bool Narrowband { get; init; }
        [JsonPropertyName("rating")] public string? Rating { get; init; }
        [JsonPropertyName("issues")] public string? Issues { get; init; }
    }
}
