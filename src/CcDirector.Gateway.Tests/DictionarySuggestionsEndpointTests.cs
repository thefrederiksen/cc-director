using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP end-to-end for the dictionary-suggestions API (devthrottle #2075). Boots the real
/// <see cref="RecordingEndpoints"/> - with the suggestion service and dismissal store wired over an isolated
/// SQLite database and an isolated CC_DIRECTOR_ROOT - on an ephemeral loopback port, then drives the whole
/// customer flow over HTTP: read the suggestions, apply a term (which must land in the glossary FILE as both
/// a vocabulary term and its wrong spellings), see it disappear from the suggestions, dismiss another, list
/// the dismissed, and restore it. This proves the acceptance criterion that everything the page does is
/// available over the API and that Apply writes the real glossary. Self-host (no tenant boundary) resolves
/// the single Local tenant throughout, exactly as a self-hosted install runs.
/// </summary>
public sealed class DictionarySuggestionsEndpointTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-dict-suggest-http-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevRoot;
    private readonly GatewayDbTestHarness _dbh = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public DictionarySuggestionsEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        _dbh.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedTerm(TranscriptStore store, DateTime at, (string spelling, int times)[] spellings)
    {
        var i = 0;
        foreach (var (spelling, times) in spellings)
            for (var n = 0; n < times; n++)
                store.Append(TenantId.Local, "dictation", $"we shipped the {spelling} change", null, false,
                    turnId: null, nowUtc: at.AddSeconds(i++));
    }

    [Fact]
    public async Task FullApiFlow_ReadApplyDismissRestore()
    {
        var db = _dbh.Open();
        var transcripts = new TranscriptStore(db);
        var dismissals = new DictionarySuggestionDismissalStore(db);
        var suggestions = new DictionarySuggestionService(
            transcripts, dismissals, TenantGlossary.Load, now: () => Base);

        // Two terms the model keeps getting wrong, neither in the (empty) glossary.
        SeedTerm(transcripts, Base, new[] { ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12) });
        SeedTerm(transcripts, Base.AddHours(1),
            new[] { ("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12) });

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add(baseUrl);
        RecordingEndpoints.Map(app, tenantBoundary: null, keyVault: null, history: null, audioArchive: null,
            suggestions: suggestions, dismissals: dismissals);
        await app.StartAsync();

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // 1. Read the suggestions - both terms, ranked, with evidence and counts.
            var list = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.NotNull(list);
            Assert.Equal(2, list!.Count);
            var mindzie = list.Suggestions.Single(s => s.Term == "mindzie");
            Assert.Equal(47, mindzie.WrongCount);
            Assert.Equal(91, mindzie.TotalCount);
            Assert.Contains(mindzie.Variants, v => v.Heard == "Mindsee");

            // The count route (the nav badge) agrees.
            var count = await http.GetFromJsonAsync<CountBody>("/ingest/dictionary/suggestions/count", Json);
            Assert.Equal(2, count!.Count);

            // 2. Apply "mindzie" - it must land in the glossary as a vocabulary term AND its wrong spellings
            //    must land as Common mistranscriptions, in one call.
            var applyResp = await http.PostAsJsonAsync("/ingest/dictionary/suggestions/apply",
                new { terms = new[] { "mindzie" } });
            applyResp.EnsureSuccessStatusCode();
            var apply = await applyResp.Content.ReadFromJsonAsync<ApplyBody>(Json);
            Assert.Equal(new[] { "mindzie" }, apply!.Applied.ToArray());
            Assert.Contains("mindzie", apply.Dictionary.Vocabulary);
            Assert.True(apply.Dictionary.CommonMistranscriptions.ContainsKey("mindzie"));
            Assert.Contains("Mindsee", apply.Dictionary.CommonMistranscriptions["mindzie"]);

            // 3. The glossary the editor route reads now shows the applied term too (same file).
            var glossary = await http.GetFromJsonAsync<DictionaryBody>("/ingest/dictionary", Json);
            Assert.Contains("mindzie", glossary!.Vocabulary);

            // 4. "mindzie" is now in the dictionary, so it is no longer suggested; only Frederiksen remains.
            var afterApply = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal(1, afterApply!.Count);
            Assert.Equal("Frederiksen", afterApply.Suggestions.Single().Term);

            // 5. Dismiss Frederiksen - it disappears and the count goes to zero.
            var dismissResp = await http.PostAsJsonAsync("/ingest/dictionary/suggestions/dismiss",
                new { term = "Frederiksen" });
            dismissResp.EnsureSuccessStatusCode();
            var afterDismiss = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal(0, afterDismiss!.Count);

            // 6. The dismissed list shows it with its evidence snapshot.
            var dismissed = await http.GetFromJsonAsync<DismissedBody>("/ingest/dictionary/dismissed", Json);
            var d = Assert.Single(dismissed!.Dismissed);
            Assert.Equal("Frederiksen", d.Term);
            Assert.Contains(d.Variants, v => v.Heard == "Fredriksson");

            // 7. Restore it - it becomes eligible again and is mined back into the suggestions.
            var restoreResp = await http.PostAsJsonAsync("/ingest/dictionary/dismissed/restore",
                new { term = "Frederiksen" });
            restoreResp.EnsureSuccessStatusCode();
            var afterRestore = await http.GetFromJsonAsync<SuggestionsBody>("/ingest/dictionary/suggestions", Json);
            Assert.Equal("Frederiksen", afterRestore!.Suggestions.Single().Term);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    // Response shapes (camelCase from the endpoint; case-insensitive binding).
    private sealed record VariantBody(string Heard, int Count);
    private sealed record SuggestionBody(string Term, List<VariantBody> Variants, int WrongCount, int TotalCount);
    private sealed record SuggestionsBody(List<SuggestionBody> Suggestions, int Count);
    private sealed record CountBody(int Count);
    private sealed record DictionaryBody(
        List<string> Vocabulary, Dictionary<string, List<string>> CommonMistranscriptions);
    private sealed record ApplyBody(DictionaryBody Dictionary, List<string> Applied, int Count);
    private sealed record DismissedItemBody(string Term, List<VariantBody> Variants, int WrongCount, int TotalCount);
    private sealed record DismissedBody(List<DismissedItemBody> Dismissed);
}
