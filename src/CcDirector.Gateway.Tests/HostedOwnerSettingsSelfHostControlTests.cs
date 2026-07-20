using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE SELF-HOST CONTROL FOR THE OWNER-SETTINGS DENY, STATED EXPLICITLY (issue #1863).
///
/// Self-host is the control for the whole hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. The existing settings tests do not prove it: they never mention <c>CC_GATEWAY_HOSTED</c> and
/// pass only because the runner happens to leave it unset. If that ambient default ever flipped - one
/// leaked environment variable, one continuous-integration image change, one test that forgot to restore
/// it - they would keep passing while self-host was completely broken, because they assert nothing about
/// which mode they are in.
///
/// So this class sets the variable ITSELF, to BOTH non-hosted forms that occur in practice - ABSENT, and
/// PRESENT-BUT-"0" - and asserts <see cref="GatewayHostedMode.IsHosted"/> really is false in each, so no
/// test below can silently be running in the mode it thinks it is not in.
///
/// AND IT ASSERTS A POSITIVE FACT PER ROUTE, NOT THE ABSENCE OF THE REFUSAL. "The refusal string is not
/// in the body" is satisfied by an empty 200, by a single-page-application shell, and by a route that was
/// deleted - it proves nothing about the route being alive. Every route below is proved by one of:
///   - its REAL PAYLOAD, field by field (the eleven read routes);
///   - an INDEPENDENTLY RE-READ EFFECT - the value is written over the wire and then read back out of the
///     configuration store through the Core config class, not out of the response (fourteen write routes);
///   - a HANDLER-UNIQUE RECEIPT: a status and message that only that one handler can produce, where the
///     route has no readable effect to assert (four routes).
/// Together with <see cref="HostedOwnerSettingsGroupFilterTests.The_brain_restart_route_exists_on_hosted_and_is_reachable_only_as_a_post"/>
/// for the one route whose handler cannot be invoked, that is all thirty-one.
///
/// These tests must stay GREEN through the revert. A control that moves with the change under test is not
/// a control.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsSelfHostControlTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-self-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public HostedOwnerSettingsSelfHostControlTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-self-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        _gateway = new GatewayHost(port: HostedOwnerSettingsDenyTests.FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// null = the variable is ABSENT. "0" = the variable is PRESENT and explicitly not hosted. Both are
    /// real self-host deployments and both must serve. Stating both is the point: a gate written against
    /// "the variable is set at all" would pass the first and fail the second.
    /// </summary>
    public static TheoryData<string?> NonHostedForms => new() { null, "0" };

    /// <summary>
    /// Puts the process into a STATED non-hosted mode and proves the statement took, so nothing below can
    /// silently be running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    public static TheoryData<string?, string> ReadRoutes
    {
        get
        {
            var data = new TheoryData<string?, string>();
            foreach (var hosted in new string?[] { null, "0" })
                foreach (var path in new[]
                         {
                             "gateway/settings",
                             "gateway/wingman/training-capture",
                             "gateway/addressing-mode",
                             "gateway/snooze-default",
                             "gateway/injected-text",
                             "gateway/snooze-presets",
                             "gateway/time-zone",
                             "gateway/transcription-mode",
                             "gateway/ai-provider",
                             "gateway/tts-voice",
                             "gateway/telemetry-consent",
                         })
                    data.Add(hosted, path);
            return data;
        }
    }

    /// <summary>
    /// The eleven read routes, each asserted by the REAL PAYLOAD it carries - the fields, and where a field
    /// has an independently knowable value, that value. Format facts (status, media type) are asserted
    /// BEFORE the body is parsed, on this side too: if a route were gone, the Gateway's single-page
    /// -application fallback would answer with HTML and parsing first would turn that finding into a crash.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task Every_read_route_serves_its_real_payload_on_self_host(string? hostedForm, string path)
    {
        DeclareSelfHost(hostedForm);

        var response = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // The refusal envelope is exactly one property named "error". Proving the real payload means
        // naming what this route actually carries, one field at a time.
        var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("error", properties);

        switch (path)
        {
            case "gateway/settings":
                foreach (var expected in new[]
                         {
                             "version", "state", "port", "uptimeSeconds", "directors", "mode",
                             "addressingMode", "cockpit", "brain", "autostart", "wingmanTrainingCapture",
                             "telemetryConsent", "snoozeDefaultMinutes", "snoozePresets", "snoozeMaxPresets",
                             "timeZone", "timeZoneMachineDefault",
                         })
                    Assert.Contains(expected, properties);
                Assert.Equal("Running", root.GetProperty("state").GetString());
                Assert.Equal(_gateway.Port, root.GetProperty("port").GetInt32());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
                Assert.True(root.GetProperty("brain").TryGetProperty("agents", out _));
                break;

            case "gateway/wingman/training-capture":
                Assert.Equal(new[] { "enabled" }, properties);
                Assert.Equal(WingmanTrainingCaptureConfig.Get(), root.GetProperty("enabled").GetBoolean());
                break;

            case "gateway/addressing-mode":
                Assert.Equal(new[] { "mode" }, properties);
                Assert.Equal(AddressingModeConfig.Get().ToConfigString(), root.GetProperty("mode").GetString());
                break;

            case "gateway/snooze-default":
                Assert.Equal(new[] { "minutes" }, properties);
                Assert.Equal(SnoozeDefaultConfig.Get(), root.GetProperty("minutes").GetInt32());
                break;

            case "gateway/injected-text":
                Assert.Equal(new[] { "use_yours", "yours", "ours", "placeholders" }, properties);
                // "ours" is the shipped agent-launch instruction text, and its presence here is exactly the
                // disclosure the hosted deny closes - so on self-host it must really be here, not empty.
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("ours").GetString()));
                Assert.True(root.GetProperty("placeholders").GetArrayLength() > 0);
                break;

            case "gateway/snooze-presets":
                Assert.Equal(new[] { "presets", "defaultMinutes", "maxPresets" }, properties);
                Assert.True(root.GetProperty("presets").GetArrayLength() > 0);
                Assert.Equal(SnoozePresetsConfig.MaxPresets, root.GetProperty("maxPresets").GetInt32());
                break;

            case "gateway/time-zone":
                Assert.Equal(new[] { "timeZone", "machineDefault" }, properties);
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("machineDefault").GetString()));
                break;

            case "gateway/transcription-mode":
                Assert.Equal(new[] { "mode" }, properties);
                Assert.Equal("devthrottle", root.GetProperty("mode").GetString());
                break;

            case "gateway/ai-provider":
                foreach (var expected in new[]
                         {
                             "provider", "wingmanModel", "wingmanFastModel", "carModeModel",
                             "carModeEndPhrase", "transcriptionModel", "ttsModel", "ttsVoice", "voices",
                         })
                    Assert.Contains(expected, properties);
                Assert.Equal("devthrottle", root.GetProperty("provider").GetString());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("wingmanModel").GetString()));
                break;

            case "gateway/tts-voice":
                Assert.Equal(new[] { "voice", "voices" }, properties);
                Assert.True(root.GetProperty("voices").GetArrayLength() > 0);
                Assert.Equal(TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()),
                    root.GetProperty("voice").GetString());
                break;

            case "gateway/telemetry-consent":
                Assert.Equal(new[] { "enabled" }, properties);
                Assert.Equal(TelemetryConsentConfig.Get(), root.GetProperty("enabled").GetBoolean());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "no payload assertion written for this route");
        }
    }

    public static TheoryData<string?, string, string, string, string, string> WriteRoutes
    {
        get
        {
            var data = new TheoryData<string?, string, string, string, string, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                data.Add(hosted, "PUT", "gateway/wingman/training-capture", "{\"enabled\":true}", "training-capture", "true");
                data.Add(hosted, "PUT", "gateway/addressing-mode", "{\"mode\":\"lan\"}", "addressing-mode", "lan");
                data.Add(hosted, "PUT", "gateway/snooze-default", "{\"minutes\":45}", "snooze-default", "45");
                data.Add(hosted, "PUT", "gateway/injected-text", "{\"use_yours\":true,\"yours\":\"words from another tenant\"}", "injected-text", "words from another tenant");
                data.Add(hosted, "PUT", "gateway/snooze-presets", "{\"presets\":[15,30,60],\"defaultMinutes\":30}", "snooze-presets", "15,30,60");
                data.Add(hosted, "PUT", "gateway/time-zone", "{\"timeZone\":\"America/New_York\"}", "time-zone", "America/New_York");
                data.Add(hosted, "PUT", "gateway/transcription-mode", "{\"mode\":\"devthrottle\"}", "transcription-mode", "devthrottle");
                data.Add(hosted, "PUT", "gateway/tts-voice", "{\"voice\":\"shimmer\"}", "tts-voice", "shimmer");
                data.Add(hosted, "PUT", "gateway/telemetry-consent", "{\"enabled\":false}", "telemetry-consent", "false");
                data.Add(hosted, "PUT", "gateway/ai/wingman-model", "{\"model\":\"hosted-wingman\"}", "wingman-model", "hosted-wingman");
                data.Add(hosted, "PUT", "gateway/ai/wingman-fast-model", "{\"model\":\"hosted-fast\"}", "wingman-fast-model", "hosted-fast");
                data.Add(hosted, "PUT", "gateway/ai/car-mode-model", "{\"model\":\"hosted-car\"}", "car-mode-model", "hosted-car");
                data.Add(hosted, "PUT", "gateway/ai/car-mode-end-phrase", "{\"phrase\":\"finished here\"}", "car-mode-end-phrase", "finished here");
                data.Add(hosted, "PUT", "gateway/ai/tts-model", "{\"model\":\"hosted-speech\"}", "tts-model", "hosted-speech");
            }
            return data;
        }
    }

    /// <summary>
    /// Reads a setting back through the Core configuration class, NOT out of the response body. Reading the
    /// response would only prove the handler can echo; reading the store proves the write landed.
    /// </summary>
    private static string ReadBack(string key) => key switch
    {
        "training-capture" => WingmanTrainingCaptureConfig.Get() ? "true" : "false",
        "addressing-mode" => AddressingModeConfig.Get().ToConfigString(),
        "snooze-default" => SnoozeDefaultConfig.Get().ToString(),
        "injected-text" => InjectedTextConfig.Get().Yours ?? "",
        "snooze-presets" => string.Join(",", SnoozePresetsConfig.Get()),
        "time-zone" => TimeZoneConfig.Get() ?? "",
        "transcription-mode" => TranscriptionModeConfig.Get().ToConfigString(),
        "tts-voice" => TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()),
        "telemetry-consent" => TelemetryConsentConfig.Get() ? "true" : "false",
        "wingman-model" => WingmanModelConfig.Resolve(TranscriptionModeConfig.Get()),
        "wingman-fast-model" => WingmanModelConfig.ResolveFast(TranscriptionModeConfig.Get()),
        "car-mode-model" => CarModeModelConfig.Get(),
        "car-mode-end-phrase" => CarModeEndPhraseConfig.Get(),
        "tts-model" => TtsModelConfig.Get(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no read-back written for this setting"),
    };

    /// <summary>
    /// Fourteen write routes, each proved by an INDEPENDENTLY RE-READ EFFECT: the value goes over the wire
    /// and is then read back out of the configuration store through its Core config class. A 200 alone
    /// would not do - a handler that accepted the request and wrote nothing returns the same 200.
    ///
    /// <c>PUT /gateway/transcription-mode</c> is the one row whose re-read cannot distinguish a write from
    /// a no-op, because "devthrottle" is the only value the endpoint accepts and is also the default. Its
    /// 200 and its echoed payload are asserted here for what they are worth, and the row is called out in
    /// the pull request rather than left looking like the others.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Every_write_route_really_writes_and_the_write_reads_back_on_self_host(
        string? hostedForm, string verb, string path, string body, string readBackKey, string expected)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("error", document.RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Equal(expected, ReadBack(readBackKey));
    }

    /// <summary>
    /// <c>PUT /gateway/ai-provider</c> on its own, because its effect is a RESET rather than a value being
    /// stored: it repoints the wingman, speech and voice settings back to the provider defaults. Proving it
    /// therefore needs a before-state that the reset must move away from, which is what the sentinel write
    /// below is for. That is a real, independently re-read effect - the strongest served-side fact
    /// available for this route.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedForms))]
    public async Task The_provider_reset_really_resets_the_models_on_self_host(string? hostedForm)
    {
        DeclareSelfHost(hostedForm);

        // A value the reset cannot possibly produce, so "unchanged" and "reset" cannot be confused.
        const string Sentinel = "sentinel-model-no-provider-would-return";
        WingmanModelConfig.Set(Sentinel);
        Assert.Equal(Sentinel, WingmanModelConfig.Resolve(TranscriptionModeConfig.Get()));

        var response = await OwnerSettingsRoutes.SendAsync(_http, "PUT", "gateway/ai-provider",
            "{\"provider\":\"devthrottle\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("devthrottle", document.RootElement.GetProperty("provider").GetString());

        var afterwards = WingmanModelConfig.Resolve(TranscriptionModeConfig.Get());
        Assert.NotEqual(Sentinel, afterwards);
        Assert.False(string.IsNullOrWhiteSpace(afterwards));
    }

    public static TheoryData<string?, string, string, string, int, string> ReceiptRoutes
    {
        get
        {
            var data = new TheoryData<string?, string, string, string, int, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                // The brain-config handler is the only place in the Gateway that says this sentence, and it
                // can only say it after it has loaded this machine's registered agents and failed to find
                // the one asked for - so the receipt proves the handler ran, not merely that something
                // answered.
                data.Add(hosted, "PUT", "gateway/brain/config", "{\"agentId\":\"agent-1\",\"model\":\"opus\"}",
                    400, "agentId must be a registered, enabled agent on this machine");

                // The catalog and test-chat routes both resolve the provider credential out of the key
                // vault first. With no credential stored they answer 503 with this exact sentence - a
                // handler-unique receipt that costs no network round-trip.
                data.Add(hosted, "GET", "gateway/ai/models", "",
                    503, "not signed in to DevThrottle - sign in on the Account tab");
                data.Add(hosted, "POST", "gateway/ai/test-chat", "{\"model\":\"some-model\"}",
                    503, "not signed in to DevThrottle - sign in on the Account tab");
            }
            return data;
        }
    }

    /// <summary>
    /// Three routes with no readable effect to assert, each proved by a HANDLER-UNIQUE RECEIPT: a status
    /// and a sentence only that one handler can produce. A refusal is a 404 carrying one <c>error</c>
    /// property, so none of these can be confused with one - and, unlike "the refusal is absent", each of
    /// these is a positive statement about which code ran.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReceiptRoutes))]
    public async Task The_routes_without_a_readable_effect_answer_with_their_own_receipt_on_self_host(
        string? hostedForm, string verb, string path, string body, int expectedStatus, string expectedMessage)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// <c>PUT /gateway/autostart</c>, the fourth receipt route. On a Gateway with no autostart hook - which
    /// is every Gateway not hosted by the desktop tray application, including this test host - the handler
    /// answers 200 with exactly <c>{ supported: false }</c>. Nothing else on the Gateway produces that
    /// object, and a refusal could not: it is a 200, not a 404, and its one property is not <c>error</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedForms))]
    public async Task The_autostart_route_answers_with_its_own_receipt_on_self_host(string? hostedForm)
    {
        DeclareSelfHost(hostedForm);

        var response = await OwnerSettingsRoutes.SendAsync(_http, "PUT", "gateway/autostart", "{\"enabled\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "supported" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(document.RootElement.GetProperty("supported").GetBoolean());
    }
}

