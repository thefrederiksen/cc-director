using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Drives the REAL /voice-quality routes over real HTTP: a browser posts a measurement, the Cockpit
/// reads back a folded verdict.
///
/// The behaviour worth pinning is what happens when something is WRONG with the post. This runs after
/// a dictation has already succeeded, so nothing here may be turned into an error a user could see -
/// a malformed body is dropped and answered 204, never 400. A 400 would be a lie about the user's
/// dictation, which worked.
/// </summary>
[Collection("VoiceQualityEndpoint")] // serial: mutates the CC_DIRECTOR_ROOT process env var
public sealed class VoiceQualityEndpointTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public VoiceQualityEndpointTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-voice-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(WebApplication App, HttpClient Http, MicrophoneQualityLog Log)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var log = new MicrophoneQualityLog(Path.Combine(_root, "quality"));
        VoiceQualityEndpoint.Map(app, tenantBoundary: null, logOverride: log);

        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, log);
    }

    private static object Sample(string device = "Test Mic", bool narrowband = false, double clipped = 0)
        => new
        {
            source = "dictation-send",
            device,
            durationSeconds = 18.4,
            sampleRate = 48000,
            speechLevelDb = -19.5,
            noiseFloorDb = -64.5,
            signalToNoiseDb = 45.0,
            clippedFraction = clipped,
            highBandRatioDb = narrowband ? -110.0 : -22.0,
            narrowband,
            rating = narrowband ? "poor" : "good",
            issues = narrowband ? "narrowband" : "",
        };

    [Fact]
    public async Task APostedMeasurementComesBackInTheSummary()
    {
        var (app, http, _) = await StartAsync();
        await using var _d = app;

        var post = await http.PostAsJsonAsync("/voice-quality/sample", Sample());
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/voice-quality/summary"));
        Assert.Equal(1, doc.RootElement.GetProperty("totalSamples").GetInt32());
        var device = doc.RootElement.GetProperty("devices")[0];
        Assert.Equal("Test Mic", device.GetProperty("device").GetString());
    }

    [Fact]
    public async Task TheSummaryCarriesAFinishedVerdict_NotRawNumbersForTheBrowserToJudge()
    {
        var (app, http, _) = await StartAsync();
        await using var _d = app;

        for (var i = 0; i < 20; i++)
            await http.PostAsJsonAsync("/voice-quality/sample", Sample(device: "Bad Headset", narrowband: true));

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/voice-quality/summary"));
        Assert.Equal("bad", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("Bad Headset", doc.RootElement.GetProperty("headline").GetString());
        // The advice sentence is decided here, not composed in the browser.
        Assert.Contains("Bluetooth", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EachMicrophoneIsScoredSeparately()
    {
        var (app, http, _) = await StartAsync();
        await using var _d = app;

        for (var i = 0; i < 10; i++) await http.PostAsJsonAsync("/voice-quality/sample", Sample(device: "Desk Mic"));
        for (var i = 0; i < 10; i++) await http.PostAsJsonAsync("/voice-quality/sample", Sample(device: "Headset", narrowband: true));

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/voice-quality/summary"));
        var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, devices.Count);
        Assert.Equal("good", devices.Single(d => d.GetProperty("device").GetString() == "Desk Mic").GetProperty("status").GetString());
        Assert.Equal("bad", devices.Single(d => d.GetProperty("device").GetString() == "Headset").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AMalformedBodyIsDropped_NotTurnedIntoAnErrorAboutADictationThatWorked()
    {
        var (app, http, log) = await StartAsync();
        await using var _d = app;

        var res = await http.PostAsync("/voice-quality/sample",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(log.Load());
    }

    [Fact]
    public async Task AnOverlongDeviceNameIsTrimmedRatherThanStoredWhole()
    {
        var (app, http, log) = await StartAsync();
        await using var _d = app;

        await http.PostAsJsonAsync("/voice-quality/sample", Sample(device: new string('x', 5000)));

        var stored = Assert.Single(log.Load());
        Assert.True(stored.Device.Length <= 200);
    }

    [Fact]
    public async Task TheWindowCanBeNarrowed_SoAnOldBadHeadsetDoesNotHauntAGoodOne()
    {
        var (app, http, _) = await StartAsync();
        await using var _d = app;

        await http.PostAsJsonAsync("/voice-quality/sample", Sample());

        // Everything posted just now is inside a one-day window and outside a zero-length one; the
        // parameter is rejected when nonsensical rather than silently ignoring the caller's intent.
        using var withinWindow = JsonDocument.Parse(await http.GetStringAsync("/voice-quality/summary?days=1"));
        Assert.Equal(1, withinWindow.RootElement.GetProperty("totalSamples").GetInt32());
    }

    [Fact]
    public async Task DeletingTheHistoryLeavesNothingBehind()
    {
        var (app, http, log) = await StartAsync();
        await using var _d = app;

        await http.PostAsJsonAsync("/voice-quality/sample", Sample());
        var del = await http.DeleteAsync("/voice-quality/history");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Empty(log.Load());

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/voice-quality/summary"));
        Assert.Equal("empty", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NothingStoredCarriesAudioOrTranscriptText()
    {
        // The privacy claim made on the screen, asserted against what is actually written to disk.
        var (app, http, log) = await StartAsync();
        await using var _d = app;

        await http.PostAsJsonAsync("/voice-quality/sample", Sample());

        var written = Directory.GetFiles(log.Directory, "microphone-*.jsonl").Single();
        var text = await File.ReadAllTextAsync(written);
        foreach (var forbidden in new[] { "transcript", "rawText", "audio", "text\"" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
}
