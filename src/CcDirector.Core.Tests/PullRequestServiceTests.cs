using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>Golden tests: real-shaped gh/az JSON fixtures parsed to display rows.</summary>
public class PullRequestServiceTests
{
    private const string GitHubFixture = """
[
  {
    "number": 2081,
    "title": "Repositories v1 polish",
    "author": { "login": "thefrederiksen" },
    "headRefName": "feat/repository-warmstart-cache",
    "isDraft": false,
    "reviewDecision": "REVIEW_REQUIRED",
    "url": "https://github.com/thefrederiksen/devthrottle/pull/2081",
    "createdAt": "2026-07-23T18:00:00Z",
    "statusCheckRollup": [
      { "status": "COMPLETED", "conclusion": "SUCCESS" },
      { "status": "COMPLETED", "conclusion": "SUCCESS" }
    ]
  },
  {
    "number": 2065,
    "title": "Bump dependencies (weekly)",
    "author": { "login": "dependabot" },
    "headRefName": "deps/weekly",
    "isDraft": false,
    "reviewDecision": "CHANGES_REQUESTED",
    "url": "https://github.com/thefrederiksen/devthrottle/pull/2065",
    "createdAt": "2026-07-17T09:00:00Z",
    "statusCheckRollup": [
      { "status": "COMPLETED", "conclusion": "SUCCESS" },
      { "status": "COMPLETED", "conclusion": "FAILURE" },
      { "status": "IN_PROGRESS", "conclusion": "" }
    ]
  },
  {
    "number": 2072,
    "title": "Tenant-scoped worker seam",
    "author": { "login": "fleet" },
    "headRefName": "mtr/g8-worker-seam",
    "isDraft": true,
    "reviewDecision": "",
    "url": "https://github.com/thefrederiksen/devthrottle/pull/2072",
    "createdAt": "2026-07-22T14:30:00Z",
    "statusCheckRollup": [
      { "status": "IN_PROGRESS", "conclusion": "" }
    ]
  },
  {
    "number": 2001,
    "title": "No checks configured",
    "author": { "login": "thefrederiksen" },
    "headRefName": "docs/tidy",
    "isDraft": false,
    "reviewDecision": "APPROVED",
    "url": "https://github.com/thefrederiksen/devthrottle/pull/2001",
    "createdAt": "2026-07-20T08:00:00Z",
    "statusCheckRollup": []
  }
]
""";

    [Fact]
    public void ParseGitHub_MapsRowsChecksAndReviews()
    {
        var items = PullRequestService.ParseGitHub(GitHubFixture);
        Assert.Equal(4, items.Count);

        var passing = items[0];
        Assert.Equal(2081, passing.Number);
        Assert.Equal("thefrederiksen", passing.Author);
        Assert.Equal("feat/repository-warmstart-cache", passing.Branch);
        Assert.Equal(ChecksState.Passing, passing.Checks);
        Assert.Equal("review required", passing.ReviewState);
        Assert.False(passing.IsDraft);
        Assert.NotNull(passing.CreatedUtc);

        var failing = items[1];
        Assert.Equal(ChecksState.Failing, failing.Checks); // a failure outranks the one still running
        Assert.Equal("changes requested", failing.ReviewState);

        var draft = items[2];
        Assert.True(draft.IsDraft);
        Assert.Equal(ChecksState.Running, draft.Checks);
        Assert.Equal("", draft.ReviewState);

        var noChecks = items[3];
        Assert.Equal(ChecksState.None, noChecks.Checks);
        Assert.Equal("approved", noChecks.ReviewState);
    }

    private const string AzureFixture = """
[
  {
    "pullRequestId": 4102,
    "title": "ERP knowledge base builder",
    "createdBy": { "displayName": "Soren Frederiksen" },
    "sourceRefName": "refs/heads/feature/erp-knowledge-base-builder",
    "isDraft": false,
    "url": "https://dev.azure.com/mindzie/_apis/git/pullRequests/4102"
  }
]
""";

    [Fact]
    public void ParseAzure_MapsRows_BranchWithoutRefsPrefix()
    {
        var items = PullRequestService.ParseAzure(AzureFixture);
        var pr = Assert.Single(items);
        Assert.Equal(4102, pr.Number);
        Assert.Equal("Soren Frederiksen", pr.Author);
        Assert.Equal("feature/erp-knowledge-base-builder", pr.Branch);
        Assert.Equal(ChecksState.None, pr.Checks); // listing parity in v1
    }

    [Theory]
    [InlineData("APPROVED", "approved")]
    [InlineData("CHANGES_REQUESTED", "changes requested")]
    [InlineData("REVIEW_REQUIRED", "review required")]
    [InlineData("", "")]
    [InlineData("SOMETHING_NEW", "")]
    public void MapReviewDecision_FinishedStrings(string decision, string expected)
        => Assert.Equal(expected, PullRequestService.MapReviewDecision(decision));
}

public class GitHistoryServiceTests
{
    [Fact]
    public void Parse_TabSeparatedLog_MapsRows_SubjectMayContainTabsSafely()
    {
        var output = "abc1234\tSoren\t1753293600\tfix: the thing\n"
                   + "def5678\tAgent\t1753207200\tfeat: subject\twith a tab\n";
        var items = GitHistoryService.Parse(output);
        Assert.Equal(2, items.Count);
        Assert.Equal("abc1234", items[0].ShortHash);
        Assert.Equal("Soren", items[0].Author);
        Assert.NotNull(items[0].WhenUtc);
        Assert.Equal("fix: the thing", items[0].Subject);
        Assert.Equal("feat: subject\twith a tab", items[1].Subject); // split limited to 4 parts
    }
}
