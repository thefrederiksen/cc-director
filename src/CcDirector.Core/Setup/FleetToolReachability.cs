using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>What the reachability run concluded about the cc-devthrottle on this machine's PATH.</summary>
public enum FleetToolVerdict
{
    /// <summary>Not judged yet. Must be rendered as "checking", never as a pass.</summary>
    Unchecked,

    /// <summary>PATH resolves cc-devthrottle and it authenticated against this Director.</summary>
    Working,

    /// <summary>Nothing named cc-devthrottle is on PATH at all.</summary>
    NotFound,

    /// <summary>
    /// PATH resolves a cc-devthrottle, and it CANNOT drive this Director. This is the fault: agents in
    /// sessions report "cannot connect to DevThrottle" while the Director is healthy and connected.
    /// </summary>
    CannotReachDirector,
}

/// <summary>
/// One reachability run. <see cref="ResolvedPath"/> and <see cref="ExpectedBinDir"/> are what the
/// explanation panel shows; they never decide the verdict.
/// </summary>
public sealed record FleetToolCheck(
    FleetToolVerdict Verdict,
    string? ResolvedPath,
    string? ExpectedBinDir,
    string Detail)
{
    /// <summary>
    /// True when the resolved tool belongs to a different install than this Director's. Explanation
    /// only - a development build legitimately runs from outside any install directory, so this must
    /// never be allowed to decide a verdict.
    /// </summary>
    public bool IsDifferentInstall
    {
        get
        {
            if (ResolvedPath is not { Length: > 0 } resolved) return false;
            if (ExpectedBinDir is not { Length: > 0 } expected) return false;
            try
            {
                var resolvedDir = Path.GetFullPath(Path.GetDirectoryName(resolved) ?? resolved)
                    .TrimEnd(Path.DirectorySeparatorChar);
                var expectedDir = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar);
                return !string.Equals(resolvedDir, expectedDir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unreadable path cannot support a claim either way, and this only ever decorates a
                // verdict that has already been reached.
                return false;
            }
        }
    }
}

/// <summary>
/// Answers ONE question, from inside the Director, about the machine the Director is running on:
///
///     Can a session I spawn actually drive me?
///
/// It exists because the two halves of the product can disagree without either being wrong. The
/// Director is a C# application; cc-devthrottle is a separate Python command line that reaches it over
/// HTTP. When PATH resolves a cc-devthrottle belonging to an older install, every agent in every
/// session reports "cannot connect to DevThrottle" while the Director's own chip correctly says
/// Connected. The outage is imaginary; the cost is that the agent blames the network and the owner
/// spends the morning there.
///
/// TWO DESIGN RULES, both learned the expensive way:
///
/// 1. The verdict is FUNCTIONAL, never structural. "Is the resolved path under my install directory"
///    looks like the same question and is not: a development build running from a local build
///    directory matches no install root and would be reported broken on a machine with no fault. So
///    the Director runs the tool PATH actually gives and sees whether it comes back. The path
///    comparison supplies the explanation and nothing else.
///
/// 2. The Director judges; the tool is never asked to judge itself. The case this check exists to
///    catch is a tool too OLD to be correct - and a tool too old to be correct is also too old to know
///    about any self-report we add today. Detection therefore lives entirely here, in the component
///    that is known-good, and reads only the exit code.
/// </summary>
public sealed class FleetToolReachability
{
    /// <summary>The command agents are told is on their PATH.</summary>
    public const string ToolName = "cc-devthrottle";

    // Any verb that requires a credential. `session list` is the cheapest one every shipped build has
    // had; its OUTPUT is irrelevant here, only whether it was allowed to run. A public route such as
    // /healthz would be answered without a credential and would pass on the broken machine - it would
    // not be a check.
    private static readonly string[] ProbeArgs = ["session", "list"];

    private readonly TimeSpan _timeout;
    private readonly Func<string, string?> _resolve;

    public FleetToolReachability() : this(TimeSpan.FromSeconds(30)) { }

    public FleetToolReachability(TimeSpan timeout) : this(timeout, ExecutableResolver.Resolve) { }

    /// <param name="resolve">How a command name becomes a concrete executable. Defaults to the same
    /// rules CreateProcess uses, so the check finds the binary a spawned session would actually run;
    /// injectable so tests need not mutate the process-global PATH.</param>
    public FleetToolReachability(TimeSpan timeout, Func<string, string?> resolve)
    {
        _timeout = timeout;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>
    /// Resolve cc-devthrottle the way a spawned session would, then make it prove it can authenticate
    /// against <paramref name="controlApiBaseUrl"/>.
    /// </summary>
    /// <param name="controlApiBaseUrl">This Director's own Control API address - the literal value it
    /// stamps into every session as CC_DIRECTOR_API, so the check and a real session ask the same
    /// question of the same endpoint.</param>
    /// <param name="expectedBinDir">This Director's own tool bin directory. Explanation only.</param>
    public async Task<FleetToolCheck> RunAsync(
        string controlApiBaseUrl, string? expectedBinDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(controlApiBaseUrl))
            throw new ArgumentException("A Control API base address is required.", nameof(controlApiBaseUrl));

        FileLog.Write($"[FleetToolReachability] RunAsync: probing {ToolName} against {controlApiBaseUrl}");

        var resolved = _resolve(ToolName);
        if (resolved is null)
        {
            FileLog.Write($"[FleetToolReachability] {ToolName} is not on PATH");
            return new FleetToolCheck(
                FleetToolVerdict.NotFound, null, expectedBinDir,
                $"Nothing named {ToolName} is on this machine's PATH.");
        }

        var (exitCode, output) = await RunProbeAsync(resolved, controlApiBaseUrl, ct);

        if (exitCode == 0)
        {
            FileLog.Write($"[FleetToolReachability] {ToolName} at {resolved} reached this Director");
            return new FleetToolCheck(
                FleetToolVerdict.Working, resolved, expectedBinDir, "reached this Director");
        }

        // Log the reason HERE, where it is known. A red badge whose cause is not in the log is a
        // second investigation for whoever finds it.
        var detail = FirstMeaningfulLine(output) ?? $"exit {exitCode}";
        FileLog.Write(
            $"[FleetToolReachability] {ToolName} at {resolved} FAILED to reach {controlApiBaseUrl}: {detail}");
        return new FleetToolCheck(FleetToolVerdict.CannotReachDirector, resolved, expectedBinDir, detail);
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(
        string toolPath, string controlApiBaseUrl, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in ProbeArgs) psi.ArgumentList.Add(arg);

        // The address this Director answers on. Without it the tool has no endpoint to aim at and
        // would fail for a reason that says nothing about the fault we are looking for.
        psi.Environment["CC_DIRECTOR_API"] = controlApiBaseUrl;

        var captured = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) captured.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) captured.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return (-1, $"timed out after {_timeout.TotalSeconds:0}s");
            }

            return (process.ExitCode, captured.ToString());
        }
        catch (Exception ex)
        {
            // A tool old enough to reject the arguments, or broken enough not to launch, is exactly the
            // case this check exists for. It is a failure, reported as one, with its real reason.
            FileLog.Write($"[FleetToolReachability] launch error for {toolPath}: {ex.Message}");
            return (-1, $"could not run it: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
        }
        return null;
    }
}
