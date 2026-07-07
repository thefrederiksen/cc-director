using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.ControlApi;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

var exitCode = await TerminalInjectHarnessApp.RunAsync(args);
return exitCode;

internal static class TerminalInjectHarnessApp
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var options = HarnessOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        var report = new HarnessReport
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            RepositoryRoot = FindRepositoryRoot(),
            OutputDirectory = Path.GetFullPath(options.OutputDirectory),
            RunsRequested = options.Runs,
        };

        await using var context = await HarnessContext.StartAsync(options, report);

        var matrix = MatrixBuilder.Build(options);
        foreach (var agentKind in matrix.Agents)
        {
            var probe = AgentProbe.Probe(agentKind, context.AgentOptions);
            report.AgentProbes.Add(probe);
            if (!probe.Installed)
            {
                foreach (var strategy in matrix.Strategies)
                foreach (var route in matrix.Routes)
                foreach (var testCase in matrix.Cases)
                {
                    var runs = RunPolicy.GetRunCount(options, agentKind, testCase, route, strategy);
                    for (var run = 1; run <= runs; run++)
                    {
                        var skipped = HarnessResult.Skipped(
                            agentKind, testCase.Id, route, strategy, "tool_missing", probe.SkipReason ?? "Tool is not installed.");
                        skipped.Run = run;
                        report.Results.Add(skipped);
                    }
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(probe.ResolvedPath))
                SetConfiguredPath(context.AgentOptions, agentKind, probe.ResolvedPath);

            foreach (var strategy in matrix.Strategies)
            foreach (var route in matrix.Routes)
            foreach (var testCase in matrix.Cases)
            {
                var runs = RunPolicy.GetRunCount(options, probe.AgentKind, testCase, route, strategy);
                for (var run = 1; run <= runs; run++)
                {
                    var result = await RunOneAsync(context, options, probe, testCase, route, strategy, run);
                    report.Results.Add(result);
                }
            }
        }

        if (options.IncludeForcedParkedCase)
            report.Results.Add(await RunForcedParkedComposerAsync(context, options, report.AgentProbes));

        report.FinishedAtUtc = DateTimeOffset.UtcNow;
        ReportSummarizer.Populate(report);
        ResultWriter.Write(report, options.OutputDirectory);

        Console.WriteLine($"summary_json={Path.Combine(options.OutputDirectory, "summary.json")}");
        Console.WriteLine($"summary_html={Path.Combine(options.OutputDirectory, "summary.html")}");
        Console.WriteLine($"passed={report.Results.Count(r => r.Status == HarnessStatus.Pass)} failed={report.Results.Count(r => r.Status == HarnessStatus.Fail)} skipped={report.Results.Count(r => r.Status == HarnessStatus.Skip)}");

        return !options.AllowFailures && report.Results.Any(r => r.Status == HarnessStatus.Fail) ? 1 : 0;
    }

    private static async Task<HarnessResult> RunOneAsync(
        HarnessContext context,
        HarnessOptions options,
        AgentProbeResult probe,
        HarnessCase testCase,
        HarnessRoute route,
        SubmitStrategy strategy,
        int run)
    {
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{probe.AgentKind}-{testCase.Id}-{route}-{FormatStrategy(strategy)}-run{run}";
        var runDirectory = Path.Combine(options.OutputDirectory, "runs", SanitizeFileName(runId));
        Directory.CreateDirectory(runDirectory);

        var token = BuildToken(probe.AgentKind, testCase.Id, route, run);
        var promptCase = testCase.BuildPrompt(token);
        var prompt = promptCase.Text;
        var repositoryPath = context.RepositoryFor(probe.AgentKind, runId);

        Session? session = null;
        long bufferCursor = 0;
        var sentAtUtc = DateTimeOffset.UtcNow;
        try
        {
            session = context.SessionManager.CreateSession(
                repositoryPath,
                AgentPluginRegistry.CreateAgentWithPathOverride(probe.AgentKind, context.AgentOptions, probe.ResolvedPath),
                ResolveLaunchArgs(probe.AgentKind, context.AgentOptions),
                SessionBackendType.ConPty,
                resumeSessionId: null,
                nameFactory: id => $"terminal inject {probe.AgentKind} {testCase.Id} {route} {id:N}"[..60]);

            await LiveSessionRunner.WaitForReadyAsync(session, options.StartupTimeout);

            var transcriptBefore = TranscriptProof.Snapshot(session);
            bufferCursor = session.Buffer?.TotalBytesWritten ?? 0;
            sentAtUtc = DateTimeOffset.UtcNow;
            var bracketedPasteMode = session.BracketedPasteEnabled;

            var strategyGate = await RouteDriver.SendAsync(context, session, route, strategy, prompt, options.TurnTimeout);
            if (!strategyGate.Applicable)
            {
                WriteRunArtifacts(session, runDirectory, prompt, SubmitProof.Skipped(strategyGate.Reason), bufferCursor);
                var skipped = HarnessResult.Skipped(
                    probe.AgentKind,
                    testCase.Id,
                    route,
                    strategy,
                    "not_applicable",
                    strategyGate.Reason);
                skipped.Run = run;
                skipped.SentText = prompt;
                skipped.ExpectedToken = token;
                skipped.ExpectedFragments = promptCase.ExpectedFragments;
                skipped.BracketedPasteModeObserved = bracketedPasteMode;
                skipped.SessionId = session.Id.ToString();
                skipped.RepositoryPath = repositoryPath;
                skipped.AgentExecutablePath = probe.ResolvedPath;
                skipped.AgentVersion = probe.Version;
                skipped.StartedAtUtc = sentAtUtc;
                skipped.FinishedAtUtc = DateTimeOffset.UtcNow;
                skipped.ArtifactDirectory = Path.GetFullPath(runDirectory);
                return skipped;
            }

            var proof = await SubmitVerifier.WaitForProofAsync(
                session,
                prompt,
                token,
                promptCase.ExpectedFragments,
                promptCase.MinimumTerminalFragmentCount,
                testCase.RequiresAssistantToken,
                transcriptBefore,
                bufferCursor,
                options.TurnTimeout);

            WriteRunArtifacts(session, runDirectory, prompt, proof, bufferCursor);

            return new HarnessResult
            {
                Agent = probe.AgentKind.ToString(),
                Case = testCase.Id,
                Route = route.ToString().ToLowerInvariant(),
                Strategy = FormatStrategy(strategy),
                Run = run,
                Status = proof.Passed ? HarnessStatus.Pass : HarnessStatus.Fail,
                FailureClass = proof.Passed ? null : proof.FailureClass,
                Message = proof.Message,
                SentText = prompt,
                ExpectedToken = token,
                ExpectedFragments = promptCase.ExpectedFragments,
                ContentFragmentsObserved = proof.ContentFragmentsObserved,
                TokenObserved = proof.TokenObserved,
                TranscriptUserMessageObserved = proof.TranscriptUserMessageObserved,
                TranscriptAssistantTokenObserved = proof.TranscriptAssistantTokenObserved,
                TurnStarted = proof.TurnStarted,
                ParkedComposer = proof.FailureClass == "parked_composer",
                BracketedPasteModeObserved = bracketedPasteMode,
                BufferCursor = bufferCursor,
                SessionId = session.Id.ToString(),
                RepositoryPath = repositoryPath,
                AgentExecutablePath = probe.ResolvedPath,
                AgentVersion = probe.Version,
                StartedAtUtc = sentAtUtc,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                ArtifactDirectory = Path.GetFullPath(runDirectory),
            };
        }
        catch (Exception ex)
        {
            var proof = session is null || bufferCursor <= 0
                ? SubmitVerifier.ClassifyHarnessException(ex)
                : SubmitVerifier.ClassifySendFailure(session, prompt, bufferCursor, ex);

            if (session is not null)
                WriteRunArtifacts(session, runDirectory, prompt, proof, bufferCursor);

            return new HarnessResult
            {
                Agent = probe.AgentKind.ToString(),
                Case = testCase.Id,
                Route = route.ToString().ToLowerInvariant(),
                Strategy = FormatStrategy(strategy),
                Run = run,
                Status = HarnessStatus.Fail,
                FailureClass = proof.FailureClass,
                Message = proof.Message,
                SentText = prompt,
                ExpectedToken = token,
                ExpectedFragments = promptCase.ExpectedFragments,
                TokenObserved = proof.TokenObserved,
                ContentFragmentsObserved = proof.ContentFragmentsObserved,
                TurnStarted = proof.TurnStarted,
                ParkedComposer = proof.FailureClass == "parked_composer",
                BracketedPasteModeObserved = session?.BracketedPasteEnabled,
                BufferCursor = bufferCursor,
                SessionId = session?.Id.ToString(),
                RepositoryPath = repositoryPath,
                AgentExecutablePath = probe.ResolvedPath,
                AgentVersion = probe.Version,
                StartedAtUtc = sentAtUtc,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                ArtifactDirectory = Path.GetFullPath(runDirectory),
            };
        }
        finally
        {
            if (session is not null)
                await context.RemoveSessionAsync(session);
        }
    }

    private static async Task<HarnessResult> RunForcedParkedComposerAsync(
        HarnessContext context,
        HarnessOptions options,
        IReadOnlyList<AgentProbeResult> probes)
    {
        var probe = probes.FirstOrDefault(p => p.Installed && p.AgentKind is AgentKind.ClaudeCode or AgentKind.Codex);
        if (probe is null)
            return HarnessResult.Skipped(AgentKind.ClaudeCode, "forced-parked", HarnessRoute.Direct, SubmitStrategy.Current, "tool_missing", "No Phase 2 agent is installed for forced parked-composer proof.");

        var runDirectory = Path.Combine(options.OutputDirectory, "runs", $"forced-parked-{probe.AgentKind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runDirectory);
        var prompt = $"FORCED_PARKED_{Guid.NewGuid():N}".ToUpperInvariant();
        var repositoryPath = context.RepositoryFor(probe.AgentKind, $"forced-parked-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Session? session = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(probe.ResolvedPath))
                SetConfiguredPath(context.AgentOptions, probe.AgentKind, probe.ResolvedPath);

            session = context.SessionManager.CreateSession(
                repositoryPath,
                AgentPluginRegistry.CreateAgentWithPathOverride(probe.AgentKind, context.AgentOptions, probe.ResolvedPath),
                ResolveLaunchArgs(probe.AgentKind, context.AgentOptions),
                SessionBackendType.ConPty,
                resumeSessionId: null,
                nameFactory: id => $"terminal inject forced parked {id:N}"[..60]);

            await LiveSessionRunner.WaitForReadyAsync(session, options.StartupTimeout);
            var cursor = session.Buffer?.TotalBytesWritten ?? 0;
            session.SendInput(Encoding.UTF8.GetBytes(prompt));
            await Task.Delay(TimeSpan.FromSeconds(2));

            var proof = SubmitVerifier.ClassifyTimeout(session, prompt, cursor);
            WriteRunArtifacts(session, runDirectory, prompt, proof, cursor);

            return new HarnessResult
            {
                Agent = probe.AgentKind.ToString(),
                Case = "forced-parked",
                Route = "direct-no-enter",
                Strategy = TerminalInjectHarnessApp.FormatStrategy(SubmitStrategy.Current),
                Run = 1,
                Status = proof.FailureClass == "parked_composer" ? HarnessStatus.Pass : HarnessStatus.Fail,
                FailureClass = proof.FailureClass,
                Message = proof.Message,
                SentText = prompt,
                ExpectedToken = prompt,
                TokenObserved = proof.TokenObserved,
                TurnStarted = proof.TurnStarted,
                ParkedComposer = proof.FailureClass == "parked_composer",
                BufferCursor = cursor,
                SessionId = session.Id.ToString(),
                RepositoryPath = repositoryPath,
                AgentExecutablePath = probe.ResolvedPath,
                AgentVersion = probe.Version,
                StartedAtUtc = DateTimeOffset.UtcNow,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                ArtifactDirectory = Path.GetFullPath(runDirectory),
            };
        }
        catch (Exception ex)
        {
            return new HarnessResult
            {
                Agent = probe.AgentKind.ToString(),
                Case = "forced-parked",
                Route = "direct-no-enter",
                Strategy = TerminalInjectHarnessApp.FormatStrategy(SubmitStrategy.Current),
                Run = 1,
                Status = HarnessStatus.Fail,
                FailureClass = "harness_error",
                Message = ex.Message,
                SentText = prompt,
                ExpectedToken = prompt,
                RepositoryPath = repositoryPath,
                AgentExecutablePath = probe.ResolvedPath,
                AgentVersion = probe.Version,
                StartedAtUtc = DateTimeOffset.UtcNow,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                ArtifactDirectory = Path.GetFullPath(runDirectory),
            };
        }
        finally
        {
            if (session is not null)
                await context.RemoveSessionAsync(session);
        }
    }

    private static void WriteRunArtifacts(Session session, string runDirectory, string prompt, SubmitProof proof, long cursor)
    {
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(Path.Combine(runDirectory, "sent-payload.txt"), prompt, Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "screen.txt"), string.Join(Environment.NewLine, session.SnapshotScreenRows()), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "proof.json"), JsonSerializer.Serialize(proof, JsonOptions.Indented), Encoding.UTF8);

        var bytes = DumpAllWithRetry(session.Buffer);
        File.WriteAllBytes(Path.Combine(runDirectory, "raw-terminal.bin"), bytes);
        if (session.Buffer is not null)
        {
            var (sinceBytes, _) = GetWrittenSinceWithRetry(session.Buffer, cursor);
            File.WriteAllBytes(Path.Combine(runDirectory, "raw-terminal-since-cursor.bin"), sinceBytes);
        }
    }

    private static byte[] DumpAllWithRetry(CcDirector.Core.Memory.CircularTerminalBuffer? buffer)
    {
        if (buffer is null)
            return [];

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var bytes = buffer.DumpAll();
            if (bytes.Length > 0 || buffer.TotalBytesWritten == 0)
                return bytes;
            Thread.Sleep(50);
        }

        return buffer.DumpAll();
    }

    private static (byte[] Bytes, long StartOffset) GetWrittenSinceWithRetry(
        CcDirector.Core.Memory.CircularTerminalBuffer buffer,
        long cursor)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = buffer.GetWrittenSince(cursor);
            if (result.Data.Length > 0 || buffer.TotalBytesWritten <= cursor)
                return result;
            Thread.Sleep(50);
        }

        return buffer.GetWrittenSince(cursor);
    }

    private static string? ResolveLaunchArgs(AgentKind agentKind, AgentOptions options)
    {
        var args = agentKind switch
        {
            AgentKind.ClaudeCode => AgentToolCatalog.ClaudeSkipPermissionsArg,
            AgentKind.Codex => AgentToolCatalog.CodexFullAccessArg,
            _ => AgentLaunchDefaults.ResolveDefaultArgs(agentKind, options),
        };
        return string.IsNullOrWhiteSpace(args) ? null : args;
    }

    private static string BuildToken(AgentKind agentKind, string testCase, HarnessRoute route, int run)
    {
        var agentPart = agentKind == AgentKind.ClaudeCode ? "CLAUDE" : agentKind.ToString().ToUpperInvariant();
        var casePart = testCase.Equals("sentence", StringComparison.OrdinalIgnoreCase) ? "SENT" : testCase.ToUpperInvariant();
        var routePart = route.ToString().ToUpperInvariant();
        return $"OK{agentPart}{casePart}{routePart}{run}{Guid.NewGuid():N}"[..32].ToUpperInvariant();
    }

    internal static string FormatStrategy(SubmitStrategy strategy) =>
        strategy == SubmitStrategy.BracketedPaste ? "bracketed-paste" : "current";

    private static void SetConfiguredPath(AgentOptions options, AgentKind agentKind, string path)
    {
        var plugin = AgentPluginRegistry.Get(agentKind);
        plugin.Settings.SetConfiguredPath(options, path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.ToString();
    }
}

