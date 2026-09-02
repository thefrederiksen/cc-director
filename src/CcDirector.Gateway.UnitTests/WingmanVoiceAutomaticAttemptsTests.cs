using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The bookkeeping behind the narration retry schedule (<see cref="VoiceRetryPolicy"/>): the voice service
/// counts each automatic attempt that produced no audio, and tells the sweep when a session is due.
///
/// WHAT ENDS A TURN'S EPISODE, precisely, because a review found this list wrong once already. The count is
/// dropped when AUDIO ARRIVES and when VOICE IS TURNED OFF. It is NOT dropped on an observed Working
/// transition: the count is keyed on the turn, so a genuinely different reply starts clean without being
/// told, and clearing on the sampled edge would let a work cycle that produced no new reply re-arm a spent
/// turn for another five tries.
/// </summary>
public sealed class WingmanVoiceAutomaticAttemptsTests : IDisposable
{
    /// <summary>The turn these attempts are about - the schedule counts against a turn, never a session.</summary>
    private const string Turn = "turn-abc";

    private readonly GatewayDbTestHarness _settingsData = new();
    private TenantSettingsResolver? _settings;

    private TenantSettingsResolver Settings =>
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));

    public void Dispose() => _settingsData.Dispose();

    /// <summary>A speech upstream that answers 200 with a few audio bytes, for the "audio arrived" reset.</summary>
    private sealed class SpeechStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(resp);
        }
    }

    private WingmanVoiceService Service()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmvat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vault = new KeyVault(Path.Combine(dir, "test.vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        Func<TenantId, Core.Configuration.WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _) => throw new InvalidOperationException("the brain must not be reached by an attempt-count test");
        return new WingmanVoiceService(brain, vault, Settings, Path.Combine(dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(new SpeechStub()));
    }

    [Fact]
    public void AFreshSession_HasNoAttempts_AndIsDue()
    {
        var svc = Service();
        Assert.Null(svc.AutomaticAttemptsFor(TenantId.Local, "sid"));
        Assert.Equal(0, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
        Assert.True(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));
    }

    [Fact]
    public void EachFailedAttempt_Counts_AndTheSessionStopsBeingDueUntilTheSpacingPasses()
    {
        var svc = Service();

        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);
        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);

        Assert.Equal(2, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
        // Just recorded, so the spacing has not passed: the sweep's next pass must leave it alone.
        Assert.False(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));
    }

    [Fact]
    public void TheCountIsPerSession_AndPerTenant()
    {
        var svc = Service();
        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "a", Turn);

        Assert.Equal(1, svc.AutomaticAttemptCountFor(TenantId.Local, "a"));
        Assert.Equal(0, svc.AutomaticAttemptCountFor(TenantId.Local, "b"));
        Assert.True(svc.IsDueForAutomaticRetry(TenantId.Local, "b"));
    }

    [Fact]
    public void AnAttemptAtADifferentTurn_StartsTheCountAgain_WithoutAnyObservedTransition()
    {
        // THE RECOVERY PATH that makes a spent schedule safe. The Working transition is observed on a sampled
        // boundary and a quick turn can slip through it entirely; if the count only reset on that edge, the
        // new turn would inherit a spent schedule and never be narrated. It resets on the turn's IDENTITY
        // instead, so the first attempt at a new reply starts from one - no transition required.
        var svc = Service();
        for (var i = 0; i < VoiceRetryPolicy.MaxAutomaticAttempts; i++)
            svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);
        Assert.False(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));   // spent, for THAT turn

        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", "a-completely-different-turn");

        Assert.Equal(1, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
        Assert.False(VoiceRetryPolicy.IsExhausted(svc.AutomaticAttemptsFor(TenantId.Local, "sid")));
    }

    [Fact]
    public void AnObservedWorkingTransition_DoesNOTResetTheSchedule()
    {
        // THIS TEST ASSERTED THE OPPOSITE UNTIL A REVIEW LOOKED AT IT, and it passed - by calling
        // OnSessionWorking and CALLING that "a new turn" in its own name. It never supplied a different
        // reply, so it proved nothing about turns at all; it proved that clearing on the transition cleared
        // on the transition.
        //
        // Clearing here is a real weakening, not a harmless one. A work cycle that produces no new reply - a
        // cancelled turn, an agent that thinks and answers nothing, a duplicate or delayed state
        // notification - would re-arm the SAME spent turn for another five attempts, and a session that kept
        // twitching would keep the Generate button off the screen indefinitely while the Gateway hammered a
        // turn it had already given up on. The whole reason the count is keyed on the turn is that this edge
        // is sampled and cannot be trusted; resetting on it puts the trust straight back.
        var svc = Service();
        for (var i = 0; i < VoiceRetryPolicy.MaxAutomaticAttempts; i++)
            svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);
        Assert.False(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));   // spent

        svc.OnSessionWorking(TenantId.Local, "sid");

        Assert.Equal(VoiceRetryPolicy.MaxAutomaticAttempts, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
    }

    [Fact]
    public void ARealNewTurn_ResetsTheSchedule_BecauseItIsADifferentReply()
    {
        // What actually restores the schedule: an attempt at a DIFFERENT turn. No transition needs to have
        // been observed, which is the point - see AnAttemptAtADifferentTurn_StartsTheCountAgain above for
        // the same claim on the counting side, and VoiceAttempts.TurnKey for why identity beats the edge.
        var svc = Service();
        for (var i = 0; i < VoiceRetryPolicy.MaxAutomaticAttempts; i++)
            svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);

        Assert.False(VoiceRetryPolicy.IsDue(svc.AutomaticAttemptsFor(TenantId.Local, "sid"), DateTime.UtcNow, Turn));
        Assert.True(VoiceRetryPolicy.IsDue(svc.AutomaticAttemptsFor(TenantId.Local, "sid"), DateTime.UtcNow, "a-different-turn"));
    }

    [Fact]
    public async Task AudioArriving_ResetsTheSchedule()
    {
        var svc = Service();
        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);
        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);

        await svc.StoreSpokenAsync(TenantId.Local, "sid", "a spoken summary", "the reply");

        Assert.True(svc.HasVoice(TenantId.Local, "sid"));
        Assert.Equal(0, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
    }

    [Fact]
    public void VoiceTurnedOff_ResetsTheSchedule()
    {
        var svc = Service();
        svc.Mark(TenantId.Local, "sid");
        svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);

        svc.Unmark(TenantId.Local, "sid");

        Assert.Equal(0, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
    }
}
