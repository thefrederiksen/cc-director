using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// TTS fallback (mission Phase 2): when the cloud speech proxy quietly fails the primary voice provider
/// over to the backup, it sets the out-of-band <c>X-DevThrottle-TTS-Fallback</c> response header on the
/// SUCCESS. The Gateway must read that header, record it on the ready clip as a success-with-a-note, and
/// surface it through <see cref="WingmanVoiceService.ServedViaFallbackFor"/> for the VoiceDisplay fold -
/// WITHOUT ever treating it as an outage (no VoiceUnavailable state). The header's presence is the whole
/// signal; its value is never shown to a user.
/// </summary>
public sealed class WingmanVoiceFallbackTests
{
    /// <summary>A speech upstream that returns 200 + audio bytes, optionally with the fallback header.</summary>
    private sealed class SpeechStub(byte[] audio, bool withFallbackHeader) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            if (withFallbackHeader)
                resp.Headers.TryAddWithoutValidation("X-DevThrottle-TTS-Fallback", "openai");
            return Task.FromResult(resp);
        }
    }

    private static WingmanVoiceService ServiceWith(HttpMessageHandler handler, string persistPath)
    {
        var vault = new KeyVault(Path.Combine(Path.GetTempPath(), "wmvfb-" + Guid.NewGuid().ToString("N") + ".vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        // StoreSpokenAsync takes the spoken text directly, so the brain is never reached here.
        Func<Core.Configuration.WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => throw new InvalidOperationException("the brain must not be reached by a fallback header test");
        return new WingmanVoiceService(brain, vault, persistPath, ttsHttpClient: new HttpClient(handler));
    }

    // A unique SUBDIRECTORY per test, not a bare temp filename. The service derives its durable audio
    // cache dir from the persist path's DIRECTORY (".../voice-audio"), so tests that drop the persist file
    // straight in Path.GetTempPath() all share one TEMP/voice-audio - and a session id written by one test
    // is then loaded by another test's fresh service (LoadReadyAudio runs in the constructor), which breaks
    // them when classes run in parallel. An isolated directory keeps each test's cache to itself.
    private static string TempPersist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmvfb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "voice-sessions.json");
    }

    [Fact]
    public async Task Synthesis_WithFallbackHeader_MarksTheClipServedViaFallback_AndIsNotAnOutage()
    {
        var svc = ServiceWith(new SpeechStub(new byte[] { 1, 2, 3 }, withFallbackHeader: true), TempPersist());

        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice("sid-1"));                       // a fallback is a real, playable clip
        Assert.True(svc.ServedViaFallbackFor("sid-1"));           // ...noted as backup-served
        Assert.Null(svc.VoiceUnavailableFor("sid-1"));            // ...and NEVER an outage/unavailable state
    }

    [Fact]
    public async Task Synthesis_WithoutFallbackHeader_IsANormalReadyClip_NoFallbackNote()
    {
        var svc = ServiceWith(new SpeechStub(new byte[] { 1, 2, 3 }, withFallbackHeader: false), TempPersist());

        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice("sid-1"));
        Assert.False(svc.ServedViaFallbackFor("sid-1"));
    }

    [Fact]
    public void ServedViaFallback_SurvivesAGatewayRestart()
    {
        // The fallback fact is a property of the specific clip, persisted next to its audio so the notice
        // does not vanish on a gateway restart while that clip is still the current one.
        var persist = TempPersist();
        var first = ServiceWith(new SpeechStub(Array.Empty<byte>(), withFallbackHeader: false), persist);
        first.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 9, 9, 9 }, "audio/mpeg", servedViaFallback: true);
        Assert.True(first.ServedViaFallbackFor("sid-1"));

        // A fresh instance from the SAME persist path reloads the durable cache.
        var reloaded = ServiceWith(new SpeechStub(Array.Empty<byte>(), withFallbackHeader: false), persist);
        Assert.True(reloaded.HasVoice("sid-1"));
        Assert.True(reloaded.ServedViaFallbackFor("sid-1"));
    }
}
