namespace CcDirector.Core.Tools;

/// <summary>
/// One tool's inputs for the home tool-health roll-up. <see cref="Passed"/> is only meaningful when
/// <see cref="IsBuilt"/> is true (a not-built tool ran no tests). <see cref="IsExpected"/> marks a tool
/// this install was meant to provide (shim or built), so a not-built-but-expected tool is a repairable
/// half-install rather than an optional/never-installed one. <see cref="FailureReason"/> carries WHY the
/// tool failed - which check, and what it said - so the failure travels with the count instead of being
/// discarded (issue #1045).
/// </summary>
public readonly record struct ToolHealthInput(
    string Name, bool IsBuilt, bool IsExpected, bool Passed, string? FailureReason = null);

/// <summary>
/// A tool that failed its checks, with the reason attached: which check failed and what it reported
/// (an exit code, a timeout, a missing expected string). The reason is the whole point - "1 fail" tells
/// a user nothing they can act on, and told us nothing when a clean install reported cc-pdf failing and
/// the log had kept no record of why.
/// </summary>
public sealed record ToolFailure(string Name, string Reason)
{
    /// <summary>"cc-pdf (smoke check: timed out after 90s)", or just the name when no reason was captured.</summary>
    public override string ToString()
        => string.IsNullOrWhiteSpace(Reason) ? Name : $"{Name} ({Reason})";
}

/// <summary>
/// Aggregate cc-* tool health for the home readiness: how many built tools pass their checks, how many
/// fail, and how many are not built - plus how many of the not-built ones are "broken" (expected here
/// but missing, i.e. repairable) versus simply optional/never-installed. The home alarms only on real
/// problems (a failing built tool or a broken one); optional not-built tools are shown but stay quiet.
/// </summary>
public sealed record ToolHealthSummary(
    int Pass, int Fail, int NotBuilt, int Broken, IReadOnlyList<ToolFailure> Failures)
{
    /// <summary>Total tools considered (pass + fail + not-built).</summary>
    public int Total => Pass + Fail + NotBuilt;

    /// <summary>The names of the failing tools, without their reasons.</summary>
    public IReadOnlyList<string> Failing => Failures.Select(f => f.Name).ToList();

    /// <summary>
    /// True when the home should warn: ANY tool that is not passing - a built tool whose test failed, or
    /// a not-built tool (broken half-install OR optional/never-installed). The home shows the true picture
    /// and routes to the Tools page rather than hiding not-built tools behind "all systems go".
    /// </summary>
    public bool HasProblem => Fail > 0 || NotBuilt > 0;

    /// <summary>
    /// True when a tool is missing from the install - absent, or present as a shim with no binary behind
    /// it. This IS repairable by a reconcile (it writes the shim, or rebuilds the venv), so it is the half
    /// of a problem that warrants an automatic attempt.
    /// </summary>
    public bool HasMissingTool => NotBuilt > 0 || Broken > 0;

    /// <summary>
    /// True when a tool that IS installed fails its own checks. A reconcile has no mechanism for this -
    /// the shim is there, the binary is there, the venv is healthy, and the tool still does not work - so
    /// retrying one changes nothing. It is a to-do to report with its reason, not drift to loop on.
    /// </summary>
    public bool HasFailingTool => Fail > 0;

    public static ToolHealthSummary From(IEnumerable<ToolHealthInput> inputs)
    {
        int pass = 0, fail = 0, notBuilt = 0, broken = 0;
        var failures = new List<ToolFailure>();
        foreach (var t in inputs)
        {
            if (!t.IsBuilt)
            {
                notBuilt++;
                if (t.IsExpected) broken++; // shim present, exe missing = repairable half-install
                continue;
            }
            if (t.Passed) pass++;
            else { fail++; failures.Add(new ToolFailure(t.Name, t.FailureReason ?? "")); }
        }
        return new ToolHealthSummary(pass, fail, notBuilt, broken, failures);
    }
}
