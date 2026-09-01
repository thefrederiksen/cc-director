using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>
/// The acceptance fact for the prompt-delete erasure (mission work item W2), driven the way a member
/// drives it: over a real HTTP pipeline, through the same wiring <see cref="GatewayHost"/> uses, against
/// a real database.
///
/// WHAT WAS WRONG. <c>POST /prompts</c> copies each session's first prompt into <c>session_history</c>
/// (<see cref="SessionHistoryRecorder.ObservePrompts"/>), where the History page serves it for ninety
/// days. <c>DELETE /prompts</c> deleted the prompt FILES and left that copy behind, while the endpoint's
/// own documentation stated that the delete was the erasure. The member had exercised their delete and
/// their own words were still on the screen.
///
/// WHY IT IS TESTED HERE AND NOT ONLY AT THE STORE. The store test proves the erasure clears the
/// columns; it cannot prove the endpoint CALLS it, in the request tenant's scope, before it deletes the
/// files. Every one of those is a way for this fix to be present in the code and absent in the product -
/// and the store-level proof would stay green through all of them.
/// </summary>
public sealed class PromptDeleteErasesTheDerivedCopyTests : IAsyncLifetime
{
    private readonly GatewayDbTestHarness _harness = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private GatewayDatabase _db = null!;
    private SessionHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gw-prompt-erase-" + Guid.NewGuid().ToString("N"));
        _db = _harness.Open();
        _store = new SessionHistoryStore(_db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        // Self-host wiring: no boundary, so the request tenant is Local - the same tenant the harness
        // database is opened under, which is what makes the ambient scope real rather than assumed.
        PromptEndpoints.Map(_app, new GatewayPromptLog(_dir), tenantBoundary: null,
            historyStore: _store, history: new SessionHistoryRecorder(_store));
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _harness.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PromptRecord Rec(DateTime ts, string text, string sessionId = "s1") => new()
    {
        TsUtc = ts,
        Machine = "SOREN_NORTH",
        SessionId = sessionId,
        ContextId = "ctx-1",
        RepoPath = @"D:\ReposFred\devthrottle",
        Agent = "TestAgent",
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        Text = text,
    };

    private static SessionDto Session(string id = "s1") => new()
    {
        SessionId = id,
        Name = null,
        Number = 1,
        RepoPath = @"D:\ReposFred\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "TestAgent",
        MachineName = "SOREN_NORTH",
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        LastActivityAt = DateTime.UtcNow.AddMinutes(-1),
        ActivityState = "Working",
        Status = "Running",
    };

    private const string TheMembersOwnWords = "Rewrite the billing screen and do not lose the tax lines";

    [Fact]
    public async Task Deleting_the_prompt_history_also_erases_the_copy_the_gateway_derived_from_it()
    {
        // A Director pushes the session, so there is a history row for the prompt to land on - the
        // ordinary order of events, not a contrivance.
        _store.UpsertLive("dir-1", Session(), DateTime.UtcNow);
        var post = await _client.PostAsJsonAsync("/prompts", new PromptIngestRequest
        {
            Records = new[] { Rec(DateTime.UtcNow, TheMembersOwnWords) },
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        // The copy EXISTS first. Without this, a delete over nothing would look like a successful
        // erasure - the shape of proof that has certified broken features in this repository before.
        Assert.Equal(TheMembersOwnWords, FirstPromptLineInTheDatabase());
        var rollupDay = DateTime.UtcNow.Date;
        _store.SaveRollup("thefrederiksen/devthrottle", rollupDay,
            $"A day summarised from prompts including: {TheMembersOwnWords}", "hash", 0, DateTime.UtcNow, DateTime.UtcNow);

        var del = await _client.DeleteAsync("/prompts");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = JsonDocument.Parse(await del.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, body.GetProperty("deletedFiles").GetInt32());
        // The endpoint REPORTS what it erased rather than asserting an erasure happened.
        Assert.Equal(1, body.GetProperty("erasedHistoryRows").GetInt32());
        Assert.Equal(1, body.GetProperty("deletedHistoryRollups").GetInt32());

        Assert.Null(FirstPromptLineInTheDatabase());
        Assert.Empty(_store.ReadRollups(rollupDay, rollupDay));
        // The customer-visible surface: the History page's description no longer carries the prompt.
        var row = Assert.Single(_store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.DoesNotContain(TheMembersOwnWords, row.DescriptionLine ?? "", StringComparison.Ordinal);
        // And the prompt itself is gone from the log, which was already true and stays true.
        var after = await _client.GetFromJsonAsync<PromptsResponse>("/prompts");
        Assert.Equal(0, after!.Count);
    }

    /// <summary>
    /// A delete with nothing derived to erase reports zero rather than inventing work - and still
    /// succeeds. The counts are the member's evidence, so an inflated one is its own small lie.
    /// </summary>
    [Fact]
    public async Task A_delete_with_nothing_derived_reports_zero_and_still_succeeds()
    {
        var del = await _client.DeleteAsync("/prompts");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = JsonDocument.Parse(await del.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, body.GetProperty("deletedFiles").GetInt32());
        Assert.Equal(0, body.GetProperty("erasedHistoryRows").GetInt32());
        Assert.Equal(0, body.GetProperty("deletedHistoryRollups").GetInt32());
    }

    /// <summary>Read the derived copy from the COLUMN, not through the fold: the folded description
    /// falls back to the repository name when the prompt line is gone, so a fold-level check cannot
    /// tell an erased row from a row that still holds the prompt behind a different description.</summary>
    private string? FirstPromptLineInTheDatabase()
    {
        using var ctx = _db.CreateContext();
        return ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1").FirstPromptLine;
    }

    private sealed record PromptsResponse(int Count, IReadOnlyList<PromptRecord> Records);
}
