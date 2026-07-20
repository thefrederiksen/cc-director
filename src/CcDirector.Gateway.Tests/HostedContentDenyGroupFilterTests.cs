using System.Net;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PROVES THE PROPERTY THAT MADE THE GROUP FILTER THE RIGHT SHAPE, WHICH NOTHING ELSE HERE CAN SEE.
///
/// Each of the four denied families is guarded by ONE <c>AddEndpointFilter</c> on the route group rather
/// than a <c>DenyOnHosted()</c> call repeated in every handler. The stated reason is that a per-route
/// guard ROTS - the moment someone adds a route to that file it is undefended and nothing fails - whereas
/// a group filter runs before every route in the group INCLUDING ROUTES THAT DO NOT EXIST YET.
///
/// That difference is completely invisible to any test that only drives the routes existing today: a
/// per-route guard and a group filter behave identically on those. Which is exactly what makes the
/// per-route shape dangerous, and exactly why the claim needs its own proof rather than an assurance in a
/// pull request body. Until this file existed, the pull request asserted the future-route property and
/// nothing tested it.
///
/// So each family maps a BRAND-NEW route onto its returned group, with NO deny written for it anywhere,
/// and asserts:
///
///   1. HOSTED           - the probe route is refused, carrying that family's refusal and nothing else.
///   2. SELF-HOST        - the SAME probe route is SERVED, over BOTH non-hosted forms that occur in
///                         practice (the variable absent, and the variable present but "0").
///   3. SCOPING CONTROL  - a route mapped OUTSIDE the group still SERVES on hosted.
///
/// All three are needed and none is redundant. Without (2) a filter that refused everything
/// unconditionally would pass every hosted assertion in this file while having silently killed the route
/// for self-host too - a brick is indistinguishable from a working gate if you only push on it from one
/// side. Without (3) the hosted passes could be the host refusing everything rather than the filter doing
/// its job.
///
/// The self-host legs also assert <see cref="GatewayHostedMode.IsHosted"/> is genuinely false rather than
/// trusting the environment variable to have taken effect, so no leg can silently run in the mode it
/// believes it is not in.
/// </summary>
[Collection("DirectorRoot")]   // serialized: every leg mutates the process-wide CC_GATEWAY_HOSTED and
                              // CC_DIRECTOR_ROOT, so running beside another collection would let one
                              // test's mode leak into another's - the exact silent-wrong-mode failure
                              // the IsHosted assertions above exist to catch.
public sealed class HostedContentDenyGroupFilterTests
{
    private const string ProbeBody = "probe-served-zqxjv";

    private const string TranscriptionRefusal = "transcription analysis is not available on the hosted gateway";
    private const string InstructionsRefusal = "the wingman instructions surface is not available on the hosted gateway";
    private const string UtteranceRefusal = "the wingman utterance upload is not available on the hosted gateway";
    private const string DictationRefusal = "dictation upload is not available on the hosted gateway";

    /// <summary>Which family, its group-mapper, and the refusal its filter must produce.</summary>
    public static TheoryData<string> Families() => new() { "transcription", "instructions", "utterance", "dictation" };

    private static string RefusalFor(string family) => family switch
    {
        "transcription" => TranscriptionRefusal,
        "instructions" => InstructionsRefusal,
        "utterance" => UtteranceRefusal,
        "dictation" => DictationRefusal,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    // ===== 1. HOSTED: a route added to the group later is refused, with no deny of its own =====

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        await using var rig = await Rig.StartAsync(family);

        var resp = await rig.Http.GetAsync("probe-added-later");

        var root = await ContentFingerprint.AsJsonObjectAsync(resp, $"{family} probe on hosted");
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(RefusalFor(family), ContentFingerprint.Text(root, "error", family));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ===== 2. SELF-HOST: the same probe route still serves, over BOTH non-hosted forms =====

    [Theory]
    [InlineData("transcription", null)]
    [InlineData("transcription", "0")]
    [InlineData("instructions", null)]
    [InlineData("instructions", "0")]
    [InlineData("utterance", null)]
    [InlineData("utterance", "0")]
    [InlineData("dictation", null)]
    [InlineData("dictation", "0")]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string family, string? hostedValue)
    {
        using var env = new HostedEnv(hostedValue);
        Assert.False(GatewayHostedMode.IsHosted,
            $"the self-host leg must actually be in self-host mode (CC_GATEWAY_HOSTED={hostedValue ?? "absent"})");

        await using var rig = await Rig.StartAsync(family);

        var resp = await rig.Http.GetAsync("probe-added-later");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(ProbeBody, body);
    }

