using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Screens;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// ROW 7 of the Terminal Rules phase 0 proofs
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>): a voice turn completes with NO
/// tunnel screen read.
///
/// THE PASS CONDITION IS A CONJUNCTION OF TWO POSITIVE ARTIFACTS, and that is the whole point of this
/// class. "The counter did not move" is satisfied by a turn that crashed on its first line, by a turn
/// that was never triggered, and by a turn that silently produced nothing - so the counter alone proves
/// the wrong half of the claim. Both halves are asserted here:
///
///   1. THE TURN COMPLETED, evidenced by what it PRODUCED - narration audio exists for that session
///      afterwards, read back through <see cref="WingmanVoiceService.HasVoice"/>. Never "no error was
///      logged".
///   2. AND the tunnel screen-pull counter, read immediately before and immediately after THAT turn,
///      differs by zero.
///
/// And a third test is the KNOWN-BAD CONTROL: the same turn with nothing in the screen store moves the
/// same counter by one. Without it the zero above would be a number that cannot rise, which proves
/// nothing at all.
///
/// WHAT IS STUBBED, said plainly rather than glossed. The model leg and the speech leg are stubs - a
/// brain that returns a fixed sentence and an HTTP client that returns fixed audio bytes. So this does
/// NOT exercise a real provider, and it is not a claim about one. Everything between them is the real
/// product: the real <see cref="WingmanVoiceService"/>, the real <see cref="GatewayScreenReader"/>, the
/// real <see cref="SessionScreenStore"/>, the real freshness rule, and the real
/// <see cref="SessionVerbClient"/> whose tunnel sends are what the counter counts.
///
/// <b>Proven against the mapped model, not the migrated schema.</b> See <see cref="ScreenStoreTestDb"/>.
/// </summary>
[Collection(ScreenPullCounterCollection.Name)]
public class VoiceTurnCostsNoTunnelScreenReadTests
{
    private const string Tenant = "local";
    private const string DirectorId = "director-voice";
    private const string SessionId = "33333333-3333-3333-3333-333333333333";
    private const string ConnectionId = "conn-voice";

    private static readonly string[] ScreenRows =
    {
        "> the agent finished and is waiting",
        "",
        "  Try \"what did you change?\"",
    };

