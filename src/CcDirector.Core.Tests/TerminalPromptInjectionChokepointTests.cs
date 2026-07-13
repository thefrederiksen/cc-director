using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

public sealed class TerminalPromptInjectionChokepointTests
{
    [Fact]
    public void Desktop_prompt_and_dictation_send_through_session_send_text_only()
    {
        var root = RepoRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Avalonia", "MainWindow.axaml.cs"));
        var fifo = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Avalonia", "FifoWindow.axaml.cs"));

        // DevThrottle Stats threaded an InputOrigin through the desktop choke-point calls: the composer is
        // typed-desktop, the background dictation submit and the FIFO injections are voice-desktop.
        // The chokepoint (SendTextAsync only, no Enter-retry, no raw SendInput) is unchanged - the calls still
        // funnel here; they now also carry the honest origin tag. (The VoiceModeController this audit used
        // to read was deleted: it was orphaned code no shipping app instantiated, and it pushed the
        // transcript through a language-model summarize step the product removed everywhere else.)
        Assert.Contains("await _activeSession.Session.SendTextAsync(text, origin: InputOrigin.DesktopTyped);", main);
        Assert.Contains("await target.SendTextAsync(text, origin: InputOrigin.DesktopVoice);", main);
        Assert.Contains("await _activeSession.Session.SendTextAsync(\"/handover\", SendSource.Internal);", main);
        Assert.Contains("await _current.SendTextAsync(transcript, SendSource.Internal, InputOrigin.DesktopVoice);", fifo);

        Assert.DoesNotContain("ScheduleEnterRetry", main);
        Assert.DoesNotContain("RetryEnterAfterDelay", main);
        Assert.DoesNotContain("Enter retry", main);
        Assert.DoesNotContain(".SendEnterAsync(", main);
        Assert.DoesNotContain("SendText(\"/handover", main);
        Assert.DoesNotContain("SendTextAsync(transcript +", fifo);
    }

    [Fact]
    public void Control_api_prompt_routes_send_submitted_text_through_session_send_text()
    {
        var root = RepoRoot();
        var control = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "ControlEndpoints.cs"));
        var executor = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "SessionCommandExecutor.cs"));
        var voice = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "VoiceTurnEndpoint.cs"));
        var chat = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "Chat", "ChatService.cs"));
        // Gateway Cleanup mission, Phase 0: the queue-send path's chokepoint call moved with the verb, from
        // ControlEndpoints.cs into the QueueGitExecutor.cs core (the REST route and the tunnel verb now share
        // that one core). The audit stays STRICT - it pins the call to its new known home, not "any file".
        var queueGit = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "QueueGitExecutor.cs"));

        Assert.Contains("Verb = \"prompt\"", control);
        Assert.Contains("SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, source: source);", control);
        Assert.Contains("await session.SendTextAsync(request.Text, source, origin);", executor);
        Assert.Contains("await local.SendTextAsync(framed, SendSource.Internal);", control);
        Assert.Contains("await s.SendTextAsync(framed, SendSource.Internal);", control);
        Assert.Contains("await session.SendTextAsync(text, SendSource.Internal);", queueGit);
        Assert.Contains("await session.SendTextAsync(inputText, SendSource.Internal);", voice);
        Assert.Contains("await session.SendTextAsync(req.Text, SendSource.Internal);", chat);

        // Raw SendInput is still allowed when the caller explicitly asked not to append Enter; that is
        // terminal typing, not prompt submission.
        Assert.Contains("if (request.AppendEnter)", executor);
        Assert.Contains("session.SendInput(Encoding.UTF8.GetBytes(request.Text), origin);", executor);
    }

    [Fact]
    public void Web_and_gateway_prompt_routes_keep_submit_separate_from_raw_terminal_input()
    {
        var root = RepoRoot();
        var client = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "api", "client.ts"));
        var cockpit = File.ReadAllText(Path.Combine(root, "apps", "cockpit", "src", "sessions", "SessionComposer.tsx"));
        var mobileControls = File.ReadAllText(Path.Combine(root, "apps", "mobile", "src", "components", "SessionControls.tsx"));
        // Mobile Voice mode's submit was hoisted into the shared client-core hook (issue #1213), so the
        // chokepoint assertion follows it there; it still funnels through sendPrompt, never raw terminal input.
        var mobileVoice = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "voice", "useVoiceMode.ts"));
        var interactive = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "terminal", "interactive.ts"));
        var gateway = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Gateway", "Api", "GatewayEndpoints.cs"));
        var directorClient = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Gateway", "Discovery", "DirectorEndpointClient.cs"));
        var stream = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "TerminalStreamEndpoint.cs"));

        Assert.Contains("gatewayFetch(`/sessions/${sid}/prompt`", client);
        Assert.Contains("await sendPrompt(sessionId, text, true);", cockpit);
        Assert.Contains("await sendPrompt(sessionId, text, true);", mobileControls);
        Assert.Contains("await sendPrompt(sessionId, combined, true);", mobileControls);
        Assert.Contains("await sendPrompt(sid, trimmed, true);", mobileVoice);

        Assert.Contains("await sendPrompt(this.sessionId, chunk, false);", interactive);
        Assert.Contains("sessionManager.GetSession(guid)?.SendInput(bytes, InputOrigin.Typed(InputSurface.Unknown));", stream);

        Assert.Contains("await client.PostPromptAsync(director.ControlEndpoint, sid, req);", gateway);
        Assert.Contains("new HttpRequestMessage(HttpMethod.Post, $\"{endpoint}/sessions/{sessionId}/prompt\")", directorClient);
    }

    [Fact]
    public void Conpty_and_builtin_driver_submit_paths_funnel_through_terminal_submit()
    {
        var root = RepoRoot();
        var conpty = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Core", "Backends", "ConPtyBackend.cs"));
        Assert.Contains("TerminalSubmit.SharedSubmitAsync(this, text, \"ConPtyBackend\")", conpty);
        Assert.DoesNotContain("Task.Delay(50)", conpty);

        var unixPty = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Core", "Backends", "UnixPtyBackend.cs"));
        Assert.Contains("TerminalSubmit.SharedSubmitAsync(this, text, \"UnixPtyBackend\")", unixPty);
        Assert.DoesNotContain("Task.Delay(50)", unixPty);

        var driversDir = Path.Combine(root, "src", "CcDirector.Core", "Drivers");
        foreach (var file in Directory.GetFiles(driversDir, "*Driver.cs")
                     .Where(f => !Path.GetFileName(f).StartsWith("I", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            var submitMatch = Regex.Match(
                text,
                @"public\s+Task\s+SubmitAsync\s*\([^)]*\)\s*=>\s*(?<body>[^;]+);",
                RegexOptions.Singleline);

            Assert.True(submitMatch.Success, $"{name} should expose an expression-bodied SubmitAsync so this chokepoint audit can read it.");
            var body = submitMatch.Groups["body"].Value;
            Assert.Contains("TerminalSubmit.", body);
            Assert.DoesNotContain("backend.SendTextAsync", body);
            Assert.DoesNotContain("backend.Write", body);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