internal sealed class HarnessContext : IAsyncDisposable
{
    private readonly ControlApiHost _host;
    private readonly List<Guid> _createdSessions = new();

    private HarnessContext(HarnessOptions options, SessionManager sessionManager, ControlApiHost host, HttpClient httpClient, AgentOptions agentOptions, string repositoryRoot)
    {
        Options = options;
        SessionManager = sessionManager;
        _host = host;
        HttpClient = httpClient;
        AgentOptions = agentOptions;
        RepositoryRoot = repositoryRoot;
    }

    internal HarnessOptions Options { get; }
    internal SessionManager SessionManager { get; }
    internal HttpClient HttpClient { get; }
    internal AgentOptions AgentOptions { get; }
    internal string RepositoryRoot { get; }
    internal string ApiBaseUrl => HttpClient.BaseAddress?.ToString().TrimEnd('/') ?? "";

    internal static async Task<HarnessContext> StartAsync(HarnessOptions options, HarnessReport report)
    {
        var agentOptions = new AgentOptions();
        var sessionManager = new SessionManager(agentOptions, Console.WriteLine)
        {
            CleanExitReapDelayMs = 600_000
        };
        var instancesDirectory = Path.Combine(options.OutputDirectory, "instances");
        Directory.CreateDirectory(instancesDirectory);

        var host = new ControlApiHost(
            sessionManager,
            version: "terminal-inject-harness",
            requestShutdownAsync: () => Task.CompletedTask,
            useEphemeralPort: true,
            authEnabled: false,
            instancesDirectory: instancesDirectory);

        var port = await host.StartAsync();
        report.ControlApiBaseUrl = $"http://127.0.0.1:{port}";

        var http = new HttpClient
        {
            BaseAddress = new Uri(report.ControlApiBaseUrl),
            Timeout = options.TurnTimeout + TimeSpan.FromSeconds(45),
        };
        return new HarnessContext(options, sessionManager, host, http, agentOptions, report.RepositoryRoot);
    }