    /// <summary>A brain that answers with a fixed sentence, so the narration leg completes without a
    /// provider. It records whether it was called, because a narration that never asked the model is not
    /// a turn that ran.</summary>
    private sealed class FixedBrain : IAgentBrain
    {
        public int Asks;
        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Asks);
            return Task.FromResult(new AskResult { Text = "The agent finished the change and is waiting for you." });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    /// <summary>Speech that answers with fixed bytes, so narration AUDIO exists and the turn has a
    /// completion artifact to show.</summary>
    private sealed class FixedSpeechHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x01, 0x02, 0x03 }),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(response);
        }
    }

    /// <summary>The tunnel. It answers the conversation read the narration needs, and counts screen-grid
    /// pulls with rows DIFFERENT from the stored ones so a store answer is distinguishable from a tunnel
    /// answer by content rather than by a label.</summary>
    private sealed class FakeTunnel
    {
        public int ScreenGridCalls;

        public DirectorCommandRouter.SendDirectorCommandAsync Send => (directorId, command, ct) =>
        {
            switch (command.Verb)
            {
                case "turns":
                    var turns = new TurnsResponse
                    {
                        SessionId = command.SessionId,
                        Status = "ok",
                        Widgets = new List<TurnWidgetDto>
                        {
                            new() { Kind = "Text", Header = "You", Content = "please change the thing" },
                            new() { Kind = "Text", Header = "Assistant", Content = "Done - I changed the thing and the tests pass." },
                        },
                    };
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(Json(turns)));

                case "screen-grid":
                    ScreenGridCalls++;
                    var grid = new ScreenGridResponse
                    {
                        SessionId = command.SessionId,
                        Rows = new List<string> { "PULLED OVER THE TUNNEL" },
                        CursorRow = 0,
                        CursorCol = 0,
                        CursorVisible = true,
                        HasGrid = true,
                    };
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(Json(grid)));

                default:
                    return Task.FromResult<DirectorCommandResult?>(null);
            }
        };

        private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(
            value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private sealed class Rig : IDisposable
    {
        private readonly GatewayDbTestHarness _settingsData = new();
        public ScreenStoreTestDb Db { get; } = new();
        public SessionScreenStore Store { get; }
        public PushedSessionStore Pushed { get; } = new();
        public GatewayScreenReader Reader { get; }
        public FakeTunnel Tunnel { get; } = new();
        public FixedBrain Brain { get; } = new();
        public WingmanVoiceService Voice { get; }
        private readonly string _persist;
        private readonly HttpClient _speech = new(new FixedSpeechHandler());

        public Rig()
        {
            Store = Db.StoreFor(Tenant);
            Reader = new GatewayScreenReader(Store, Pushed);

            var vaultPath = Path.Combine(Path.GetTempPath(), "screen-voice-" + Guid.NewGuid().ToString("N") + ".vault");
            var vault = new CcDirector.Core.KeyVault(vaultPath);
            vault.Set("DEVTHROTTLE_API_KEY", "dt_live_not_a_real_key");
            vault.Set("OPENAI_API_KEY", "sk-not-a-real-key");

            var Settings = new CcDirector.Gateway.Settings.TenantSettingsResolver(
                new CcDirector.Gateway.Settings.TenantSettingsStore(_settingsData.Open()));

            _persist = Path.Combine(Path.GetTempPath(), "screen-voice-" + Guid.NewGuid().ToString("N"), "voice-sessions.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_persist)!);

            Voice = new WingmanVoiceService(
                (_, _, _) => Task.FromResult<IAgentBrain>(Brain),
                vault,
                Settings,
                _persist,
                ttsHttpClient: _speech,
                screens: Reader);

            Pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        }

        public SessionVerbClient Route => new(new DirectorDto { DirectorId = DirectorId }, Tunnel.Send);

        /// <summary>Put a screen in the store and make the pushed snapshot agree with it, so the reader's
        /// three freshness facts all hold and the store is entitled to answer.</summary>
        public void StoreACurrentScreen(long bufferBytes = 4096)
        {
            var now = DateTime.UtcNow;
            Store.Append(DirectorId, new ScreenPush
            {
                SessionId = SessionId,
                CapturedAtUtc = now,
                Rows = ScreenRows.ToList(),
                CursorRow = 0,
                CursorCol = 2,
                CursorVisible = true,
                HasGrid = true,
                BufferBytes = bufferBytes,
                ActivityState = "WaitingForInput",
                Agent = "ClaudeCode",
            }, now);
            PushSnapshot(bufferBytes);
        }

        private long _sequence;

        /// <summary>Push a session snapshot. The sequence must ADVANCE: PushedSessionStore drops a snapshot
        /// whose sequence is at or below the last applied one as stale, so a second push reusing sequence 1
        /// is silently ignored - which made this rig's "the terminal moved" case quietly not move it.</summary>
        public void PushSnapshot(long bufferBytes) =>
            Pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, ++_sequence, new[]
            {
                new SessionDto
                {
                    SessionId = SessionId,
                    DirectorId = DirectorId,
                    ActivityState = "WaitingForInput",
                    TotalBufferBytes = bufferBytes,
                },
            });

        public void Dispose()
        {
            _speech.Dispose();
            Db.Dispose();
            _settingsData.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(_persist)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AVoiceTurnCompletes_AndCostsNoTunnelScreenRead()
    {
        using var rig = new Rig();
        rig.StoreACurrentScreen();

        // Read immediately before and immediately after THIS turn - not across the suite, which would be a
        // different number about a different thing.
        var before = SessionVerbClient.ScreenGridPulls;
        await rig.Voice.GenerateAsync(new TenantId(Tenant), SessionId, rig.Route);
        var after = SessionVerbClient.ScreenGridPulls;

        // HALF ONE - the turn COMPLETED, evidenced by what it produced. Narration audio exists for this
        // session. A turn that crashed, never ran, or produced nothing fails here, which is exactly what
        // the counter on its own cannot see.
        Assert.True(rig.Voice.HasVoice(new TenantId(Tenant), SessionId),
            "no narration was produced, so there was no voice turn for the counter below to be about");
        Assert.True(rig.Brain.Asks > 0, "the model leg was never reached, so no turn ran");

        // HALF TWO - and it cost no tunnel screen read.
        Assert.Equal(0, after - before);
        Assert.Equal(0, rig.Tunnel.ScreenGridCalls);
    }

    [Fact]
    public async Task TheKnownBadControl_TheSameTurnWithAnEmptyStoreDoesPullOverTheTunnel()
    {
        // Without this the zero above is a number that cannot rise. Everything is identical except that no
        // screen is stored, so the reader has nothing to certify and must go to the tunnel.
        using var rig = new Rig();
        rig.PushSnapshot(4096);

        var before = SessionVerbClient.ScreenGridPulls;
        await rig.Voice.GenerateAsync(new TenantId(Tenant), SessionId, rig.Route);
        var after = SessionVerbClient.ScreenGridPulls;

        Assert.True(rig.Voice.HasVoice(new TenantId(Tenant), SessionId),
            "the control turn must complete too, or it is not the same turn being compared");
        Assert.Equal(1, after - before);
        Assert.Equal(1, rig.Tunnel.ScreenGridCalls);
    }

    [Fact]
    public async Task AStoredScreenWhoseTerminalHasMovedDoesNotSaveTheRoundTrip()
    {
        // The second control, and it is about the RULE rather than the instrument: a stored screen only
        // saves the round trip while the Gateway can still prove it describes that terminal. Move the
        // terminal by one byte and the same turn pays for the pull again.
        using var rig = new Rig();
        rig.StoreACurrentScreen(bufferBytes: 4096);
        rig.PushSnapshot(4097);

        var before = SessionVerbClient.ScreenGridPulls;
        await rig.Voice.GenerateAsync(new TenantId(Tenant), SessionId, rig.Route);
        var after = SessionVerbClient.ScreenGridPulls;

        Assert.True(rig.Voice.HasVoice(new TenantId(Tenant), SessionId));
        Assert.Equal(1, after - before);
    }
}
