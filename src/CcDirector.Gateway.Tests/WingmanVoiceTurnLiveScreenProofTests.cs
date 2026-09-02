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
/// The FLOOR of the menu-handling mission (issue #1777), proven end-to-end against a REAL streamMode
/// <see cref="GatewayHost"/> over the tunnel. THE INVARIANT this pins: voice-turn NEVER presses a key into a
/// session, and it types the spoken words as a prompt ONLY on a confident plain-text composer (not the
/// alternate screen, cursor VISIBLE, cursor within the composer's input span, no menu-ish structure anywhere,
/// and re-confirmed plain-text immediately before the send). Every menu, and every uncertain / unreadable /
/// alternate-screen / hidden-cursor screen, types NOTHING and presses NOTHING.
///
/// Every dispatched command is recorded, so a test proves exactly what did - and did not - reach the terminal.
/// No wingman brain is configured (the send path never calls one); the translate step after a successful type
/// then fails, which is irrelevant - the claim is that the prompt WAS (or was NOT) dispatched.
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
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59924/", // nothing listens here
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

    // A real Claude permission menu drawn with its selection marker. Like the real Ink picker, the hardware
    // cursor is HIDDEN and its cell is stale - the menu is owned by the drawn marker.
    private static ScreenGridResponse MenuGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = new List<string> { "Do you want to proceed?", "❯ 1. Yes", "  2. No" },
        CursorRow = 0,
        CursorCol = -1,
        CursorVisible = false,
        IsAlternateScreen = true,
        HasGrid = true,
    };

    // A Claude composer, VISIBLE cursor in the empty input box (row 2), framed by box borders and a footer.
    private static ScreenGridResponse ComposerGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = new List<string>
        {
            "I finished the change. What next?",
            "╭──────────────────────────────────────╮",
            "│ >                                     │",
            "╰──────────────────────────────────────╯",
            "  ? for shortcuts",
        },
        CursorRow = 2,
        CursorCol = 4,
        CursorVisible = true,
        IsAlternateScreen = false,
        HasGrid = true,
    };

    // ===================== The floor: a menu types nothing AND presses nothing =====================

    [Fact]
    public async Task VoiceTurn_MenuOnScreen_TypesNothingAndPressesNothing()
    {
        // The invariant: a drawn menu (hidden cursor, real Claude case) is detected and the turn fails closed.
        // NOTHING is sent into the session - not the spoken words, not any menu keystroke.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(MenuGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "yes go ahead" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());

        // The absolute floor invariant: NO prompt of ANY kind reached the terminal on this branch.
        Assert.Empty(DispatchedPrompts());
        Assert.Contains(_commands, c => c.Verb == "screen-grid");
    }

    // ===================== The one thing allowed: type on a confident plain-text composer ===============

    /// <summary>Accept the prompt AND put the agent's reply into the Gateway's store, which is what the
    /// owning Director's push does the moment the turn ends. The voice-turn wait watches the store, so this
    /// is what lets it find an answer instead of waiting out its deadline.</summary>
    private DirectorCommandResult SeedReplyThenOk(string sid, string reply)
    {
        _gateway.SeedStoredConversationForTest(CcDirector.Core.Tenancy.TenantId.Local, DirectorId, sid,
            new[] { ("User", "the spoken question"), ("Assistant", reply) });
        return Ok(new PromptResponse { Accepted = true });
    }

    [Fact]
    public async Task VoiceTurn_ConfidentPlainTextComposer_TypesTheSpokenAnswer()
    {
        var sid = await PushSessionAsync();
        var spoken = "add a retry to the upload path";
        var promptSent = false;

        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(ComposerGrid(sid)),   // both the classify and the pre-send re-confirm see it
            // The reply arrives the way it does in production now (turn-push mission): the Director PUSHES it
            // once the turn ends and the Gateway reads its own store. There is no "turns" command any more,
            // so seeding the store as the prompt lands is what stands in for that push - and without it the
            // wait for the answer sits out its full deadline.
            "prompt" => Mark(ref promptSent, SeedReplyThenOk(sid, "Added the retry.")),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        // The translate step fails (no brain configured), which is irrelevant: the claim is the words WERE
        // typed as an ordinary prompt onto the composer.
        await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = spoken });
        Assert.Contains(DispatchedPrompts(), p => p.Text == spoken && p.AppendEnter);
    }

    [Fact]
    public async Task VoiceTurn_EmptyFooterComposer_TrimmedRow_TypesTheSpokenAnswer()
    {
        // Regression (final fix): a NORMAL empty footer-only composer, in the REAL trailing-trimmed
        // representation ("> " arrives as ">"), with the visible cursor at the true input column (2). A normal
        // voice answer MUST be typed here - the trailing-edge rule must not over-block the empty composer.
        var sid = await PushSessionAsync();
        var spoken = "run the tests";
        var promptSent = false;

        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { ">", "  ? for shortcuts" },   // the trimmed "> " row
                CursorRow = 0,
                CursorCol = 2,
                CursorVisible = true,
                IsAlternateScreen = false,
                HasGrid = true,
            }),
            // The reply arrives the way it does in production now (turn-push mission): the Director PUSHES it
            // once the turn ends and the Gateway reads its own store. There is no "turns" command any more,
            // so seeding the store as the prompt lands is what stands in for that push - and without it the
            // wait for the answer sits out its full deadline.
            "prompt" => Mark(ref promptSent, SeedReplyThenOk(sid, "Running.")),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = spoken });
        Assert.Contains(DispatchedPrompts(), p => p.Text == spoken && p.AppendEnter);
    }

    [Fact]
    public async Task VoiceTurn_PlainTextThenBecomesMenuBeforeSend_TypesNothing()
    {
        // Finding 2 (snapshot-to-send race): the screen is a plain-text composer when classified, but a menu by
        // the time we re-confirm immediately before the send. The re-confirm must catch it and type nothing.
        var sid = await PushSessionAsync();
        var gridCall = 0;
        _dispatch = cmd =>
        {
            if (cmd.Verb == "screen-grid")
            {
                var call = System.Threading.Interlocked.Increment(ref gridCall);
                return call == 1 ? Ok(ComposerGrid(sid)) : Ok(MenuGrid(sid));
            }
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "do it" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        Assert.Empty(DispatchedPrompts());
    }

    // ===================== Finding 3: menu-ish structure blocks typing =====================

    [Fact]
    public async Task VoiceTurn_BorderedSelectorHiddenCursor_TypesNothing()
    {
        // A BORDERED "> production" selector - the thing that used to read as a composer because of the box
        // frame. The hardware cursor is HIDDEN (a menu selection), and "Choose a deployment:" is a menu prompt,
        // so typing is refused.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "Choose a deployment:", "╭──────────────╮", "│ > production │", "╰──────────────╯" },
                CursorRow = 2,
                CursorCol = 4,
                CursorVisible = false,
                IsAlternateScreen = false,
                HasGrid = true,
            })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "production");
    }

    [Fact]
    public async Task VoiceTurn_PickPromptSelectorVisibleCursorAtMarker_TypesNothing()
    {
        // Blocker A: "Pick environment:" over a framed "> production" selector, with a VISIBLE cursor at the
        // marker (col 4). It looks composer-like, but the cursor is at the selection marker, not trailing typed
        // text - so it is a selector, not a composer. Type NOTHING.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "Pick environment:", "╭──────────────╮", "│ > production │", "╰──────────────╯" },
                CursorRow = 2,
                CursorCol = 4,
                CursorVisible = true,
                IsAlternateScreen = false,
                HasGrid = true,
            })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "production");
    }

    [Fact]
    public async Task VoiceTurn_ComposerWithANumberedListPresent_TypesNothing()
    {
        // Finding 3, conservative floor: even a VISIBLE-cursor composer is not typed into when a numbered
        // (menu-ish) list is anywhere on the grid. When in doubt, block.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string>
                {
                    "Here are the choices:",
                    "1. staging",
                    "2. production",
                    "╭──────────────────────────────────────╮",
                    "│ >                                     │",
                    "╰──────────────────────────────────────╯",
                    "  ? for shortcuts",
                },
                CursorRow = 4,
                CursorCol = 4,
                CursorVisible = true,
                IsAlternateScreen = false,
                HasGrid = true,
            })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "staging");
    }

    // ===================== Finding 6-style fail-closed edges - each types NOTHING =====================

    [Fact]
    public async Task VoiceTurn_ScreenGridVerbFails_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "director did not answer");
        await AssertTypesNothing(sid, "option two");
    }

    [Fact]
    public async Task VoiceTurn_ScreenGridMalformedBody_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? DirectorCommandResult.Success("this is not valid json for a ScreenGridResponse {{{")
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "option two");
    }

    [Fact]
    public async Task VoiceTurn_EmptyRowsWithHasGrid_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse { SessionId = sid, Rows = new List<string>(), HasGrid = true })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "option two");
    }

    [Fact]
    public async Task VoiceTurn_AllBlankRows_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse { SessionId = sid, Rows = new List<string> { "", "   ", "\t" }, HasGrid = true, CursorRow = 0, CursorCol = 0 })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "option two");
    }

    [Fact]
    public async Task VoiceTurn_AlternateScreenUnrecognized_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "a full screen viewer", "line of content", "another line", "status: reading" },
                CursorRow = 3,
                CursorCol = 8,
                CursorVisible = false,
                IsAlternateScreen = true,
                HasGrid = true,
            })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "option two");
    }

    private async Task AssertTypesNothing(string sid, string spoken)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = spoken });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        Assert.Empty(DispatchedPrompts());
    }

    private static DirectorCommandResult Mark(ref bool flag, DirectorCommandResult result)
    {
        flag = true;
        return result;
    }

}
