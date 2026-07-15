using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable, always-available aggregate of the DevThrottle Stats input tally. Every session's
/// per-session tally (submitted turns + character volume by modality and surface) rides up the existing
/// director-stream snapshot/delta path on <see cref="SessionDto.InputStats"/>; this aggregator folds them
/// into all-time totals the private Gateway dashboard reads with no cloud round-trip.
///
/// Correct across BOTH a Director restart and a Gateway restart, with no double-counting, via a high-water
/// increment: for each live session it remembers the last per-bucket counts it saw, and adds only the
/// increase to the totals. A session that ends (RemoveSession) leaves its contribution in the totals and
/// its high-water entry is pruned. A session whose reported counts DROP (a Director restarted and the
/// session began a fresh tally) is treated as new activity from zero. Both the totals and the high-water
/// map are persisted (atomic temp-write + rename, corrupt file quarantined) so a Gateway restart neither
/// loses the totals nor re-adds live sessions' current counts.
///
/// Only counts ever pass through here - never the text of anything typed or said (mission decision 5).
/// </summary>
public sealed class GatewayInputStatsAggregator
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    // All-time totals, keyed by (modality token, surface token).
    private readonly Dictionary<(string Modality, string Surface), Counters> _totals = new();

    // Per-live-session last-seen counts, so only the INCREASE is folded into the totals.
    private readonly Dictionary<string, Dictionary<(string Modality, string Surface), Counters>> _highWater = new();

    // Per UTC clock hour: the turns (by modality) and characters SUBMITTED in that hour - the "working day"
    // series. Accumulated from the same high-water deltas as the totals, attributed to the hour the delta
    // was observed. Pruned past the retention window so the store stays bounded.
    private const string HourFormat = "yyyy-MM-ddTHH";
    private const int RetentionDays = 90;
    private readonly Dictionary<string, HourTurns> _hourly = new(StringComparer.Ordinal);

    // Wingman usage (owner's definition: a session "uses the wingman" when it has voice mode on).
    // _wingmanSessions is the all-time set of session ids ever seen with voice mode on (never pruned on
    // removal, like the totals); _wingmanTurns is the count of submitted turns folded while a session had
    // voice mode on (a subset of the all-time turn total). Both persist across restarts.
    private long _wingmanTurns;
    private readonly HashSet<string> _wingmanSessions = new(StringComparer.Ordinal);

    // Per-repository all-time tally, keyed by the session's RepoPath (working directory). Accumulated from
    // the SAME high-water deltas as the totals: when a session's counted input grows, that increase is also
    // attributed to the session's repo. Feeds the private Repos page ("where your development actually
    // happens"). All-time, like the totals - never pruned. Only counts travel; never any message text.
    private readonly Dictionary<string, RepoTally> _repos = new(StringComparer.OrdinalIgnoreCase);

    // Per-agent all-time tally, keyed by the session's Agent token (the AgentKind name: "ClaudeCode",
    // "Codex", ...). Accumulated from the SAME high-water deltas as the totals: when a session's counted
    // input grows, that increase is also attributed to the agent CLI that session drives. Feeds the private
    // Agents page ("which agent you actually drive"). All-time, like the totals - never pruned.
    private readonly Dictionary<string, AgentTally> _agents = new(StringComparer.OrdinalIgnoreCase);

    // When the per-agent tally started counting. The totals predate this breakdown, so the agent numbers do
    // NOT reconcile with them and the page must say so rather than imply the earlier turns had no agent.
    // Stamped once, the first time a Gateway runs a build that has the tally, and persisted from then on.
    private string _agentsSinceUtc = "";

    private sealed class HourTurns
    {
        public long VoiceTurns;
        public long TypedTurns;
        public long Characters;
    }

    private sealed class RepoTally
    {
        public long VoiceTurns;
        public long TypedTurns;
        public long Characters;
        // The distinct session ids that drove counted input into this repo, so "sessions" is a true
        // distinct count that never double-counts across a re-push or a Gateway restart (the set is
        // persisted and re-adding a known id is a no-op).
        public readonly HashSet<string> Sessions = new(StringComparer.Ordinal);
    }

    private sealed class AgentTally
    {
        public long VoiceTurns;
        public long TypedTurns;
        public long Characters;
        // The distinct session ids that drove counted input through this agent, so "sessions" is a true
        // distinct count across a re-push or a Gateway restart (the set is persisted; re-adding is a no-op).
        public readonly HashSet<string> Sessions = new(StringComparer.Ordinal);
    }

    private sealed class Counters
    {
        public long Turns { get; set; }
        public long Characters { get; set; }
    }

    /// <param name="path">The durable store file. Defaults to gateway-input-stats.json under the cc-director
    /// storage root, beside the other Gateway stores.</param>
    public GatewayInputStatsAggregator(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "gateway-input-stats.json")
            : path!;
        Load();
    }

    /// <summary>Fold every session in a full snapshot into the totals and the per-hour turn log.</summary>
    public void ObserveSnapshot(IEnumerable<SessionDto>? sessions, DateTime? nowUtc = null)
    {
        if (sessions is null) return;
        var now = nowUtc ?? DateTime.UtcNow;
        var hourKey = HourKey(now);
        lock (_lock)
        {
            var changed = StampAgentsSinceLocked(now);
            foreach (var s in sessions)
                changed |= FoldLocked(s, hourKey);
            if (changed) { PruneLocked(now); Save(); }
        }
    }

    /// <summary>Fold one session (a delta) into the totals and the per-hour turn log.</summary>
    public void Observe(SessionDto? session, DateTime? nowUtc = null)
    {
        if (session is null) return;
        var now = nowUtc ?? DateTime.UtcNow;
        var hourKey = HourKey(now);
        lock (_lock)
        {
            var changed = StampAgentsSinceLocked(now);
            changed |= FoldLocked(session, hourKey);
            if (changed) { PruneLocked(now); Save(); }
        }
    }

    // Stamp when the per-agent tally started counting, the first time a Gateway with the tally observes
    // anything - from either entry point, so the date does not depend on which one fired first. The all-time
    // totals predate the breakdown, so this is what lets the Agents page state which window its numbers
    // cover instead of implying the earlier turns ran under no agent. Returns true when it stamped (the
    // caller must persist it). Caller holds the lock.
    private bool StampAgentsSinceLocked(DateTime nowUtc)
    {
        if (_agentsSinceUtc.Length > 0) return false;
        _agentsSinceUtc = nowUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Forget a removed session's high-water entry. Its contribution stays in the totals (it was folded in
    /// as it happened); dropping the high-water entry just stops the map growing without bound.
    /// </summary>
    public void Forget(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock)
        {
            if (_highWater.Remove(sessionId)) Save();
        }
    }

    /// <summary>An immutable snapshot of the all-time totals for the dashboard, buckets in a stable order.</summary>
    public InputStatsDto CurrentTotals()
    {
        lock (_lock)
        {
            return ToDtoLocked(_totals);
        }
    }

    /// <summary>All-time wingman usage: the number of submitted turns folded while a session had voice mode
    /// on, and the count of distinct sessions ever seen with voice mode on.</summary>
    public WingmanUsageDto WingmanUsage()
    {
        lock (_lock)
        {
            return new WingmanUsageDto { Turns = _wingmanTurns, Sessions = _wingmanSessions.Count };
        }
    }

    /// <summary>The per-hour turn log (the "working day" series: turns by modality and character volume per
    /// UTC clock hour), oldest hour first.</summary>
    public IReadOnlyList<InputHourDto> HourlyTurns()
    {
        lock (_lock)
        {
            var list = new List<InputHourDto>(_hourly.Count);
            foreach (var kvp in _hourly)
                list.Add(new InputHourDto
                {
                    Hour = kvp.Key,
                    VoiceTurns = kvp.Value.VoiceTurns,
                    TypedTurns = kvp.Value.TypedTurns,
                    Turns = kvp.Value.VoiceTurns + kvp.Value.TypedTurns,
                    Characters = kvp.Value.Characters,
                });
            list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return list;
        }
    }

    /// <summary>The per-repository all-time tally (turns by modality, character volume, distinct sessions),
    /// ranked most-driven first, for the private Repos page. Repos with no counted turns are omitted.</summary>
    public IReadOnlyList<RepoStatBucketDto> RepoTotals()
    {
        lock (_lock)
        {
            var list = new List<RepoStatBucketDto>(_repos.Count);
            foreach (var kvp in _repos)
            {
                var t = kvp.Value;
                list.Add(new RepoStatBucketDto
                {
                    Repo = kvp.Key,
                    RepoName = RepoLeaf(kvp.Key),
                    Turns = t.VoiceTurns + t.TypedTurns,
                    VoiceTurns = t.VoiceTurns,
                    TypedTurns = t.TypedTurns,
                    Characters = t.Characters,
                    Sessions = t.Sessions.Count,
                });
            }
            // Rank by turns (the headline metric), then characters, then name, so the order is stable.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.RepoName, b.RepoName);
            });
            return list;
        }
    }

    /// <summary>The per-agent all-time tally (turns by modality, character volume, distinct sessions),
    /// ranked most-driven first, for the private Agents page. Agents with no counted turns are omitted.</summary>
    public IReadOnlyList<AgentStatBucketDto> AgentTotals()
    {
        lock (_lock)
        {
            var list = new List<AgentStatBucketDto>(_agents.Count);
            foreach (var kvp in _agents)
            {
                var t = kvp.Value;
                list.Add(new AgentStatBucketDto
                {
                    Agent = kvp.Key,
                    AgentName = AgentDisplayName(kvp.Key),
                    Turns = t.VoiceTurns + t.TypedTurns,
                    VoiceTurns = t.VoiceTurns,
                    TypedTurns = t.TypedTurns,
                    Characters = t.Characters,
                    Sessions = t.Sessions.Count,
                });
            }
            // Rank by turns (the headline metric), then characters, then name, so the order is stable.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.AgentName, b.AgentName);
            });
            return list;
        }
    }

    /// <summary>When the per-agent tally started counting (round-trip UTC), or "" if it has never been
    /// stamped. The all-time totals predate it, so the Agents page states this rather than implying the
    /// earlier turns ran under no agent.</summary>
    public string AgentsSinceUtc
    {
        get { lock (_lock) { return _agentsSinceUtc; } }
    }

    // The display name of an agent token (the AgentKind enum name the Director reports). An explicit map,
    // not a PascalCase splitter: "OpenCode" is the product's own spelling and must not become "Open Code".
    // An unrecognised token is shown verbatim - it is a real value we simply do not have a nicer name for,
    // so showing it is honest where hiding it behind "Other" would not be.
    private static string AgentDisplayName(string agent) => agent switch
    {
        "" => "(unknown)",
        "ClaudeCode" => "Claude Code",
        "RawCli" => "Raw CLI",
        _ => agent,
    };

    // The display leaf of a repository path: the last path segment of the working directory, so
    // "D:\ReposFred\devthrottle" reads as "devthrottle". Handles both slash styles across machines (the
    // Gateway aggregates repos from Windows and Unix Directors alike). Empty path reads as "(unknown)".
    private static string RepoLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryParseHour(string key, out DateTime utc) =>
        DateTime.TryParseExact(key, HourFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out utc);

    private void PruneLocked(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var stale = _hourly.Keys.Where(k => TryParseHour(k, out var dt) && dt < cutoff).ToList();
        foreach (var k in stale) _hourly.Remove(k);
    }

    // Fold one session's tally into the totals via the high-water increment, attributing each increase to
    // <paramref name="hourKey"/> in the per-hour turn log. Returns true when the totals changed. Caller
    // holds the lock.
    private bool FoldLocked(SessionDto s, string hourKey)
    {
        if (string.IsNullOrEmpty(s.SessionId))
            return false;

        var changed = false;

        // Wingman usage (owner's definition): a session "uses the wingman" the moment it is seen with voice
        // mode on - recorded even if it has no input this fold, so a voice-mode session that never typed a
        // turn still counts. The set is all-time, never pruned on removal (like the totals).
        if (s.VoiceMode && _wingmanSessions.Add(s.SessionId))
            changed = true;

        if (s.InputStats?.Buckets is null || s.InputStats.Buckets.Count == 0)
            return changed;

        if (!_highWater.TryGetValue(s.SessionId, out var hw))
        {
            hw = new Dictionary<(string, string), Counters>();
            _highWater[s.SessionId] = hw;
        }

        long sessionDeltaTurns = 0;
        foreach (var b in s.InputStats.Buckets)
        {
            var key = (b.Modality ?? "", b.Surface ?? "");
            hw.TryGetValue(key, out var prev);
            var prevTurns = prev?.Turns ?? 0;
            var prevChars = prev?.Characters ?? 0;

            // Normal case: counts only grow, so add the increase. Reset case (a Director restarted this
            // session id with a fresh tally): the reported count is LOWER than last seen, so the whole
            // current count is new activity from zero.
            var deltaTurns = b.Turns >= prevTurns ? b.Turns - prevTurns : b.Turns;
            var deltaChars = b.Characters >= prevChars ? b.Characters - prevChars : b.Characters;

            if (deltaTurns > 0 || deltaChars > 0)
            {
                if (!_totals.TryGetValue(key, out var total))
                {
                    total = new Counters();
                    _totals[key] = total;
                }
                total.Turns += deltaTurns;
                total.Characters += deltaChars;

                var isVoice = string.Equals(key.Item1, "voice", StringComparison.OrdinalIgnoreCase);

                // Attribute this increase to the hour it was observed (the "working day" series). Turns are
                // split by modality; characters are summed. Surface is not split here - the hourly log is
                // about WHEN work happened, not from where.
                if (!_hourly.TryGetValue(hourKey, out var hour))
                {
                    hour = new HourTurns();
                    _hourly[hourKey] = hour;
                }
                if (isVoice)
                    hour.VoiceTurns += deltaTurns;
                else
                    hour.TypedTurns += deltaTurns;
                hour.Characters += deltaChars;

                sessionDeltaTurns += deltaTurns;

                // Attribute the SAME increase to the session's repository (the private Repos page): which
                // codebase the work landed in. Turns split by modality, characters summed, and the session
                // id remembered so a repo's distinct-session count is honest.
                var repoKey = s.RepoPath ?? "";
                if (!_repos.TryGetValue(repoKey, out var repo))
                {
                    repo = new RepoTally();
                    _repos[repoKey] = repo;
                }
                if (isVoice)
                    repo.VoiceTurns += deltaTurns;
                else
                    repo.TypedTurns += deltaTurns;
                repo.Characters += deltaChars;
                if (!string.IsNullOrEmpty(s.SessionId))
                    repo.Sessions.Add(s.SessionId);

                // Attribute the SAME increase to the agent CLI the session drives (the private Agents page):
                // which agent the work went through. A session whose agent the Director did not report is
                // counted under the empty key and shown as "(unknown)" - never silently dropped, and never
                // guessed at from the command line.
                var agentKey = s.Agent ?? "";
                if (!_agents.TryGetValue(agentKey, out var agent))
                {
                    agent = new AgentTally();
                    _agents[agentKey] = agent;
                }
                if (isVoice)
                    agent.VoiceTurns += deltaTurns;
                else
                    agent.TypedTurns += deltaTurns;
                agent.Characters += deltaChars;
                if (!string.IsNullOrEmpty(s.SessionId))
                    agent.Sessions.Add(s.SessionId);

                changed = true;
            }

            hw[key] = new Counters { Turns = b.Turns, Characters = b.Characters };
        }

        // Turns submitted while this session had voice mode on are wingman turns (a subset of the totals).
        if (s.VoiceMode && sessionDeltaTurns > 0)
            _wingmanTurns += sessionDeltaTurns;

        return changed;
    }

    private static InputStatsDto ToDtoLocked(Dictionary<(string Modality, string Surface), Counters> src)
    {
        var dto = new InputStatsDto();
        foreach (var kvp in src.OrderBy(k => k.Key.Modality, StringComparer.Ordinal).ThenBy(k => k.Key.Surface, StringComparer.Ordinal))
        {
            dto.Buckets.Add(new InputStatBucketDto
            {
                Modality = kvp.Key.Modality,
                Surface = kvp.Key.Surface,
                Turns = kvp.Value.Turns,
                Characters = kvp.Value.Characters,
            });
        }
        return dto;
    }

    private sealed class StoreFile
    {
        public List<InputStatBucketDto> Totals { get; set; } = new();
        public Dictionary<string, List<InputStatBucketDto>> HighWater { get; set; } = new();
        public Dictionary<string, HourTurnsStore> Hourly { get; set; } = new();
        public long WingmanTurns { get; set; }
        public List<string> WingmanSessions { get; set; } = new();
        public Dictionary<string, RepoTallyStore> Repos { get; set; } = new();
        public Dictionary<string, AgentTallyStore> Agents { get; set; } = new();
        public string AgentsSinceUtc { get; set; } = "";
    }

    private sealed class HourTurnsStore
    {
        public long VoiceTurns { get; set; }
        public long TypedTurns { get; set; }
        public long Characters { get; set; }
    }

    private sealed class RepoTallyStore
    {
        public long VoiceTurns { get; set; }
        public long TypedTurns { get; set; }
        public long Characters { get; set; }
        public List<string> Sessions { get; set; } = new();
    }

    private sealed class AgentTallyStore
    {
        public long VoiceTurns { get; set; }
        public long TypedTurns { get; set; }
        public long Characters { get; set; }
        public List<string> Sessions { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[GatewayInputStatsAggregator] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }
        if (parsed is null)
        {
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        foreach (var b in parsed.Totals)
            _totals[(b.Modality ?? "", b.Surface ?? "")] = new Counters { Turns = b.Turns, Characters = b.Characters };
        foreach (var (sid, buckets) in parsed.HighWater)
        {
            var hw = new Dictionary<(string, string), Counters>();
            foreach (var b in buckets)
                hw[(b.Modality ?? "", b.Surface ?? "")] = new Counters { Turns = b.Turns, Characters = b.Characters };
            _highWater[sid] = hw;
        }
        foreach (var (hour, ht) in parsed.Hourly)
            _hourly[hour] = new HourTurns { VoiceTurns = ht.VoiceTurns, TypedTurns = ht.TypedTurns, Characters = ht.Characters };
        _wingmanTurns = parsed.WingmanTurns;
        foreach (var id in parsed.WingmanSessions)
            if (!string.IsNullOrEmpty(id)) _wingmanSessions.Add(id);
        foreach (var (repoKey, rt) in parsed.Repos)
        {
            var tally = new RepoTally { VoiceTurns = rt.VoiceTurns, TypedTurns = rt.TypedTurns, Characters = rt.Characters };
            foreach (var sid in rt.Sessions) tally.Sessions.Add(sid);
            _repos[repoKey] = tally;
        }
        foreach (var (agentKey, at) in parsed.Agents)
        {
            var tally = new AgentTally { VoiceTurns = at.VoiceTurns, TypedTurns = at.TypedTurns, Characters = at.Characters };
            foreach (var sid in at.Sessions) tally.Sessions.Add(sid);
            _agents[agentKey] = tally;
        }
        _agentsSinceUtc = parsed.AgentsSinceUtc ?? "";
        FileLog.Write($"[GatewayInputStatsAggregator] Load: restored {_totals.Count} total bucket(s), {_highWater.Count} live session(s), {_hourly.Count} hourly bucket(s), {_wingmanSessions.Count} wingman session(s)/{_wingmanTurns} wingman turn(s), {_repos.Count} repo(s), {_agents.Count} agent(s) since '{_agentsSinceUtc}' from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[GatewayInputStatsAggregator] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    // Write-through under the lock: serialize the whole store and atomically replace the file (temp +
    // rename) so a concurrent reader or a crash mid-write never sees a half-written store. A failed save is
    // a LOGGED error that propagates - never a silent skip.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Totals = ToDtoLocked(_totals).Buckets };
            foreach (var (sid, hw) in _highWater)
                file.HighWater[sid] = ToDtoLocked(hw).Buckets;
            foreach (var (hour, ht) in _hourly)
                file.Hourly[hour] = new HourTurnsStore { VoiceTurns = ht.VoiceTurns, TypedTurns = ht.TypedTurns, Characters = ht.Characters };
            file.WingmanTurns = _wingmanTurns;
            file.WingmanSessions = _wingmanSessions.ToList();
            foreach (var (repoKey, rt) in _repos)
                file.Repos[repoKey] = new RepoTallyStore
                {
                    VoiceTurns = rt.VoiceTurns,
                    TypedTurns = rt.TypedTurns,
                    Characters = rt.Characters,
                    Sessions = rt.Sessions.ToList(),
                };
            foreach (var (agentKey, at) in _agents)
                file.Agents[agentKey] = new AgentTallyStore
                {
                    VoiceTurns = at.VoiceTurns,
                    TypedTurns = at.TypedTurns,
                    Characters = at.Characters,
                    Sessions = at.Sessions.ToList(),
                };
            file.AgentsSinceUtc = _agentsSinceUtc;

            var json = JsonSerializer.Serialize(file, FileJsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayInputStatsAggregator] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}

/// <summary>One hour of the input "working day" log: the turns (total and by modality) and character
/// volume submitted in that UTC clock hour ("yyyy-MM-ddTHH").</summary>
public sealed class InputHourDto
{
    public string Hour { get; set; } = "";
    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }
}

/// <summary>All-time wingman usage (owner's definition: a session uses the wingman when it has voice mode
/// on): the count of turns submitted while a session had voice mode on, and the number of distinct sessions
/// ever seen with voice mode on.</summary>
public sealed class WingmanUsageDto
{
    public long Turns { get; set; }
    public int Sessions { get; set; }
}

/// <summary>One repository's all-time input tally for the private Repos page: how much development landed
/// in this codebase, measured in submitted TURNS (total and split voice vs typed), CHARACTER volume, and
/// the count of distinct SESSIONS that drove input into it. Only counts ever travel - never message text.</summary>
public sealed class RepoStatBucketDto
{
    /// <summary>The full repository / working-directory path the sessions ran in (the grouping key).</summary>
    public string Repo { get; set; } = "";

    /// <summary>The display leaf of <see cref="Repo"/> (its last path segment), e.g. "devthrottle".</summary>
    public string RepoName { get; set; } = "";

    /// <summary>Total submitted turns into this repo (voice + typed).</summary>
    public long Turns { get; set; }

    /// <summary>Submitted turns driven by voice.</summary>
    public long VoiceTurns { get; set; }

    /// <summary>Submitted turns driven by typing.</summary>
    public long TypedTurns { get; set; }

    /// <summary>Total character volume of input into this repo.</summary>
    public long Characters { get; set; }

    /// <summary>Distinct sessions that drove counted input into this repo.</summary>
    public int Sessions { get; set; }
}

/// <summary>One agent CLI's all-time input tally for the private Agents page: how much development you
/// drive through this agent, measured in submitted TURNS (total and split voice vs typed), CHARACTER
/// volume, and the count of distinct SESSIONS that drove input through it. Only counts ever travel - never
/// message text. The tally starts when the breakdown shipped, so it does not reconcile with the all-time
/// totals; see <see cref="GatewayInputStatsAggregator.AgentsSinceUtc"/>.</summary>
public sealed class AgentStatBucketDto
{
    /// <summary>The agent token the Director reported (the AgentKind name: "ClaudeCode", "Codex", ...),
    /// or "" when the session carried no agent (the grouping key).</summary>
    public string Agent { get; set; } = "";

    /// <summary>The display name of <see cref="Agent"/>, e.g. "Claude Code"; "(unknown)" when empty.</summary>
    public string AgentName { get; set; } = "";

    /// <summary>Total submitted turns driven through this agent (voice + typed).</summary>
    public long Turns { get; set; }

    /// <summary>Submitted turns driven by voice.</summary>
    public long VoiceTurns { get; set; }

    /// <summary>Submitted turns driven by typing.</summary>
    public long TypedTurns { get; set; }

    /// <summary>Total character volume of input driven through this agent.</summary>
    public long Characters { get; set; }

    /// <summary>Distinct sessions that drove counted input through this agent.</summary>
    public int Sessions { get; set; }
}
