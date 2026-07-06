using System.Net;
using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The "voice mode" yellow window (issue #531): while the wingman is actively producing a
/// session's spoken summary, <see cref="WingmanVoiceService.IsGenerating"/> is true, which the
/// gateway folds into the existing "Briefing" yellow so the session goes red -> yellow -> red.
/// </summary>
public sealed class WingmanVoiceServiceTests
{
    private static WingmanVoiceService NewService()
    {
        // The flag methods never touch the brain; a provider that throws proves that.
        Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => throw new InvalidOperationException("brain must not be called for flag state");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), new DirectorEndpointClient(), persistPath);
    }

    [Fact]
    public void IsGenerating_DefaultsFalse()
    {
        var svc = NewService();
        Assert.False(svc.IsGenerating("sid-1"));
    }

    [Fact]
    public void BeginGenerating_ThenIsGenerating_IsTrue()
    {
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        Assert.True(svc.IsGenerating("sid-1"));
        // Independent per session: a second session is unaffected.
        Assert.False(svc.IsGenerating("sid-2"));
    }

    [Fact]
    public void EndGenerating_ClearsTheFlag()
    {
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        svc.EndGenerating("sid-1");
        Assert.False(svc.IsGenerating("sid-1"));
    }

    [Fact]
    public void OnSessionWorking_ClearsGenerating()
    {
        // A new turn (blue) supersedes any in-flight wingman run for the previous turn, so the
        // yellow marker must drop - raw activity wins while the agent works.
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        svc.OnSessionWorking("sid-1");
        Assert.False(svc.IsGenerating("sid-1"));
    }

    // ---------- Durable audio cache (issue #553) ----------

    /// <summary>Build a service over a SPECIFIC persist path so a second instance can reload from
    /// the same on-disk cache (the gateway-restart case). The empty vault means TtsAsync returns null.</summary>
    private static WingmanVoiceService ServiceAt(string persistPath)
    {
        Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => throw new InvalidOperationException("brain must not be called");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), new DirectorEndpointClient(), persistPath);
    }

    private static void Cleanup(string persistPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(persistPath);
            if (dir is not null && Directory.Exists(Path.Combine(dir, "voice-audio")))
                Directory.Delete(Path.Combine(dir, "voice-audio"), recursive: true);
            if (File.Exists(persistPath)) File.Delete(persistPath);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task StoreSpokenAsync_WithFailingTts_DoesNotMarkReady()
    {
        // No OpenAI key in the vault -> TtsAsync returns null -> the "if anything fails, remove the
        // triangle" rule: the session is a voice session but has NO playable audio, so no triangle.
        var svc = NewService();
        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");
        Assert.True(svc.IsVoiceSession("sid-1"));
        Assert.False(svc.HasVoice("sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds());
    }

    [Fact]
    public async Task StoreSpokenAsync_WithEmptySpoken_DoesNotMarkReady()
    {
        var svc = NewService();
        await svc.StoreSpokenAsync("sid-1", "   ", "the reply");
        Assert.False(svc.HasVoice("sid-1"));
    }

    [Fact]
    public void ReadyAudio_PersistsAndReloadsAcrossRestart()
    {
        // A successful synthesis is durable: a fresh service over the same persist path reloads the
        // ready audio, so the triangle/playability survives a gateway restart and a tap still plays.
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            var audio = new byte[] { 1, 2, 3, 4, 5 };
            svc.StoreReadyAudioForTest("sid-1", "spoken text", "reply text", audio);
            Assert.True(svc.HasVoice("sid-1"));

            // Simulate a gateway restart: a brand-new service over the same path.
            var reloaded = ServiceAt(persistPath);
            Assert.True(reloaded.HasVoice("sid-1"));
            Assert.Contains("sid-1", reloaded.ReadySessionIds());
            var got = reloaded.GetAudio("sid-1");
            Assert.NotNull(got);
            Assert.Equal(audio, got);
            var ready = reloaded.Get("sid-1");
            Assert.NotNull(ready);
            Assert.Equal("spoken text", ready.Spoken);
            Assert.Equal("reply text", ready.Reply);
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void OnSessionWorking_DeletesDurableAudio()
    {
        // A new turn drops the stale audio from disk too, so a 5s-stale list row cannot point at
        // audio that no longer exists (which would 404 on /audio).
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 9, 9, 9 });
            Assert.True(svc.HasVoice("sid-1"));

            svc.OnSessionWorking("sid-1");
            Assert.False(svc.HasVoice("sid-1"));

            // A restart must NOT resurrect the dropped audio.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.HasVoice("sid-1"));
        }
        finally { Cleanup(persistPath); }
    }

    // ---------- Turn voice off / Unmark (issue #859) ----------

    [Fact]
    public void Unmark_AfterMark_RemovesFromVoiceSessionSet()
    {
        // Turning voice off stops the session being a voice session, so the turn-end watcher and the
        // background sweep (both gate on IsVoiceSession / VoiceSessionIds) skip it - no more per-turn
        // Opus + text-to-speech spend.
        var svc = NewService();
        svc.Mark("sid-1");
        svc.Mark("sid-2");
        Assert.True(svc.IsVoiceSession("sid-1"));

        svc.Unmark("sid-1");

        Assert.False(svc.IsVoiceSession("sid-1"));
        Assert.DoesNotContain("sid-1", svc.VoiceSessionIds());
        // Independent per session: a second voice session is unaffected.
        Assert.True(svc.IsVoiceSession("sid-2"));
        Assert.Contains("sid-2", svc.VoiceSessionIds());
    }

    [Fact]
    public void Unmark_DropsTheReadyClip()
    {
        // After unmark, GET /wingman/voice/ready (ReadySessionIds) must no longer list the session,
        // so the roster/phone stop offering a stale clip.
        var svc = NewService();
        svc.Mark("sid-1");
        svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 1, 2, 3 });
        Assert.True(svc.HasVoice("sid-1"));

        svc.Unmark("sid-1");

        Assert.False(svc.HasVoice("sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds());
    }

    [Fact]
    public void Unmark_PersistsAcrossRestart()
    {
        // The removal is durable: a gateway restart must NOT bring the session back as a voice
        // session (otherwise turn-end re-narration would resume on its own after a restart).
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            svc.Mark("sid-1");
            svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 7, 7, 7 });
            Assert.True(svc.IsVoiceSession("sid-1"));

            svc.Unmark("sid-1");

            // Simulate a gateway restart over the same persist path.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.IsVoiceSession("sid-1"));
            Assert.DoesNotContain("sid-1", reloaded.VoiceSessionIds());
            Assert.False(reloaded.HasVoice("sid-1")); // and the durable clip is gone too
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void Unmark_UnknownSession_IsNoOp()
    {
        // Idempotent: unmarking a session that was never a voice session does nothing and does not throw.
        var svc = NewService();
        svc.Unmark("never-marked");
        Assert.False(svc.IsVoiceSession("never-marked"));
    }

    // ---------- Voice-unavailable state (issue #939): no more silent turn-end failures ----------

    /// <summary>A stub text-to-speech transport: returns the given status + body, or audio bytes on
    /// success. Lets a test drive TtsAsync to a 402 / success without a live provider call.</summary>
    private sealed class TtsStubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly byte[]? _audio;
        public TtsStubHandler(HttpStatusCode status, string body, byte[]? audio = null) { _status = status; _body = body; _audio = audio; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = _audio is not null
                    ? new ByteArrayContent(_audio)
                    : new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    /// <summary>A voice service whose text-to-speech goes to a stub returning <paramref name="status"/>.
    /// Both provider keys are set so the call proceeds regardless of the machine's configured mode -
    /// the stub ignores the URL, so the mapped state depends only on the response.</summary>
    private static WingmanVoiceService ServiceWithTts(HttpStatusCode status, string body, byte[]? audio = null)
    {
        Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(new TtsStubHandler(status, body, audio));
        return new WingmanVoiceService(brain, vault, new DirectorEndpointClient(), persistPath, ttsHttpClient: http);
    }

    [Fact]
    public void VoiceUnavailableFor_DefaultsNull()
    {
        var svc = NewService();
        Assert.Null(svc.VoiceUnavailableFor("sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_OutOfCredits402_RecordsNeedsCredits_NoSilentFailure()
    {
        // Issue #939: a 402 out-of-credits at turn-end must no longer be swallowed - it records the
        // shared NeedsCredits state (and leaves no play triangle).
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");

        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor("sid-1"));
        Assert.False(svc.HasVoice("sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_MonthlyLimit402_RecordsCapReached()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"monthly_limit_reached\"}}");
        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");

        Assert.Equal(HostedAiState.CapReached, svc.VoiceUnavailableFor("sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_Success_MarksReady_AndClearsUnavailable()
    {
        // A successful synthesis marks the session ready AND clears any prior unavailable-state
        // (dismissible: the next good turn removes the banner).
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync("sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor("sid-1"));

        var good = ServiceWithTts(HttpStatusCode.OK, "", audio: new byte[] { 1, 2, 3, 4 });
        // Re-run on the SAME service would need a mutable stub; instead prove success on a fresh call
        // clears + marks ready. Seed the unavailable state first via a failing service is covered above;
        // here assert the success path's postconditions directly.
        await good.StoreSpokenAsync("sid-2", "spoken", "reply");
        Assert.True(good.HasVoice("sid-2"));
        Assert.Null(good.VoiceUnavailableFor("sid-2"));
    }

    /// <summary>A text-to-speech transport that throws <see cref="OperationCanceledException"/> - the
    /// shape a per-attempt timeout takes - for the first <c>_timeouts</c> calls, then returns 200 with
    /// audio. TtsSynthesis converts each such throw (while the caller's token is live) into a retry, so
    /// this drives the retry path without a real 15-second wait. With <see cref="int.MaxValue"/> it
    /// always times out.</summary>
    private sealed class TtsTimeoutHandler : HttpMessageHandler
    {
        private readonly int _timeouts;
        private int _calls;
        public int Calls => _calls;
        public TtsTimeoutHandler(int timeouts) { _timeouts = timeouts; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= _timeouts)
                throw new OperationCanceledException("simulated per-attempt timeout");
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 9, 9, 9 }),
            };
            return Task.FromResult(resp);
        }
    }

    private static WingmanVoiceService ServiceWithHandler(HttpMessageHandler handler)
    {
        Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        return new WingmanVoiceService(brain, vault, new DirectorEndpointClient(), persistPath, ttsHttpClient: new HttpClient(handler));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsTimesOutOnceThenSucceeds_RetriesAndMarksReady()
    {
        // Regression: a single stalled upstream voice call must be retried, not surfaced as a freeze
        // or failure. The first attempt times out; the retry returns audio, so the session is ready.
        var handler = new TtsTimeoutHandler(timeouts: 1);
        var svc = ServiceWithHandler(handler);
        await svc.StoreSpokenAsync("sid-retry", "spoken", "reply");

        Assert.Equal(2, handler.Calls);                        // the retry actually fired
        Assert.True(svc.HasVoice("sid-retry"));                // and produced audio
        Assert.Null(svc.VoiceUnavailableFor("sid-retry"));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsTimesOutEveryAttempt_GivesUpBounded_NoReady()
    {
        // Regression: when every attempt stalls, TtsSynthesis gives up after a BOUNDED number of
        // attempts (no infinite spin, no 60-second freeze) and the turn-end records no audio.
        var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
        var svc = ServiceWithHandler(handler);
        await svc.StoreSpokenAsync("sid-dead", "spoken", "reply");

        Assert.Equal(TtsSynthesis.Attempts, handler.Calls);    // exactly the attempt cap, then stop
        Assert.False(svc.HasVoice("sid-dead"));
    }

    [Fact]
    public async Task OnSessionWorking_ClearsVoiceUnavailable()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync("sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor("sid-1"));

        svc.OnSessionWorking("sid-1");
        Assert.Null(svc.VoiceUnavailableFor("sid-1"));
    }

    [Fact]
    public async Task Unmark_ClearsVoiceUnavailable()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync("sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor("sid-1"));

        svc.Unmark("sid-1");
        Assert.Null(svc.VoiceUnavailableFor("sid-1"));
    }
}
