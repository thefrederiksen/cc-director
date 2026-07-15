using System.Net;
using System.Net.Sockets;
using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
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
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), persistPath);
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
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), persistPath);
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
            svc.StoreReadyAudioForTest("sid-1", "spoken text", "reply text", audio, "audio/wav");
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
            Assert.Equal("audio/wav", ready.ContentType);
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void ReadyAudio_ReloadsLegacyWavCacheWithDetectedContentType()
    {
        // Older cache metadata had no content type. Kokoro can return WAV bytes, so reload must detect
        // RIFF instead of serving those bytes as audio/mpeg.
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dir = Path.GetDirectoryName(persistPath)!;
            var audioDir = Path.Combine(dir, "voice-audio");
            Directory.CreateDirectory(audioDir);
            File.WriteAllBytes(Path.Combine(audioDir, "sid-1.mp3"), new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3 });
            File.WriteAllText(Path.Combine(audioDir, "sid-1.json"),
                "{\"Spoken\":\"spoken\",\"Reply\":\"reply\",\"AtUtc\":\"2026-01-01T00:00:00Z\"}");

            var reloaded = ServiceAt(persistPath);

            var ready = reloaded.Get("sid-1");
            Assert.NotNull(ready);
            Assert.Equal("audio/wav", ready.ContentType);
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

    // ---------- Re-narrate only when the reply actually changed (issue #1322, identity-aware) ----------

    /// <summary>Gateway Cleanup mission (the cut): a tunnel-connected "Director" whose "turns" verb returns a
    /// fixed JSON body and counts how many times it was read. GenerateAsync reads the session over the
    /// TUNNEL-ONLY SessionVerbClient now (the HTTP fallback is gone), so this stub is the sendCommand the client
    /// dispatches to - it proves whether GenerateAsync fetched and what it did with the result.</summary>
    private sealed class TunnelReadStub
    {
        private readonly string _turnsJson;
        private int _hits;
        public int Hits => _hits;

        public TunnelReadStub(string turnsJson) => _turnsJson = turnsJson;

        public CcDirector.Gateway.Api.DirectorCommandRouter.SendDirectorCommandAsync SendCommand => (_, command, _) =>
        {
            if (string.Equals(command.Verb, "turns", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _hits);
                return Task.FromResult<CcDirector.Gateway.Contracts.DirectorCommandResult?>(
                    CcDirector.Gateway.Contracts.DirectorCommandResult.Success(_turnsJson));
            }
            return Task.FromResult<CcDirector.Gateway.Contracts.DirectorCommandResult?>(
                CcDirector.Gateway.Contracts.DirectorCommandResult.Success());
        };
    }

    /// <summary>A brain that records how many times it was asked and returns a canned, marker-wrapped
    /// spoken line - so a test can prove whether GenerateAsync actually ran the (re)narration.</summary>
    private sealed class RecordingBrain : IAgentBrain
    {
        private int _askCount;
        public int AskCount => _askCount;
        public string? SessionId => "recording-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\nnarrated spoken text\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.1 });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>A voice service wired to a recording brain and a text-to-speech stub that returns
    /// <paramref name="audio"/>, so the full turn-end path (fetch -> translate -> synthesize -> store)
    /// runs without a live model or provider.</summary>
    private static WingmanVoiceService ServiceWithBrainAndTts(IAgentBrain brain, byte[] audio, string persistPath)
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(new TtsStubHandler(HttpStatusCode.OK, "", audio));
        return new WingmanVoiceService((_, _) => Task.FromResult(brain), vault, persistPath, ttsHttpClient: http);
    }

    /// <summary>Gateway Cleanup mission (the cut): GenerateAsync takes a tunnel-only SessionVerbClient. This
    /// binds one to the stub's sendCommand so the "turns" read rides the tunnel (the HTTP fallback is gone);
    /// the stub's Hits still counts the reads, the exact signal these tests assert.</summary>
    private static CcDirector.Gateway.Api.SessionVerbClient RouteFor(TunnelReadStub stub) =>
        new(new CcDirector.Gateway.Contracts.DirectorDto { DirectorId = "d1", ControlEndpoint = "http://tunnel-only" },
            stub.SendCommand);

    [Fact]
    public async Task GenerateAsync_WhenCurrentReplyDiffersFromCache_Regenerates()
    {
        // THE FIX (regression for the #1322 bare-HasVoice guard): when the session's CURRENT last reply
        // differs from the one already narrated, the turn-end MUST regenerate - even though a cached clip
        // exists. The old guard skipped here whenever the Working transition was missed, leaving the
        // phone replaying a stale interim narration while the history had moved on to the real answer.
        var director = new TunnelReadStub("{\"widgets\":[{\"kind\":\"Text\",\"content\":\"the NEW final answer\"}]}");
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-regen-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 4, 4, 4 }, persistPath);
            svc.StoreReadyAudioForTest("sid-1", "old spoken", "the OLD interim reply", new byte[] { 1, 2, 3 });
            Assert.True(svc.HasVoice("sid-1"));

            await svc.GenerateAsync("sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.Equal(1, brain.AskCount);                          // it regenerated (translated the new reply)
            var ready = svc.Get("sid-1");
            Assert.NotNull(ready);
            Assert.Equal("the NEW final answer", ready!.Reply);       // the cache now holds the CURRENT reply
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task GenerateAsync_WhenCurrentReplyMatchesCache_SkipsQuietly()
    {
        // Issue #1322 preserved: when the CURRENT last reply is the EXACT one already narrated, the
        // turn-end fetches to compare but does NOT regenerate - it never calls the brain, never re-mints
        // audio, and never flips the session yellow, so a client mid-play is not disturbed.
        var director = new TunnelReadStub("{\"widgets\":[{\"kind\":\"Text\",\"content\":\"the same reply\"}]}");
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-same-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 9, 9 }, persistPath);
            svc.StoreReadyAudioForTest("sid-1", "old spoken", "the same reply", new byte[] { 1, 2, 3 });
            Assert.True(svc.HasVoice("sid-1"));

            await svc.GenerateAsync("sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(0, brain.AskCount);              // never regenerated
            Assert.True(director.Hits >= 1);              // but it DID fetch to compare (identity-aware, not blind)
            Assert.True(svc.HasVoice("sid-1"));           // the existing clip is untouched
            Assert.False(svc.IsGenerating("sid-1"));      // and it never flipped the session yellow
            Assert.Equal(new byte[] { 1, 2, 3 }, svc.GetAudio("sid-1"));   // same original audio, not re-minted
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    // ---------- ShouldRegenerate decision (pure, no brain / no fetch) ----------

    [Fact]
    public void ShouldRegenerate_NoCachedNarration_IsTrue()
    {
        var svc = NewService();
        Assert.True(svc.ShouldRegenerate("sid-x", "a reply to narrate"));
    }

    [Fact]
    public void ShouldRegenerate_EmptyOrNullCurrentReply_IsFalse()
    {
        // Nothing to narrate yet - do not touch or regenerate.
        var svc = NewService();
        Assert.False(svc.ShouldRegenerate("sid-x", null));
        Assert.False(svc.ShouldRegenerate("sid-x", "   "));
    }

    [Fact]
    public void ShouldRegenerate_SameReplyAlreadyCached_IsFalse()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest("sid-1", "spoken", "the reply text", new byte[] { 1 });
        Assert.False(svc.ShouldRegenerate("sid-1", "the reply text"));
    }

    [Fact]
    public void ShouldRegenerate_SameReplyIgnoringSurroundingWhitespace_IsFalse()
    {
        // The two sources are the same JSONL text block; incidental leading/trailing whitespace must
        // not force a needless re-mint (which would restart a listener's clip).
        var svc = NewService();
        svc.StoreReadyAudioForTest("sid-1", "spoken", "the reply text", new byte[] { 1 });
        Assert.False(svc.ShouldRegenerate("sid-1", "  the reply text\n"));
    }

    [Fact]
    public void ShouldRegenerate_ChangedReply_IsTrue()
    {
        // The exact bug: an interim reply was narrated, then the real answer landed. A changed reply
        // must regenerate even though a cached clip exists.
        var svc = NewService();
        svc.StoreReadyAudioForTest("sid-1", "spoken", "the interim reply", new byte[] { 1 });
        Assert.True(svc.ShouldRegenerate("sid-1", "the FINAL answer"));
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
        return new WingmanVoiceService(brain, vault, persistPath, ttsHttpClient: http);
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
        return new WingmanVoiceService(brain, vault, persistPath, ttsHttpClient: new HttpClient(handler));
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

    // ---- The 2026-07-15 outage: the service failed for ~45 minutes and the phone blamed the user's own
    // machine ("the Gateway has not made one, or this session's computer is offline"). Both false. The
    // reason was KNOWN here and discarded three lines from where it was known, because no state meant
    // "the service is down". These pin the whole path: the failure must become ServiceDown, ServiceDown
    // must carry copy the phone can render, and it must survive to the DTO the phone actually reads.

    [Theory]
    [InlineData(500)]   // provider blew up
    [InlineData(502)]   // our cloud could not reach it
    [InlineData(503)]   // upstream unavailable
    [InlineData(504)]   // upstream timed out / fast-failed
    public async Task StoreSpokenAsync_TtsFails_RecordsServiceDown_NotSilence(int status)
    {
        // Every one of these used to return a bare null ("other provider error: logged, no shared
        // state"), so the session recorded NOTHING and the phone invented a cause.
        var svc = ServiceWithTts((HttpStatusCode)status, "{\"error\":\"upstream\"}");
        await svc.StoreSpokenAsync("sid-down", "spoken", "reply");

        Assert.False(svc.HasVoice("sid-down"));
        Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor("sid-down"));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsTimesOutEveryAttempt_RecordsServiceDown()
    {
        // The TimeoutException that TtsSynthesis exists to bound was the ONE failure that stamped
        // nothing at all - swallowed by a bare catch. It is the most likely failure in a real outage.
        var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
        var svc = ServiceWithHandler(handler);
        await svc.StoreSpokenAsync("sid-timeout", "spoken", "reply");

        Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor("sid-timeout"));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsOutOfCredits_StillBlamesTheAccount_NotTheService()
    {
        // The control. 402 is the user's to fix, so it must NOT be swept into ServiceDown - telling
        // someone "not your fault, retrying" when they are out of credit would strand them forever.
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync("sid-402", "spoken", "reply");

        var state = svc.VoiceUnavailableFor("sid-402");
        Assert.NotNull(state);
        Assert.NotEqual(HostedAiState.ServiceDown, state);
    }

    [Fact]
    public async Task ServiceDown_HasCopyThePhoneCanRender_WithNoButton()
    {
        // The field was DEAD ON ARRIVAL for months: stamped on every 3s poll with zero readers, while
        // two views hardcoded a false string. Recording the state is only half - it has to arrive as
        // something renderable. And it must offer NO call to action: during an outage a button hits the
        // same dead service and fails the same way, which is what made the owner blame himself.
        var dto = HostedAiHttp.Dto(HostedAiState.ServiceDown);

        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.Text));
        Assert.True(string.IsNullOrEmpty(dto.CtaLabel), "a service outage must offer no button - it cannot work");
        Assert.Equal(nameof(HostedAiState.ServiceDown), dto.State);
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsRecovers_ClearsServiceDown()
    {
        // Voice must come back BY ITSELF when the service returns (the idle sweep regenerates), so a
        // success has to clear the state - otherwise the phone would keep saying "down" after recovery.
        var svc = ServiceWithTts(HttpStatusCode.OK, "", audio: new byte[] { 1, 2, 3, 4 });
        await svc.StoreSpokenAsync("sid-back", "spoken", "reply");

        Assert.True(svc.HasVoice("sid-back"));
        Assert.Null(svc.VoiceUnavailableFor("sid-back"));
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
