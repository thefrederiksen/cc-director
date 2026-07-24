using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The production <see cref="ICarModeFleet"/>: the brain's tools reach the fleet by calling THIS
/// Gateway's own HTTP endpoints over loopback with the per-machine token, exactly the way the Web Push
/// needs-you notifier reads its own <c>/sessions</c> (GatewayHost.GetNeedsYouCountAsync). Going through
/// the real endpoints means the brain sees the identical aggregated roster - the same names, states, and
/// effective "needs you" fold - that every client sees, with zero re-implementation of the aggregation.
/// The loopback hop is to this process on 127.0.0.1, so it adds no meaningful latency to the voice loop.
///
/// Phase 2 is read-only. Phase 3 adds the act tools (message / start / approve) and the confirmed
/// destructive tools (delete) as more methods that POST/DELETE the same endpoints the buttons call.
/// </summary>
public sealed class LoopbackCarModeFleet : ICarModeFleet
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long a roster read is reused before a fresh loopback GET. A single Car Mode turn often
    ///  reads the roster more than once (list_sessions, then resolve a target), and several turns come in
    ///  quick succession; a tiny cache collapses those into one aggregation without ever showing stale
    ///  state a person would notice (Car Mode performance round).</summary>
    private static readonly TimeSpan RosterCacheTtl = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly Action<string> _log;

    // The short-lived roster cache (2s TTL). Guarded by _rosterLock; a hit returns the cached list, a miss
    // does one loopback GET and refills. Kept deliberately tiny so nothing a person would notice goes stale.
    private readonly object _rosterLock = new();
    private IReadOnlyList<SessionDto>? _rosterCache;
    private DateTime _rosterCachedAtUtc = DateTime.MinValue;

    /// <summary>The one loopback client every fleet instance shares. Instances are now created per turn
    ///  (one per caller credential - issue #2129), so each newing up its own HttpClient would leak
    ///  sockets; the client is stateless, so sharing is safe.</summary>
    private static readonly HttpClient SharedLoopbackClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <param name="port">This Gateway's own listening port (loopback).</param>
    /// <param name="token">The credential the loopback calls authenticate WITH - normally the CALLING
    ///  device's own authenticated credential (issue #2129), so on the hosted Gateway every read and act
    ///  resolves to the caller's own tenant exactly as it would for any client. Self-host with the auth
    ///  gate off has no caller credential; the factory in GatewayHost passes the machine token for that
    ///  one case (self-host is single-tenant Local, so no isolation is at stake).</param>
    /// <param name="http">HTTP client; the shared loopback client when null.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public LoopbackCarModeFleet(int port, string token, HttpClient? http = null, Action<string>? log = null)
    {
        _http = http ?? SharedLoopbackClient;
        _baseUrl = $"http://127.0.0.1:{port}";
        _token = token ?? "";
        _log = log ?? FileLog.Write;
    }

    public async Task<IReadOnlyList<CarModeSessionInfo>> ListSessionsAsync(CancellationToken ct)
    {
        var sessions = await GetSessionsAsync(ct);
        // Newest-created first so "the latest one" is a stable, obvious reference (index 0).
        var ordered = sessions
            .OrderByDescending(s => s.CreatedAt)
            .Select(ToInfo)
            .ToList();
        _log($"[CarModeFleet] list -> {ordered.Count} sessions ({ordered.Count(i => i.NeedsYou)} need you)");
        return ordered;
    }

    public async Task<CarModeActivity?> GetSessionActivityAsync(string sessionReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionReference))
            throw new ArgumentException("A session reference is required.", nameof(sessionReference));

        var sessions = await GetSessionsAsync(ct);
        var match = ResolveSession(sessions, sessionReference);
        if (match is null)
        {
            _log($"[CarModeFleet] activity: no session matched \"{sessionReference}\"");
            return null;
        }
        var info = ToInfo(match);
        return new CarModeActivity
        {
            SessionId = info.SessionId,
            Name = info.Name,
            Repo = info.Repo,
            State = info.State,
            Summary = info.Summary,
            NeedsYou = info.NeedsYou,
        };
    }

    public async Task<CarModeSessionInfo?> ResolveSessionAsync(string sessionReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionReference))
            throw new ArgumentException("A session reference is required.", nameof(sessionReference));
        var sessions = await GetSessionsAsync(ct);
        var match = ResolveSession(sessions, sessionReference);
        return match is null ? null : ToInfo(match);
    }

    public async Task<string> StartSessionAsync(string repo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("A repository name is required.", nameof(repo));
        var wanted = repo.Trim();

        // Find a machine that knows a repository by this leaf name, then create the session there the
        // same way the "+ New session" button does (POST /directors/{id}/sessions). No repository of that
        // name anywhere is a clear, spoken failure - never a silent guess at some other repo.
        var directors = await GetJsonArrayAsync("/directors", ct);
        foreach (var d in directors)
        {
            var directorId = GetString(d, "directorId");
            var machineName = GetString(d, "machineName");
            if (string.IsNullOrWhiteSpace(directorId)) continue;

            var repos = await GetJsonArrayAsync($"/directors/{Uri.EscapeDataString(directorId)}/repos", ct);
            foreach (var r in repos)
            {
                var path = GetString(r, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var leaf = RepoLeaf(path);
                if (!string.Equals(leaf, wanted, StringComparison.OrdinalIgnoreCase)
                    && !leaf.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                var created = await CreateSessionAsync(directorId, path, ct);
                var createdName = string.IsNullOrWhiteSpace(created?.Name) ? "a new session" : created!.Name!.Trim();
                _log($"[CarModeFleet] started session in {leaf} on {machineName}");
                return $"Started {createdName} in the {leaf} repository on {machineName}.";
            }
        }
        throw new InvalidOperationException($"I couldn't find a repository called \"{wanted}\" on any machine.");
    }

    public async Task MessageSessionAsync(string sessionId, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A message is required.", nameof(message));
        await PostJsonAsync($"/sessions/{Uri.EscapeDataString(sessionId)}/prompt", new { text = message, appendEnter = true }, ct);
        _log($"[CarModeFleet] messaged session {sessionId}");
    }

    public async Task ApproveSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        // Press Enter to accept the highlighted default, exactly the Enter button's payload.
        await PostJsonAsync($"/sessions/{Uri.EscapeDataString(sessionId)}/prompt", new { text = "\r", appendEnter = false }, ct);
        _log($"[CarModeFleet] approved (Enter) session {sessionId}");
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/sessions/{Uri.EscapeDataString(sessionId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The delete-session call failed: {(int)response.StatusCode} {response.StatusCode}.");
        _log($"[CarModeFleet] deleted session {sessionId}");
    }

    public async Task<CarModeExplain> ExplainSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        // POST /sessions/{id}/wingman/explain with no body, exactly as the Voice screen's onSwitchOn does.
        // The Gateway reads the session's latest completed turn and returns { reply, spoken, nothingYet }.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions/{Uri.EscapeDataString(sessionId)}/wingman/explain");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The wingman explain call failed: {(int)response.StatusCode} {response.StatusCode}.");
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var spoken = GetString(root, "spoken");
        var nothingYet = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("nothingYet", out var n) && n.ValueKind == JsonValueKind.True;
        _log($"[CarModeFleet] explained session {sessionId} (nothingYet={nothingYet})");
        return new CarModeExplain(spoken, nothingYet);
    }

    public async Task SwitchVoiceModeAsync(string sessionId, bool enabled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        await PostJsonAsync($"/sessions/{Uri.EscapeDataString(sessionId)}/voice-mode", new { enabled }, ct);
        _log($"[CarModeFleet] set voice-mode {enabled} on session {sessionId}");
    }

    public async Task SnoozeSessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        // Snooze IS the hold: the Gateway's /hold handler records the Gateway-owned snooze-until timer (from
        // the per-user default length) when onHold is true, so the session returns to "needs you" on its own.
        await PostJsonAsync($"/sessions/{Uri.EscapeDataString(sessionId)}/hold", new { onHold = true }, ct);
        _log($"[CarModeFleet] snoozed session {sessionId}");
    }

    public async Task<CarModeCredits> GetCreditsAsync(CancellationToken ct)
    {
        // Issue #2129: /account/credits reads the MACHINE's one stored account credential, which has no
        // per-tenant meaning on the hosted Gateway (absent by design there; were it present, every tenant
        // would be shown the same account's balance). Refuse with a relayable fact instead of either lie.
        if (GatewayHostedMode.IsHosted)
            throw new CarModeToolUnavailableException(
                "The credit balance is not available per account on the hosted Gateway yet. Sessions, machines, and schedules all still work.");
        // The SAME token-free proxy the Settings account section reads (GET /account/credits). A signed-out
        // Gateway answers signedIn=false explicitly; a cloud failure is a non-success status and throws loud.
        var root = await GetJsonObjectAsync("/account/credits", ct);
        var signedIn = root.TryGetProperty("signedIn", out var si) && si.ValueKind == JsonValueKind.True;
        var balance = GetInt64OrNull(root, "balanceMicros");
        var lastDebit = GetInt64OrNull(root, "lastDebitMicros");
        // Codex review finding 6: a signed-in response with no balance is a MALFORMED payload, and turning
        // it into a zero-dollar answer is exactly the plausible fabricated number the no-fallback rule
        // exists to prevent. Fail loud; the turn reports a specific contract failure instead.
        if (signedIn && balance is null)
            throw new InvalidOperationException("The /account/credits response said signedIn but carried no balanceMicros - malformed payload.");
        _log($"[CarModeFleet] credits: signedIn={signedIn}");
        return new CarModeCredits(signedIn, balance, lastDebit);
    }

    public async Task<IReadOnlyList<CarModeMachineInfo>> ListMachinesAsync(CancellationToken ct)
    {
        // Machines = the Director registry grouped by machine name (GET /directors), which serves hosted
        // per-tenant too - never the hosted-denied /machines or /launchers surfaces. Session counts come
        // from the same aggregated roster every client sees.
        var directors = await GetJsonArrayAsync("/directors", ct);
        var sessions = await GetSessionsAsync(ct);
        var machines = directors
            .Select(d => new
            {
                Machine = GetString(d, "machineName"),
                Version = GetString(d, "version"),
                LastSeen = GetDateTimeOrNull(d, "lastSeen"),
            })
            .Where(d => !string.IsNullOrWhiteSpace(d.Machine))
            .GroupBy(d => d.Machine, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CarModeMachineInfo
            {
                MachineName = g.Key,
                Version = g.Select(d => d.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "",
                LastSeenMinutesAgo = MinutesAgo(g.Max(d => d.LastSeen)),
                SessionCount = sessions.Count(s => string.Equals(s.MachineName, g.Key, StringComparison.OrdinalIgnoreCase)),
            })
            .OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _log($"[CarModeFleet] machines -> {machines.Count}");
        return machines;
    }

    public async Task<IReadOnlyList<CarModeScheduleInfo>> ListSchedulesAsync(CancellationToken ct)
    {
        // GET /cron/jobs returns { jobs: [CronJobDto...] }; map each to the compact speakable view.
        var root = await GetJsonObjectAsync("/cron/jobs", ct);
        // Codex review finding 6: a response without a jobs ARRAY is a contract failure, not an empty
        // schedule list - "you have no scheduled jobs" from a malformed body is a confident false answer.
        if (!root.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The /cron/jobs response carried no jobs array - malformed payload.");
        var jobs = arr.EnumerateArray().ToList();
        var schedules = jobs.Select(j =>
        {
            var kind = GetString(j, "scheduleKind");
            var schedule = string.Equals(kind, "oneOff", StringComparison.OrdinalIgnoreCase)
                ? $"once at {GetString(j, "runAt")}"
                : GetString(j, "cronExpression");
            var machine = j.TryGetProperty("target", out var target) ? GetString(target, "machine") : "";
            var actionSummary = "";
            if (j.TryGetProperty("action", out var action))
            {
                var repoLeaf = RepoLeaf(GetString(action, "repoPath"));
                var workList = GetString(action, "workListName");
                var seed = GetString(action, "seed");
                actionSummary = !string.IsNullOrWhiteSpace(workList)
                    ? $"drain the {workList} work list in {repoLeaf}"
                    : $"run \"{Truncate(seed, 80)}\" in {repoLeaf}";
            }
            return new CarModeScheduleInfo
            {
                Name = GetString(j, "name"),
                Enabled = j.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                Schedule = schedule,
                Machine = machine,
                ActionSummary = actionSummary,
                NextRunUtc = GetDateTimeOrNull(j, "nextRunUtc"),
                LastFiredUtc = GetDateTimeOrNull(j, "lastFiredUtc"),
                LastStatus = NullIfEmpty(GetString(j, "lastStatus")),
            };
        }).ToList();
        _log($"[CarModeFleet] schedules -> {schedules.Count}");
        return schedules;
    }

    public async Task<CarModeSpendSummary> GetSpendAsync(int days, CancellationToken ct)
    {
        // Issue #2129: the governance spend store is process-wide, so on the hosted Gateway its total would
        // AGGREGATE every tenant's spending - an aggregate no single tenant may see (partition when
        // attributable, deny when aggregate). Refuse with a relayable fact until a per-tenant read exists.
        if (GatewayHostedMode.IsHosted)
            throw new CarModeToolUnavailableException(
                "Spending totals are not available per account on the hosted Gateway yet. Sessions, machines, and schedules all still work.");
        if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days), "The spend window must be at least one day.");
        var until = DateTime.UtcNow;
        var since = until.AddDays(-days);
        var root = await GetJsonObjectAsync(
            $"/gateway/governance/hosted-ai-spend/summary?since={Uri.EscapeDataString(since.ToString("o"))}&until={Uri.EscapeDataString(until.ToString("o"))}", ct);
        // A summary with no total is a malformed response, not a zero - zero dollars is exactly the kind of
        // plausible fabricated number that ships unnoticed, so it fails loud instead.
        var total = GetInt64OrNull(root, "totalMicros")
            ?? throw new InvalidOperationException("The hosted-AI spend summary came back without a totalMicros field.");
        var summary = new CarModeSpendSummary(
            total,
            (int)(GetInt64OrNull(root, "debitCount")
                ?? throw new InvalidOperationException("The hosted-AI spend summary came back without a debitCount field.")),
            GetDateTimeOrNull(root, "sinceUtc") ?? since,
            GetDateTimeOrNull(root, "untilUtc") ?? until);
        _log($"[CarModeFleet] spend over {days}d -> {summary.DebitCount} debits");
        return summary;
    }

    /// <summary>Read THIS Gateway's aggregated roster, reusing a read taken within the last
    ///  <see cref="RosterCacheTtl"/> so one turn's several roster reads (and back-to-back turns) collapse to
    ///  a single loopback aggregation. A cache miss does one real GET and refills the cache.</summary>
    private async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct)
    {
        lock (_rosterLock)
        {
            if (_rosterCache is not null && DateTime.UtcNow - _rosterCachedAtUtc < RosterCacheTtl)
                return _rosterCache;
        }

        var fresh = await GetSessionsFreshAsync(ct);

        lock (_rosterLock)
        {
            _rosterCache = fresh;
            _rosterCachedAtUtc = DateTime.UtcNow;
        }
        return fresh;
    }

    /// <summary>One real loopback read of the aggregated roster. A non-success status throws with the code
    ///  named (no-fallback) so the brain surfaces a loud, specific failure instead of an empty list.</summary>
    private async Task<IReadOnlyList<SessionDto>> GetSessionsFreshAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The fleet roster call failed: {(int)response.StatusCode} {response.StatusCode}.");
        var json = await response.Content.ReadAsStringAsync(ct);
        var sessions = JsonSerializer.Deserialize<List<SessionDto>>(json, JsonOptions);
        return sessions ?? new List<SessionDto>();
    }

    /// <summary>GET a loopback endpoint that returns a JSON object and hand back its root element (a clone,
    ///  safe after the document is disposed). Throws on a non-success status or a non-object body, so a
    ///  malformed response is a loud failure rather than a silently empty read.</summary>
    private async Task<JsonElement> GetJsonObjectAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {path} failed: {(int)response.StatusCode} {response.StatusCode}.");
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"GET {path} returned {doc.RootElement.ValueKind}, expected a JSON object.");
        return doc.RootElement.Clone();
    }

    private static long? GetInt64OrNull(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt64()
            : null;

    private static DateTime? GetDateTimeOrNull(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            && p.TryGetDateTime(out var t)
            ? t
            : null;

    private static int? MinutesAgo(DateTime? utc)
    {
        if (utc is not { } t) return null;
        var minutes = (DateTime.UtcNow - t.ToUniversalTime()).TotalMinutes;
        return minutes <= 0 ? 0 : (int)Math.Round(minutes);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "...";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>GET a loopback endpoint that returns a JSON array and hand back its elements. Throws on a
    ///  non-success status AND on a non-array body (Codex review finding 6): every caller's endpoint
    ///  contract is a JSON array, so a non-array body is a malformed response - treating it as an empty
    ///  list would turn a contract failure into a confident "there are none".</summary>
    private async Task<IReadOnlyList<JsonElement>> GetJsonArrayAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {path} failed: {(int)response.StatusCode} {response.StatusCode}.");
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"GET {path} returned {doc.RootElement.ValueKind}, expected a JSON array.");
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>POST a JSON body to a loopback endpoint with the Bearer, throwing on a non-success status
    ///  so the brain surfaces a loud, specific failure.</summary>
    private async Task PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} failed: {(int)response.StatusCode} {response.StatusCode}.");
    }

    /// <summary>Create a session on a Director (POST /directors/{id}/sessions), the same call the
    ///  "+ New session" button makes, and return the created session.</summary>
    private async Task<SessionDto?> CreateSessionAsync(string directorId, string repoPath, CancellationToken ct)
    {
        var body = new { repoPath = repoPath.Trim(), agent = "ClaudeCode", wingmanEnabled = false };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/directors/{Uri.EscapeDataString(directorId)}/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Creating the session failed: {(int)response.StatusCode} {response.StatusCode}. {detail}");
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SessionDto>(json, JsonOptions);
    }

    private static string GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    /// <summary>Resolve a fuzzy reference to one session: exact number, then exact name, then a NAME match,
    ///  then (only when nothing matched by name) a REPO match, newest first. Static and pure so it is
    ///  unit-tested directly.
    ///
    ///  Two safety properties matter here, both learned the hard way (a wrong-session act during Car Mode QA):
    ///  1. The reference is punctuation-normalized first. The model echoes its own spoken narration back as the
    ///     tool argument - for example "Car Mode Demo, in the devthrottle repo" - and the comma must not stop
    ///     "demo," from matching the name word "demo".
    ///  2. A NAME match ALWAYS beats a REPO match. That same "Name, in the repo repo" phrase contains the repo
    ///     word ("devthrottle"), which every session in that repo shares; without name-priority the reference
    ///     would spuriously match an arbitrary other same-repo session (the newest one) instead of the session
    ///     actually named. Repo matching is only a fallback for a reference that names no session at all
    ///     (for example "the devthrottle session").</summary>
    internal static SessionDto? ResolveSession(IReadOnlyList<SessionDto> sessions, string reference)
    {
        var reff = NormalizeRef(reference);
        if (reff.Length == 0) return null;

        // A number reference ("session 104", "one hundred four" already digitized by the model).
        var digits = new string(reff.Where(char.IsDigit).ToArray());
        if (digits.Length > 0 && int.TryParse(digits, out var num))
        {
            var byNumber = sessions.FirstOrDefault(s => s.Number == num);
            if (byNumber is not null) return byNumber;
        }

        // Exact (normalized) name.
        var exact = sessions.FirstOrDefault(s => NormalizeRef(s.Name ?? "") == reff);
        if (exact is not null) return exact;

        // A session matches by NAME when the reference is a substring of its name (a spoken fragment) or its
        // name appears as whole words inside the reference (the name said within a longer phrase). A NAME
        // match wins outright; only if no session matches by name do we fall back to the newest session whose
        // REPO leaf appears as whole words in the reference. Newest first so a tie picks the latest.
        SessionDto? nameMatch = null;
        SessionDto? repoMatch = null;
        foreach (var s in sessions.OrderByDescending(s => s.CreatedAt))
        {
            var name = NormalizeRef(s.Name ?? "");
            var repo = NormalizeRef(RepoLeaf(s.RepoPath));
            if (name.Length > 0 && (name.Contains(reff) || ContainsAsWords(reff, name)))
            {
                nameMatch = s;
                break; // a name match always wins; the newest matching name is the answer
            }
            if (repoMatch is null && repo.Length > 0 && ContainsAsWords(reff, repo))
                repoMatch = s;
        }
        return nameMatch ?? repoMatch;
    }

    /// <summary>Lower-case and collapse a reference (or a name/repo) to space-separated alphanumeric words,
    ///  dropping ALL punctuation. So "Car Mode Demo, in the devthrottle repo" and "Car Mode - Manager" become
    ///  clean word runs that <see cref="ContainsAsWords"/> can compare without a stray comma or dash breaking a
    ///  whole-word match.</summary>
    internal static string NormalizeRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>True when <paramref name="needle"/>'s words appear as a contiguous run of whole words in
    ///  <paramref name="haystack"/>. Whole-word, so "one" matches "the one repo" but not "nonexistent".</summary>
    internal static bool ContainsAsWords(string haystack, string needle)
    {
        var hay = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var need = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (need.Length == 0 || need.Length > hay.Length) return false;
        for (var i = 0; i + need.Length <= hay.Length; i++)
        {
            var all = true;
            for (var j = 0; j < need.Length; j++)
            {
                if (!string.Equals(hay[i + j], need[j], StringComparison.Ordinal)) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    private static CarModeSessionInfo ToInfo(SessionDto s)
    {
        var needsYou = string.Equals(s.TriageBucket, "needsYou", StringComparison.OrdinalIgnoreCase);
        var summary = !string.IsNullOrWhiteSpace(s.RailLine) ? s.RailLine!
            : !string.IsNullOrWhiteSpace(s.LastStatusReason) ? s.LastStatusReason
            : "";
        return new CarModeSessionInfo
        {
            SessionId = s.SessionId,
            Name = string.IsNullOrWhiteSpace(s.Name) ? "(unnamed session)" : s.Name!.Trim(),
            Number = s.Number,
            Repo = RepoLeaf(s.RepoPath),
            MachineName = s.MachineName ?? "",
            MissionName = string.IsNullOrWhiteSpace(s.MissionName) ? null : s.MissionName,
            // GAP 6: Car Mode SPEAKS the fold's label, and nothing else. This used to read
            // `StateLabel ?? (EffectiveColor ?? StatusColor)` - a fallback chain that ended by speaking the
            // DIRECTOR'S COOKED COLOUR out loud, which is illegal twice over: this repository forbids
            // fallback programming outright, and law 2 forbids a client rendering a colour the Director
            // decided. It was the last presentation reader of the cooked colour anywhere in the product.
            //
            // The chain is DELETED rather than reordered, because the hole it was catching is closed at the
            // producer: SessionOrdering.StateLabel now returns a non-empty literal on every arm (the
            // dictation arm treats a blank as absent - see DictationPhaseLabel), and the Gateway's fleet
            // pass stamps it for every session before Car Mode ever deserializes one. So there is nothing
            // left to fall back FROM, and no question about what to say when the label is blank.
            //
            // A missing label here is therefore a Gateway defect - the fold did not run - and it fails loud
            // instead of being papered over. That is the whole lesson of the chain it replaces: the old code
            // turned a Gateway bug into a plausible-sounding sentence in the owner's car, which is precisely
            // how this mission's defects stayed invisible for months.
            State = string.IsNullOrWhiteSpace(s.StateLabel)
                ? throw new InvalidOperationException(
                    $"Session {s.SessionId} reached Car Mode with no fold label. The Gateway stamps " +
                    "SessionDto.StateLabel for every session in the fleet pass, so this means the fold did " +
                    "not run - a Gateway defect. Car Mode will not invent a state or speak a raw colour.")
                : s.StateLabel,
            NeedsYou = needsYou,
            WaitingMinutes = WaitingMinutes(s.NeedsYouSince),
            Summary = summary,
            AgeHours = AgeHours(s.CreatedAt),
            IdleMinutes = s.IdleSeconds <= 0 ? 0 : (int)Math.Round(s.IdleSeconds / 60.0),
        };
    }

    /// <summary>Whole hours since a session was created; 0 when under an hour (or the clock skewed).</summary>
    private static int AgeHours(DateTime createdAt)
    {
        var hours = (DateTime.UtcNow - createdAt.ToUniversalTime()).TotalHours;
        return hours <= 0 ? 0 : (int)Math.Floor(hours);
    }

    /// <summary>The last path segment of a repository path (the human name a person calls a repo), for
    ///  both directory separators.</summary>
    internal static string RepoLeaf(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return "";
        var trimmed = repoPath.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static int WaitingMinutes(DateTime? since)
    {
        if (since is not { } t) return 0;
        var minutes = (DateTime.UtcNow - t).TotalMinutes;
        return minutes <= 0 ? 0 : (int)Math.Round(minutes);
    }
}
