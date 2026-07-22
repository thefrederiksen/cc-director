using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// MTR audit gap H5 - the TWO INTERNAL readers of the untenanted turn-brief store are QUARANTINED on hosted.
//
// The deny in TurnBriefGatewayEndpoints (HostedTurnBriefDenyTests) closes the HTTP surface, but the same
// bare-session-id store is also read by two INTERNAL callers wired in GatewayHost:
//   - interruptedBriefFor(sid) - enriches each GET /interrupted row with the store's last RailLine/Headline;
//   - briefHistoryFor(sid)     - seeds POST /interrupted/{dir}/{pid}/restore's continuation prompt from the
//                                store's full brief history.
// Both key on BARE session id with no tenant, so on a hosted box a brief left under a session id (issue #549
// retired the writer, so it is legacy data attributable to no tenant) would be embedded into THIS caller's
// Interrupted list and into a NEW continuation session's first prompt. The GatewayHost wiring quarantines
// both on hosted - interruptedBriefFor returns (null, null), briefHistoryFor returns an empty list.
//
// These two classes drive the REAL production paths (a real hosted GatewayHost, a real tunnel-connected
// Director answering interrupted-list / create / patch over the stream) with a foreign brief seeded on disk
// under the exact session id the crash journal reports - the same wire path production uses. The self-host
// control proves the same paths DO embed the brief off hosted, so the hosted absence is a gate firing, not a
// route that never carries a brief. Without a running Director the fan-out surfaces no row and the restore
// 502s, so both hosted assertions fail loud (no false green) if the path never reaches the store at all.
//
// REVERT-PROOF - the recipe to RUN, not describe. In src/CcDirector.Gateway/GatewayHost.cs remove the two
// hosted guards so the readers serve the untenanted store on hosted too:
//   interruptedBriefFor: sid => { var b = _turnBriefStore.Latest(sid); return (b?.NeedsYou?.RailLine, b?.Headline); }
//   briefHistoryFor:     sid => _turnBriefStore.List(sid)
// Rebuild, CONFIRM ZERO ERRORS (a run after a failed build executes the previous binary and reports a false
// pass), then run this file and record every red BY NAME:
//   - The_interrupted_list_is_not_enriched_with_a_foreign_brief_on_hosted reddens: the row's Headline/RailLine
//     become the seeded foreign text ("expected null, got 'Another tenant's private headline'");
//   - The_restore_prompt_carries_no_foreign_brief_history_on_hosted reddens: ContextSent embeds
//     "Headline: Another tenant's private headline" instead of taking the "No turn briefs survived" branch.
// A red only counts if it fails WITH THE SYMPTOM - the disclosed foreign text - not a crash.
// ============================================================================
[Collection("GatewayHostedMode")]
public sealed class HostedInterruptedBriefQuarantineTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // Legacy brief text left in the untenanted store under a bare session id, from BEFORE #549 retired the
    // writer. It belongs to no tenant this hosted caller can be shown.
    private const string ForeignHeadline = "Another tenant's private headline";
    private const string ForeignRailLine = "another tenant's private rail line";
    private const string ForeignIntent = "another tenant's private mission";

    private const string DeadDir = "dead-1";
    private const int DeadPid = 9001;
    // The interrupted session id the caller's OWN Director reports - and, by coincidence of the store's only
    // key being the bare session id, also the id a foreign brief sits under on disk.
    private static readonly string Sid = "restored-" + Guid.NewGuid().ToString("N")[..8];

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dir = null!;
    private string _key = "";
    private string? _lastCreatePrePrompt;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-interrupted-tb-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            turnBriefDirectory: Path.Combine(_instancesDir, "gateway-turnbriefs"),
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A hosted caller resolves its tenant from an authenticated device key (bound to a real GUID tenant,
        // as production mints them). Without it every read route 403s with no bound tenant.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", "55555555-5555-5555-5555-555555555555");

        // The caller's OWN live Director, tunnel-connected under that device key's tenant. It reports the crash
        // journal (interrupted-list) and answers the restore continuation verbs (create + patch).
        _dir = await FakeTunnelDirector.StartAsync(_gateway, _key, "live-a", "MA", dispatch: Dispatch);

        // Legacy untenanted brief on disk under the bare session id the journal reports. A read that is NOT
        // quarantined has real foreign material to hand back.
        _gateway.TurnBriefs.Append(Sid, new TurnBriefDto
        {
            SessionId = Sid,
            TurnNumber = 3,
            Headline = ForeignHeadline,
            Intent = ForeignIntent,
            NeedsYou = new TurnBriefNeedsYou { Statement = "pending decision", RailLine = ForeignRailLine },
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dir.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task The_interrupted_list_is_not_enriched_with_a_foreign_brief_on_hosted()
    {
        // The row IS served - the caller's own Director reported the journal, which proves the fan-out reached
        // interruptedBriefFor for this id. On hosted the enrichment is quarantined: no foreign RailLine/Headline.
        var row = (await GetInterrupted()).Single(r => r.SessionId == Sid);

        Assert.Null(row.Headline);
        Assert.Null(row.RailLine);
        var raw = JsonSerializer.Serialize(row);
        Assert.DoesNotContain(ForeignHeadline, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignRailLine, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_restore_prompt_carries_no_foreign_brief_history_on_hosted()
    {
        var resp = await _http.SendAsync(Post($"interrupted/{DeadDir}/{DeadPid}/restore",
            new RestoreInterruptedRequest { SessionId = Sid, Via = _dir.DirectorId }));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<RestoreInterruptedResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Restored);

        // ContextSent is the exact continuation prompt seeded into the new session. On hosted briefHistoryFor
        // is empty, so it takes the "no briefs survived" branch and never embeds the foreign brief. A positive
        // branch assertion (not absence alone) proves the empty-history path was actually taken.
        var context = body.ContextSent ?? "";
        Assert.Contains("No turn briefs survived", context, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignHeadline, context, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignRailLine, context, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignIntent, context, StringComparison.Ordinal);

        // Belt and braces: the prompt actually handed to the Director over the tunnel (create verb) is the
        // same text, and it too is clean.
        Assert.NotNull(_lastCreatePrePrompt);
        Assert.DoesNotContain(ForeignHeadline, _lastCreatePrePrompt!, StringComparison.Ordinal);
    }

    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        switch (cmd.Verb)
        {
            case "interrupted-list":
                return FakeTunnelDirector.Ok(new[] { Journal() });
            case "create":
                var req = JsonSerializer.Deserialize<NewSessionRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                _lastCreatePrePrompt = req?.PrePrompt;
                return FakeTunnelDirector.Ok(new SessionDto { SessionId = "new-1", RepoPath = req?.RepoPath ?? "" });
            case "patch":
                var pr = JsonSerializer.Deserialize<SessionUpdateRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                return FakeTunnelDirector.Ok(new SessionDto { SessionId = cmd.SessionId, Name = pr?.Name });
            case "interrupted-remove":
                return FakeTunnelDirector.Ok(new { removed = true });
            default:
                return FakeTunnelDirector.Ok(new { ok = true });
        }
    }

    private static CrashJournalDto Journal() => new()
    {
        DirectorId = DeadDir,
        Pid = DeadPid,
        MachineName = "MA",
        User = "alice",
        LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        Sessions =
        {
            new CrashJournalSessionDto
            {
                SessionId = Sid, Name = "alpha work", RepoPath = "/repo/a", ClaudeSessionId = "claude-abc",
            },
        },
    };

    private async Task<List<InterruptedSessionDto>> GetInterrupted()
    {
        var resp = await _http.SendAsync(Get("interrupted"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<InterruptedSessionDto>>()) ?? new();
    }

    private HttpRequestMessage Get(string path) => Authed(new HttpRequestMessage(HttpMethod.Get, path));

    private HttpRequestMessage Post(string path, object body)
    {
        var req = Authed(new HttpRequestMessage(HttpMethod.Post, path));
        req.Content = JsonContent.Create(body);
        return req;
    }

    private HttpRequestMessage Authed(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return req;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL for the two internal brief readers. Self-host is the mission's control: the same
/// interrupted-list enrichment and restore continuation-prompt build DO embed the brief off hosted, so the
/// hosted absence proven above is a gate firing rather than a path that never carries a brief. This drives the
/// same real GatewayHost + tunnel Director path with CC_GATEWAY_HOSTED explicitly NOT hosted (auth is by the
/// shared token, and the tenant resolves to Local), asserts REAL PRESENCE of the seeded headline and rail line,
/// and must stay GREEN through the revert described on HostedInterruptedBriefQuarantineTests.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class SelfHostInterruptedBriefControlTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string OwnHeadline = "The owner's own headline";
    private const string OwnRailLine = "the owner's own rail line";

    private const string DeadDir = "dead-1";
    private const int DeadPid = 9001;
    private static readonly string Sid = "restored-" + Guid.NewGuid().ToString("N")[..8];

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dir = null!;
    private string? _lastCreatePrePrompt;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-interrupted-tb-self-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        // Explicitly NOT hosted, and prove the statement took.
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "0");
        Assert.False(GatewayHostedMode.IsHosted);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            turnBriefDirectory: Path.Combine(_instancesDir, "gateway-turnbriefs"),
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "live-a", "MA", dispatch: Dispatch);

        _gateway.TurnBriefs.Append(Sid, new TurnBriefDto
        {
            SessionId = Sid,
            TurnNumber = 3,
            Headline = OwnHeadline,
            Intent = "the owner's mission",
            NeedsYou = new TurnBriefNeedsYou { Statement = "pending", RailLine = OwnRailLine },
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dir.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task The_interrupted_list_is_enriched_with_the_owners_brief_on_self_host()
    {
        var row = (await _http.GetFromJsonAsync<List<InterruptedSessionDto>>("interrupted"))!
            .Single(r => r.SessionId == Sid);

        Assert.Equal(OwnHeadline, row.Headline);
        Assert.Equal(OwnRailLine, row.RailLine);
    }

    [Fact]
    public async Task The_restore_prompt_carries_the_owners_brief_history_on_self_host()
    {
        var resp = await _http.PostAsJsonAsync($"interrupted/{DeadDir}/{DeadPid}/restore",
            new RestoreInterruptedRequest { SessionId = Sid, Via = _dir.DirectorId });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<RestoreInterruptedResponse>();
        var context = body!.ContextSent ?? "";
        Assert.Contains($"Headline: {OwnHeadline}", context, StringComparison.Ordinal);
        Assert.NotNull(_lastCreatePrePrompt);
        Assert.Contains(OwnHeadline, _lastCreatePrePrompt!, StringComparison.Ordinal);
    }

    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        switch (cmd.Verb)
        {
            case "interrupted-list":
                return FakeTunnelDirector.Ok(new[] { Journal() });
            case "create":
                var req = JsonSerializer.Deserialize<NewSessionRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                _lastCreatePrePrompt = req?.PrePrompt;
                return FakeTunnelDirector.Ok(new SessionDto { SessionId = "new-1", RepoPath = req?.RepoPath ?? "" });
            case "patch":
                var pr = JsonSerializer.Deserialize<SessionUpdateRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                return FakeTunnelDirector.Ok(new SessionDto { SessionId = cmd.SessionId, Name = pr?.Name });
            case "interrupted-remove":
                return FakeTunnelDirector.Ok(new { removed = true });
            default:
                return FakeTunnelDirector.Ok(new { ok = true });
        }
    }

    private static CrashJournalDto Journal() => new()
    {
        DirectorId = DeadDir,
        Pid = DeadPid,
        MachineName = "MA",
        User = "owner",
        LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        Sessions =
        {
            new CrashJournalSessionDto
            {
                SessionId = Sid, Name = "alpha work", RepoPath = "/repo/a", ClaudeSessionId = "claude-abc",
            },
        },
    };

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }
}
