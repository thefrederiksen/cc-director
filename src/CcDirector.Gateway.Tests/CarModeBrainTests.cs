using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Car Mode brain (Car Mode mission, New build A) is a hand-rolled tool-calling loop. These tests
/// drive the loop with a scripted fake chat and a fake fleet (no network, no model), proving it executes
/// the tools the model calls, feeds the results back, returns the final spoken reply, keeps per-device
/// context, and fails loud rather than looping forever - plus the fuzzy session resolver and the parse.
/// </summary>
public sealed class CarModeBrainTests
{
    /// <summary>A chat that returns pre-scripted assistant turns in order, and records the messages it saw.</summary>
    private sealed class ScriptedChat : ICarModeChat
    {
        private readonly Queue<CarModeAssistantTurn> _turns;
        public List<string> SeenMessages { get; } = new();
        public ScriptedChat(params CarModeAssistantTurn[] turns) => _turns = new Queue<CarModeAssistantTurn>(turns);
        public Task<CarModeAssistantTurn> CompleteAsync(string messagesJson, string toolsJson, CancellationToken ct)
        {
            SeenMessages.Add(messagesJson);
            if (_turns.Count == 0) throw new InvalidOperationException("script exhausted");
            return Task.FromResult(_turns.Dequeue());
        }
    }

    private sealed class FakeFleet : ICarModeFleet
    {
        public int ListCalls;
        public List<string> ActivityRefs { get; } = new();
        public IReadOnlyList<CarModeSessionInfo> Sessions { get; set; } = new List<CarModeSessionInfo>();
        public CarModeActivity? Activity { get; set; }
        public Task<IReadOnlyList<CarModeSessionInfo>> ListSessionsAsync(CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult(Sessions);
        }
        public Task<CarModeActivity?> GetSessionActivityAsync(string sessionReference, CancellationToken ct)
        {
            ActivityRefs.Add(sessionReference);
            return Task.FromResult(Activity);
        }
    }

    private static CarModeToolCall Call(string name, string args = "{}") => new("call_1", name, args);

