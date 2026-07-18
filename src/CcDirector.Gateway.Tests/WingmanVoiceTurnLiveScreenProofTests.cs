using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The fail-closed floor of the menu-handling mission (issue #1777), proven end-to-end against a REAL
/// streamMode <see cref="GatewayHost"/> over the tunnel. Every session verb (<c>screen-grid</c> /
/// <c>turns</c> / <c>prompt</c>) is answered per scenario and every dispatched command is recorded, so a test
/// proves exactly what did - and did NOT - reach the terminal. A STUB warm brain is injected so the menu
/// detector can be exercised WITH a working model (proving detection off the live grid and the menu-press
/// path), while the fail-closed tests leave the brain throwing (no reply set) to prove that a menu the brain
/// cannot read still types nothing.
///
/// The classification is decided ONLY from the live screen grid: the <c>buffer</c> (scrollback) verb is never
/// read on this path, which these tests assert.
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

    // ---- Stub warm brain (issue #1777, finding 4) ----
    // Every prompt the brain received (so a test can assert the LIVE grid text reached menu detection).
    private readonly ConcurrentQueue<string> _brainPrompts = new();
    // What the brain replies. Null => AskAsync throws (the real no-key brain behavior) => fail-closed path.
    private Func<string, string>? _brainReply;

    private sealed class StubBrain : IAgentBrain
    {
        private readonly WingmanVoiceTurnLiveScreenProofTests _owner;
        public StubBrain(WingmanVoiceTurnLiveScreenProofTests owner) => _owner = owner;
        public string? SessionId => "stub";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            _owner._brainPrompts.Enqueue(prompt);
            var reply = _owner._brainReply;
            if (reply is null)
                throw new InvalidOperationException("stub brain: no reply configured (fail-closed path)");
            return Task.FromResult(new AskResult { Text = reply(prompt) });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    /// <summary>Wrap a spoken answer in the brain's answer markers, the way the model is told to.</summary>
    private static string Wrapped(string body) =>
        SessionAskRunner.AnswerBeginMarker + "\n" + body + "\n" + SessionAskRunner.AnswerEndMarker;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            brainProviderOverride: (_, _) => Task.FromResult<IAgentBrain>(new StubBrain(this)));
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

    public WingmanVoiceTurnLiveScreenProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-vlsproof-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
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
    // cursor is HIDDEN (CursorVisible=false) and its cell is stale - the menu is owned by the drawn marker.
    private static ScreenGridResponse PrimaryMenuGrid(string sid) => new()
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

    // ===================== Finding 4: detection off the LIVE grid, menu-press path =====================

    [Fact]
    public async Task VoiceTurn_LiveGridMenu_StubBrainRecognizesIt_PressesTheOptionOffTheLiveGrid()
    {
        var sid = await PushSessionAsync();
        var promptSent = false;

        // The brain SUCCEEDS: it returns the menu it read from the live grid.
        _brainReply = _ => Wrapped(
            "{\"isMenu\":true,\"question\":\"Proceed?\",\"selectionMode\":\"single\",\"submit\":\"\"," +
            "\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\"},{\"key\":\"2. No\",\"send\":\"2\\r\"}]}");

        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(PrimaryMenuGrid(sid)),
            "turns" => Ok(new TurnsResponse
            {
                SessionId = sid,
                Status = "ok",
                Widgets = promptSent
                    ? new List<TurnWidgetDto> { new() { Kind = "Text", Content = "Ran the tests, all green." } }
                    : new List<TurnWidgetDto>(),
            }),
            "prompt" => Mark(ref promptSent, Ok(new PromptResponse { Accepted = true })),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "option one" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The menu-press path was taken with the cursor HIDDEN (the real Ink-picker case, PrimaryMenuGrid has
        // CursorVisible=false): the menu was recognized via its DRAWN marker and answered. The option's
        // keystroke reached the terminal (NOT the spoken words).
        var prompts = DispatchedPrompts();
        Assert.Contains(prompts, p => p.Text == "1\r");
        Assert.DoesNotContain(prompts, p => p.Text == "option one");

        // Detection ran off the LIVE grid text - the brain's first prompt carried the live menu content.
        Assert.True(_brainPrompts.TryPeek(out var firstPrompt));
        Assert.Contains("Do you want to proceed?", firstPrompt);

        // The scrollback (buffer) verb was never read - the live grid is the only detection source (finding 2).
        Assert.DoesNotContain(_commands, c => c.Verb == "buffer");
    }

    // ===================== Finding 3: re-verify the menu is still on screen before pressing =====================

    [Fact]
    public async Task VoiceTurn_MenuClosedBetweenReadAndPress_PressesNothing()
    {
        var sid = await PushSessionAsync();
        var gridCall = 0;

        // The brain recognizes the menu on the FIRST read. But by the time we go to press, the menu has closed
        // and a shell prompt is on screen - the re-verify must catch that and press NOTHING (no selection bytes
        // into the shell).
        _brainReply = _ => Wrapped(
            "{\"isMenu\":true,\"question\":\"Proceed?\",\"selectionMode\":\"single\",\"submit\":\"\"," +
            "\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\"},{\"key\":\"2. No\",\"send\":\"2\\r\"}]}");

        _dispatch = cmd =>
        {
            if (cmd.Verb == "screen-grid")
            {
                // First read: the menu. Re-verify read (and after): the menu is gone, a shell prompt is up.
                var call = System.Threading.Interlocked.Increment(ref gridCall);
                return call == 1
                    ? Ok(PrimaryMenuGrid(sid))
                    : Ok(new ScreenGridResponse
                    {
                        SessionId = sid,
                        Rows = new List<string> { "user@host:~/repo$ ", "" },
                        CursorRow = 0,
                        CursorCol = 18,
                        IsAlternateScreen = false,
                        HasGrid = true,
                    });
            }
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "option one" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        // No keystrokes reached the terminal - not the option, not the spoken words.
        Assert.Empty(DispatchedPrompts());
    }

    // ===================== Fail-closed: a menu the brain cannot read types nothing =====================

    [Fact]
    public async Task VoiceTurn_LiveGridMenu_BrainCannotRead_TypesNothing()
    {
        var sid = await PushSessionAsync();
        _brainReply = null; // brain throws => detection fails => fail closed

        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(PrimaryMenuGrid(sid)),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "yeah go ahead" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        Assert.Empty(DispatchedPrompts());
        Assert.Contains(_commands, c => c.Verb == "screen-grid");
    }

    // ===================== Finding 5: plain text types ONLY on a positive composer signal =====================

    [Fact]
    public async Task VoiceTurn_PrimaryComposerWithCursor_TypesTheSpokenAnswer()
    {
        var sid = await PushSessionAsync();
        var spoken = "add a retry to the upload path";
        var promptSent = false;

        _dispatch = cmd => cmd.Verb switch
        {
            "screen-grid" => Ok(ComposerGrid(sid)),
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

        // It types only because the LIVE screen was POSITIVELY classified plain text (primary composer, cursor
        // on the prompt line). The translate step then fails (stub brain has no reply set), which is
        // irrelevant to the claim: the answer WAS typed.
        await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = spoken });

        Assert.Contains(DispatchedPrompts(), p => p.Text == spoken && p.AppendEnter);
    }

    [Fact]
    public async Task VoiceTurn_BorderedSelectorHiddenCursor_TypesNothing()
    {
        // Named test / B1: a BORDERED "> production" selector - the exact thing that used to read as a composer
        // because of the box frame. The hardware cursor is HIDDEN (a menu selection), so typing is refused; and
        // it is not a recognized menu (no numbered option after the marker), so nothing is pressed either.
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
    public async Task VoiceTurn_SelectorAboveFooterHiddenCursor_TypesNothing()
    {
        // Named test / B1: a ">"-row directly above the mode-status footer, cursor HIDDEN. The footer used to
        // be read as composer framing; with a hidden cursor it fails closed.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "> production", "  ? for shortcuts" },
                CursorRow = 0,
                CursorCol = 5,
                CursorVisible = false,
                IsAlternateScreen = false,
                HasGrid = true,
            })
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "production");
    }

    [Fact]
    public async Task VoiceTurn_MenuMarkerAndLiveComposerBothPresent_TypesNothing()
    {
        // Named test B (round-4 correction): a drawn menu marker AND a live composer on the same grid is
        // AMBIGUOUS. When in doubt, block - neither type the words nor press the (possibly stale) menu.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string>
                {
                    "Do you want to proceed?",
                    "❯ 1. Yes",
                    "  2. No",
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
        await AssertTypesNothing(sid, "now run the integration tests");
    }

    [Fact]
    public async Task VoiceTurn_ReplacementMenuSameLabels_ReVerifyFailsClosed_PressesNothing()
    {
        // Named test C: the menu is REPLACED between classification and press by a different menu with the SAME
        // option labels (Delete production? -> Deploy production?, both 1.Yes / 2.No). The re-verify compares
        // the captured menu BLOCK (question + options), so it catches the change and presses nothing.
        var sid = await PushSessionAsync();
        var gridCall = 0;

        _brainReply = _ => Wrapped(
            "{\"isMenu\":true,\"question\":\"Delete production?\",\"selectionMode\":\"single\",\"submit\":\"\"," +
            "\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\"},{\"key\":\"2. No\",\"send\":\"2\\r\"}]}");

        _dispatch = cmd =>
        {
            if (cmd.Verb == "screen-grid")
            {
                var call = System.Threading.Interlocked.Increment(ref gridCall);
                var question = call == 1 ? "Delete production?" : "Deploy production?";
                // The question is separated from the options by a BLANK line - the signature must still catch
                // the change (finding B3, round-4). Cursor hidden, like a real menu.
                return Ok(new ScreenGridResponse
                {
                    SessionId = sid,
                    Rows = new List<string> { question, "", "❯ 1. Yes", "  2. No" },
                    CursorRow = 0,
                    CursorCol = -1,
                    CursorVisible = false,
                    IsAlternateScreen = true,
                    HasGrid = true,
                });
            }
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        };

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/voice-turn", new { text = "yes" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["cannotType"]?.GetValue<bool>());
        Assert.Empty(DispatchedPrompts());
    }

    [Fact]
    public async Task VoiceTurn_MenuLookingScreen_DetectorReturnsNotAMenu_TypesNothing()
    {
        // Named test D: the live screen looks like a menu and the brain is reached, but it returns isMenu:false
        // (no pressable options). This must fail closed - never fall through to typing.
        var sid = await PushSessionAsync();
        _brainReply = _ => Wrapped("{\"isMenu\":false}");
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(PrimaryMenuGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "yes go ahead");
    }

    [Fact]
    public async Task VoiceTurn_MenuWithBareNumberLabels_TypesNothing()
    {
        // Finding 4: the detector returns isMenu:true but the options are bare "1."/"2." with no words - not
        // answerable. Block it rather than pressing a meaningless choice.
        var sid = await PushSessionAsync();
        // The grid IS a real drawn menu (so the classifier says Menu), but the detector extracts bare labels.
        _brainReply = _ => Wrapped(
            "{\"isMenu\":true,\"question\":\"Pick\",\"selectionMode\":\"single\",\"submit\":\"\"," +
            "\"options\":[{\"key\":\"1.\",\"send\":\"1\\r\"},{\"key\":\"2.\",\"send\":\"2\\r\"}]}");
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(PrimaryMenuGrid(sid))
            : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "x");
        await AssertTypesNothing(sid, "one");
    }

    // ===================== Finding 6: cover the fail-closed edges - each types NOTHING =====================

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
        // A full-screen app on the alternate screen we did NOT recognize as a menu (no fingerprint). The old
        // inversion would have called this plain text and typed into it.
        var sid = await PushSessionAsync();
        _dispatch = cmd => cmd.Verb == "screen-grid"
            ? Ok(new ScreenGridResponse
            {
                SessionId = sid,
                Rows = new List<string> { "a full screen viewer", "line of content", "another line", "status: reading" },
                CursorRow = 3,
                CursorCol = 8,
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

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
