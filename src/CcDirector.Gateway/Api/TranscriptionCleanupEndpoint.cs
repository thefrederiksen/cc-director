using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Runs ONLY the deterministic dictionary cleanup over caller-supplied text and a caller-supplied term
/// list - no audio, no transcription. This is the text-in / text-out engine the multilingual evaluation
/// harness drives (hold transcription constant, feed a raw transcript + a per-fixture term list, score
/// the correction), and it lets any agent test the cleanup on arbitrary text and terms.
///
///   POST /transcription/cleanup   { "text": "...", "terms": ["mindzie", ...], "language": "en"? }
///       -&gt; 200 { cleaned, applied, changes:[{find,replace}], language }
///       -&gt; 400 { error }   missing text
///
/// The cleanup is exactly the production path (<see cref="CleanupOrchestrator"/> validated by
/// <see cref="TranscriptEditEngine"/>), so what the harness scores is what ships. Language is carried
/// through untouched for now (the matcher is language-agnostic); it lets the harness label results and
/// pick per-language scoring. Inherits the host-wide token middleware like every other Gateway route.
/// </summary>
internal static class TranscriptionCleanupEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/transcription/cleanup", async (HttpContext ctx) =>
        {
            CleanupRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<CleanupRequest>(ctx.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "body must be JSON { text, terms[], language? }" }); }

            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "field 'text' is required" });

            var terms = (req.Terms ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

            FileLog.Write($"[TranscriptionCleanupEndpoint] POST /transcription/cleanup: chars={req.Text.Length}, "
                          + $"terms={terms.Length}, language={req.Language ?? "?"}");

            // The fuzzy matcher is enabled EXPLICITLY here, and only here. This route is the
            // text-in/text-out evaluation surface: the caller hands over the term list itself and gets
            // the answer back rather than having it substituted into their dictation, so measuring the
            // matcher is the whole point of the endpoint. It carries no alias map, so with the
            // field-wide default it could never correct anything and would be a silent no-op that
            // still returned 200. Live dictation keeps the safe default - see
            // DictationProfile.FuzzyCorrectionEnabled.
            var dictionary = new DictationDictionary(
                terms,
                new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, DictationProfile>
                {
                    ["default"] = new("default", CleanupEnabled: true, FuzzyCorrectionEnabled: true),
                });

            var outcome = await new CleanupOrchestrator().CleanAsync(req.Text, dictionary, "default", ctx.RequestAborted);

            return Results.Json(new
            {
                cleaned = outcome.Text,
                applied = outcome.Applied,
                reason = outcome.Reason,
                changes = outcome.ChangedWords.Select(e => new { find = e.Find, replace = e.Replace }),
                language = req.Language,
            });
        });
    }

    private sealed record CleanupRequest
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("terms")] public string[]? Terms { get; init; }
        [JsonPropertyName("language")] public string? Language { get; init; }
    }
}
