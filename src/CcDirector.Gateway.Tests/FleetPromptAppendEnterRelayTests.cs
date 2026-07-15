using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// `session prompt --no-submit` stages text in a session's composer WITHOUT pressing Enter - you write it,
/// a human reads it, a human sends it. Submitting instead is not a cosmetic slip: it hands an agent's draft
/// to a live agent as a real instruction, and the tool reports success either way.
///
/// The local path honored the flag. The relay path for a target on ANOTHER Director did not carry it at
/// all, and <see cref="GatewayClient.SendPromptToFleetAsync"/> hardcoded AppendEnter = true - so the SAME
/// command did different things depending only on which machine the target session happened to be on, which
/// is not a property of the request. <see cref="FleetPromptRequest.AppendEnter"/>'s own doc comment says it
/// "is passed straight through" to <see cref="PromptRequest.AppendEnter"/>; these tests are what make that
/// sentence true.
///
/// The end-to-end tests drive the REAL /fleet/prompt endpoint through the REAL GatewayClient to a Kestrel
/// stub standing in for the Gateway, and read the PromptRequest that actually went out on the wire. That
/// spans BOTH halves of the defect at once - the endpoint that dropped the flag and the client that
/// hardcoded it. A test of the client alone would stay green while the endpoint quietly dropped it.
/// </summary>
public sealed class FleetPromptAppendEnterRelayTests
{
    /// <summary>Captures the PromptRequest the Director relays to the Gateway.</summary>
    private sealed class Captured
    {
        public PromptRequest? Body { get; set; }
    }

    /// <summary>A Kestrel stub standing in for the Gateway's POST /sessions/{sid}/prompt.</summary>
    private static async Task<(WebApplication app, string url, Captured captured)> StartGatewayStubAsync()
    {
        var captured = new Captured();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));   // OS-assigned free port
        var app = builder.Build();
        app.MapPost("/sessions/{sid}/prompt", (string sid, PromptRequest body) =>
        {
            captured.Body = body;
            return Results.Json(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow });
        });
        await app.StartAsync();
        return (app, app.Urls.First(), captured);
    }

    private static GatewayClient ClientFor(string gatewayUrl) =>
        new(new GatewayConfig { Url = gatewayUrl }, Guid.NewGuid().ToString(), 7879, "1.0.0");

    /// <summary>The Director's own /fleet/prompt endpoint, wired to a Gateway that is the stub.</summary>
    private static async Task<(WebApplication app, HttpClient http, SessionManager sm)> StartDirectorAsync(GatewayClient gateway)
    {
        var sm = new SessionManager(new AgentOptions());
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        ControlEndpoints.Map(app, sm, Guid.NewGuid().ToString(), "1.0.0-test", () => Task.CompletedTask,
            gatewayClientProvider: () => gateway);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, sm);
    }

    /// <summary>Relay a fleet prompt at a session id this Director does not own, so it takes the Gateway
    /// path, and return what reached the Gateway.</summary>
    private static async Task<PromptRequest> RelayAsync(bool? appendEnter)
    {
        var (stub, url, captured) = await StartGatewayStubAsync();
        await using var _ = stub;
        using var gateway = ClientFor(url);
        var (director, http, sm) = await StartDirectorAsync(gateway);
        await using var __ = director;
        using var ___ = http;
        using var ____ = sm;

        // A session id this Director has never heard of: not local, so it relays.
        var body = new Dictionary<string, object> { ["toSessionId"] = Guid.NewGuid().ToString(), ["text"] = "staged for review" };
        if (appendEnter is not null) body["appendEnter"] = appendEnter.Value;

        var resp = await http.PostAsJsonAsync("/fleet/prompt", body);
        Assert.True(resp.IsSuccessStatusCode, $"relay returned HTTP {(int)resp.StatusCode}");

        Assert.NotNull(captured.Body);
        return captured.Body!;
    }

    [Fact]
    public async Task No_submit_on_a_remote_target_does_NOT_press_enter()
    {
        var sent = await RelayAsync(appendEnter: false);

        // The whole point of --no-submit: the text is staged for a human to read and send. True here means
        // an agent's draft was handed to a live agent as a real instruction, and the tool said "sent".
        Assert.False(sent.AppendEnter);
        Assert.Equal("staged for review", sent.Text);
    }

    [Fact]
    public async Task A_remote_prompt_still_submits_by_default()
    {
        // The control. A prompt normally submits, and the flag defaults to true - so the fix must carry the
        // caller's intent through, not simply invert a constant.
        Assert.True((await RelayAsync(appendEnter: true)).AppendEnter);
        Assert.True((await RelayAsync(appendEnter: null)).AppendEnter);
    }

    // ===== the client's own contract, at the wire =====

    [Fact]
    public async Task SendPromptToFleetAsync_carries_the_callers_append_enter()
    {
        var (stub, url, captured) = await StartGatewayStubAsync();
        await using var _ = stub;
        using var gateway = ClientFor(url);

        await gateway.SendPromptToFleetAsync(Guid.NewGuid().ToString(), "staged", appendEnter: false);

        Assert.False(captured.Body!.AppendEnter);
    }

    [Fact]
    public async Task SendPromptToFleetAsync_defaults_to_submitting()
    {
        // A fleet MESSAGE (/fleet/send) relays through this same call and must keep submitting: it is a
        // delivered message, not a draft. The parameter defaults to true so that caller is unchanged.
        var (stub, url, captured) = await StartGatewayStubAsync();
        await using var _ = stub;
        using var gateway = ClientFor(url);

        await gateway.SendPromptToFleetAsync(Guid.NewGuid().ToString(), "a fleet message");

        Assert.True(captured.Body!.AppendEnter);
    }

    [Fact]
    public async Task The_relay_still_marks_the_prompt_agent_driven()
    {
        // Guard on the neighbouring field this change must not disturb (issue #1636). The local/relay
        // disagreement on AgentDriven is a known, separate, next-release defect and is deliberately NOT
        // touched here - this only pins that the relay's own behavior is unchanged by the AppendEnter fix.
        var (stub, url, captured) = await StartGatewayStubAsync();
        await using var _ = stub;
        using var gateway = ClientFor(url);

        await gateway.SendPromptToFleetAsync(Guid.NewGuid().ToString(), "text", appendEnter: false);

        Assert.True(captured.Body!.AgentDriven);
        Assert.False(captured.Body!.WaitForIdle);
    }
}