/// <summary>
/// THE SERVED HALF OF THE FUTURE-ROUTE PROOF, and the half most likely to be skipped because everything
/// looks green without it.
///
/// <see cref="HostedOwnerSettingsGroupFilterTests"/> maps a brand-new route onto each group and finds it
/// REFUSED on hosted. On its own that cannot tell a working gate apart from a brick: a filter that refused
/// everything unconditionally would satisfy every one of those assertions while having silently killed the
/// routes for self-host too. This class drives the SAME probe paths with hosted mode explicitly OFF, in
/// BOTH non-hosted forms, and asserts they are SERVED with their real payload.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsSelfHostProbeTests : IAsyncLifetime
{
    private const string ProbePayload = "the-probe-route-really-served-on-self-host";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-selfprobe-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;

    public HostedOwnerSettingsSelfHostProbeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-selfprobe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: HostedOwnerSettingsDenyTests.FreePort(), token: "probe-token",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    public static TheoryData<string?, string, string> Probes
    {
        get
        {
            var data = new TheoryData<string?, string, string>();
            foreach (var hosted in new string?[] { null, "0" })
            {
                data.Add(hosted, "settings", "/gateway/added-after-the-deny-was-written");
                data.Add(hosted, "models", "/gateway/ai/added-after-the-deny-was-written");
                data.Add(hosted, "telemetry", "/gateway/telemetry-added-after-the-deny-was-written");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(
        string? hostedForm, string family, string probePath)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hostedForm);
        Assert.False(GatewayHostedMode.IsHosted);

        Func<IEndpointRouteBuilder, RouteGroupBuilder> map = family switch
        {
            "settings" => routes => Api.SettingsEndpoints.Map(routes, _gateway),
            "models" => routes => Api.AiModelsEndpoint.Map(routes, new Core.KeyVault(Path.Combine(_root, "vault.json"))),
            "telemetry" => Api.TelemetryConsentEndpoint.Map,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown owner-settings family"),
        };

        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            map,
            mapIntoGroup: group => group.MapGet(probePath, () => Results.Json(new { probe = ProbePayload })));
        try
        {
            var response = await http.GetAsync(probePath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(ProbePayload, document.RootElement.GetProperty("probe").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
