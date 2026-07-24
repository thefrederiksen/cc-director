using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public enum ChecksState { None, Passing, Running, Failing }

/// <summary>One open pull request, provider-neutral, display-ready.</summary>
public sealed record PullRequestInfo
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool IsDraft { get; init; }

    /// <summary>Finished string: "approved", "changes requested", "review required", or "".</summary>
    public string ReviewState { get; init; } = "";

    public ChecksState Checks { get; init; }
    public string Url { get; init; } = "";
    public DateTime? CreatedUtc { get; init; }
}

/// <summary>The result of listing pull requests: rows, or a plain-English reason there are none.</summary>
public sealed record PullRequestListResult
{
    public IReadOnlyList<PullRequestInfo> Items { get; init; } = Array.Empty<PullRequestInfo>();
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Lists open pull requests for a repository via the CLIs the machine is already signed into -
/// gh for GitHub, az for Azure DevOps (the per-repo/per-org auth model: the repo's remote URL
/// decides the provider; Credential Manager and the CLI logins hold the keys). Read-only.
/// </summary>
public sealed class PullRequestService
{
    /// <summary>Lists open pull requests, dispatching on the repository's provider.</summary>
    public async Task<PullRequestListResult> ListOpenAsync(string repoPath, RepoProvider provider, CancellationToken ct = default)
    {
        FileLog.Write($"[PullRequestService] ListOpenAsync: {repoPath} ({provider})");
        try
        {
            return provider switch
            {
                RepoProvider.GitHub => await ListGitHubAsync(repoPath, ct),
                RepoProvider.AzureDevOps => await ListAzureAsync(repoPath, ct),
                _ => new PullRequestListResult { Success = false, Error = "This repository's remote is not GitHub or Azure DevOps." },
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PullRequestService] ListOpenAsync FAILED: {ex.Message}");
            return new PullRequestListResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<PullRequestListResult> ListGitHubAsync(string repoPath, CancellationToken ct)
    {
        var (exit, output, error) = await RunCliAsync("gh", new[]
        {
            "pr", "list", "--state", "open", "--limit", "30",
            "--json", "number,title,author,headRefName,isDraft,reviewDecision,statusCheckRollup,url,createdAt"
        }, repoPath, ct);
        if (exit != 0)
            return new PullRequestListResult { Success = false, Error = $"gh pr list failed: {FirstLine(error)} (is gh signed in? run: gh auth login)" };
        return new PullRequestListResult { Items = ParseGitHub(output), Success = true };
    }

    private async Task<PullRequestListResult> ListAzureAsync(string repoPath, CancellationToken ct)
    {
        // --detect resolves org/project/repository from the git remote in the working directory.
        var (exit, output, error) = await RunCliAsync("az", new[]
        {
            "repos", "pr", "list", "--status", "active", "--detect", "true", "--output", "json"
        }, repoPath, ct);
        if (exit != 0)
            return new PullRequestListResult { Success = false, Error = $"az repos pr list failed: {FirstLine(error)} (is az signed in to this organization? run: az login)" };
        return new PullRequestListResult { Items = ParseAzure(output), Success = true };
    }

    // ----- pure parsers (golden-tested from fixture JSON) -----

    internal static IReadOnlyList<PullRequestInfo> ParseGitHub(string json)
    {
        var items = new List<PullRequestInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            items.Add(new PullRequestInfo
            {
                Number = e.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
                Title = Str(e, "title"),
                Author = e.TryGetProperty("author", out var a) ? Str(a, "login") : "",
                Branch = Str(e, "headRefName"),
                IsDraft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
                ReviewState = MapReviewDecision(Str(e, "reviewDecision")),
                Checks = SummarizeGitHubChecks(e),
                Url = Str(e, "url"),
                CreatedUtc = e.TryGetProperty("createdAt", out var c) && c.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null,
            });
        }
        return items;
    }

    internal static IReadOnlyList<PullRequestInfo> ParseAzure(string json)
    {
        var items = new List<PullRequestInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var source = Str(e, "sourceRefName");
            items.Add(new PullRequestInfo
            {
                Number = e.TryGetProperty("pullRequestId", out var n) ? n.GetInt32() : 0,
                Title = Str(e, "title"),
                Author = e.TryGetProperty("createdBy", out var a) ? Str(a, "displayName") : "",
                Branch = source.StartsWith("refs/heads/", StringComparison.Ordinal) ? source["refs/heads/".Length..] : source,
                IsDraft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
                ReviewState = "", // Azure listing parity: review votes need a second call - out of v1
                Checks = ChecksState.None,
                Url = Str(e, "url"),
            });
        }
        return items;
    }

    internal static string MapReviewDecision(string decision) => decision switch
    {
        "APPROVED" => "approved",
        "CHANGES_REQUESTED" => "changes requested",
        "REVIEW_REQUIRED" => "review required",
        _ => "",
    };

    internal static ChecksState SummarizeGitHubChecks(JsonElement pr)
    {
        if (!pr.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Array)
            return ChecksState.None;

        bool any = false, running = false, failing = false;
        foreach (var check in rollup.EnumerateArray())
        {
            any = true;
            var status = Str(check, "status");     // check runs: QUEUED / IN_PROGRESS / COMPLETED
            var conclusion = Str(check, "conclusion"); // SUCCESS / FAILURE / ...
            var state = Str(check, "state");       // status contexts: SUCCESS / FAILURE / PENDING
            if (status is "QUEUED" or "IN_PROGRESS" || state == "PENDING")
                running = true;
            if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" || state is "FAILURE" or "ERROR")
                failing = true;
        }
        if (!any) return ChecksState.None;
        if (failing) return ChecksState.Failing;   // a failure outranks anything still running
        if (running) return ChecksState.Running;
        return ChecksState.Passing;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string FirstLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";

    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(string exe, string[] args, string workingDir, CancellationToken ct)
    {
        try
        {
            // ProcessRunner drains stdout and stderr concurrently and kills the child tree on
            // cancellation - a CLI that fills its stderr pipe (an auth prompt, a stack of warnings)
            // can no longer deadlock the capture, and a cancelled call leaves no orphaned process.
            var r = await ProcessRunner.RunAsync(exe, args, workingDir, ct);
            return r.Started ? (r.ExitCode, r.StandardOutput, r.StandardError) : (-1, "", $"{exe} could not start");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message); // exe missing entirely
        }
    }
}
