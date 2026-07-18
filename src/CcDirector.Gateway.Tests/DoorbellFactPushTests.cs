using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Event-to-stream fact propagation: when a fact the Gateway folds or a client reads changes on a Session,
/// the Director must PUSH it up the stream on that change - not leave it to sit until the periodic full
/// re-push. The rule the whole suite proves: if the fold or a client READS it, it pushes.
///
/// This began as "defect 14", three COLOUR inputs invisible to the Gateway until something else happened.
/// The Gateway folds orange from <c>IsTranscribing</c>, orange from <c>IsAutoExplaining</c> and purple from
/// <c>IsBackgroundRunning</c>, reading them off the pushed <c>SessionDto</c>, but nothing pushed when they
/// CHANGED - each raised a Director event into an empty room (verified by looking for subscribers, not call
/// sites). A fourth test covers the GATE on two of those (<c>WingmanEnabled</c>). The suite now also covers a
/// fact that is NOT a colour - the mobile <c>VoiceMode</c>/<c>ViewMode</c>, which the phone reads for a row's
/// link target and voice affordance.
///
/// THIS IS THE REAL WIRE. A real <see cref="GatewayHost"/>, and a real <see cref="ControlApiHost"/> whose
/// OWN config points at it, so the Director builds its real <c>GatewayStreamClient</c> and runs the real
/// <c>WireDoorbellPush</c>. Nothing is hand-pushed: the test flips the fact on the Session exactly as
/// production does and waits for it to appear in the Gateway's pushed store.
///
/// THE PUSH IS THE ASSERTION, ISOLATED. The host is built with the periodic re-push pushed far beyond the
/// run (<see cref="RePushBeyondTheRun"/>), so no periodic full snapshot fires during a test, and the value
/// tests observe an explicit <c>false</c> baseline then both transitions, so a constant stamp cannot satisfy
/// them. Remove a subscription and the matching test fails by timing out, which is the defect exactly.
///
/// One residual, stated honestly: <c>GatewayStreamClient</c> also sends a full snapshot on RECONNECT. This
/// fixture holds a single stable loopback connection and never induces a disconnect, so no reseed occurs -
/// but that is a property of the harness, not a hard assertion. Proving delta-vs-snapshot at the received
/// message is left to the doorbell-lifetime follow-up, which adds that seam.
///
/// Design: docs/new_architecture/session-state.html, defect 14.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DoorbellFactPushTests : IAsyncLifetime
{
    private const string Token = "test-token-doorbell";
    private const string DirectorId = "dir-doorbell";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _gatewayInstances = Path.Combine(Path.GetTempPath(), "cc-doorbell-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorInstances = Path.Combine(Path.GetTempPath(), "cc-doorbell-dir-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private SessionManager _sm = null!;
    private ControlApiHost _host = null!;

    /// <summary>How long we allow an event-driven delta to land. Isolation does NOT come from this being
    /// under the re-push (see <see cref="RePushBeyondTheRun"/>); it comes from the re-push being disabled for
    /// the run, so anything that lands inside this window can only be the per-fact push.</summary>
    private static readonly TimeSpan PushWindow = TimeSpan.FromSeconds(4);

    /// <summary>The full re-push interval, set far beyond the whole test so NO snapshot fires during it. This
    /// is what makes a pass provably a push rather than a coincidental re-push carrying the changed value.</summary>
    private static readonly TimeSpan RePushBeyondTheRun = TimeSpan.FromMinutes(5);

    public DoorbellFactPushTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-doorbell-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _gatewayInstances,
            workListsPath: Path.Combine(_gatewayInstances, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        // Point THIS Director's own config at the real Gateway, so ControlApiHost.StartAsync builds its real
        // stream client and wires the real doorbell. That is the whole point: the push under test is the
        // host's own, not one the test constructs.
        // CcStorage.ConfigJson() is <root>/config/config.json - NOT <root>/config.json.
        var configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "config.json"), $$"""
        {
          "gateway": {
            "url": "http://127.0.0.1:{{_gateway.Port}}",
            "token": "{{Token}}",
            "streamMode": true
          }
        }
        """);

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: false, directorId: DirectorId, instancesDirectory: _directorInstances,
            // Disable the full re-push for the run so a fact that appears in the pushed store can only have got
            // there by its own event-driven delta - the whole point of these tests.
            rePushInterval: RePushBeyondTheRun);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        TryDelete(_gatewayInstances);
        TryDelete(_directorInstances);
        TryDelete(_root);
    }

    private static int AllocateFreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>Wait for a pushed session to satisfy a predicate, or return false at the deadline.</summary>
    private async Task<bool> WaitForPushed(string sessionId, Func<Contracts.SessionDto, bool> predicate, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            var pushed = _gateway.PushedSessions.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30));
            var mine = pushed.FirstOrDefault(t => t.Session.SessionId == sessionId).Session;
            if (mine is not null && predicate(mine)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task<Session> ASessionTheGatewayCanSee()
    {
        var s = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        Assert.True(await WaitForPushed(s.Id.ToString(), _ => true, TimeSpan.FromSeconds(15)),
            "the Director's stream never delivered the session - the harness is broken, not the fix");
        return s;
    }

    [Fact]
    public async Task Transcribing_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.IsTranscribing = true; // exactly what the desktop's background dictation send does

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsTranscribing, PushWindow),
            "IsTranscribing never reached the Gateway inside the push window - the Gateway folds the " +
            "dictation orange from this fact, so it would lag by up to one ten-second re-push (defect 14)");
    }

    [Fact]
    public async Task BackgroundRunning_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.SetBackgroundRunning(true, "running in background"); // what ProactiveExplainService does

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsBackgroundRunning, PushWindow),
            "IsBackgroundRunning never reached the Gateway inside the push window - the Gateway folds the " +
            "purple from this fact. Before defect 14 this event had ZERO subscribers anywhere.");
    }

    /// <summary>
    /// THE FOURTH INPUT DEFECT 14 DID NOT COUNT: the GATE on the other three.
    ///
    /// This class says "three colour inputs" and the fold reads four - yellow needs WingmanEnabled AND
    /// IsAutoExplaining, purple needs WingmanEnabled AND IsBackgroundRunning. So turning the Wingman OFF on
    /// a session parked on its background task changes the right answer from purple "Background" to red
    /// "Needs you" while none of the three flags move, nothing pushes, and the phone keeps the stale fold
    /// until the ten-second re-push.
    ///
    /// It survived defect 14 and three later passes hunting exactly this, because a gate is not the thing
    /// being rendered - it does not look like a colour input. Which is why the rule cannot be a judgement
    /// call: if the fold READS it, it pushes.
    /// </summary>
    [Fact]
    public async Task TurningTheWingmanOff_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        // The state the gate actually gates: parked on its own background task, Wingman on -> purple.
        s.WingmanEnabled = true;
        s.SetBackgroundRunning(true, "running in background");
        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.WingmanEnabled && d.IsBackgroundRunning, PushWindow),
            "the purple setup never reached the Gateway - the real assertion below cannot mean anything yet");

        // Exactly what the wingman-enabled=false command does (SessionWriteExecutor).
        s.WingmanEnabled = false;

        Assert.True(await WaitForPushed(s.Id.ToString(), d => !d.WingmanEnabled, PushWindow),
            "WingmanEnabled=false never reached the Gateway inside the push window - it GATES the purple " +
            "and yellow the fold reads, so the phone would keep folding purple 'Background' for a session " +
            "the desktop already calls red 'Needs you', until one ten-second re-push");
    }

    [Fact]
    public async Task AutoExplaining_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.IsExplaining = true; // what ProactiveExplainService sets around a briefing

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsAutoExplaining, PushWindow),
            "IsAutoExplaining never reached the Gateway inside the push window - the Gateway folds the " +
            "auto-explain orange from this fact. Before defect 14 this event had ZERO subscribers anywhere.");
    }

    /// <summary>
    /// THE SAME WIRE FOR A FACT THAT IS NOT A COLOUR (Dumb Clients mission, slice A).
    ///
    /// The map stamps <c>VoiceMode</c> (derived from <c>ViewMode</c>) onto the pushed <c>SessionDto</c>, and
    /// the mobile roster READS it: a voice-mode session's row links straight to its Voice tab and shows a
    /// voice affordance. But <c>WireDoorbellPush</c> subscribed to activity, hold, the three colour inputs and
    /// the wingman gate - and not to <c>OnViewModeChanged</c>. So switching a session to voice mode told the
    /// Gateway nothing until the re-push. Same wire, same rule: if a client reads it, it pushes.
    ///
    /// Proven both ways round an observed <c>false</c> baseline, so a mapper that stamped a CONSTANT
    /// <c>VoiceMode</c> could not satisfy it: the false-&gt;true assertion would fail a constant false, the
    /// true-&gt;false assertion would fail a constant true. With the re-push disabled (see the fixture), each
    /// transition inside the window can only be its own <c>OnViewModeChanged</c> delta - remove the
    /// subscription and this fails by timing out.
    /// </summary>
    [Fact]
    public async Task VoiceMode_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();
        var id = s.Id.ToString();

        // Baseline: the create-time push stamped VoiceMode=false. Observe it, so "became true" below cannot
        // be satisfied by a row that was already true.
        Assert.True(await WaitForPushed(id, d => !d.VoiceMode, TimeSpan.FromSeconds(5)),
            "the baseline VoiceMode=false was never observed - the transitions below could not mean a change");

        s.ViewMode = MobileViewMode.Voice; // exactly what entering voice mode does (SessionWriteExecutor)
        Assert.True(await WaitForPushed(id, d => d.VoiceMode, PushWindow),
            "VoiceMode false->true never reached the Gateway on the OnViewModeChanged push - the mobile roster " +
            "reads it for the row's link target and voice affordance, so it would lag by up to one re-push.");

        s.ViewMode = MobileViewMode.Text; // leaving voice mode
        Assert.True(await WaitForPushed(id, d => !d.VoiceMode, PushWindow),
            "VoiceMode true->false never reached the Gateway on the OnViewModeChanged push.");
    }
}