    // ===== 3. SCOPING CONTROL: a route OUTSIDE the group still serves on hosted =====

    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_outside_the_group_still_serves_on_hosted(string family)
    {
        using var env = new HostedEnv("1");
        Assert.True(GatewayHostedMode.IsHosted, "the hosted leg must actually be in hosted mode");

        await using var rig = await Rig.StartAsync(family);

        // Mapped on the application, NOT on the guarded group. If this were refused, the hosted passes
        // above would be the host refusing everything rather than the filter being correctly scoped.
        var resp = await rig.Http.GetAsync("outside-the-group");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(ProbeBody, body);
    }

    /// <summary>Sets CC_GATEWAY_HOSTED for one test and restores whatever was there before.</summary>
    private sealed class HostedEnv : IDisposable
    {
        private readonly string? _prior;

        public HostedEnv(string? value)
        {
            _prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _prior);
    }

    /// <summary>
    /// A minimal application carrying ONE family's guarded group, plus a probe route mapped onto that
    /// group and a control route mapped outside it. Deliberately not a whole GatewayHost: the point is to
    /// hold the returned group and map onto it, which only the endpoint's own Map can hand back.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _root;
        private readonly string? _priorRoot;
        private readonly GatewayDbTestHarness? _db;
        public HttpClient Http { get; }

        private Rig(WebApplication app, HttpClient http, string root, string? priorRoot,
            GatewayDbTestHarness? db)
        {
            _app = app;
            Http = http;
            _root = root;
            _priorRoot = priorRoot;
            _db = db;
        }

        public static async Task<Rig> StartAsync(string family)
        {
            var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            var root = Path.Combine(Path.GetTempPath(), "ccd-probe-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");

            GatewayDbTestHarness? db = null;
            var group = MapFamily(app, family, root, ref db);

            // The brand-new route, on the guarded group, with NO deny of its own.
            group.MapGet("/probe-added-later", () => Results.Text(ProbeBody));
            // The control, deliberately OUTSIDE the group.
            app.MapGet("/outside-the-group", () => Results.Text(ProbeBody));

            await app.StartAsync();
            return new Rig(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
                root, priorRoot, db);
        }

        private static RouteGroupBuilder MapFamily(WebApplication app, string family, string root,
            ref GatewayDbTestHarness? db)
        {
            var brain = (WingmanModelRole _, CancellationToken _) =>
                Task.FromException<IAgentBrain>(
                    new InvalidOperationException("the brain must not be reached by a routing probe"));

            switch (family)
            {
                case "transcription":
                    return TranscriptionAnalysisEndpoint.Map(app);

                case "instructions":
                    db = new GatewayDbTestHarness();
                    return WingmanInstructionsEndpoint.Map(
                        app,
                        new WingmanInstructionsStore(db.Open(), db.LegacyPath("probe-legacy.json")),
                        new WingmanTrainingStore(() => false, Path.Combine(root, "training")),
                        brain);

                case "utterance":
                {
                    var vault = new KeyVault(Path.Combine(root, "probe.vault"));
                    var voice = new WingmanVoiceService(brain, vault, Path.Combine(root, "voice.json"));
                    return GatewayWingmanVoiceEndpoint.Map(
                        app,
                        new DirectorRegistry(Path.Combine(root, "instances")),
                        brain,
                        vault,
                        voice);
                }

                case "dictation":
                {
                    var vault = new KeyVault(Path.Combine(root, "probe.vault"));
                    return GatewayDictationEndpoint.Map(
                        app,
                        new DirectorRegistry(Path.Combine(root, "instances")),
                        owners: null,
                        token: "probe-token",
                        transcription: new GatewayTranscriptionService(vault),
                        transcribingSessions: new TranscribingSessions(),
                        uploads: new VoiceUploadStore(Path.Combine(root, "uploads")),
                        devices: new Pairing.DeviceRegistry(Path.Combine(root, "devices.json")));
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(family), family, "unknown family");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
            _db?.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }
}
