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
/// counts each automatic attempt that produced no audio, tells the sweep when a session is due, and drops the
/// count at the three moments that end a turn's episode - a new turn, audio arriving, voice turned off.
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
    public void ANewTurn_ResetsTheSchedule()
    {
        var svc = Service();
        for (var i = 0; i < VoiceRetryPolicy.MaxAutomaticAttempts; i++)
            svc.NoteAutomaticAttemptProducedNoAudio(TenantId.Local, "sid", Turn);
        Assert.False(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));   // spent

        svc.OnSessionWorking(TenantId.Local, "sid");

        Assert.Equal(0, svc.AutomaticAttemptCountFor(TenantId.Local, "sid"));
        Assert.True(svc.IsDueForAutomaticRetry(TenantId.Local, "sid"));
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