    internal string RepositoryFor(AgentKind agentKind, string runId)
    {
        if (!string.IsNullOrWhiteSpace(Options.RepositoryOverride))
            return Path.GetFullPath(Options.RepositoryOverride);

        var path = Path.Combine(
            Options.OutputDirectory,
            "repositories",
            agentKind.ToString().ToLowerInvariant(),
            SanitizePathSegment(runId));
        Directory.CreateDirectory(path);
        var readme = Path.Combine(path, "README.md");
        if (!File.Exists(readme))
            File.WriteAllText(readme, "# Terminal inject disposable repository" + Environment.NewLine, Encoding.UTF8);

        var gitDirectory = Path.Combine(path, ".git");
        if (!Directory.Exists(gitDirectory))
            GitInit(path);

        return Path.GetFullPath(path);
    }

    internal async Task RemoveSessionAsync(Session session)
    {
        if (!_createdSessions.Contains(session.Id))
            _createdSessions.Add(session.Id);

        await session.KillAsync(AgentOptions.GracefulShutdownTimeoutSeconds * 1000);
        SessionManager.RemoveSession(session.Id);
        _createdSessions.Remove(session.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await SessionManager.KillAllSessionsAsync();
        foreach (var id in _createdSessions.ToArray())
            SessionManager.RemoveSession(id);
        HttpClient.Dispose();
        await _host.StopAsync();
        SessionManager.Dispose();
    }

    private static void GitInit(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.WithArgument("init"));
        process?.WaitForExit(10_000);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.ToString();
    }
}

internal static class RouteDriver
{
    private static readonly byte[] BracketedPasteStart = Encoding.UTF8.GetBytes("\x1b[200~");
    private static readonly byte[] BracketedPasteEnd = Encoding.UTF8.GetBytes("\x1b[201~");
    private static readonly byte[] EnterByte = [0x0D];

    internal static async Task<StrategyGate> SendAsync(
        HarnessContext context,
        Session session,
        HarnessRoute route,
        SubmitStrategy strategy,
        string prompt,
        TimeSpan timeout)
    {
        if (strategy == SubmitStrategy.BracketedPaste)
            return await SendBracketedPasteAsync(session, route, prompt);

        await SendCurrentAsync(context, session, route, prompt, timeout);
        return StrategyGate.Applied;
    }

