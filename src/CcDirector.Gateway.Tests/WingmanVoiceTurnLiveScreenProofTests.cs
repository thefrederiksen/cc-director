using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The floor of the menu-handling mission (issue #1777): the wingman voice-turn must READ THE LIVE SCREEN and
/// FAIL CLOSED. It boots a REAL streamMode <see cref="GatewayHost"/>, dials the REAL DirectorHub over the
/// tunnel, and answers the session verbs (<c>screen-grid</c> / <c>buffer</c> / <c>turns</c> / <c>prompt</c>)
/// per scenario, recording every command the Gateway dispatches so a test can prove exactly what was - and
/// was NOT - sent into the session.
///
/// The three cases the brief demands:
///   1. Alternate-screen Claude menu, empty scrollback -> the menu is detected off the LIVE grid and NEITHER
///      the spoken words NOR any selection bytes reach the terminal (no <c>prompt</c> verb at all).
///   2. Confident plain-text prompt -> the spoken answer IS typed (a <c>prompt</c> verb carrying the words).
///   3. Unreadable screen -> nothing is typed (no <c>prompt</c> verb).
///
/// TUNNEL-BY-CONSTRUCTION (as in <see cref="TunnelWingmanVoiceProofTests"/>): the Director is registered
/// unreachable and the session arrives only via PushSnapshot, so any answered read rode the tunnel. No live
/// wingman brain is configured, so the brain call throws immediately on the empty key - which is precisely the
/// fail-closed path case 1 exercises (a menu on screen the brain could not read must still type nothing).
/// </summary>
[Collection("DirectorRoot")]
public sealed class WingmanVoiceTurnLiveScreenProofTests : IAsyncLifetime
{
    private const string Token = "test-token-voice-liveScreen-proof";
    private const string DirectorId = "dir-voice-liveScreen";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-vlsproof-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Every command the Gateway dispatched over the tunnel, in order (thread-safe).</summary>
    private readonly ConcurrentQueue<DirectorCommand> _commands = new();

    /// <summary>The per-test verb dispatcher; set by each test before it drives the endpoint.</summary>
    private Func<DirectorCommand, DirectorCommandResult> _dispatch =
        cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

    public WingmanVoiceTurnLiveScreenProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-vlsproof-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59923/", // nothing listens here
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", cmd =>
        {
            _commands.Enqueue(cmd);
            return _dispatch(cmd);
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private Task PushAsync(long sequence, params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", sequence, sessions);

    private static DirectorCommandResult Ok(object body) =>
        DirectorCommandResult.Success(JsonSerializer.Serialize(body, Web));

    /// <summary>Push a voice session and return its id.</summary>
    private async Task<string> PushSessionAsync()
    {
        var sid = Guid.NewGuid().ToString();
        await PushAsync(1L, new SessionDto
        {
            SessionId = sid,
            Name = "a voice session",
            Status = "WaitingForInput",
            ActivityState = "WaitingForInput",
            RepoPath = @"D:\repo",
        });
        return sid;
    }

    /// <summary>Every prompt the Gateway dispatched into the session (the bytes that would reach the terminal).</summary>
    private List<PromptRequest> DispatchedPrompts()
    {
        var prompts = new List<PromptRequest>();
        foreach (var cmd in _commands)
            if (cmd.Verb == "prompt")
            {
                var req = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson ?? "{}", Web);
                if (req is not null) prompts.Add(req);
            }
        return prompts;
    }

    // A real Claude Code permission menu, as it renders on the ALTERNATE screen (full-screen picker). The
    // scrollback is empty on the alternate screen by design, so this menu is visible ONLY on the live grid.
    private static readonly List<string> AltScreenClaudeMenuRows = new()
    {
        "╭──────────────────────────────────────────────╮",
        "│ Bash command                                 │",
        "│ dotnet test                                  │",
        "│                                              │",
        "│ Do you want to proceed?                      │",
        "│ ❯ 1. Yes                                     │",
        "│   2. Yes, and don't ask again this session   │",
        "│   3. No, and tell Claude what to do          │",
        "╰──────────────────────────────────────────────╯",
    };

    [Fact]
    public async Task VoiceTurn_AltScreenClaudeMenu_EmptyScrollback_DetectsMenuOffLiveGridAndTypesNothing()
    {
        var sid = await PushSessionAsync();

        // The alternate-screen menu is on the LIVE grid; the scrollback (buffer) is empty, as it is on the
        // alternate screen. A "prompt" answer would be the exact defect - so any prompt dispatch fails loud.
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = AltScreenClaudeMenuRows,
                CursorRow = 5,
                CursorCol = 3,
                IsAlternateScreen = true,
                HasGrid = true,
            }),
            "buffer" => Ok(new BufferResponse { SessionId = sid, Text = "" }),   // empty scrollback (alt screen)
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn",
            new { text = "yeah go ahead and run them" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        // Fail closed: the client is told it cannot type here and to look at the terminal.
        Assert.True(node?["cannotType"]?.GetValue<bool>());

        // The whole point: the spoken words - and any selection bytes - NEVER reached the terminal.
        Assert.Empty(DispatchedPrompts());

        // And the menu situation was detected off the LIVE screen grid (the authoritative read), not scrollback.
        Assert.Contains(_commands, c => c.Verb == "screen-grid" && c.SessionId == sid);
    }

    [Fact]
    public async Task VoiceTurn_ConfidentPlainTextPrompt_TypesTheSpokenAnswer()
    {
        var sid = await PushSessionAsync();
        var spoken = "add a retry to the upload path";
        var promptSent = false;

        // A readable, plain-text prompt (no menu) is the one case where typing is safe. "turns" returns no
        // widgets before the prompt and one Text widget after, so WaitForReplyAsync converges quickly.
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "I finished the last change. What next?", "", "> " },
                CursorRow = 2,
                CursorCol = 2,
                IsAlternateScreen = false,
                HasGrid = true,
            }),
            "turns" => Ok(new TurnsResponse
            {
                SessionId = sid,
                Status = "ok",
                Widgets = promptSent
                    ? new List<TurnWidgetDto> { new() { Kind = "Text", Content = "Added the retry." } }
                    : new List<TurnWidgetDto>(),
            }),
            "prompt" => Mark(ref promptSent, Ok(new PromptResponse { Accepted = true })),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        // The translate step needs a wingman brain, which this test does not configure, so the final response
        // is a 502 - irrelevant to the claim. What matters is that the spoken answer WAS typed into the
        // session as an ordinary prompt (a "prompt" verb carrying the words).
        await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = spoken });

        var prompts = DispatchedPrompts();
        Assert.Contains(prompts, p => p.Text == spoken && p.AppendEnter);
    }

    [Fact]
    public async Task VoiceTurn_UnreadableScreen_TypesNothing()
    {
        var sid = await PushSessionAsync();

        // The session has no resolved live grid (HasGrid=false) - the screen is unreadable. A "prompt" answer
        // would be typing blind, so it must not happen.
        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(new ScreenGridResponse { SessionId = sid, Rows = new List<string>(), HasGrid = false }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn",
            new { text = "option two please" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        Assert.Empty(DispatchedPrompts());
    }

    /// <summary>Set a flag as a side effect while returning a dispatcher result (used to flip "turns" output
    /// once the prompt has been sent, so the reply-wait converges without a real running session).</summary>
    private static DirectorCommandResult Mark(ref bool flag, DirectorCommandResult result)
    {
        flag = true;
        return result;
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
