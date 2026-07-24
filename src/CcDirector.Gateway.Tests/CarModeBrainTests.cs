using CcDirector.Gateway.CarMode;
using CcDirector.Core.Tenancy;
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
        public Task<CarModeAssistantTurn> CompleteAsync(TenantId tenant, string messagesJson, string toolsJson, CancellationToken ct)
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

        // Phase 3 act tools: record what was called so tests can assert the loop reached the fleet.
        public CarModeSessionInfo? ResolveResult { get; set; }
        public List<string> StartedRepos { get; } = new();
        public List<(string SessionId, string Message)> Messaged { get; } = new();
        public List<string> Approved { get; } = new();
        public List<string> Deleted { get; } = new();

        // Voice-screen-actions phase tools.
        public CarModeExplain ExplainResult { get; set; } = new("", false);
        public List<string> Explained { get; } = new();
        public List<(string SessionId, bool Enabled)> VoiceModeSet { get; } = new();
        public List<string> Snoozed { get; } = new();

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
        public Task<CarModeSessionInfo?> ResolveSessionAsync(string sessionReference, CancellationToken ct)
            => Task.FromResult(ResolveResult);
        public Task<string> StartSessionAsync(string repo, CancellationToken ct)
        {
            StartedRepos.Add(repo);
            return Task.FromResult($"Started a session in the {repo} repository on TESTBOX.");
        }
        public Task MessageSessionAsync(string sessionId, string message, CancellationToken ct)
        {
            Messaged.Add((sessionId, message));
            return Task.CompletedTask;
        }
        public Task ApproveSessionAsync(string sessionId, CancellationToken ct)
        {
            Approved.Add(sessionId);
            return Task.CompletedTask;
        }
        public Task DeleteSessionAsync(string sessionId, CancellationToken ct)
        {
            Deleted.Add(sessionId);
            return Task.CompletedTask;
        }
        public Task<CarModeExplain> ExplainSessionAsync(string sessionId, CancellationToken ct)
        {
            Explained.Add(sessionId);
            return Task.FromResult(ExplainResult);
        }
        public Task SwitchVoiceModeAsync(string sessionId, bool enabled, CancellationToken ct)
        {
            VoiceModeSet.Add((sessionId, enabled));
            return Task.CompletedTask;
        }
        public Task SnoozeSessionAsync(string sessionId, CancellationToken ct)
        {
            Snoozed.Add(sessionId);
            return Task.CompletedTask;
        }

        // Fleet-overview read tools (the cockpit Assistant build).
        public int CreditsCalls;
        public CarModeCredits CreditsResult { get; set; } = new(false, null, null);
        public IReadOnlyList<CarModeMachineInfo> Machines { get; set; } = new List<CarModeMachineInfo>();
        public IReadOnlyList<CarModeScheduleInfo> Schedules { get; set; } = new List<CarModeScheduleInfo>();
        public List<int> SpendDays { get; } = new();
        public CarModeSpendSummary SpendResult { get; set; } = new(0, 0, DateTime.UtcNow, DateTime.UtcNow);

        public Task<CarModeCredits> GetCreditsAsync(CancellationToken ct)
        {
            CreditsCalls++;
            return Task.FromResult(CreditsResult);
        }
        public Task<IReadOnlyList<CarModeMachineInfo>> ListMachinesAsync(CancellationToken ct)
            => Task.FromResult(Machines);
        public Task<IReadOnlyList<CarModeScheduleInfo>> ListSchedulesAsync(CancellationToken ct)
            => Task.FromResult(Schedules);
        public Task<CarModeSpendSummary> GetSpendAsync(int days, CancellationToken ct)
        {
            SpendDays.Add(days);
            return Task.FromResult(SpendResult);
        }
    }

    private static CarModeToolCall Call(string name, string args = "{}") => new("call_1", name, args);

    /// <summary>A final spoken answer the way the real model must produce it under tool_choice=required: by
    ///  calling speak_answer. Bare-content final answers are rejected by the brain's hallucination guard, so
    ///  tests model the final say through speak_answer, matching production.</summary>
    private static CarModeAssistantTurn Speak(string text) =>
        new(null, new[] { new CarModeToolCall("call_speak", "speak_answer", "{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}") });

    private static CarModeSessionInfo Info(string name, string id) => new()
    {
        SessionId = id,
        Name = name,
        Repo = "devthrottle",
        MachineName = "TESTBOX",
        State = "Working",
        Summary = "",
    };

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
            Speak("One session needs you: Local Files Manager, in the devthrottle repo."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "how many need me over", CancellationToken.None);

        Assert.Equal(1, fleet.ListCalls);
        Assert.Contains("Local Files Manager", result.Spoken);
        Assert.Empty(result.Actions); // a read produces no action record
        // The second round's message list must carry the tool result back to the model.
        Assert.Equal(2, chat.SeenMessages.Count);
        Assert.Contains("needsYouCount", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_MeasuresPerStageTiming_ModelCallsAndFleetReads()
    {
        // Performance round: the turn response carries a per-stage timing breakdown. A list-then-answer turn
        // is two model round trips and one fleet read; the timing must reflect exactly that.
        var fleet = new FakeFleet { Sessions = new[] { Session("Local Files Manager", true) } };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_sessions") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"One session needs you.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "who needs me over", CancellationToken.None);

        Assert.NotNull(result.Timing);
        Assert.Equal(2, result.Timing!.ModelCallCount);
        Assert.Equal(2, result.Timing.ModelMs.Count);
        Assert.Equal(2, result.Timing.Rounds);
        Assert.Equal(1, result.Timing.FleetReadCount); // exactly one roster read for list_sessions
        Assert.True(result.Timing.TotalMs >= 0);
    }

    [Fact]
    public async Task RunTurn_GeneralQuestion_AnswersDirectly_WithNoFleetRead()
    {
        // The fleet-read suppression: a general question the model answers directly by calling speak_answer
        // makes NO fleet read, so the roster aggregation never runs and the turn is fast.
        var fleet = new FakeFleet { Sessions = new[] { Session("Local Files Manager", true) } };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"I can run your fleet by voice.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "what can you help me with over", CancellationToken.None);

        Assert.Equal("I can run your fleet by voice.", result.Spoken);
        Assert.Equal(0, fleet.ListCalls);
        Assert.NotNull(result.Timing);
        Assert.Equal(0, result.Timing!.FleetReadCount);
        Assert.Equal(1, result.Timing.ModelCallCount);
    }

    [Fact]
    public async Task RunTurn_ModelCallsGetHelp_SpeaksCuratedScriptVerbatim_WithNoFleetRead()
    {
        // Help Mode (issue #1441): when the model classifies a "help" / "what can you do" turn it calls
        // get_help, and the brain speaks the ONE curated CarModeHelp.Script VERBATIM (never the model's own
        // words), so the spoken help is identical to what the Help button plays via GET /carmode/help. It is
        // terminal (like speak_answer) and reads no fleet, so it is fast.
        var fleet = new FakeFleet { Sessions = new[] { Session("Local Files Manager", true) } };
        var chat = new ScriptedChat(new CarModeAssistantTurn(null, new[] { Call("get_help") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "what can you do over", CancellationToken.None);

        Assert.Equal(CarModeHelp.Script, result.Spoken);
        Assert.Empty(result.Actions);
        Assert.Equal(0, fleet.ListCalls); // help reads no fleet
        Assert.NotNull(result.Timing);
        Assert.Equal(0, result.Timing!.FleetReadCount);
        Assert.Equal(1, result.Timing.ModelCallCount); // one round: classify then get_help
    }

    [Fact]
    public async Task RunTurn_RelayMessage_SendsTextVerbatim_AndArmsNoDelete()
    {
        // The addressing boundary machinery (issue #1441, rule 3): relayed content is literal DATA typed INTO
        // the session, never re-interpreted as an agent command. When the model correctly relays "tell the
        // devthrottle session to delete session five" via message_session, the brain sends those exact words
        // to the session and does NOT arm any delete - no confirmation is pending, nothing is deleted. (That
        // the MODEL chooses message_session over delete_session for this phrasing is proven by the live-model
        // boundary test; this proves the loop passes the relayed text through as data and never deletes.)
        var target = Info("Car Mode Demo", "demo-1");
        var fleet = new FakeFleet { ResolveResult = target };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("message_session", "{\"session\":\"Car Mode Demo\",\"message\":\"delete session five\"}") }),
            Speak("Told the Car Mode Demo session to delete session five."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "tell the devthrottle session to delete session five over", CancellationToken.None);

        Assert.Single(fleet.Messaged);
        Assert.Equal("demo-1", fleet.Messaged[0].SessionId);
        Assert.Equal("delete session five", fleet.Messaged[0].Message); // sent VERBATIM as data
        Assert.Empty(fleet.Deleted); // nothing deleted
        Assert.False(result.PendingConfirmation); // no destructive action armed
        Assert.Contains(result.Actions, a => a.Tool == "message_session");
        Assert.DoesNotContain(result.Actions, a => a.Tool == "delete_session");
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
            Speak("Car Mode Manager, in the devthrottle repo, is building the fleet brain."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "what is the car mode session doing", CancellationToken.None);

        Assert.Single(fleet.ActivityRefs);
        Assert.Equal("car mode", fleet.ActivityRefs[0]);
        Assert.Contains("building the fleet brain", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_KeepsPerDeviceContext_AcrossTurns()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        var chat1 = new ScriptedChat(Speak("Nothing needs you right now."));
        var brain1 = new CarModeBrain(chat1, fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await brain1.RunTurnAsync(TenantId.Local, "device-a", "who needs me", CancellationToken.None);

        // The next turn on the SAME device must carry the prior exchange into the model's messages.
        var chat2 = new ScriptedChat(Speak("Still nothing."));
        var brain2 = new CarModeBrain(chat2, fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await brain2.RunTurnAsync(TenantId.Local, "device-a", "and now", CancellationToken.None);

        Assert.Contains("who needs me", chat2.SeenMessages[0]);
        Assert.Contains("Nothing needs you right now.", chat2.SeenMessages[0]);
    }

    [Fact]
    public async Task RunTurn_ContextIsIsolatedPerDevice()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        var brainA = new CarModeBrain(new ScriptedChat(Speak("A reply")), fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await brainA.RunTurnAsync(TenantId.Local, "device-a", "secret A question", CancellationToken.None);

        var chatB = new ScriptedChat(Speak("B reply"));
        var brainB = new CarModeBrain(chatB, fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await brainB.RunTurnAsync(TenantId.Local, "device-b", "question B", CancellationToken.None);

        Assert.DoesNotContain("secret A question", chatB.SeenMessages[0]);
    }

    [Fact]
    public async Task RunTurn_EmptyText_Throws()
    {
        var brain = new CarModeBrain(new ScriptedChat(), new FakeFleet(), new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await Assert.ThrowsAsync<ArgumentException>(() => brain.RunTurnAsync(TenantId.Local, "device-a", "   ", CancellationToken.None));
    }

    [Fact]
    public async Task RunTurn_ModelLoopsToolsForever_HitsRoundCap_ReturnsLoudFailure()
    {
        var fleet = new FakeFleet { Sessions = Array.Empty<CarModeSessionInfo>() };
        // Every round asks for a tool and never answers - the loop must stop and speak a failure.
        var turns = Enumerable.Range(0, 10).Select(_ => new CarModeAssistantTurn(null, new[] { Call("list_sessions") })).ToArray();
        var brain = new CarModeBrain(new ScriptedChat(turns), fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "who needs me", CancellationToken.None);

        Assert.Contains("trouble", result.Spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTurn_EmptyFinalReply_Throws()
    {
        var brain = new CarModeBrain(
            new ScriptedChat(Speak("   ")),
            new FakeFleet(), new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.RunTurnAsync(TenantId.Local, "device-a", "hi", CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"session\":\"devthrottle\"}", "session", "devthrottle")]
    [InlineData("{}", "session", null)]
    [InlineData("not json", "session", null)]
    [InlineData("{\"other\":1}", "session", null)]
    // Defensive against the malformed shapes a model occasionally emits (must never crash the turn):
    [InlineData("[{\"repo\":\"cc-consult\"}]", "repo", "cc-consult")] // arguments wrapped in a one-element array
    [InlineData("{\"n\":5}", "n", "5")]                                // a number coerced to its text
    [InlineData("[\"just a string\"]", "repo", null)]                 // array of non-objects -> null, no throw
    [InlineData("[]", "repo", null)]                                   // empty array -> null, no throw
    public void ReadStringArg_ParsesOrDegradesToNull(string args, string name, string? expected)
        => Assert.Equal(expected, CarModeBrain.ReadStringArg(args, name));

    // ---- speak_answer (tool_choice=required): the model says its final words by calling a tool ----

    [Fact]
    public async Task RunTurn_SpeakAnswerTool_IsTheFinalReply()
    {
        // A pure conversational turn under tool_choice=required: the model calls only speak_answer.
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"Nothing needs you right now.\"}") }));
        var brain = new CarModeBrain(chat, new FakeFleet(), new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "anything for me", CancellationToken.None);

        Assert.Equal("Nothing needs you right now.", result.Spoken);
        Assert.Empty(result.Actions);
        Assert.False(result.PendingConfirmation);
    }

    [Fact]
    public async Task RunTurn_ListThenSpeakAnswer_ExecutesToolThenSpeaks()
    {
        var fleet = new FakeFleet { Sessions = new[] { Session("Local Files Manager", true) } };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_sessions") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"One session needs you: Local Files Manager.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "who needs me", CancellationToken.None);

        Assert.Equal(1, fleet.ListCalls);
        Assert.Contains("Local Files Manager", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_DeleteThenSpeakAnswer_ArmsAndSpeaksTheQuestion()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Old Worker", "s-9") };
        var pending = new CarModePendingStore(_ => { });
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("delete_session", "{\"session\":\"old worker\"}") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"Deleting Old Worker is permanent. Say confirm to proceed.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), pending, new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "delete old worker", CancellationToken.None);

        Assert.Empty(fleet.Deleted);          // not deleted - only armed
        Assert.True(result.PendingConfirmation);
        Assert.Contains("permanent", result.Spoken);
        Assert.NotNull(pending.Get("device-a"));
    }

    [Fact]
    public async Task RunTurn_EmptySpeakAnswer_RetriesInLoopRatherThanFailing()
    {
        // A model occasionally calls speak_answer with no words. The loop must NOT fail the turn - it
        // nudges the model, which speaks proper words on the next round.
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"  \"}") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"All set.\"}") }));
        var brain = new CarModeBrain(chat, new FakeFleet(), new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "hi", CancellationToken.None);

        Assert.Equal("All set.", result.Spoken);
    }

    // ---- Phase 3: the ordinary act tools run immediately ----

    [Fact]
    public async Task RunTurn_StartSession_CallsFleetAndRecordsAction()
    {
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("start_session", "{\"repo\":\"devthrottle\"}") }),
            Speak("Started a session in the devthrottle repo."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "start a session in devthrottle", CancellationToken.None);

        Assert.Single(fleet.StartedRepos);
        Assert.Equal("devthrottle", fleet.StartedRepos[0]);
        Assert.Contains(result.Actions, a => a.Tool == "start_session");
        Assert.False(result.PendingConfirmation);
    }

    [Fact]
    public async Task RunTurn_MessageSession_ResolvesThenSends()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Car Mode Worker", "s-42") };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("message_session", "{\"session\":\"car mode worker\",\"message\":\"run the tests\"}") }),
            Speak("Told Car Mode Worker to run the tests."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "message the worker", CancellationToken.None);

        Assert.Single(fleet.Messaged);
        Assert.Equal("s-42", fleet.Messaged[0].SessionId);
        Assert.Equal("run the tests", fleet.Messaged[0].Message);
        Assert.Contains(result.Actions, a => a.Tool == "message_session");
    }

    [Fact]
    public async Task RunTurn_MessageSession_UnresolvedReference_DoesNotSend()
    {
        var fleet = new FakeFleet { ResolveResult = null };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("message_session", "{\"session\":\"ghost\",\"message\":\"hi\"}") }),
            Speak("I couldn't find a session called ghost."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "message ghost", CancellationToken.None);

        Assert.Empty(fleet.Messaged);
    }

    // ---- Phase 3: the destructive tool is HELD for a spoken confirmation ----

    [Fact]
    public async Task RunTurn_DeleteSession_ArmsConfirmation_DoesNotDelete()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Old Worker", "s-9") };
        var pending = new CarModePendingStore(_ => { });
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("delete_session", "{\"session\":\"old worker\"}") }),
            Speak("Deleting Old Worker is permanent. Say confirm to proceed, or cancel."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), pending, new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "delete the old worker", CancellationToken.None);

        Assert.Empty(fleet.Deleted); // NOT deleted yet
        Assert.True(result.PendingConfirmation);
        Assert.NotNull(pending.Get("device-a"));
        Assert.Equal("s-9", pending.Get("device-a")!.SessionId);
    }

    [Fact]
    public async Task RunTurn_ConfirmAfterArmedDelete_ExecutesDelete()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Old Worker", "s-9") };
        var pending = new CarModePendingStore(_ => { });
        var store = new CarModeConversationStore();

        // Turn 1: arm.
        var brain1 = new CarModeBrain(
            new ScriptedChat(
                new CarModeAssistantTurn(null, new[] { Call("delete_session", "{\"session\":\"old worker\"}") }),
                Speak("Say confirm to delete Old Worker.")),
            fleet, store, pending, new CarModeSubjectStore(_ => { }), _ => { });
        await brain1.RunTurnAsync(TenantId.Local, "device-a", "delete old worker", CancellationToken.None);
        Assert.Empty(fleet.Deleted);

        // Turn 2: the owner confirms. The model is NOT consulted - the gate executes deterministically.
        var chat2 = new ScriptedChat(); // must not be called
        var brain2 = new CarModeBrain(chat2, fleet, store, pending, new CarModeSubjectStore(_ => { }), _ => { });
        var result = await brain2.RunTurnAsync(TenantId.Local, "device-a", "confirm", CancellationToken.None);

        Assert.Single(fleet.Deleted);
        Assert.Equal("s-9", fleet.Deleted[0]);
        Assert.Empty(chat2.SeenMessages); // no model call on the confirmation turn
        Assert.False(result.PendingConfirmation);
        Assert.Contains(result.Actions, a => a.Tool == "delete_session");
        Assert.Null(pending.Get("device-a")); // disarmed
    }

    [Fact]
    public async Task RunTurn_CancelAfterArmedDelete_DoesNotDelete()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Old Worker", "s-9") };
        var pending = new CarModePendingStore(_ => { });
        pending.Arm("device-a", new CarModePendingAction("delete", "s-9", "Old Worker"));

        var chat = new ScriptedChat(); // must not be called
        var brain = new CarModeBrain(chat, new FakeFleet(), new CarModeConversationStore(), pending, new CarModeSubjectStore(_ => { }), _ => { });
        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "no cancel that", CancellationToken.None);

        Assert.Empty(fleet.Deleted);
        Assert.Contains("Old Worker", result.Spoken);
        Assert.Null(pending.Get("device-a"));
    }

    [Fact]
    public async Task RunTurn_UnrelatedCommandAfterArmedDelete_DisarmsAndProceeds()
    {
        var pending = new CarModePendingStore(_ => { });
        pending.Arm("device-a", new CarModePendingAction("delete", "s-9", "Old Worker"));
        var fleet = new FakeFleet { Sessions = Array.Empty<CarModeSessionInfo>() };
        var chat = new ScriptedChat(Speak("Nothing needs you."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), pending, new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "who needs me", CancellationToken.None);

        // The armed delete is dropped (safe) and the new command is processed normally.
        Assert.Null(pending.Get("device-a"));
        Assert.Equal("Nothing needs you.", result.Spoken);
    }

    // ---- Voice-screen-actions phase: read wingman, switch voice, snooze, focus next, current subject ----

    private static CarModeSessionInfo Waiting(string name, string id, int waitingMinutes) => new()
    {
        SessionId = id,
        Name = name,
        Repo = "devthrottle",
        MachineName = "SOREN_NORTH",
        State = "Needs you",
        NeedsYou = true,
        WaitingMinutes = waitingMinutes,
        Summary = "waiting for your answer",
    };

    [Fact]
    public async Task RunTurn_ReadWingman_ReadsRealNarrationForResolvedSession()
    {
        var fleet = new FakeFleet
        {
            ResolveResult = Info("Car Mode Demo", "s-demo"),
            ExplainResult = new CarModeExplain("I need you to pick option two before I continue.", false),
        };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("read_wingman", "{\"session\":\"car mode demo\"}") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"Car Mode Demo needs you to pick option two.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "what does the car mode demo need over", CancellationToken.None);

        Assert.Single(fleet.Explained);
        Assert.Equal("s-demo", fleet.Explained[0]);
        // The REAL narration was handed to the model (round 2 sees it), not a canned line.
        Assert.Contains("pick option two", chat.SeenMessages[1]);
        Assert.Contains("Car Mode Demo", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_SwitchToVoiceMode_CallsFleetAndRecordsAction()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Car Mode Demo", "s-demo") };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("switch_to_voice_mode", "{\"session\":\"car mode demo\"}") }),
            Speak("Switched Car Mode Demo to voice mode."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "put the car mode demo in voice mode over", CancellationToken.None);

        Assert.Single(fleet.VoiceModeSet);
        Assert.Equal(("s-demo", true), fleet.VoiceModeSet[0]);
        Assert.Contains(result.Actions, a => a.Tool == "switch_to_voice_mode");
    }

    [Fact]
    public async Task RunTurn_Snooze_CallsFleetAndRecordsAction()
    {
        var fleet = new FakeFleet { ResolveResult = Info("Car Mode Demo", "s-demo") };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("snooze_session", "{\"session\":\"car mode demo\"}") }),
            Speak("Snoozed Car Mode Demo."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "snooze the car mode demo over", CancellationToken.None);

        Assert.Single(fleet.Snoozed);
        Assert.Equal("s-demo", fleet.Snoozed[0]);
        Assert.Contains(result.Actions, a => a.Tool == "snooze_session");
    }

    [Fact]
    public async Task RunTurn_FocusNext_PicksLongestWaiting_AndSetsSubject()
    {
        // Two need the owner; the one waiting longest is focused and becomes the current subject.
        var fleet = new FakeFleet
        {
            Sessions = new[] { Waiting("Recently Waiting", "s-new", 3), Waiting("Longest Waiting", "s-old", 40) },
        };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("focus_next_needs_me") }),
            new CarModeAssistantTurn(null, new[] { Call("speak_answer", "{\"text\":\"Longest Waiting has been waiting forty minutes.\"}") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "who needs me next over", CancellationToken.None);

        // The model saw the longest-waiting session as the focus result.
        Assert.Contains("Longest Waiting", chat.SeenMessages[1]);
        Assert.DoesNotContain("s-new", chat.SeenMessages[1]);
        Assert.Contains("Longest Waiting", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_FocusThenAnswerIt_ResolvesTheCurrentSubject()
    {
        // The whole loop: focus the next needs-you session (turn 1), then "answer it" (turn 2) with the
        // session OMITTED must message the focused session - proving the server-side current subject.
        var subjects = new CarModeSubjectStore(_ => { });
        var store = new CarModeConversationStore();
        var fleet = new FakeFleet { Sessions = new[] { Waiting("Car Mode Demo", "s-demo", 12) } };

        var brain1 = new CarModeBrain(
            new ScriptedChat(
                new CarModeAssistantTurn(null, new[] { Call("focus_next_needs_me") }),
                Speak("Car Mode Demo has been waiting twelve minutes.")),
            fleet, store, new CarModePendingStore(_ => { }), subjects, _ => { });
        await brain1.RunTurnAsync(TenantId.Local, "device-a", "read me the next one over", CancellationToken.None);

        // Turn 2: "answer it and say yes" - message_session with NO session argument -> the current subject.
        var brain2 = new CarModeBrain(
            new ScriptedChat(
                new CarModeAssistantTurn(null, new[] { Call("message_session", "{\"message\":\"yes\"}") }),
                Speak("Answered Car Mode Demo.")),
            fleet, store, new CarModePendingStore(_ => { }), subjects, _ => { });
        await brain2.RunTurnAsync(TenantId.Local, "device-a", "answer it and say yes over", CancellationToken.None);

        Assert.Single(fleet.Messaged);
        Assert.Equal("s-demo", fleet.Messaged[0].SessionId);
        Assert.Equal("yes", fleet.Messaged[0].Message);
    }

    [Fact]
    public async Task RunTurn_OmittedSessionWithNoSubject_ReturnsWhichOne_DoesNotAct()
    {
        // "snooze it" with nothing focused yet: the tool must not guess - it returns a "which one" the model
        // relays, and no fleet act happens.
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("snooze_session", "{}") }),
            Speak("Which session should I snooze?"));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "snooze it over", CancellationToken.None);

        Assert.Empty(fleet.Snoozed);
        // The model was handed the "which one" prompt to relay.
        Assert.Contains("Which session", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_DeleteOmittedSession_UsesSubject_AndNamesItInConfirmation()
    {
        // Even via the current-subject shortcut, delete must name the session and repo for the spoken
        // confirmation (Architect safety flag) and must NOT delete on this turn.
        var subjects = new CarModeSubjectStore(_ => { });
        subjects.Set("device-a", new CarModeSubject("s-demo", "Car Mode Demo", "devthrottle"));
        var pending = new CarModePendingStore(_ => { });
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("delete_session", "{}") }),
            Speak("Deleting Car Mode Demo in the devthrottle repo. Say confirm."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), pending, subjects, _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "remove it over", CancellationToken.None);

        Assert.Empty(fleet.Deleted);
        Assert.True(result.PendingConfirmation);
        Assert.Equal("s-demo", pending.Get("device-a")!.SessionId);
        // The tool result the model saw named the session and repo.
        Assert.Contains("Car Mode Demo", chat.SeenMessages[1]);
        Assert.Contains("devthrottle", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_BareContentClaimingAction_IsRejected_ForcesRealToolCall()
    {
        // The Car Mode hallucination gotcha: the model answers in plain text claiming it snoozed, WITHOUT
        // calling snooze_session. The guard must NOT return that false success - it re-prompts, and only the
        // real tool call (and its speak_answer) is the answer.
        var subjects = new CarModeSubjectStore(_ => { });
        subjects.Set("device-a", new CarModeSubject("s-demo", "Car Mode Demo", "devthrottle"));
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn("Okay, I've snoozed it for you.", Array.Empty<CarModeToolCall>()), // hallucinated claim, no tool
            new CarModeAssistantTurn(null, new[] { Call("snooze_session", "{}") }),                      // re-prompted -> real call
            Speak("Snoozed the Car Mode Demo."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), subjects, _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "snooze it", CancellationToken.None);

        Assert.Single(fleet.Snoozed);                       // the action really happened
        Assert.Equal("s-demo", fleet.Snoozed[0]);
        Assert.Equal("Snoozed the Car Mode Demo.", result.Spoken);   // NOT the hallucinated "I've snoozed it"
        Assert.Contains(result.Actions, a => a.Tool == "snooze_session");
    }

    [Fact]
    public async Task RunTurn_PersistentBareContent_EndsInLoudFailure_NeverTheContent()
    {
        // A model that keeps answering in plain text and never calls a tool must end in the loud failure,
        // never have its unbacked plain-text claim spoken as the answer.
        var turns = Enumerable.Range(0, 10)
            .Select(_ => new CarModeAssistantTurn("I did it.", Array.Empty<CarModeToolCall>())).ToArray();
        var brain = new CarModeBrain(new ScriptedChat(turns), new FakeFleet(), new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "snooze it", CancellationToken.None);

        Assert.DoesNotContain("I did it.", result.Spoken);
        Assert.Contains("trouble", result.Spoken, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The spoken-confirmation words ----

    [Theory]
    [InlineData("confirm")]
    [InlineData("yes")]
    [InlineData("yes do it")]
    [InlineData("go ahead")]
    [InlineData("confirmed")]
    public void CarModeConfirm_Affirmatives(string text) => Assert.True(CarModeConfirm.IsAffirmative(text));

    [Theory]
    [InlineData("no")]
    [InlineData("cancel")]
    [InlineData("cancel that")]
    [InlineData("don't")]
    [InlineData("never mind")]
    public void CarModeConfirm_NotAffirmative(string text) => Assert.False(CarModeConfirm.IsAffirmative(text));

    [Fact]
    public void CarModeConfirm_MixedIsNotAffirmative_NegativesWin()
    {
        // "yes but no" or "confirm... no wait" must NOT delete - negatives win for safety.
        Assert.False(CarModeConfirm.IsAffirmative("yes no wait"));
    }

    [Fact]
    public void CarModeConfirm_NoInsideNorthDoesNotFire()
    {
        // Whole-word: "north" must not read as "no".
        Assert.False(CarModeConfirm.IsNegative("soren north"));
    }

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

    [Fact]
    public void ResolveSession_NameAndRepoPhrase_ResolvesByName_NotAnotherSameRepoSession()
    {
        // The wrong-session bug (Car Mode QA): the model echoes its own narration - "Car Mode Demo, in the
        // devthrottle repo" - as the tool argument. The comma must not break the name match, and a NAME match
        // must beat every other session that merely shares the devthrottle repo (here a NEWER one).
        var sessions = new[]
        {
            Dto("Car Mode Demo", 104, "C:/repos/devthrottle", createdMinutesAgo: 60),        // older, the real target
            Dto("Gateway Cleanup - Manager", 110, "C:/repos/devthrottle", createdMinutesAgo: 5), // newer, same repo
        };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "Car Mode Demo, in the devthrottle repo");
        Assert.NotNull(match);
        Assert.Equal("Car Mode Demo", match!.Name);
    }

    [Fact]
    public void ResolveSession_RepoOnlyPhrase_FallsBackToNewestInThatRepo()
    {
        // With NO name in the reference, the repo fallback still resolves "the devthrottle session" to the
        // newest one - name-priority does not disable the repo fallback, it only outranks it.
        var sessions = new[]
        {
            Dto("Alpha", 101, "C:/repos/website", 60),
            Dto("Beta", 102, "C:/repos/devthrottle", 30),
            Dto("Gamma", 103, "C:/repos/devthrottle", 5),
        };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "the devthrottle session");
        Assert.NotNull(match);
        Assert.Equal("Gamma", match!.Name);
    }

    [Fact]
    public void ResolveSession_PunctuationAndDashInName_StillMatches()
    {
        var sessions = new[] { Dto("Car Mode - Manager", 109, "C:/repos/devthrottle", 5) };
        var match = LoopbackCarModeFleet.ResolveSession(sessions, "car mode manager");
        Assert.NotNull(match);
        Assert.Equal("Car Mode - Manager", match!.Name);
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

    // ---- Phase 4b: the idempotency + single-flight turn cache (issue #1427) ----
    // The mission guarantee: a turn retried with the SAME client turn key ACTS and APPENDS to the
    // conversation AT MOST ONCE, so an already-sent turn whose result was lost in a dead zone can auto-retry
    // safely. Distinct keys act independently; a brain FAILURE evicts the key so a transient error recovers.

    [Fact]
    public async Task TurnCache_DuplicateKey_ActsOnceAndAppendsOnce()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        // One turn: start a session, then speak. The scripted chat has exactly the two rounds for ONE run;
        // if the cache wrongly re-ran the brain, the script would be exhausted (throw) and the fleet would
        // start twice - so this test fails loudly on any dedupe break.
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("start_session", "{\"repo\":\"devthrottle\"}") }),
            Speak("Started a session in the devthrottle repo."));
        var brain = new CarModeBrain(chat, fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        var cache = new CarModeTurnCache(_ => { });

        var r1 = await cache.GetOrRunAsync("dev", "turn-1", () => brain.RunTurnAsync(TenantId.Local, "dev", "start a session in devthrottle", CancellationToken.None));
        var r2 = await cache.GetOrRunAsync("dev", "turn-1", () => brain.RunTurnAsync(TenantId.Local, "dev", "start a session in devthrottle", CancellationToken.None));

        Assert.Single(fleet.StartedRepos);                 // the fleet action fired EXACTLY once
        Assert.Equal("devthrottle", fleet.StartedRepos[0]);
        Assert.Equal(2, store.GetHistory("dev").Count);    // ONE exchange appended (user + assistant), not two
        Assert.Equal("Started a session in the devthrottle repo.", r1.Spoken);
        Assert.Equal(r1.Spoken, r2.Spoken);                // the duplicate got the SAME cached result
    }

    [Fact]
    public async Task TurnCache_DistinctKeys_ActTwice()
    {
        var fleet = new FakeFleet();
        var store = new CarModeConversationStore();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("start_session", "{\"repo\":\"devthrottle\"}") }),
            Speak("Started one."),
            new CarModeAssistantTurn(null, new[] { Call("start_session", "{\"repo\":\"devthrottle\"}") }),
            Speak("Started two."));
        var brain = new CarModeBrain(chat, fleet, store, new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });
        var cache = new CarModeTurnCache(_ => { });

        await cache.GetOrRunAsync("dev", "key-A", () => brain.RunTurnAsync(TenantId.Local, "dev", "start a session", CancellationToken.None));
        await cache.GetOrRunAsync("dev", "key-B", () => brain.RunTurnAsync(TenantId.Local, "dev", "start another", CancellationToken.None));

        Assert.Equal(2, fleet.StartedRepos.Count);         // two distinct turn keys act independently
        Assert.Equal(4, store.GetHistory("dev").Count);    // two exchanges appended
    }

    [Fact]
    public async Task TurnCache_BrainThrows_EvictsKeySoRetryReRuns()
    {
        // A brain exception must NOT be cached: the key is evicted so a transient failure can recover on the
        // next retry (caching the failure would trap the error until the TTL).
        var cache = new CarModeTurnCache(_ => { });
        var calls = 0;
        Func<Task<CarModeTurnResponse>> run = () =>
        {
            calls++;
            return Task.FromException<CarModeTurnResponse>(new InvalidOperationException("transient"));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrRunAsync("dev", "k", run));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrRunAsync("dev", "k", run));

        Assert.Equal(2, calls);                            // evicted after the first failure -> the retry re-ran
    }

    [Fact]
    public async Task TurnCache_SuccessIsRetainedForTheKey()
    {
        var cache = new CarModeTurnCache(_ => { });
        var calls = 0;
        Func<Task<CarModeTurnResponse>> run = () =>
        {
            calls++;
            return Task.FromResult(new CarModeTurnResponse { Spoken = "done" });
        };

        var a = await cache.GetOrRunAsync("dev", "k", run);
        var b = await cache.GetOrRunAsync("dev", "k", run);

        Assert.Equal(1, calls);                            // ran once; the duplicate returned the cached result
        Assert.Equal("done", a.Spoken);
        Assert.Same(a, b);                                 // the very same cached response object
        Assert.Equal(1, cache.Count());
    }

    // ---- Fleet-overview read tools (the cockpit Assistant build) ----

    [Fact]
    public async Task RunTurn_GetCredits_SignedIn_ReturnsDollarsToTheModel()
    {
        var fleet = new FakeFleet { CreditsResult = new CarModeCredits(true, 12_340_000, 5_600) };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_credits") }),
            Speak("You have about twelve dollars and thirty four cents left."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "how are we doing on credits", CancellationToken.None);

        Assert.Equal(1, fleet.CreditsCalls);
        Assert.Contains("twelve dollars", result.Spoken);
        // The tool result the model saw carries real dollars, converted from micro-dollars.
        Assert.Contains("12.34", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_GetCredits_SignedOut_TellsTheModelThereIsNoBalance()
    {
        var fleet = new FakeFleet { CreditsResult = new CarModeCredits(false, null, null) };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_credits") }),
            Speak("The Gateway is not signed in, so there is no balance to read."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "credits", CancellationToken.None);

        Assert.Contains("not signed in", chat.SeenMessages[1]);
        Assert.Contains("no balance", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_ListMachines_ReturnsMachineFactsToTheModel()
    {
        var fleet = new FakeFleet
        {
            Machines = new[]
            {
                new CarModeMachineInfo { MachineName = "SOREN_NORTH", Version = "1.2.3", LastSeenMinutesAgo = 0, SessionCount = 9 },
                new CarModeMachineInfo { MachineName = "dev-machine-1", Version = "1.2.3", LastSeenMinutesAgo = 4, SessionCount = 2 },
            },
        };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_machines") }),
            Speak("Two machines are online."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "which machines are online", CancellationToken.None);

        Assert.Contains("SOREN_NORTH", chat.SeenMessages[1]);
        Assert.Contains("dev-machine-1", chat.SeenMessages[1]);
        Assert.Contains("Two machines", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_ListSchedules_ReturnsScheduleFactsToTheModel()
    {
        var fleet = new FakeFleet
        {
            Schedules = new[]
            {
                new CarModeScheduleInfo
                {
                    Name = "Nightly hygiene", Enabled = true, Schedule = "0 3 * * *", Machine = "SOREN_NORTH",
                    ActionSummary = "run \"/repo-hygiene\" in devthrottle", LastStatus = "succeeded",
                },
            },
        };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_schedules") }),
            Speak("One schedule: Nightly hygiene at three in the morning."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        var result = await brain.RunTurnAsync(TenantId.Local, "device-a", "what runs automatically", CancellationToken.None);

        Assert.Contains("Nightly hygiene", chat.SeenMessages[1]);
        Assert.Contains("0 3 * * *", chat.SeenMessages[1]);
        Assert.Contains("Nightly hygiene", result.Spoken);
    }

    [Fact]
    public async Task RunTurn_GetSpend_DefaultsToSevenDays_AndConvertsDollars()
    {
        var fleet = new FakeFleet { SpendResult = new CarModeSpendSummary(2_500_000, 41, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow) };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_spend") }),
            Speak("About two and a half dollars this week."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "what have we spent", CancellationToken.None);

        Assert.Equal(new[] { 7 }, fleet.SpendDays);
        Assert.Contains("2.5", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_GetSpend_InvalidDays_IsAToolError_NotASilentSevenDayAnswer()
    {
        // Codex review finding 6: an explicitly supplied but invalid window must come back to the model
        // as a tool error it can correct - never silently become the seven-day default, which would
        // answer a question the owner did not ask.
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_spend", "{\"days\":\"three weeks\"}") }),
            Speak("How many days did you mean?"));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "spend for three weeks", CancellationToken.None);

        Assert.Empty(fleet.SpendDays);                     // the fleet was never asked
        Assert.Contains("days must be a whole number", chat.SeenMessages[1]);
    }

    [Fact]
    public async Task RunTurn_GetCredits_SignedInWithNoBalance_FailsLoud_NeverZeroDollars()
    {
        // Codex review finding 6: a signed-in credits read with no balance is a malformed payload. The
        // fleet contract is to throw before the brain ever sees it; if a (fake or future) fleet hands the
        // brain that shape anyway, the brain itself must fail loud - zero dollars is the classic
        // plausible fabricated number and must be impossible to produce.
        var fleet = new FakeFleet { CreditsResult = new CarModeCredits(true, null, null) };
        var chat = new ScriptedChat(new CarModeAssistantTurn(null, new[] { Call("get_credits") }));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.RunTurnAsync(TenantId.Local, "device-a", "credits", CancellationToken.None));
    }

    [Fact]
    public async Task RunTurn_GetSpend_HonorsTheDaysArgument()
    {
        var fleet = new FakeFleet { SpendResult = new CarModeSpendSummary(0, 0, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow) };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("get_spend", "{\"days\":\"30\"}") }),
            Speak("Nothing in the last thirty days."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "spend for the month", CancellationToken.None);

        Assert.Equal(new[] { 30 }, fleet.SpendDays);
    }

    // ---- The desk surface (the cockpit Assistant screen) ----

    [Fact]
    public async Task DeskSurface_AppendsTheDeskOverridesToTheSystemPrompt()
    {
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(Speak("Hello."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { }, CarModeSurface.Desk);

        await brain.RunTurnAsync(TenantId.Local, "device-a", "hello", CancellationToken.None);

        Assert.Contains("DESK SURFACE OVERRIDES", chat.SeenMessages[0]);
    }

    [Fact]
    public async Task CarSurface_DoesNotCarryTheDeskOverrides()
    {
        var fleet = new FakeFleet();
        var chat = new ScriptedChat(Speak("Hello."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "hello", CancellationToken.None);

        Assert.DoesNotContain("DESK SURFACE OVERRIDES", chat.SeenMessages[0]);
    }

    [Fact]
    public async Task ListSessions_CarriesAgeAndIdleFacts()
    {
        var session = Session("Old Timer", false) with { AgeHours = 31, IdleMinutes = 95 };
        var fleet = new FakeFleet { Sessions = new[] { session } };
        var chat = new ScriptedChat(
            new CarModeAssistantTurn(null, new[] { Call("list_sessions") }),
            Speak("Old Timer has been open thirty one hours."));
        var brain = new CarModeBrain(chat, fleet, new CarModeConversationStore(), new CarModePendingStore(_ => { }), new CarModeSubjectStore(_ => { }), _ => { });

        await brain.RunTurnAsync(TenantId.Local, "device-a", "anything open too long", CancellationToken.None);

        // The tool result rides inside the messages list as an escaped JSON string, and System.Text.Json's
        // default encoder writes an embedded double-quote as the u0022 escape - match that exact form.
        Assert.Contains("\\u0022AgeHours\\u0022:31", chat.SeenMessages[1]);
        Assert.Contains("\\u0022IdleMinutes\\u0022:95", chat.SeenMessages[1]);
    }
}
