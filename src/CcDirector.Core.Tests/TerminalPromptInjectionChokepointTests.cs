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
        var voiceMode = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Core", "Voice", "Controllers", "VoiceModeController.cs"));

        Assert.Contains("await _activeSession.Session.SendTextAsync(text);", main);
        Assert.Contains("await target.SendTextAsync(text);", main);
        Assert.Contains("await _activeSession.Session.SendTextAsync(\"/handover\", SendSource.Internal);", main);
        Assert.Contains("await _current.SendTextAsync(transcript, SendSource.Internal);", fifo);
        Assert.Contains("await _activeSession.SendTextAsync(transcription, SendSource.Internal);", voiceMode);

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

        Assert.Contains("Verb = \"prompt\"", control);
        Assert.Contains("SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, source: source);", control);
        Assert.Contains("await session.SendTextAsync(request.Text, source);", executor);
        Assert.Contains("await local.SendTextAsync(framed, SendSource.Internal);", control);
        Assert.Contains("await s.SendTextAsync(framed, SendSource.Internal);", control);
        Assert.Contains("await session.SendTextAsync(text, SendSource.Internal);", control);
        Assert.Contains("await session.SendTextAsync(inputText, SendSource.Internal);", voice);
        Assert.Contains("await session.SendTextAsync(req.Text, SendSource.Internal);", chat);

        // Raw SendInput is still allowed when the caller explicitly asked not to append Enter; that is
        // terminal typing, not prompt submission.
        Assert.Contains("if (request.AppendEnter)", executor);
        Assert.Contains("session.SendInput(Encoding.UTF8.GetBytes(request.Text));", executor);
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

        Assert.Contains("fetch(`/sessions/${sid}/prompt`", client);
        Assert.Contains("await sendPrompt(sessionId, text, true);", cockpit);
        Assert.Contains("await sendPrompt(sessionId, text, true);", mobileControls);
        Assert.Contains("await sendPrompt(sessionId, combined, true);", mobileControls);
        Assert.Contains("await sendPrompt(sid, trimmed, true);", mobileVoice);

        Assert.Contains("await sendPrompt(this.sessionId, chunk, false);", interactive);
        Assert.Contains("sessionManager.GetSession(guid)?.SendInput(bytes);", stream);

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