    private static async Task SendCurrentAsync(HarnessContext context, Session session, HarnessRoute route, string prompt, TimeSpan timeout)
    {
        switch (route)
        {
            case HarnessRoute.Direct:
                await session.SendTextAsync(prompt);
                break;
            case HarnessRoute.Rest:
                var promptResponse = await context.HttpClient.PostAsJsonAsync(
                    $"/sessions/{session.Id}/prompt",
                    new PromptRequest { Text = prompt, AppendEnter = true });
                await EnsureSuccessAsync(promptResponse);
                break;
            case HarnessRoute.Fleet:
                var fleetResponse = await context.HttpClient.PostAsJsonAsync(
                    "/fleet/send",
                    new FleetSendRequest { ToSessionId = session.Id.ToString(), Text = prompt });
                await EnsureSuccessAsync(fleetResponse);
                break;
            case HarnessRoute.Voice:
                using (var cts = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(30)))
                {
                    var voiceResponse = await context.HttpClient.PostAsJsonAsync(
                        $"/sessions/{session.Id}/voice-turn",
                        new { Text = prompt },
                        cts.Token);
                    await EnsureSuccessAsync(voiceResponse);
                    var body = await voiceResponse.Content.ReadAsStringAsync(cts.Token);
                    if (body.Contains("\"stage\":\"error\"", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("send_failed:", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("timeout waiting", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Voice-turn route reported an error: " + body);
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown route.");
        }
    }

    private static async Task<StrategyGate> SendBracketedPasteAsync(Session session, HarnessRoute route, string prompt)
    {
        if (route != HarnessRoute.Direct)
            return StrategyGate.NotApplicable("bracketed-paste strategy requires raw PTY input; this public route has no strategy parameter.");

        if (!session.BracketedPasteEnabled)
            return StrategyGate.NotApplicable("bracketed paste mode ?2004 was not observed for this session.");

        session.SendInput(BracketedPasteStart);
        session.SendInput(Encoding.UTF8.GetBytes(prompt));
        session.SendInput(BracketedPasteEnd);
        await Task.Delay(TimeSpan.FromMilliseconds(80));
        session.SendInput(EnterByte);
        return StrategyGate.Applied;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}");
    }
}

internal sealed record StrategyGate(bool Applicable, string Reason)
{
    internal static StrategyGate Applied { get; } = new(true, "");
    internal static StrategyGate NotApplicable(string reason) => new(false, reason);
}

internal static class LiveSessionRunner
{
    internal static async Task WaitForReadyAsync(Session session, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        long lastBytes = -1;
        var stableSince = DateTimeOffset.UtcNow;
        var acceptedCodexTrustPrompt = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
                throw new InvalidOperationException($"Session exited before ready: status={session.Status}, exitCode={session.ExitCode}");

            var bytes = session.Buffer?.TotalBytesWritten ?? 0;
            if (!acceptedCodexTrustPrompt && session.AgentKind == AgentKind.Codex && IsCodexTrustPrompt(session))
            {
                session.SendInput([0x0D]);
                acceptedCodexTrustPrompt = true;
                lastBytes = bytes;
                stableSince = DateTimeOffset.UtcNow;
                await Task.Delay(750);
                continue;
            }

            if (bytes != lastBytes)
            {
                lastBytes = bytes;
                stableSince = DateTimeOffset.UtcNow;
            }

            var stableFor = DateTimeOffset.UtcNow - stableSince;
            if (bytes > 500 && stableFor >= TimeSpan.FromSeconds(1))
                return;

            await Task.Delay(250);
        }

        throw new TimeoutException($"Session did not become ready within {timeout.TotalSeconds:F0} seconds.");
    }

    private static bool IsCodexTrustPrompt(Session session)
    {
        var text = TerminalSubmit.NormalizeForEcho(string.Join("\n", session.SnapshotScreenRows()));
        return text.Contains("Doyoutrustthecontentsofthisdirectory", StringComparison.OrdinalIgnoreCase)
               && text.Contains("Pressentertocontinue", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SubmitVerifier
{
    internal static async Task<SubmitProof> WaitForProofAsync(
        Session session,
        string prompt,
        string expectedToken,
        IReadOnlyList<string> expectedFragments,
        int minimumTerminalFragmentCount,
        bool requiresAssistantToken,
        TranscriptSnapshot transcriptBefore,
        long bufferCursor,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var transcript = TranscriptProof.Observe(session, transcriptBefore, prompt, expectedToken, expectedFragments);
            if (transcript.UserMessageObserved && (!requiresAssistantToken || transcript.AssistantFragmentsObserved))
            {
                return SubmitProof.CreatePassed(
                    tokenObserved: transcript.AssistantFragmentsObserved,
                    contentFragmentsObserved: transcript.AssistantFragmentsObserved,
                    transcriptUserMessageObserved: transcript.UserMessageObserved,
                    transcriptAssistantTokenObserved: transcript.AssistantFragmentsObserved,
                    message: "Transcript proof observed.");
            }

            if (!requiresAssistantToken && TurnStarted(session, bufferCursor, prompt))
            {
                return SubmitProof.CreatePassed(
                    tokenObserved: CountTokenSince(session, bufferCursor, expectedToken) > 0,
                    contentFragmentsObserved: FragmentsObservedSince(session, bufferCursor, expectedFragments, minimumCount: 1),
                    transcriptUserMessageObserved: transcript.UserMessageObserved,
                    transcriptAssistantTokenObserved: transcript.AssistantFragmentsObserved,
                    message: "Terminal turn evidence observed.");
            }

            if (requiresAssistantToken
                && TurnStarted(session, bufferCursor, prompt)
                && FragmentsObservedSince(session, bufferCursor, expectedFragments, minimumTerminalFragmentCount))
            {
                return SubmitProof.CreatePassed(
                    tokenObserved: true,
                    contentFragmentsObserved: true,
                    transcriptUserMessageObserved: transcript.UserMessageObserved,
                    transcriptAssistantTokenObserved: transcript.AssistantFragmentsObserved,
                    message: "Terminal content fragments observed.");
            }

            await Task.Delay(750);
        }

        return ClassifyTimeout(session, prompt, bufferCursor);
    }

    private static bool TurnStarted(Session session, long bufferCursor, string prompt)
    {
        var (bytes, _) = session.Buffer?.GetWrittenSince(bufferCursor) ?? (Array.Empty<byte>(), 0);
        return bytes.Length > Encoding.UTF8.GetByteCount(prompt) + 500;
    }

    internal static SubmitProof ClassifyTimeout(Session session, string prompt, long bufferCursor)
    {
        var normalizedPrompt = TerminalSubmit.NormalizeForEcho(prompt);
        var screen = string.Join("\n", session.SnapshotScreenRows());
        var normalizedScreen = TerminalSubmit.NormalizeForEcho(screen);
        var (bytes, _) = session.Buffer?.GetWrittenSince(bufferCursor) ?? (Array.Empty<byte>(), 0);
        var normalizedRecent = TerminalSubmit.NormalizeForEcho(
            TerminalSubmit.StripAnsi(Encoding.UTF8.GetString(bytes)));
        var visible = normalizedPrompt.Length > 0
            && (normalizedScreen.Contains(normalizedPrompt, StringComparison.Ordinal)
                || normalizedRecent.Contains(normalizedPrompt, StringComparison.Ordinal));
        var lowGrowth = bytes.Length < Math.Max(2000, Encoding.UTF8.GetByteCount(prompt) + 500);

        if (visible && lowGrowth)
        {
            return SubmitProof.Failed(
                "parked_composer",
                tokenObserved: false,
                turnStarted: false,
                message: "Prompt text remains visible with no turn evidence.");
        }

        return SubmitProof.Failed(
            "turn_timeout",
            tokenObserved: false,
            turnStarted: bytes.Length > Encoding.UTF8.GetByteCount(prompt) + 500,
            message: "No transcript or token proof before timeout.");
    }

    internal static SubmitProof ClassifySendFailure(Session session, string prompt, long bufferCursor, Exception exception)
    {
        var timeoutProof = ClassifyTimeout(session, prompt, bufferCursor);
        if (timeoutProof.FailureClass == "parked_composer")
        {
            timeoutProof.Message += " Send path error: " + exception.Message;
            return timeoutProof;
        }

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
        {
            return SubmitProof.Failed(
                "session_exited_mid_turn",
                tokenObserved: false,
                turnStarted: timeoutProof.TurnStarted,
                message: $"Session exited during submit: status={session.Status}, exitCode={session.ExitCode}. Send path error: {exception.Message}");
        }

        if (exception is HttpRequestException || exception is InvalidOperationException)
        {
            return SubmitProof.Failed(
                "submit_route_error",
                tokenObserved: timeoutProof.TokenObserved,
                turnStarted: timeoutProof.TurnStarted,
                message: "Submit route returned an error before token proof. " + exception.Message);
        }

        return SubmitProof.HarnessError(exception.Message);
    }

    internal static SubmitProof ClassifyHarnessException(Exception exception)
    {
        if (exception is TimeoutException)
            return SubmitProof.Failed("startup_timeout", tokenObserved: false, turnStarted: false, exception.Message);

        if (exception is InvalidOperationException
            && exception.Message.Contains("Session exited before ready", StringComparison.OrdinalIgnoreCase))
        {
            return SubmitProof.Failed("startup_failed", tokenObserved: false, turnStarted: false, exception.Message);
        }

        return SubmitProof.HarnessError(exception.Message);
    }

    private static int CountTokenSince(Session session, long bufferCursor, string token)
    {
        var (bytes, _) = session.Buffer?.GetWrittenSince(bufferCursor) ?? (Array.Empty<byte>(), 0);
        var text = TerminalSubmit.NormalizeForEcho(TerminalSubmit.StripAnsi(Encoding.UTF8.GetString(bytes)));
        var screenText = TerminalSubmit.NormalizeForEcho(string.Join("\n", session.SnapshotScreenRows()));
        var normalizedToken = TerminalSubmit.NormalizeForEcho(token);
        if (normalizedToken.Length == 0)
            return 0;

        return Math.Max(
            CountOccurrences(text, normalizedToken),
            CountOccurrences(screenText, normalizedToken));
    }

    private static int CountOccurrences(string text, string normalizedToken)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(normalizedToken, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += normalizedToken.Length;
        }

        return count;
    }

    private static bool FragmentsObservedSince(Session session, long bufferCursor, IReadOnlyList<string> fragments, int minimumCount)
    {
        foreach (var fragment in fragments)
        {
            if (CountTokenSince(session, bufferCursor, fragment) < minimumCount)
                return false;
        }
        return true;
    }
}

internal static class TranscriptProof
{
    internal static TranscriptSnapshot Snapshot(Session session)
    {
        if (!SessionHistoryReader.IsSupported(session))
            return new TranscriptSnapshot(false, 0, 0);

        var history = SessionHistoryReader.Read(session);
        return new TranscriptSnapshot(
            true,
            history.Messages.Count,
            history.Messages.Count(m => m.Role == ConversationRole.Assistant));
    }

    internal static TranscriptObservation Observe(
        Session session,
        TranscriptSnapshot before,
        string prompt,
        string expectedToken,
        IReadOnlyList<string> expectedFragments)
    {
        if (!before.Supported)
            return new TranscriptObservation(false, false);

        var history = SessionHistoryReader.Read(session);
        var newMessages = history.Messages.Skip(before.MessageCount).ToList();
        var normalizedPrompt = TerminalSubmit.NormalizeForEcho(prompt);
        var normalizedFragments = expectedFragments
            .Select(TerminalSubmit.NormalizeForEcho)
            .Where(f => f.Length > 0)
            .ToArray();

        var userObserved = newMessages
            .Where(m => m.Role == ConversationRole.User)
            .Any(m => TerminalSubmit.NormalizeForEcho(MessageText(m)).Contains(normalizedPrompt, StringComparison.OrdinalIgnoreCase)
                      || TerminalSubmit.NormalizeForEcho(MessageText(m)).Contains(TerminalSubmit.NormalizeForEcho(expectedToken), StringComparison.OrdinalIgnoreCase));

        var assistantObserved = newMessages
            .Where(m => m.Role == ConversationRole.Assistant)
            .Any(m =>
            {
                var text = TerminalSubmit.NormalizeForEcho(MessageText(m));
                return normalizedFragments.All(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
            });

        return new TranscriptObservation(userObserved, assistantObserved);
    }

    private static string MessageText(ConversationMessage message) =>
        string.Join("\n", message.Parts.Select(part => part.Text));
}

internal static class AgentProbe
{
    internal static AgentProbeResult Probe(AgentKind agentKind, AgentOptions options)
    {
        var plugin = AgentPluginRegistry.Get(agentKind);
        var candidates = new List<string>();
        var configured = plugin.Settings.GetConfiguredPath(options);
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured);
        candidates.AddRange(plugin.Detection.Candidates.Select(c => c.Path));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ExecutableResolver.Resolve(candidate);
            if (resolved is null)
                continue;

            var version = ProbeVersion(resolved, plugin.Validation);
            return new AgentProbeResult(agentKind, true, resolved, version, null);
        }

        return new AgentProbeResult(agentKind, false, null, null, plugin.Detection.InstallHint);
    }

    private static string ProbeVersion(string executablePath, AgentPluginValidationMetadata validation)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var part in SplitArguments(validation.Arguments))
                process.StartInfo.ArgumentList.Add(part);
            process.Start();
            if (!process.WaitForExit((int)validation.Timeout.TotalMilliseconds))
                return "version probe timed out";
            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            return string.IsNullOrWhiteSpace(output) ? $"exit {process.ExitCode}" : output.Split('\n')[0].Trim();
        }
        catch (Exception ex)
        {
            return $"version probe failed: {ex.Message}";
        }
    }

    private static IReadOnlyList<string> SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class MatrixBuilder
{
    internal static HarnessMatrix Build(HarnessOptions options)
    {
        var agents = options.AgentFilter is null
            ? AgentPluginRegistry.BuiltIns.Select(plugin => plugin.Kind).ToArray()
            : new[] { options.AgentFilter.Value };
        var routes = options.RouteFilter is null
            ? new[] { HarnessRoute.Direct, HarnessRoute.Rest, HarnessRoute.Fleet, HarnessRoute.Voice }
            : new[] { options.RouteFilter.Value };
        var strategies = options.SubmitStrategyFilter switch
        {
            SubmitStrategyFilter.All => new[] { SubmitStrategy.Current, SubmitStrategy.BracketedPaste },
            SubmitStrategyFilter.BracketedPaste => new[] { SubmitStrategy.BracketedPaste },
            _ => new[] { SubmitStrategy.Current },
        };
        var cases = HarnessCase.All
            .Where(c => options.CaseFilters.Count == 0 || options.CaseFilters.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return new HarnessMatrix(agents, routes, strategies, cases);
    }
}

internal static class RunPolicy
{
    private const int Phase4BroadRuns = 5;
    private const int Phase4DeepRuns = 25;

    internal static int GetRunCount(
        HarnessOptions options,
        AgentKind agentKind,
        HarnessCase testCase,
        HarnessRoute route,
        SubmitStrategy strategy)
    {
        if (!options.FocusedPhase4RunCounts || strategy != SubmitStrategy.Current)
            return options.Runs;

        if (agentKind == AgentKind.Codex && route is HarnessRoute.Rest or HarnessRoute.Fleet)
            return Phase4DeepRuns;

        if (agentKind == AgentKind.ClaudeCode
            && testCase.Id is "large-line" or "multiline")
            return Phase4DeepRuns;

        return Phase4BroadRuns;
    }
}

internal static class ResultWriter
{
    internal static void Write(HarnessReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "summary.json"),
            JsonSerializer.Serialize(report, JsonOptions.Indented),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(outputDirectory, "summary.html"),
            RenderHtml(report),
            Encoding.UTF8);
    }

    private static string RenderHtml(HarnessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Terminal inject harness report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#1f2937}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d1d5db;padding:6px 8px;text-align:left;vertical-align:top}.pass{color:#166534}.fail{color:#991b1b}.skip{color:#6b7280}code{font-family:Consolas,monospace}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Terminal inject harness report</h1>");
        sb.AppendLine($"<p>Machine: <code>{Html(report.MachineName)}</code><br>Started: <code>{report.StartedAtUtc:u}</code><br>Finished: <code>{report.FinishedAtUtc:u}</code><br>Control API: <code>{Html(report.ControlApiBaseUrl)}</code></p>");
        sb.AppendLine("<h2>Agent probes</h2><table><tr><th>Agent</th><th>Installed</th><th>Path</th><th>Version</th><th>Skip reason</th></tr>");
        foreach (var probe in report.AgentProbes)
            sb.AppendLine($"<tr><td>{Html(probe.AgentKind.ToString())}</td><td>{probe.Installed}</td><td><code>{Html(probe.ResolvedPath)}</code></td><td>{Html(probe.Version)}</td><td>{Html(probe.SkipReason)}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Strategy summary</h2><table><tr><th>Agent</th><th>Case</th><th>Strategy</th><th>Pass</th><th>Fail</th><th>Skip</th><th>Applicable</th><th>Success rate</th></tr>");
        foreach (var summary in report.StrategySummaries)
        {
            sb.AppendLine($"<tr><td>{Html(summary.Agent)}</td><td>{Html(summary.Case)}</td><td>{Html(summary.Strategy)}</td><td>{summary.PassCount}</td><td>{summary.FailCount}</td><td>{summary.SkipCount}</td><td>{summary.ApplicableCount}</td><td>{summary.SuccessRate:P0}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Flake-rate matrix</h2><table><tr><th>Agent</th><th>Case</th><th>Route</th><th>Strategy</th><th>Runs</th><th>Pass</th><th>Fail</th><th>Skip</th><th>Success rate</th><th>Gate</th><th>Failure classes</th></tr>");
        foreach (var summary in report.CombinationSummaries)
        {
            var gateClass = summary.GatePassed ? "pass" : "fail";
            sb.AppendLine($"<tr><td>{Html(summary.Agent)}</td><td>{Html(summary.Case)}</td><td>{Html(summary.Route)}</td><td>{Html(summary.Strategy)}</td><td>{summary.TotalRuns}</td><td>{summary.PassCount}</td><td>{summary.FailCount}</td><td>{summary.SkipCount}</td><td>{summary.SuccessRate:P0}</td><td class=\"{gateClass}\">{(summary.GatePassed ? "pass" : "fail")}</td><td>{Html(string.Join(", ", summary.FailureClasses))}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Results</h2><table><tr><th>Status</th><th>Agent</th><th>Case</th><th>Route</th><th>Strategy</th><th>Run</th><th>Failure class</th><th>Evidence</th><th>Artifacts</th></tr>");
        foreach (var result in report.Results)
        {
            var statusClass = result.Status.ToString().ToLowerInvariant();
            var evidence = $"token={result.TokenObserved}; fragments={result.ContentFragmentsObserved}; pasteMode={result.BracketedPasteModeObserved}; userTranscript={result.TranscriptUserMessageObserved}; assistantTranscript={result.TranscriptAssistantTokenObserved}; parked={result.ParkedComposer}; {result.Message}";
            sb.AppendLine($"<tr><td class=\"{statusClass}\">{result.Status}</td><td>{Html(result.Agent)}</td><td>{Html(result.Case)}</td><td>{Html(result.Route)}</td><td>{Html(result.Strategy)}</td><td>{result.Run}</td><td>{Html(result.FailureClass)}</td><td>{Html(evidence)}</td><td><code>{Html(result.ArtifactDirectory)}</code></td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Html(string? value) => HtmlEncoder.Default.Encode(value ?? "");
}

internal static class ReportSummarizer
{
    internal static void Populate(HarnessReport report)
    {
        report.StrategySummaries.Clear();
        report.CombinationSummaries.Clear();
        var grouped = report.Results
            .Where(r => r.Case is "medium-line" or "large-line" or "multiline")
            .GroupBy(r => new { r.Agent, r.Case, r.Strategy })
            .OrderBy(g => g.Key.Agent)
            .ThenBy(g => g.Key.Case)
            .ThenBy(g => g.Key.Strategy);

        foreach (var group in grouped)
        {
            var pass = group.Count(r => r.Status == HarnessStatus.Pass);
            var fail = group.Count(r => r.Status == HarnessStatus.Fail);
            var skip = group.Count(r => r.Status == HarnessStatus.Skip);
            var applicable = pass + fail;
            report.StrategySummaries.Add(new StrategySummary
            {
                Agent = group.Key.Agent,
                Case = group.Key.Case,
                Strategy = group.Key.Strategy,
                PassCount = pass,
                FailCount = fail,
                SkipCount = skip,
                ApplicableCount = applicable,
                SuccessRate = applicable == 0 ? 0 : (double)pass / applicable,
            });
        }

        var combinations = report.Results
            .Where(r => r.Case != "forced-parked")
            .GroupBy(r => new { r.Agent, r.Case, r.Route, r.Strategy })
            .OrderBy(g => g.Key.Agent)
            .ThenBy(g => g.Key.Case)
            .ThenBy(g => g.Key.Route)
            .ThenBy(g => g.Key.Strategy);

        foreach (var group in combinations)
        {
            var pass = group.Count(r => r.Status == HarnessStatus.Pass);
            var fail = group.Count(r => r.Status == HarnessStatus.Fail);
            var skip = group.Count(r => r.Status == HarnessStatus.Skip);
            var applicable = pass + fail;
            var failureClasses = group
                .Where(r => r.Status == HarnessStatus.Fail && !string.IsNullOrWhiteSpace(r.FailureClass))
                .Select(r => r.FailureClass!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            report.CombinationSummaries.Add(new CombinationSummary
            {
                Agent = group.Key.Agent,
                Case = group.Key.Case,
                Route = group.Key.Route,
                Strategy = group.Key.Strategy,
                TotalRuns = group.Count(),
                PassCount = pass,
                FailCount = fail,
                SkipCount = skip,
                SuccessRate = applicable == 0 ? 0 : (double)pass / applicable,
                GatePassed = fail == 0,
                FailureClasses = failureClasses,
            });
        }
    }
}

internal sealed record HarnessOptions
{
    internal string OutputDirectory { get; private init; } = Path.Combine("artifacts", "terminal-inject-harness", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
    internal string? RepositoryOverride { get; private init; }
    internal AgentKind? AgentFilter { get; private init; }
    internal IReadOnlyList<string> CaseFilters { get; private init; } = [];
    internal HarnessRoute? RouteFilter { get; private init; }
    internal SubmitStrategyFilter SubmitStrategyFilter { get; private init; } = SubmitStrategyFilter.Current;
    internal int Runs { get; private init; } = 1;
    internal TimeSpan StartupTimeout { get; private init; } = TimeSpan.FromSeconds(90);
    internal TimeSpan TurnTimeout { get; private init; } = TimeSpan.FromSeconds(180);
    internal bool IncludeForcedParkedCase { get; private init; } = true;
    internal bool AllowFailures { get; private init; }
    internal bool FocusedPhase4RunCounts { get; private init; }

    internal static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            options = arg switch
            {
                "--out" => options with { OutputDirectory = Next() },
                "--repo" => options with { RepositoryOverride = Next() },
                "--agent" => options with { AgentFilter = Enum.Parse<AgentKind>(Next(), ignoreCase: true) },
                "--case" => options with { CaseFilters = SplitCsv(Next()) },
                "--route" => options with { RouteFilter = Enum.Parse<HarnessRoute>(Next(), ignoreCase: true) },
                "--submit-strategy" => options with { SubmitStrategyFilter = ParseSubmitStrategyFilter(Next()) },
                "--runs" => options with { Runs = Math.Max(1, int.Parse(Next())) },
                "--timeout" => options with { TurnTimeout = TimeSpan.FromSeconds(Math.Max(5, int.Parse(Next()))) },
                "--startup-timeout" => options with { StartupTimeout = TimeSpan.FromSeconds(Math.Max(5, int.Parse(Next()))) },
                "--no-forced-parked" => options with { IncludeForcedParkedCase = false },
                "--allow-failures" => options with { AllowFailures = true },
                "--focused-phase4" => options with { FocusedPhase4RunCounts = true },
                _ => throw new ArgumentException($"Unknown argument: {arg}"),
            };
        }
        return options;
    }

    private static IReadOnlyList<string> SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SubmitStrategyFilter ParseSubmitStrategyFilter(string value)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
            return SubmitStrategyFilter.All;
        if (value.Equals("current", StringComparison.OrdinalIgnoreCase))
            return SubmitStrategyFilter.Current;
        if (value.Equals("bracketed-paste", StringComparison.OrdinalIgnoreCase)
            || value.Equals("bracketedpaste", StringComparison.OrdinalIgnoreCase))
            return SubmitStrategyFilter.BracketedPaste;
        throw new ArgumentException($"Unknown submit strategy: {value}");
    }
}

internal static class ProcessStartInfoExtensions
{
    internal static ProcessStartInfo WithArgument(this ProcessStartInfo info, string argument)
    {
        info.ArgumentList.Add(argument);
        return info;
    }
}

internal sealed record HarnessMatrix(
    IReadOnlyList<AgentKind> Agents,
    IReadOnlyList<HarnessRoute> Routes,
    IReadOnlyList<SubmitStrategy> Strategies,
    IReadOnlyList<HarnessCase> Cases);

internal sealed record HarnessCase(string Id, bool RequiresAssistantToken)
{
    internal static IReadOnlyList<HarnessCase> All { get; } =
    [
        new("tiny", false),
        new("sentence", true),
        new("medium-line", true),
        new("large-line", true),
        new("multiline", true),
        new("leading-slash", true),
        new("special-ascii", true),
        new("unicode-data", true),
        new("fleet-framed", true),
    ];

    internal PromptCase BuildPrompt(string token)
    {
        if (!RequiresAssistantToken)
            return new PromptCase(token, [token], MinimumTerminalFragmentCount: 1);

        if (Id.Equals("sentence", StringComparison.OrdinalIgnoreCase))
            return new PromptCase($"Reply {token}", [token], MinimumTerminalFragmentCount: 2);

        if (Id.Equals("leading-slash", StringComparison.OrdinalIgnoreCase))
        {
            var promptText = $"/not-a-real-command Do not modify files. This is data, not a command. Reply with {token} only.";
            return new PromptCase(promptText, [token], MinimumTerminalFragmentCount: 2);
        }

        if (Id.Equals("special-ascii", StringComparison.OrdinalIgnoreCase))
        {
            var marker = $"ASCII{Guid.NewGuid():N}"[..24].ToUpperInvariant();
            var promptText = "Do not modify files. Read these literal ASCII characters as data: "
                + "slashes=/\\/ quotes='\" backticks=``` brackets=[]{} parens=() percent=% amp=& at=@ pipe=| "
                + $"MARKER={marker}. Reply with {token} and {marker} only.";
            return new PromptCase(promptText, [token, marker], MinimumTerminalFragmentCount: 2);
        }

        if (Id.Equals("unicode-data", StringComparison.OrdinalIgnoreCase))
        {
            var marker = $"UNICODE{Guid.NewGuid():N}"[..24].ToUpperInvariant();
            var unicodePayload = string.Concat(
                "\u03B1\u03B2\u03B3 ",
                "\u041F\u0440\u0438\u0432\u0435\u0442 ",
                "\u3053\u3093\u306B\u3061\u306F ",
                "\uD83D\uDE80");
            var promptText = $"Do not modify files. Treat this UTF-8 payload as data: {unicodePayload}. MARKER={marker}. Reply with {token} and {marker} only.";
            return new PromptCase(promptText, [token, marker], MinimumTerminalFragmentCount: 2);
        }

        if (Id.Equals("fleet-framed", StringComparison.OrdinalIgnoreCase))
        {
            var marker = $"FLEET{Guid.NewGuid():N}"[..24].ToUpperInvariant();
            var body = $"Do not modify files. Read this framed fleet-style message and reply with {token} and {marker} only.";
            var promptText = BuildFleetFramedPayload(body + $" MARKER={marker}");
            return new PromptCase(promptText, [token, marker], MinimumTerminalFragmentCount: 2);
        }

        var markerPrefix = Id.Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var middle = $"{markerPrefix}MID{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var end = $"{markerPrefix}END{Guid.NewGuid():N}"[..28].ToUpperInvariant();

        var text = Id.ToLowerInvariant() switch
        {
            "medium-line" => BuildSingleLinePayload(token, middle, end, 520),
            "large-line" => BuildSingleLinePayload(token, middle, end, 1250),
            "multiline" => BuildMultilinePayload(token, middle, end),
            _ => $"Reply {token}",
        };

        return new PromptCase(text, [token, middle, end], MinimumTerminalFragmentCount: 1);
    }

    private static string BuildSingleLinePayload(string token, string middle, string end, int minimumLength)
    {
        var sb = new StringBuilder();
        sb.Append("Do not modify files. Read the payload and reply with the token, the MIDDLE_MARKER value, and the END_MARKER value only. ");
        sb.Append("PAYLOAD_START ");
        while (sb.Length < minimumLength / 2)
            sb.Append("alpha beta gamma delta ");
        sb.Append("MIDDLE_MARKER=").Append(middle).Append(' ');
        while (sb.Length < minimumLength)
            sb.Append("theta lambda sigma omega ");
        sb.Append("END_MARKER=").Append(end).Append(" PAYLOAD_END TOKEN=").Append(token);
        return sb.ToString();
    }

    private static string BuildMultilinePayload(string token, string middle, string end) =>
        string.Join('\n',
        [
            "Do not modify files. Read the payload and reply with the token, the MIDDLE_MARKER value, and the END_MARKER value only.",
            "PAYLOAD_START",
            "line one carries ordinary words for the paste path",
            $"line two has MIDDLE_MARKER={middle}",
            "line three keeps the payload multi-line and nontrivial",
            $"line four has END_MARKER={end}",
            $"PAYLOAD_END TOKEN={token}",
        ]);

    private static string BuildFleetFramedPayload(string text)
    {
        var oneLine = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return $"Message [message from terminal-inject-harness ({Environment.MachineName}), id phase4] {oneLine}  (to reply: cc-devthrottle message send phase4 \"<your reply>\")";
    }
}

internal sealed record PromptCase(string Text, IReadOnlyList<string> ExpectedFragments, int MinimumTerminalFragmentCount);

internal enum HarnessRoute
{
    Direct,
    Rest,
    Fleet,
    Voice,
}

internal enum SubmitStrategy
{
    Current,
    BracketedPaste,
}

internal enum SubmitStrategyFilter
{
    Current,
    BracketedPaste,
    All,
}

internal enum HarnessStatus
{
    Pass,
    Fail,
    Skip,
}

internal sealed record TranscriptSnapshot(bool Supported, int MessageCount, int AssistantMessageCount);

internal sealed record TranscriptObservation(bool UserMessageObserved, bool AssistantFragmentsObserved);

internal sealed record AgentProbeResult(
    AgentKind AgentKind,
    bool Installed,
    string? ResolvedPath,
    string? Version,
    string? SkipReason);

internal sealed class HarnessReport
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public string MachineName { get; set; } = "";
    public string RepositoryRoot { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string ControlApiBaseUrl { get; set; } = "";
    public int RunsRequested { get; set; }
    public List<AgentProbeResult> AgentProbes { get; } = new();
    public List<HarnessResult> Results { get; } = new();
    public List<StrategySummary> StrategySummaries { get; } = new();
    public List<CombinationSummary> CombinationSummaries { get; } = new();
}

internal sealed class StrategySummary
{
    public string Agent { get; set; } = "";
    public string Case { get; set; } = "";
    public string Strategy { get; set; } = "";
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int SkipCount { get; set; }
    public int ApplicableCount { get; set; }
    public double SuccessRate { get; set; }
}

internal sealed class CombinationSummary
{
    public string Agent { get; set; } = "";
    public string Case { get; set; } = "";
    public string Route { get; set; } = "";
    public string Strategy { get; set; } = "";
    public int TotalRuns { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int SkipCount { get; set; }
    public double SuccessRate { get; set; }
    public bool GatePassed { get; set; }
    public IReadOnlyList<string> FailureClasses { get; set; } = [];
}

internal sealed class HarnessResult
{
    public string Agent { get; set; } = "";
    public string Case { get; set; } = "";
    public string Route { get; set; } = "";
    public string Strategy { get; set; } = "";
    public int Run { get; set; }
    public HarnessStatus Status { get; set; }
    public string? FailureClass { get; set; }
    public string Message { get; set; } = "";
    public string SentText { get; set; } = "";
    public string ExpectedToken { get; set; } = "";
    public IReadOnlyList<string> ExpectedFragments { get; set; } = [];
    public bool TokenObserved { get; set; }
    public bool ContentFragmentsObserved { get; set; }
    public bool TranscriptUserMessageObserved { get; set; }
    public bool TranscriptAssistantTokenObserved { get; set; }
    public bool TurnStarted { get; set; }
    public bool ParkedComposer { get; set; }
    public bool? BracketedPasteModeObserved { get; set; }
    public long BufferCursor { get; set; }
    public string? SessionId { get; set; }
    public string? RepositoryPath { get; set; }
    public string? AgentExecutablePath { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public string? ArtifactDirectory { get; set; }

    internal static HarnessResult Skipped(AgentKind agentKind, string testCase, HarnessRoute route, SubmitStrategy strategy, string failureClass, string message) =>
        new()
        {
            Agent = agentKind.ToString(),
            Case = testCase,
            Route = route.ToString().ToLowerInvariant(),
            Strategy = TerminalInjectHarnessApp.FormatStrategy(strategy),
            Run = 0,
            Status = HarnessStatus.Skip,
            FailureClass = failureClass,
            Message = message,
        };
}

internal sealed class SubmitProof
{
    public bool Passed { get; set; }
    public string? FailureClass { get; set; }
    public string Message { get; set; } = "";
    public bool TokenObserved { get; set; }
    public bool ContentFragmentsObserved { get; set; }
    public bool TranscriptUserMessageObserved { get; set; }
    public bool TranscriptAssistantTokenObserved { get; set; }
    public bool TurnStarted { get; set; }

    internal static SubmitProof CreatePassed(
        bool tokenObserved,
        bool contentFragmentsObserved,
        bool transcriptUserMessageObserved,
        bool transcriptAssistantTokenObserved,
        string message) =>
        new()
        {
            Passed = true,
            Message = message,
            TokenObserved = tokenObserved,
            ContentFragmentsObserved = contentFragmentsObserved,
            TranscriptUserMessageObserved = transcriptUserMessageObserved,
            TranscriptAssistantTokenObserved = transcriptAssistantTokenObserved,
            TurnStarted = true,
        };

    internal static SubmitProof Failed(string failureClass, bool tokenObserved, bool turnStarted, string message) =>
        new()
        {
            Passed = false,
            FailureClass = failureClass,
            Message = message,
            TokenObserved = tokenObserved,
            ContentFragmentsObserved = false,
            TurnStarted = turnStarted,
        };

    internal static SubmitProof HarnessError(string message) =>
        Failed("harness_error", tokenObserved: false, turnStarted: false, message);

    internal static SubmitProof Skipped(string message) =>
        Failed("not_applicable", tokenObserved: false, turnStarted: false, message);
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