    private static CarModeSessionInfo Session(string name, bool needsYou) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        Name = name,
        Repo = "devthrottle",
        MachineName = "SOREN_NORTH",
        State = needsYou ? "Needs you" : "Working",
        NeedsYou = needsYou,
        Summary = needsYou ? "waiting for your answer" : "running the tests",
    };

    [Fact]
    public async Task RunTurn_ModelCallsListTool_ThenAnswers_ExecutesToolAndReturnsSpoken()
    {
        var fleet = new FakeFleet { Sessions = new[] { Session("Local Files Manager", true), Session("Car Mode Worker", false) } };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_sessions") }),
            new CarModeAssistantTurn("One session needs you: Local Files Manager, in the devthrottle repo.", Array.Empty<CarModeToolCall>()));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), _ => { });

        var result = await brain.RunTurnAsync("device-a", "how many need me over", CancellationToken.None);

        Assert.Equal(1, fleet.ListCalls);
        Assert.Contains("Local Files Manager", result.Spoken);
        Assert.Empty(result.Actions); // a read produces no action record
        // The second round's message list must carry the tool result back to the model.
        Assert.Equal(2, chat.SeenMessages.Count);
        Assert.Contains("needsYouCount", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_ModelCallsActivityTool_PassesReferenceThrough()
    {
        var fleet = new FakeFleet
        {
            Activity = new CarModeActivity
            {
                SessionId = "s1", Name = "Car Mode Manager", Repo = "devthrottle",
                State = "Working", Summary = "building the fleet brain", NeedsYou = false,
            },
        };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_session_activity", "{\"session\":\"car mode\"}") }),
            new CarModeAssistantTurn("Car Mode Manager, in the devthrottle repo, is building the fleet brain.", Array.Empty<CarModeToolCall>()));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), _ => { });

        var result = await brain.RunTurnAsync("device-a", "what is the car mode session doing", CancellationToken.None);

        Assert.Single(fleet.ActivityRefs);
        Assert.Equal("car mode", fleet.ActivityRefs[0]);
        Assert.Contains("building the fleet brain", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_KeepsPerDeviceContext_AcrossTurns()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        var chat1 = new ScriptedChat(new CarModeAssistantTurn("Nothing needs you right now.", Array.Empty<CarModeToolCall>()));
        var brain1 = new CarModeBrain(chat1, fleet, store, _ => { });
        await brain1.RunTurnAsync("device-a", "who needs me", CancellationToken.None);

        // The next turn on the SAME device must carry the prior exchange into the model's messages.
        var chat2 = new ScriptedChat(new CarModeAssistantTurn("Still nothing.", Array.Empty<CarModeToolCall>()));
        var brain2 = new CarModeBrain(chat2, fleet, store, _ => { });
        await brain2.RunTurnAsync("device-a", "and now", CancellationToken.None);

        Assert.Contains("who needs me", chat2.SeenMessages[0]);
        Assert.Contains("Nothing needs you right now.", chat2.SeenMessages[0]);
    }

    [Fact]
    public async Task RunTurn_ContextIsIsolatedPerDevice()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        var brainA = new CarModeBrain(new ScriptedChat(new CarModeAssistantTurn("A reply", Array.Empty<CarModeToolCall>())), fleet, store, _ => { });
        await brainA.RunTurnAsync("device-a", "secret A question", CancellationToken.None);

        var chatB = new ScriptedChat(new CarModeAssistantTurn("B reply", Array.Empty<CarModeToolCall>()));
        var brainB = new CarModeBrain(chatB, fleet, store, _ => { });
        await brainB.RunTurnAsync("device-b", "question B", CancellationToken.None);

        Assert.DoesNotContain("secret A question", chatB.SeenMessages[0]);
    }

    [Fact]
    public async Task RunTurn_EmptyText_Throws()
    {
        var brain = new CarModeBrain(new ScriptedChat(), new FakeFleet(), new CarModeConversationStore(), _ => { });
        await Assert.ThrowsAsync<ArgumentException>(() => brain.RunTurnAsync("device-a", "   ", CancellationToken.None));
    }

    [Fact]
    public async Task RunTurn_ModelLoopsToolsForever_HitsRoundCap_ReturnsLoudFailure()
    {
        var fleet = new FakeFleet { Sessions = Array.Empty<CarModeSessionInfo>() };
        // Every round asks for a tool and never answers - the loop must stop and speak a failure.
        var turns = Enumerable.Range(0, 10).Select(_ => new CarModeAssistantTurn(null, new[] { Call("list_sessions") })).ToArray();
        var brain = new CarModeBrain(new ScriptedChat(turns), fleet, new CarModeConversationStore(), _ => { });

        var result = await brain.RunTurnAsync("device-a", "who needs me", CancellationToken.None);

        Assert.Contains("trouble", result.Spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTurn_EmptyFinalReply_Throws()
    {
        var brain = new CarModeBrain(
            new ScriptedChat(new CarModeAssistantTurn("   ", Array.Empty<CarModeToolCall>())),
            new FakeFleet(), new CarModeConversationStore(), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.RunTurnAsync("device-a", "hi", CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"session\":\"devthrottle\"}", "session", "devthrottle")]
    [InlineData("{}", "session", null)]
    [InlineData("not json", "session", null)]
    [InlineData("{\"other\":1}", "session", null)]
    public void ReadStringArg_ParsesOrDegradesToNull(string args, string name, string? expected)
        => Assert.Equal(expected, CarModeBrain.ReadStringArg(args, name));

    // ---- The chat transport parse ----

    [Fact]
    public void ParseAssistantTurn_ContentOnly_NoToolCalls()
    {
        var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello there\"}}]}";
        var turn = HostedCarModeChat.ParseAssistantTurn(body);
        Assert.Equal("hello there", turn.Content);
        Assert.Empty(turn.ToolCalls);
    }

    [Fact]
    public void ParseAssistantTurn_ToolCalls_ExtractsNameAndArguments()
    {
        var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":["
            + "{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"list_sessions\",\"arguments\":\"{}\"}}]}}]}";
        var turn = HostedCarModeChat.ParseAssistantTurn(body);
        Assert.Null(turn.Content);
        Assert.Single(turn.ToolCalls);
        Assert.Equal("list_sessions", turn.ToolCalls[0].Name);
        Assert.Equal("c1", turn.ToolCalls[0].Id);
    }

    [Fact]
    public void ParseAssistantTurn_NoChoices_Throws()
        => Assert.Throws<InvalidOperationException>(() => HostedCarModeChat.ParseAssistantTurn("{\"choices\":[]}"));

    // ---- The fuzzy session resolver ----

    private static SessionDto Dto(string name, int? number, string repoPath, int createdMinutesAgo) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        Name = name,
        Number = number,
        RepoPath = repoPath,
        CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo),
    };

    [Fact]
    public void ResolveSession_ByExactNumber()
    {
        var sessions = new[] { Dto("Alpha", 101, "C:/repos/one", 10), Dto("Beta", 104, "C:/repos/two", 5) };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "session 104");
        Assert.NotNull(match);
        Assert.Equal("Beta", match!.Name);
    }

    [Fact]
    public void ResolveSession_ByNameSubstring()
    {
        var sessions = new[] { Dto("Local Files Manager", 101, "C:/repos/devthrottle", 10), Dto("Car Mode Worker", 102, "C:/repos/devthrottle", 5) };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "car mode");
        Assert.NotNull(match);
        Assert.Equal("Car Mode Worker", match!.Name);
    }

    [Fact]
    public void ResolveSession_ByRepoLeaf()
    {
        var sessions = new[] { Dto("Alpha", 101, "C:/repos/website", 10), Dto("Beta", 102, "C:/repos/devthrottle", 5) };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "devthrottle");
        Assert.NotNull(match);
        Assert.Equal("Beta", match!.Name);
    }

    [Fact]
    public void ResolveSession_NoMatch_ReturnsNull()
    {
        var sessions = new[] { Dto("Alpha", 101, "C:/repos/one", 10) };
        Assert.Null(LoopbackCarModeFleet.ResolveSession(sessions, "nonexistent zebra"));
    }

    [Theory]
    [InlineData("C:/repos/devthrottle", "devthrottle")]
    [InlineData("C:\\repos\\devthrottle", "devthrottle")]
    [InlineData("C:/repos/devthrottle/", "devthrottle")]
    [InlineData("", "")]
    public void RepoLeaf_TakesLastSegment(string path, string expected)
        => Assert.Equal(expected, LoopbackCarModeFleet.RepoLeaf(path));

    // ---- The per-device conversation store ----

    [Fact]
    public void ConversationStore_AppendAndGet_RoundTrips()
    {
        var store = new CarModeConversationStore(_ => { });
        store.Append("dev", "hi", "hello");
        var history = store.GetHistory("dev");
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("hi", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
    }

    [Fact]
    public void ConversationStore_TrimsToBoundedHistory()
    {
        var store = new CarModeConversationStore(_ => { });
        for (var i = 0; i < 20; i++) store.Append("dev", $"q{i}", $"a{i}");
        var history = store.GetHistory("dev");
        Assert.True(history.Count <= 16);
        // The most recent exchange is kept.
        Assert.Equal("a19", history[^1].Content);
    }
}
