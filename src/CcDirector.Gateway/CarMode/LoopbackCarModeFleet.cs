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

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly Action<string> _log;

    /// <param name="port">This Gateway's own listening port (loopback).</param>
    /// <param name="token">The per-machine Gateway token, attached as the Bearer so the call works
    ///  whether or not the host-wide auth gate is on.</param>
    /// <param name="http">HTTP client; a short-timeout loopback client when null.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public LoopbackCarModeFleet(int port, string token, HttpClient? http = null, Action<string>? log = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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

    /// <summary>Read THIS Gateway's aggregated roster over loopback. A non-success status throws with the
    ///  code named (no-fallback) so the brain surfaces a loud, specific failure instead of an empty list.</summary>
    private async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct)
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

    /// <summary>Resolve a fuzzy reference to one session: exact number, then exact name, then a name/repo
    ///  substring, then the newest match. Static and pure so it is unit-tested directly.</summary>
    internal static SessionDto? ResolveSession(IReadOnlyList<SessionDto> sessions, string reference)
    {
        var reff = reference.Trim().ToLowerInvariant();
        if (reff.Length == 0) return null;

        // A number reference ("session 104", "one hundred four" already digitized by the model).
        var digits = new string(reff.Where(char.IsDigit).ToArray());
        if (digits.Length > 0 && int.TryParse(digits, out var num))
        {
            var byNumber = sessions.FirstOrDefault(s => s.Number == num);
            if (byNumber is not null) return byNumber;
        }

        // Exact (case-insensitive) name.
        var exact = sessions.FirstOrDefault(s => (s.Name ?? "").Trim().ToLowerInvariant() == reff);
        if (exact is not null) return exact;

        // Match by name or repo, newest first so a tie picks the latest. A session matches when the
        // reference is a substring of its name (the owner spoke a fragment of the name), or when its
        // name or repo appears as WHOLE WORDS inside the reference (the owner said the name/repo within
        // a longer phrase). Whole-word matching on the reverse direction avoids a short repo leaf like
        // "one" spuriously matching inside an unrelated word like "nonexistent".
        var candidates = sessions
            .OrderByDescending(s => s.CreatedAt)
            .Where(s =>
            {
                var name = (s.Name ?? "").Trim().ToLowerInvariant();
                var repo = RepoLeaf(s.RepoPath).ToLowerInvariant();
                if (name.Length > 0 && (name.Contains(reff) || ContainsAsWords(reff, name))) return true;
                if (repo.Length > 0 && ContainsAsWords(reff, repo)) return true;
                return false;
            })
            .ToList();
        return candidates.FirstOrDefault();
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
            State = string.IsNullOrWhiteSpace(s.StateLabel) ? (s.EffectiveColor ?? s.StatusColor) : s.StateLabel!,
            NeedsYou = needsYou,
            WaitingMinutes = WaitingMinutes(s.NeedsYouSince),
            Summary = summary,
        };
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
