using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable, always-available aggregate of the DevThrottle Stats input tally. Every session's
/// per-session tally (submitted turns + character volume by modality and surface) rides up the existing
/// director-stream snapshot/delta path on <see cref="SessionDto.InputStats"/>; this aggregator folds them
/// into all-time totals the private Gateway dashboard reads with no cloud round-trip.
///
/// Correct across BOTH a Director restart and a Gateway restart, with no double-counting, via a high-water
/// increment: for each live session it remembers the last per-bucket counts it saw, and adds only the
/// increase. A session that ends (Forget) leaves its contribution in the totals and its high-water entry is
/// dropped. A session whose reported counts DROP (a Director restarted and the session began a fresh tally)
/// is treated as new activity from zero.
///
/// Only counts ever pass through here - never the text of anything typed or said (mission decision 5).
///
/// STORAGE (mission "SQLite on the Gateway", Phase 1). This used to be six in-memory dictionaries persisted
/// by rewriting one JSON document in full on every counter move, on the GET /sessions request path. It is
/// now rows in gateway-stats.db. Three things follow:
///
///   1. THE OLD NUMBERS ARE NOT CARRIED ACROSS. The owner ruled it: on first run
///      gateway-input-stats.json is renamed aside UNREAD and this store starts empty. There is no import,
///      no baseline and no parity check. Renamed, never deleted - throwing the data away was his call;
///      destroying it irreversibly was not. Consequence he accepted: session_highwater starts empty, so the
///      first roster poll folds each live session's whole current tally as fresh activity and day one lands
///      with a lump rather than at zero. Then it counts normally.
///   2. AGGREGATES ARE QUERIES, NEVER CACHED. Every total, count and sum below is a real query. Caching one
///      would rebuild the in-memory-dictionaries-plus-a-file design this mission exists to delete.
///   3. THE MIRROR HOLDS MEMBERSHIP AND IDENTITY, NEVER A TALLY (Decision 6). The high-water maps, the
///      distinct-session sets and the repository/agent identity maps are mirrored in memory, populated FROM
///      the database at startup and never the reverse. They answer exactly one question - "have I already
///      recorded this?" - which is what keeps an IDLE roster poll at zero writes, exactly as it is today.
///      The mirror only advances AFTER the write commits, so a failed write is a loud failure retried on the
///      next poll, never a silent mirror-only success.
/// </summary>
public sealed class GatewayInputStatsAggregator : IDisposable
{
    private const string HourFormat = "yyyy-MM-ddTHH";
    private const int RetentionDays = 90;
    private const string AgentsSinceKey = "agents_since_utc";

    private readonly GatewayStatsDatabase _db;
    private readonly bool _ownsDatabase;
    private readonly object _lock = new();

    // ---- The mirror. Membership and identity ONLY - never a tally (Decision 6). ----

    private readonly Dictionary<string, Dictionary<(string Modality, string Surface), Counters>> _highWater = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Counters> _agentDrivenHighWater = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wingmanSessions = new(StringComparer.Ordinal);

    // The last cumulative token spend seen per session (issue #1637), so only the INCREASE folds - the same
    // high-water discipline as _highWater, one dimension over. A dropped count is a restart and folds fresh
    // from zero.
    private readonly Dictionary<string, TokenCounters> _tokenHighWater = new(StringComparer.Ordinal);

    // Sessions whose already-counted turns have been attributed to their agent (issue #1633). MUST persist:
    // session_highwater survives a restart, so without this the back-fill would run a second time against a
    // non-empty high-water and double every agent's numbers.
    private readonly HashSet<string> _agentsSeeded = new(StringComparer.Ordinal);

    // Display spelling -> surrogate id. THE COMPARER HERE IS THE WHOLE REASON THE SCHEMA USES SURROGATE IDS:
    // it is the same StringComparer.OrdinalIgnoreCase the old dictionaries used, so grouping is decided by
    // the identical object with identical semantics and SQLite is never asked to compare a repository,
    // agent or model string. First-seen-wins on the display spelling, which is what a Dictionary does.
    private readonly Dictionary<string, long> _repoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _agentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _modelIds = new(StringComparer.OrdinalIgnoreCase);
    // The checkout (local working directory) dimension retained beside the repository (issue: group the
    // Repos page by GitHub repository). repo_id is now the GitHub slug, so worktrees and per-machine clones
    // of one repository share a repo_id; checkout_id keeps the path each turn actually ran in so it is not
    // lost. Same first-seen-wins OrdinalIgnoreCase identity as the others.
    private readonly Dictionary<string, long> _checkoutIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _repoDisplay = new();
    private readonly Dictionary<long, string> _agentDisplay = new();
    private readonly Dictionary<long, string> _modelDisplay = new();
    private readonly Dictionary<long, string> _checkoutDisplay = new();

    private readonly HashSet<(long Id, string SessionId)> _repoSessions = new();
    private readonly HashSet<(long Id, string SessionId)> _agentSessions = new();

    private string _agentsSinceUtc = "";
    private string _modelsSinceUtc = "";

    /// <summary>Every statement executed against the database. The seam acceptance criterion 3 measures: an
    /// IDLE poll must not move this at all, and a fold must move it by an amount bounded by what CHANGED,
    /// never by how much history is stored.</summary>
    internal long StatementsExecuted { get; private set; }

    private sealed class Counters
    {
        public long Turns { get; set; }
        public long Characters { get; set; }
    }

    /// <summary>The four CUMULATIVE, additive token spend counts a session carries. Context occupancy is not
    /// here: it is a gauge, not spend, and never enters this arithmetic.</summary>
    private sealed class TokenCounters
    {
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation { get; set; }
    }

    /// <summary>
    /// Which identity table a display spelling belongs to. This replaced a <c>bool isRepo</c> when the model
    /// dimension arrived and made the question three-valued: a boolean cannot name a third kind, and the
    /// alternative - a second parallel set of NeedIdentity/Resolve methods for models - would have
    /// duplicated the batch-level OrdinalIgnoreCase dedup, which is the subtle part.
    ///
    /// <see cref="Model"/> is a first-class kind here but has NO distinct-session set: nothing asks how many
    /// sessions ran a model, so <see cref="SessionsFor"/> refuses it rather than carrying a set nothing
    /// populates. It is also the only kind that can be ABSENT - see <see cref="FoldLocked"/>.
    ///
    /// <see cref="Checkout"/> is the local working-directory path retained beside the repository slug. Like
    /// <see cref="Model"/> it keeps no distinct-session set (the session count the Repos page shows is per
    /// repository, not per checkout), so <see cref="SessionsFor"/> refuses it too. Unlike Model it is never
    /// absent - a session always has a working directory.
    /// </summary>
    private enum IdentityKind { Repo, Agent, Model, Checkout }

    private Dictionary<string, long> IdsFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoIds,
        IdentityKind.Agent => _agentIds,
        IdentityKind.Model => _modelIds,
        IdentityKind.Checkout => _checkoutIds,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity kind."),
    };

    private Dictionary<long, string> DisplayFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoDisplay,
        IdentityKind.Agent => _agentDisplay,
        IdentityKind.Model => _modelDisplay,
        IdentityKind.Checkout => _checkoutDisplay,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity kind."),
    };

    // The distinct-session sets exist for repositories and agents only. A model and a checkout have none,
    // deliberately, so asking for one is a programming error and says so rather than inventing an empty answer.
    private HashSet<(long Id, string SessionId)> SessionsFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoSessions,
        IdentityKind.Agent => _agentSessions,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "Only repositories and agents keep distinct-session sets."),
    };

    private static (string Table, string Column) IdentityTableFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => ("repo_identity", "repo_display"),
        IdentityKind.Agent => ("agent_identity", "agent_display"),
        IdentityKind.Model => ("model_identity", "model_display"),
        IdentityKind.Checkout => ("checkout_identity", "checkout_display"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity kind."),
    };

    /// <param name="path">The statistics database. Defaults to gateway-stats.db under the cc-director
    /// storage root, beside the store it replaces.</param>
    public GatewayInputStatsAggregator(string? path = null)
        : this(new GatewayStatsDatabase(path), ownsDatabase: true)
    {
    }

    /// <param name="database">An already-open database. The caller keeps ownership - Phase 2 puts the
    /// concurrency store on this same file.</param>
    public GatewayInputStatsAggregator(GatewayStatsDatabase database)
        : this(database, ownsDatabase: false)
    {
    }

    private GatewayInputStatsAggregator(GatewayStatsDatabase database, bool ownsDatabase)
    {
        _db = database;
        _ownsDatabase = ownsDatabase;
        RetireLegacyJsonStore();
        LoadMirror();
    }

    // The owner's ruling: the old numbers are not carried across. The document is renamed aside WITHOUT
    // being read - no parse, no import, no parity. Renamed and never deleted: throwing the data away was his
    // call, destroying it irreversibly was not, and a rename costs nothing and keeps the door open.
    //
    // Self-idempotent by nature: after the rename the file is gone, so there is nothing to mark done and no
    // marker to strand.
    private void RetireLegacyJsonStore()
    {
        var legacy = Path.Combine(Path.GetDirectoryName(_db.Path) ?? CcStorage.Root(), "gateway-input-stats.json");
        if (!File.Exists(legacy)) return;

        var aside = legacy + ".retired-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(legacy, aside);
        FileLog.Write($"[GatewayInputStatsAggregator] RetireLegacyJsonStore: the old numbers are not carried " +
                      $"across (owner ruling); renamed {legacy} to {aside} UNREAD; starting empty");
    }

    // Populate the mirror FROM the database - never the reverse. Once, at startup.
    private void LoadMirror()
    {
        lock (_lock)
        {
            Read("SELECT session_id, modality, surface, turns, chars FROM session_highwater", r =>
            {
                var sid = r.GetString(0);
                if (!_highWater.TryGetValue(sid, out var hw))
                {
                    hw = new Dictionary<(string, string), Counters>();
                    _highWater[sid] = hw;
                }
                hw[(r.GetString(1), r.GetString(2))] = new Counters { Turns = r.GetInt64(3), Characters = r.GetInt64(4) };
            });
            Read("SELECT session_id, turns, chars FROM agent_driven_highwater",
                r => _agentDrivenHighWater[r.GetString(0)] = new Counters { Turns = r.GetInt64(1), Characters = r.GetInt64(2) });
            Read("SELECT session_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens FROM token_highwater",
                r => _tokenHighWater[r.GetString(0)] = new TokenCounters
                {
                    Input = r.GetInt64(1), Output = r.GetInt64(2), CacheRead = r.GetInt64(3), CacheCreation = r.GetInt64(4),
                });
            Read("SELECT session_id FROM wingman_session", r => _wingmanSessions.Add(r.GetString(0)));
            Read("SELECT session_id FROM agents_seeded", r => _agentsSeeded.Add(r.GetString(0)));
            Read("SELECT repo_id, repo_display FROM repo_identity", r =>
            {
                var id = r.GetInt64(0); var d = r.GetString(1);
                _repoIds[d] = id; _repoDisplay[id] = d;
            });
            Read("SELECT agent_id, agent_display FROM agent_identity", r =>
            {
                var id = r.GetInt64(0); var d = r.GetString(1);
                _agentIds[d] = id; _agentDisplay[id] = d;
            });
            Read("SELECT model_id, model_display FROM model_identity", r =>
            {
                var id = r.GetInt64(0); var d = r.GetString(1);
                _modelIds[d] = id; _modelDisplay[id] = d;
            });
            Read("SELECT checkout_id, checkout_display FROM checkout_identity", r =>
            {
                var id = r.GetInt64(0); var d = r.GetString(1);
                _checkoutIds[d] = id; _checkoutDisplay[id] = d;
            });
            Read("SELECT repo_id, session_id FROM repo_session", r => _repoSessions.Add((r.GetInt64(0), r.GetString(1))));
            Read("SELECT agent_id, session_id FROM agent_session", r => _agentSessions.Add((r.GetInt64(0), r.GetString(1))));
            _agentsSinceUtc = ReadScalarString("SELECT value FROM meta WHERE name=$n", ("$n", AgentsSinceKey)) ?? "";

            // Written by the version 2 migration, so it is present on every database this build can open -
            // the migration runs before this does. Read into the mirror because the stats surface reports it
            // on every request and it never changes at runtime.
            _modelsSinceUtc = ReadScalarString("SELECT value FROM meta WHERE name=$n",
                ("$n", GatewayStatsDatabase.ModelsSinceKey)) ?? "";

            FileLog.Write($"[GatewayInputStatsAggregator] LoadMirror: {_highWater.Count} live session(s), " +
                          $"{_wingmanSessions.Count} wingman session(s), {_repoIds.Count} repo(s), {_agentIds.Count} agent(s), " +
                          $"{_modelIds.Count} model(s), {_checkoutIds.Count} checkout(s), {_agentsSeeded.Count} seeded, agentsSince='{_agentsSinceUtc}', " +
                          $"modelsSince='{_modelsSinceUtc}' from {_db.Path}");
        }
    }

    /// <summary>Fold every session in a full snapshot into the totals and the per-hour turn log.</summary>
    public void ObserveSnapshot(IEnumerable<SessionDto>? sessions, DateTime? nowUtc = null)
    {
        if (sessions is null) return;
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            var batch = new FoldBatch(now, HourKey(now));
            StampAgentsSinceLocked(batch);
            foreach (var s in sessions) FoldLocked(s, batch);
            CommitLocked(batch);
        }
    }

    /// <summary>Fold one session (a delta) into the totals and the per-hour turn log.</summary>
    public void Observe(SessionDto? session, DateTime? nowUtc = null)
    {
        if (session is null) return;
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            var batch = new FoldBatch(now, HourKey(now));
            StampAgentsSinceLocked(batch);
            FoldLocked(session, batch);
            CommitLocked(batch);
        }
    }

    /// <summary>
    /// Everything one observation wants to write, collected before ANY of it is written, so the mirror is
    /// advanced only after the commit succeeds. Mutating the mirror as we go would mean a failed write
    /// leaves the mirror believing a delta was recorded that is not on disk - it would never be folded
    /// again, and the loss would be silent.
    /// </summary>
    private sealed class FoldBatch
    {
        public FoldBatch(DateTime nowUtc, string hourKey) { NowUtc = nowUtc; HourKey = hourKey; }
        public DateTime NowUtc { get; }
        public string HourKey { get; }

        // Model is the only nullable member of a row: null means the owning Director had recorded no model
        // for that session when the turn folded, which is the honest state and never a lookup failure.
        // Repo is the GitHub slug (the grouping key); Checkout is the local working directory the turn ran in,
        // retained beside it so the path is not lost when worktrees and clones collapse into one repo row.
        public readonly List<(string Hour, string SessionId, string Modality, string Surface, bool IsVoice, string Repo, string Checkout, string? Model, bool Wingman, long Turns, long Chars)> Rows = new();
        public readonly List<(string Agent, bool IsVoice, long Turns, long Chars)> AgentRows = new();
        public readonly List<(string Agent, long Turns, long Chars)> AgentDrivenRows = new();
        public readonly List<(string SessionId, string Modality, string Surface, long Turns, long Chars)> HighWater = new();
        public readonly List<(string SessionId, long Turns, long Chars)> AgentDrivenHighWater = new();
        public readonly List<string> NewWingmanSessions = new();
        public readonly List<string> NewSeeded = new();
        public readonly List<(string Display, IdentityKind Kind)> NewIdentities = new();
        public readonly List<(string Display, string SessionId, IdentityKind Kind)> NewIdentitySessions = new();
        public string? StampAgentsSince;

        // Token spend (issue #1637). Model is nullable for the same reason it is on Rows: the spend
        // attributes to the model the session was recorded running, which is null until its records name one.
        public readonly List<(string Hour, string? Model, long Input, long Output, long CacheRead, long CacheCreation)> TokenRows = new();
        public readonly List<(string SessionId, long Input, long Output, long CacheRead, long CacheCreation)> TokenHighWater = new();

        public bool IsEmpty => Rows.Count == 0 && AgentRows.Count == 0 && AgentDrivenRows.Count == 0
            && HighWater.Count == 0 && AgentDrivenHighWater.Count == 0 && NewWingmanSessions.Count == 0
            && NewSeeded.Count == 0 && NewIdentities.Count == 0 && NewIdentitySessions.Count == 0
            && TokenRows.Count == 0 && TokenHighWater.Count == 0
            && StampAgentsSince is null;
    }

    private void StampAgentsSinceLocked(FoldBatch batch)
    {
        if (_agentsSinceUtc.Length > 0) return;
        batch.StampAgentsSince = batch.NowUtc.ToUniversalTime()
            .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Fold one session's tally via the high-water increment. Caller holds the lock.
    //
    // THE CORRECTNESS CORE OF THE MISSION, SEMANTICS UNCHANGED from GatewayInputStatsAggregator on
    // origin/main. It is what makes re-reading a roster safe and what lets counts survive a restart without
    // double-counting. Simplifying it breaks the mission. The ORDER below matters and mirrors the original
    // exactly - in particular the wingman registration and the agent-driven fold both happen BEFORE the
    // empty-buckets return.
    private void FoldLocked(SessionDto s, FoldBatch batch)
    {
        if (string.IsNullOrEmpty(s.SessionId)) return;

        // Read VoiceMode ONCE and pass it down. Wingman is a property of the session's MODE for this whole
        // observation, not of any bucket, so it is constant across every row this fold writes. Reading it
        // once means no row can carry a different flag than its siblings and - the failure that actually
        // matters - the emitting code has no path to derive the flag from a bucket's own modality. A turn
        // TYPED while voice mode is on IS a wingman turn.
        var wingman = s.VoiceMode;

        // A session "uses the wingman" the moment it is seen with voice mode on - recorded even with no
        // input this fold. BEFORE the empty-buckets return, deliberately; the membership mirror is what
        // keeps it free on an idle poll.
        if (wingman && !_wingmanSessions.Contains(s.SessionId) && !batch.NewWingmanSessions.Contains(s.SessionId))
            batch.NewWingmanSessions.Add(s.SessionId);

        // Issue #1636: turns other agents drove into this session, on their own lane. Folded BEFORE the
        // buckets guard, because a session driven only by other agents has no human buckets at all - and
        // those are exactly the sessions this tally is about.
        FoldAgentDrivenLocked(s, batch);

        // Issue #1637: token spend, on its own lane. Folded BEFORE the buckets guard too, and for a reason
        // that is not obvious: tokens grow at turn-END while the input buckets grow at turn SUBMISSION, so a
        // poll can see this session's spend rise with no NEW bucket delta this interval (the turn was counted
        // last poll). Gating the token fold on a bucket delta would drop exactly those increases.
        FoldTokensLocked(s, batch);

        if (s.InputStats?.Buckets is null || s.InputStats.Buckets.Count == 0) return;

        _highWater.TryGetValue(s.SessionId, out var hw);

        // Issue #1633: the first time this session is folded, attribute what it has ALREADY counted to its
        // agent. Read before the loop below moves the high-water on. On a fresh database this contributes
        // nothing (the high-water is empty); after a RESTART it would contribute real turns, which is why
        // agents_seeded is persisted.
        if (!_agentsSeeded.Contains(s.SessionId))
        {
            batch.NewSeeded.Add(s.SessionId);
            if (hw is not null)
                foreach (var (key, prior) in hw)
                    AttributeToAgentLocked(s, key.Modality, prior.Turns, prior.Characters, batch);
        }

        // The repository grouping key is the GitHub "owner/repo" slug the owning Director resolved for this
        // checkout, so every worktree and every per-machine clone of one repository folds into a single row.
        // A checkout with no github.com origin (RepoSlug empty) falls back to its path, which still groups
        // that repo sensibly and never drops its turns.
        //
        // The checkout key is the raw working-directory path, retained as its own dimension so the store
        // still records exactly which checkout each turn ran in - the path is not lost when the slug collapses
        // the worktrees together. Path separators are deliberately NOT normalized on either key: a slug never
        // carries a separator to disagree on, and for a path the old behaviour (OrdinalIgnoreCase folds case
        // but not '/' against '\') is preserved, so a fallback path row reads exactly as it did before.
        var checkoutKey = s.RepoPath ?? "";
        var repoKey = !string.IsNullOrWhiteSpace(s.RepoSlug) ? s.RepoSlug! : checkoutKey;

        // The model this session's agent was last RECORDED using (issue #1637). Unlike the repository and
        // the agent, an unknown model is stored as SQL NULL rather than folded into an empty-string
        // identity: the producer reports null until the agent's own records name a model, so "not said" is a
        // real and permanent state for a session's first turn, and it is not a model named "".
        //
        // Whitespace is treated as absent too. The producer says null, but a display spelling of " " could
        // only ever become an identity row that renders as nothing - the same broken cell an empty string
        // would give, arrived at by a different route.
        var modelKey = string.IsNullOrWhiteSpace(s.CurrentModel) ? null : s.CurrentModel;

        foreach (var b in s.InputStats.Buckets)
        {
            var key = (b.Modality ?? "", b.Surface ?? "");
            Counters? prev = null;
            hw?.TryGetValue(key, out prev);
            var prevTurns = prev?.Turns ?? 0;
            var prevChars = prev?.Characters ?? 0;

            // Normal case: counts only grow, so add the increase. Reset case (a Director restarted this
            // session id with a fresh tally): the reported count is LOWER than last seen, so the whole
            // current count is new activity from zero.
            var deltaTurns = b.Turns >= prevTurns ? b.Turns - prevTurns : b.Turns;
            var deltaChars = b.Characters >= prevChars ? b.Characters - prevChars : b.Characters;

            if (deltaTurns > 0 || deltaChars > 0)
            {
                // Decided HERE, in C#, with the same case-INSENSITIVE test the original uses - while the
                // totals bucket key stays case-SENSITIVE. That asymmetry is current behaviour and storing
                // the flag keeps it out of the query layer.
                var isVoice = string.Equals(key.Item1, "voice", StringComparison.OrdinalIgnoreCase);

                batch.Rows.Add((batch.HourKey, s.SessionId, key.Item1, key.Item2, isVoice, repoKey, checkoutKey, modelKey, wingman, deltaTurns, deltaChars));
                NeedIdentity(repoKey, IdentityKind.Repo, batch);
                if (!KnownIdentitySession(repoKey, s.SessionId, IdentityKind.Repo, batch))
                    batch.NewIdentitySessions.Add((repoKey, s.SessionId, IdentityKind.Repo));

                // The checkout the turn ran in earns an identity, retained beside the repository. No
                // distinct-session set (the Repos page counts sessions per repository, not per checkout), so
                // it is never queued into NewIdentitySessions - SessionsFor(Checkout) would refuse it.
                NeedIdentity(checkoutKey, IdentityKind.Checkout, batch);

                // Only a model the Director actually named earns an identity. An absent model writes a null
                // model_id and creates nothing, so model_identity never grows a row for "not said".
                if (modelKey is not null)
                    NeedIdentity(modelKey, IdentityKind.Model, batch);

                AttributeToAgentLocked(s, key.Item1, deltaTurns, deltaChars, batch);
            }

            if (prev is null || prev.Turns != b.Turns || prev.Characters != b.Characters)
                batch.HighWater.Add((s.SessionId, key.Item1, key.Item2, b.Turns, b.Characters));
        }
    }

    // Attribute turns/characters to the agent CLI the session drives. Used by BOTH the ordinary delta path
    // and the first-fold back-fill, so the two can never drift apart - which is exactly why the agent tally
    // has its OWN table rather than being derived from stat_delta (the back-fill has no stat_delta
    // counterpart, so deriving it would either inflate the totals or lose the attribution).
    //
    // The session id is registered even when it brought no turns, so the per-agent session counts describe
    // the agents actually being run rather than only the ones that submitted a turn in the observed window.
    // An agent the Director did not report is counted under the empty key and shown as "(unknown)".
    private void AttributeToAgentLocked(SessionDto s, string modality, long turns, long characters, FoldBatch batch)
    {
        var agentKey = s.Agent ?? "";
        NeedIdentity(agentKey, IdentityKind.Agent, batch);

        if (turns > 0 || characters > 0)
        {
            var isVoice = string.Equals(modality, "voice", StringComparison.OrdinalIgnoreCase);
            batch.AgentRows.Add((agentKey, isVoice, turns, characters));
        }

        if (!string.IsNullOrEmpty(s.SessionId) && !KnownIdentitySession(agentKey, s.SessionId, IdentityKind.Agent, batch))
            batch.NewIdentitySessions.Add((agentKey, s.SessionId, IdentityKind.Agent));
    }

    // Fold the turns OTHER agents drove into this session (issue #1636) via the same high-water increment
    // the human buckets use. Attributed to the RECEIVING session's agent. These never enter the totals, the
    // hourly log or the buckets, because the human voice-versus-typed numbers must stay about the human -
    // which is why they live in their own table where they CANNOT be summed in by accident.
    private void FoldAgentDrivenLocked(SessionDto s, FoldBatch batch)
    {
        var turns = s.InputStats?.AgentDrivenTurns ?? 0;
        var chars = s.InputStats?.AgentDrivenCharacters ?? 0;
        if (turns == 0 && chars == 0) return;

        _agentDrivenHighWater.TryGetValue(s.SessionId, out var prev);
        var prevTurns = prev?.Turns ?? 0;
        var prevChars = prev?.Characters ?? 0;

        var deltaTurns = turns >= prevTurns ? turns - prevTurns : turns;
        var deltaChars = chars >= prevChars ? chars - prevChars : chars;

        // The watermark moves whether or not the delta did - matching the original, which assigns it before
        // the delta check. But only queue a WRITE when the value actually changed: the original's assignment
        // is a free in-memory store, whereas here it would be a database upsert on every poll of a session
        // whose agent-driven counts are steady - a permanent write where there should be silence, which is
        // the same idle-poll property the human high-water above protects. The mirror lands on the same
        // value either way. (Codex non-blocking finding on the fold review.)
        if (prev is null || prev.Turns != turns || prev.Characters != chars)
            batch.AgentDrivenHighWater.Add((s.SessionId, turns, chars));
        if (deltaTurns <= 0 && deltaChars <= 0) return;

        var agentKey = s.Agent ?? "";
        NeedIdentity(agentKey, IdentityKind.Agent, batch);
        batch.AgentDrivenRows.Add((agentKey, deltaTurns, deltaChars));
        if (!string.IsNullOrEmpty(s.SessionId) && !KnownIdentitySession(agentKey, s.SessionId, IdentityKind.Agent, batch))
            batch.NewIdentitySessions.Add((agentKey, s.SessionId, IdentityKind.Agent));
    }

    // Fold this session's cumulative token spend (issue #1637) via the same high-water increment the human
    // buckets use: store only the GROWTH since the last poll, and treat a DROP - a Director that restarted
    // the session with a fresh conversation - as fresh spend from zero, never a negative. Attributed to the
    // hour and to the model the session was recorded running, on token_delta's own lane. NO modality or
    // surface: tokens are the model's work, not the human's input channel (see MigrateToVersion3).
    private void FoldTokensLocked(SessionDto s, FoldBatch batch)
    {
        var t = s.TokenTotals;
        if (t is null) return;

        _tokenHighWater.TryGetValue(s.SessionId, out var prev);
        var prevIn = prev?.Input ?? 0;
        var prevOut = prev?.Output ?? 0;
        var prevCacheR = prev?.CacheRead ?? 0;
        var prevCacheC = prev?.CacheCreation ?? 0;

        // Per-scalar reset test, exactly as the turns/characters fold: a value below the last seen is a fresh
        // conversation counting from zero, so the whole current value is new spend. All four are running sums
        // over the same transcript, so on a real restart they drop together; testing each independently is
        // simply the same safe rule applied per column.
        var dIn = t.InputTokens >= prevIn ? t.InputTokens - prevIn : t.InputTokens;
        var dOut = t.OutputTokens >= prevOut ? t.OutputTokens - prevOut : t.OutputTokens;
        var dCacheR = t.CacheReadTokens >= prevCacheR ? t.CacheReadTokens - prevCacheR : t.CacheReadTokens;
        var dCacheC = t.CacheCreationTokens >= prevCacheC ? t.CacheCreationTokens - prevCacheC : t.CacheCreationTokens;

        // Advance the high-water whenever the reported totals moved, even if nothing is folded this poll -
        // but only queue the upsert when they actually changed, so an idle re-poll of a steady session writes
        // nothing (the same idle-poll silence the other high-waters protect).
        if (prev is null || prev.Input != t.InputTokens || prev.Output != t.OutputTokens
            || prev.CacheRead != t.CacheReadTokens || prev.CacheCreation != t.CacheCreationTokens)
            batch.TokenHighWater.Add((s.SessionId, t.InputTokens, t.OutputTokens, t.CacheReadTokens, t.CacheCreationTokens));

        if (dIn <= 0 && dOut <= 0 && dCacheR <= 0 && dCacheC <= 0) return;

        // Attribute to the model the session was RECORDED running, null until its records name one - the same
        // records-only nullability as stat_delta.model_id. Only a named model earns an identity row.
        var modelKey = string.IsNullOrWhiteSpace(s.CurrentModel) ? null : s.CurrentModel;
        if (modelKey is not null)
            NeedIdentity(modelKey, IdentityKind.Model, batch);

        batch.TokenRows.Add((batch.HourKey, modelKey, dIn, dOut, dCacheR, dCacheC));
    }

    private void NeedIdentity(string display, IdentityKind kind, FoldBatch batch)
    {
        if (IdsFor(kind).ContainsKey(display)) return;
        // The pending list is compared with the SAME comparer, so two spellings differing only by case
        // inside one batch resolve to one identity, exactly as the dictionary would.
        foreach (var (d, k) in batch.NewIdentities)
            if (k == kind && StringComparer.OrdinalIgnoreCase.Equals(d, display)) return;
        batch.NewIdentities.Add((display, kind));
    }

    private bool KnownIdentitySession(string display, string sessionId, IdentityKind kind, FoldBatch batch)
    {
        if (IdsFor(kind).TryGetValue(display, out var id) && SessionsFor(kind).Contains((id, sessionId)))
            return true;
        foreach (var (d, sid, k) in batch.NewIdentitySessions)
            if (k == kind && sid == sessionId && StringComparer.OrdinalIgnoreCase.Equals(d, display)) return true;
        return false;
    }

    // Write everything the batch collected, in ONE transaction, then advance the mirror. An empty batch - an
    // IDLE poll - writes NOTHING and does not even open a transaction.
    private void CommitLocked(FoldBatch batch)
    {
        if (batch.IsEmpty) return;

        using var tx = _db.Connection.BeginTransaction();

        if (batch.StampAgentsSince is not null)
            Execute("INSERT OR REPLACE INTO meta(name, value) VALUES ($n, $v)", tx,
                ("$n", AgentsSinceKey), ("$v", batch.StampAgentsSince));

        // Freshly minted ids, per kind, keyed with the SAME comparer as the mirror they will join.
        var newIds = new Dictionary<IdentityKind, Dictionary<string, long>>
        {
            [IdentityKind.Repo] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Agent] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Model] = new(StringComparer.OrdinalIgnoreCase),
            [IdentityKind.Checkout] = new(StringComparer.OrdinalIgnoreCase),
        };
        foreach (var (display, kind) in batch.NewIdentities)
        {
            var (table, column) = IdentityTableFor(kind);
            var id = ExecuteScalarLong($"INSERT INTO {table}({column}) VALUES ($d); SELECT last_insert_rowid()", tx, ("$d", display));
            newIds[kind][display] = id;
        }

        long Resolve(string display, IdentityKind kind)
        {
            if (newIds[kind].TryGetValue(display, out var fresh)) return fresh;
            return IdsFor(kind)[display];
        }

        // An absent model resolves to nothing at all - DBNull, so the column is SQL NULL rather than a
        // sentinel id that a later reader could mistake for a real model.
        object ResolveModel(string? display) =>
            display is null ? DBNull.Value : Resolve(display, IdentityKind.Model);

        foreach (var r in batch.Rows)
            Execute(@"INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman, turns, chars)
                      VALUES ($h, $s, $m, $u, $v, $r, $k, $d, $w, $t, $c)", tx,
                ("$h", r.Hour), ("$s", r.SessionId), ("$m", r.Modality), ("$u", r.Surface),
                ("$v", r.IsVoice ? 1 : 0), ("$r", Resolve(r.Repo, IdentityKind.Repo)),
                ("$k", Resolve(r.Checkout, IdentityKind.Checkout)),
                ("$d", ResolveModel(r.Model)), ("$w", r.Wingman ? 1 : 0),
                ("$t", r.Turns), ("$c", r.Chars));

        foreach (var a in batch.AgentRows)
            Execute("INSERT INTO agent_delta(agent_id, is_voice, turns, chars) VALUES ($a, $v, $t, $c)", tx,
                ("$a", Resolve(a.Agent, IdentityKind.Agent)), ("$v", a.IsVoice ? 1 : 0), ("$t", a.Turns), ("$c", a.Chars));

        foreach (var a in batch.AgentDrivenRows)
            Execute("INSERT INTO agent_driven_delta(agent_id, turns, chars) VALUES ($a, $t, $c)", tx,
                ("$a", Resolve(a.Agent, IdentityKind.Agent)), ("$t", a.Turns), ("$c", a.Chars));

        foreach (var h in batch.HighWater)
            Execute(@"INSERT INTO session_highwater(session_id, modality, surface, turns, chars)
                      VALUES ($s, $m, $u, $t, $c)
                      ON CONFLICT(session_id, modality, surface) DO UPDATE SET turns=$t, chars=$c", tx,
                ("$s", h.SessionId), ("$m", h.Modality), ("$u", h.Surface), ("$t", h.Turns), ("$c", h.Chars));

        foreach (var h in batch.AgentDrivenHighWater)
            Execute(@"INSERT INTO agent_driven_highwater(session_id, turns, chars) VALUES ($s, $t, $c)
                      ON CONFLICT(session_id) DO UPDATE SET turns=$t, chars=$c", tx,
                ("$s", h.SessionId), ("$t", h.Turns), ("$c", h.Chars));

        foreach (var r in batch.TokenRows)
            Execute(@"INSERT INTO token_delta(hour_utc, model_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
                      VALUES ($h, $d, $i, $o, $cr, $cc)", tx,
                ("$h", r.Hour), ("$d", ResolveModel(r.Model)),
                ("$i", r.Input), ("$o", r.Output), ("$cr", r.CacheRead), ("$cc", r.CacheCreation));

        foreach (var h in batch.TokenHighWater)
            Execute(@"INSERT INTO token_highwater(session_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
                      VALUES ($s, $i, $o, $cr, $cc)
                      ON CONFLICT(session_id) DO UPDATE SET input_tokens=$i, output_tokens=$o, cache_read_tokens=$cr, cache_creation_tokens=$cc", tx,
                ("$s", h.SessionId), ("$i", h.Input), ("$o", h.Output), ("$cr", h.CacheRead), ("$cc", h.CacheCreation));

        foreach (var sid in batch.NewWingmanSessions)
            Execute("INSERT OR IGNORE INTO wingman_session(session_id) VALUES ($s)", tx, ("$s", sid));

        foreach (var sid in batch.NewSeeded)
            Execute("INSERT OR IGNORE INTO agents_seeded(session_id) VALUES ($s)", tx, ("$s", sid));

        foreach (var (display, sessionId, kind) in batch.NewIdentitySessions)
        {
            // SessionsFor refuses a model, so a model queued here would fail loudly rather than write a row
            // into a table that does not exist. Nothing queues one - the fold never adds a model to
            // NewIdentitySessions - and this is the check that keeps that true.
            _ = SessionsFor(kind);
            var (table, column) = kind == IdentityKind.Repo
                ? ("repo_session", "repo_id")
                : ("agent_session", "agent_id");
            Execute($"INSERT OR IGNORE INTO {table}({column}, session_id) VALUES ($i, $s)", tx,
                ("$i", Resolve(display, kind)), ("$s", sessionId));
        }

        if (batch.Rows.Count > 0 || batch.TokenRows.Count > 0) PruneLocked(batch.NowUtc, tx);

        tx.Commit();

        // ---- Committed. Only now does the mirror move. ----
        if (batch.StampAgentsSince is not null) _agentsSinceUtc = batch.StampAgentsSince;
        foreach (var (kind, minted) in newIds)
            foreach (var (d, id) in minted) { IdsFor(kind)[d] = id; DisplayFor(kind)[id] = d; }
        foreach (var h in batch.HighWater)
        {
            if (!_highWater.TryGetValue(h.SessionId, out var hw))
            {
                hw = new Dictionary<(string, string), Counters>();
                _highWater[h.SessionId] = hw;
            }
            hw[(h.Modality, h.Surface)] = new Counters { Turns = h.Turns, Characters = h.Chars };
        }
        foreach (var h in batch.AgentDrivenHighWater)
            _agentDrivenHighWater[h.SessionId] = new Counters { Turns = h.Turns, Characters = h.Chars };
        foreach (var h in batch.TokenHighWater)
            _tokenHighWater[h.SessionId] = new TokenCounters
            {
                Input = h.Input, Output = h.Output, CacheRead = h.CacheRead, CacheCreation = h.CacheCreation,
            };
        foreach (var sid in batch.NewWingmanSessions) _wingmanSessions.Add(sid);
        foreach (var sid in batch.NewSeeded) _agentsSeeded.Add(sid);
        foreach (var (display, sessionId, kind) in batch.NewIdentitySessions)
            SessionsFor(kind).Add((IdsFor(kind)[display], sessionId));
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
            // The token high-water is cleaned up on its own, not under the session_highwater guard: a session
            // has both, and dropping only one would leave the other's map growing without bound. Each is
            // removed only if present, so forgetting a session that never spent a token is still a no-op.
            if (_highWater.Remove(sessionId))
                Execute("DELETE FROM session_highwater WHERE session_id=$s", null, ("$s", sessionId));
            if (_tokenHighWater.Remove(sessionId))
                Execute("DELETE FROM token_highwater WHERE session_id=$s", null, ("$s", sessionId));
        }
    }

    // Prune the working-day detail past the retention window. Caller holds the lock.
    //
    // The original prunes only the hourly buckets and the all-time totals survive because they live in
    // separate dictionaries. Here ONE row feeds both, so deleting it would silently shrink the all-time
    // totals - the #1376 class of failure. Departing rows are therefore folded into ARCHIVE rows first,
    // preserving every dimension any all-time answer groups by: modality, surface, is_voice, repository and
    // the wingman flag. Pruning collapses the hour and the session id, and nothing else. agent_delta and
    // agent_driven_delta carry no hour and are never pruned, matching the all-time agent tally.
    private void PruneLocked(DateTime nowUtc, SqliteTransaction tx)
    {
        var cutoff = HourKey(nowUtc.AddDays(-RetentionDays));
        // model_id and checkout_id are carried through the archive fold, and each MUST be in BOTH lists. Left
        // out of the SELECT the archive row would read NULL and every pruned turn would silently lose that
        // dimension (model_id would become "model unknown"; checkout_id would forget which checkout it ran
        // in); left out of the GROUP BY it would collapse different values into one row and take an arbitrary
        // id with it. Adding a dimension to this table means adding it here, in both places, or pruning
        // quietly destroys it ninety days later - long after the change that caused it.
        //
        // SQLite groups NULLs together, so every unknown-model row of a bucket archives into ONE row that is
        // still honestly NULL. That is the wanted behaviour: absence aggregates as absence. (checkout_id is
        // never NULL on a row this build wrote, but it rides the same fold for the same reason.)
        Execute(@"INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman, turns, chars)
                  SELECT $marker, $marker, modality, surface, is_voice, repo_id, checkout_id, model_id, wingman, SUM(turns), SUM(chars)
                    FROM stat_delta
                   WHERE hour_utc <> $marker AND hour_utc < $cutoff
                   GROUP BY modality, surface, is_voice, repo_id, checkout_id, model_id, wingman", tx,
            ("$marker", GatewayStatsDatabase.ArchiveMarker), ("$cutoff", cutoff));
        Execute("DELETE FROM stat_delta WHERE hour_utc <> $marker AND hour_utc < $cutoff", tx,
            ("$marker", GatewayStatsDatabase.ArchiveMarker), ("$cutoff", cutoff));

        // token_delta prunes on the SAME rule and the same care: its one dimension, model_id, is carried in
        // both the SELECT and the GROUP BY, or the ninety-day fold turns every archived model's spend into
        // "model unknown". Its all-time totals INCLUDE archive rows (that is the point of archiving), so the
        // spend must not shrink when detail is pruned.
        Execute(@"INSERT INTO token_delta(hour_utc, model_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
                  SELECT $marker, model_id, SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_creation_tokens)
                    FROM token_delta
                   WHERE hour_utc <> $marker AND hour_utc < $cutoff
                   GROUP BY model_id", tx,
            ("$marker", GatewayStatsDatabase.ArchiveMarker), ("$cutoff", cutoff));
        Execute("DELETE FROM token_delta WHERE hour_utc <> $marker AND hour_utc < $cutoff", tx,
            ("$marker", GatewayStatsDatabase.ArchiveMarker), ("$cutoff", cutoff));
    }

    /// <summary>
    /// All-time turns that agents drove into other agents' sessions (issue #1636), and their character
    /// volume. NOT part of the human totals: this is the fleet driving itself, which is a different
    /// question from how the owner drives.
    /// </summary>
    public (long Turns, long Characters) AgentDrivenUsage()
    {
        lock (_lock)
        {
            long turns = 0, chars = 0;
            Read("SELECT COALESCE(SUM(turns),0), COALESCE(SUM(chars),0) FROM agent_driven_delta",
                r => { turns = r.GetInt64(0); chars = r.GetInt64(1); });
            return (turns, chars);
        }
    }

    /// <summary>An immutable snapshot of the all-time totals for the dashboard, buckets in a stable order.</summary>
    public InputStatsDto CurrentTotals()
    {
        lock (_lock)
        {
            var rows = new List<InputStatBucketDto>();
            // ARCHIVE rows are INCLUDED - that is the point of archiving: an all-time total must not shrink
            // when the hourly detail behind it is pruned.
            Read(@"SELECT modality, surface, SUM(turns), SUM(chars) FROM stat_delta GROUP BY modality, surface", r =>
                rows.Add(new InputStatBucketDto
                {
                    Modality = r.GetString(0),
                    Surface = r.GetString(1),
                    Turns = r.GetInt64(2),
                    Characters = r.GetInt64(3),
                }));

            var dto = new InputStatsDto();
            // Ordered in C#, ordinal - the comparer the original projection used.
            foreach (var b in rows.OrderBy(b => b.Modality, StringComparer.Ordinal)
                                  .ThenBy(b => b.Surface, StringComparer.Ordinal))
                dto.Buckets.Add(b);
            return dto;
        }
    }

    /// <summary>All-time wingman usage: the number of submitted turns folded while a session had voice mode
    /// on, and the count of distinct sessions ever seen with voice mode on.</summary>
    public WingmanUsageDto WingmanUsage()
    {
        lock (_lock)
        {
            // SUM(turns) WHERE wingman=1 - never anything keyed on modality. A turn TYPED while voice mode
            // was on is a wingman turn.
            var turns = ScalarLong("SELECT COALESCE(SUM(turns),0) FROM stat_delta WHERE wingman=1");
            var sessions = ScalarLong("SELECT COUNT(*) FROM wingman_session");
            return new WingmanUsageDto { Turns = turns, Sessions = (int)sessions };
        }
    }

    /// <summary>The per-hour turn log (the "working day" series: turns by modality and character volume per
    /// UTC clock hour), oldest hour first.</summary>
    public IReadOnlyList<InputHourDto> HourlyTurns()
    {
        lock (_lock)
        {
            var list = new List<InputHourDto>();
            // ARCHIVE rows are EXCLUDED: they are real all-time data with no real hour, so letting them
            // through would invent a bucket in this series that was never an hour of the working day.
            Read(@"SELECT hour_utc,
                          COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                          COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                          SUM(chars)
                     FROM stat_delta
                    WHERE hour_utc <> $marker
                    GROUP BY hour_utc", r =>
            {
                var voice = r.GetInt64(1);
                var typed = r.GetInt64(2);
                list.Add(new InputHourDto
                {
                    Hour = r.GetString(0),
                    VoiceTurns = voice,
                    TypedTurns = typed,
                    Turns = voice + typed,
                    Characters = r.GetInt64(3),
                });
            }, ("$marker", GatewayStatsDatabase.ArchiveMarker));
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
            var sessions = SessionCounts("repo_session", "repo_id");

            // The local checkouts (worktrees, per-machine clones) that rolled up into each repository, kept so
            // the page can still show which working directories a repo's turns came from - the path is not
            // lost when the slug collapses them. Display spellings come from the identity mirror, never from a
            // SQL join; sorted here so the retained list is stable rather than in row-insert order.
            var checkoutsByRepo = new Dictionary<long, List<string>>();
            Read("SELECT DISTINCT repo_id, checkout_id FROM stat_delta WHERE checkout_id IS NOT NULL", r =>
            {
                var repoId = r.GetInt64(0);
                var checkoutId = r.GetInt64(1);
                if (!_checkoutDisplay.TryGetValue(checkoutId, out var path)) return;
                if (!checkoutsByRepo.TryGetValue(repoId, out var paths))
                    checkoutsByRepo[repoId] = paths = new List<string>();
                paths.Add(path);
            });
            foreach (var paths in checkoutsByRepo.Values)
                paths.Sort(StringComparer.OrdinalIgnoreCase);

            var list = new List<RepoStatBucketDto>();
            Read(@"SELECT repo_id,
                          COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                          COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                          SUM(chars)
                     FROM stat_delta GROUP BY repo_id", r =>
            {
                var id = r.GetInt64(0);
                var voice = r.GetInt64(1);
                var typed = r.GetInt64(2);
                // The display spelling comes from the identity mirror, never from a SQL join that might be
                // tempted to group or order by the string.
                var display = _repoDisplay.TryGetValue(id, out var d) ? d : "";
                list.Add(new RepoStatBucketDto
                {
                    Repo = display,
                    RepoName = RepoLeaf(display),
                    Turns = voice + typed,
                    VoiceTurns = voice,
                    TypedTurns = typed,
                    Characters = r.GetInt64(3),
                    Sessions = sessions.TryGetValue(id, out var n) ? n : 0,
                    Checkouts = checkoutsByRepo.TryGetValue(id, out var cks) ? cks : new List<string>(),
                });
            });
            // Ranked in C#, matching the original ordering exactly.
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
    /// ranked most-driven first, for the private Agents page.</summary>
    public IReadOnlyList<AgentStatBucketDto> AgentTotals()
    {
        lock (_lock)
        {
            var sessions = SessionCounts("agent_session", "agent_id");

            var human = new Dictionary<long, (long Voice, long Typed, long Chars)>();
            Read(@"SELECT agent_id,
                          COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                          COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                          SUM(chars)
                     FROM agent_delta GROUP BY agent_id",
                r => human[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));

            var driven = new Dictionary<long, (long Turns, long Chars)>();
            Read("SELECT agent_id, SUM(turns), SUM(chars) FROM agent_driven_delta GROUP BY agent_id",
                r => driven[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt64(2)));

            // Every agent EVER attributed appears, including one registered with no turns - the original
            // creates the tally entry on attribution regardless, so the page describes the agents actually
            // being run rather than only those that submitted a turn in the window.
            var list = new List<AgentStatBucketDto>();
            foreach (var (id, display) in _agentDisplay)
            {
                human.TryGetValue(id, out var h);
                driven.TryGetValue(id, out var d);
                list.Add(new AgentStatBucketDto
                {
                    Agent = display,
                    AgentName = AgentDisplayName(display),
                    Turns = h.Voice + h.Typed,
                    VoiceTurns = h.Voice,
                    TypedTurns = h.Typed,
                    Characters = h.Chars,
                    AgentDrivenTurns = d.Turns,
                    AgentDrivenCharacters = d.Chars,
                    Sessions = sessions.TryGetValue(id, out var n) ? n : 0,
                });
            }
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

    private Dictionary<long, int> SessionCounts(string table, string column)
    {
        var counts = new Dictionary<long, int>();
        Read($"SELECT {column}, COUNT(*) FROM {table} GROUP BY {column}", r => counts[r.GetInt64(0)] = r.GetInt32(1));
        return counts;
    }

    /// <summary>When the per-agent tally started counting (round-trip UTC), or "" if never stamped.</summary>
    public string AgentsSinceUtc
    {
        get { lock (_lock) { return _agentsSinceUtc; } }
    }

    /// <summary>
    /// When the model dimension started recording (round-trip UTC), stamped by the schema version 2
    /// migration. A page NEEDS this to read a null model honestly: a turn folded before this moment predates
    /// the dimension and could never have carried a model, while one folded after it belongs to a session
    /// whose agent had recorded no model yet. Both store null and only this stamp tells them apart.
    /// </summary>
    public string ModelsSinceUtc
    {
        get { lock (_lock) { return _modelsSinceUtc; } }
    }

    /// <summary>
    /// The per-model all-time tally (turns by modality, character volume), ranked most-driven first, for the
    /// private Models page. Includes the null-model bucket, which is a real answer and not a gap - see
    /// <see cref="ModelStatBucketDto"/>.
    ///
    /// No distinct-session count, unlike the repository and agent tallies: a session's model can CHANGE
    /// mid-session (the whole reason the producer re-reads it at every turn-end), so "sessions that ran this
    /// model" is not a question this store can answer without a per-session-per-model set nothing keeps. A
    /// count of sessions here would be a number that looks like the neighbouring ones and means something
    /// weaker, so it is absent rather than approximated.
    /// </summary>
    public IReadOnlyList<ModelStatBucketDto> ModelTotals()
    {
        lock (_lock)
        {
            var list = new List<ModelStatBucketDto>();
            // Archive rows are INCLUDED - the marker convention only excludes them from hourly and
            // working-day series, and an all-time tally that dropped them would shrink as history is pruned.
            Read(@"SELECT model_id,
                          COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                          COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                          SUM(chars)
                     FROM stat_delta GROUP BY model_id", r =>
            {
                var voice = r.GetInt64(1);
                var typed = r.GetInt64(2);
                // A null model_id is the "not recorded" bucket and stays null all the way to the page. The
                // display spelling comes from the identity mirror, never from a SQL join that might be
                // tempted to group or order by the string.
                string? display = r.IsDBNull(0)
                    ? null
                    : (_modelDisplay.TryGetValue(r.GetInt64(0), out var d) ? d : "");
                list.Add(new ModelStatBucketDto
                {
                    Model = display,
                    Turns = voice + typed,
                    VoiceTurns = voice,
                    TypedTurns = typed,
                    Characters = r.GetInt64(3),
                });
            });
            // Ranked in C#, matching the repository and agent tallies. The null bucket sorts by its numbers
            // like any other and is deliberately not pinned anywhere: it is a bucket, not a footnote.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
            });
            return list;
        }
    }

    /// <summary>
    /// All-time token spend (issue #1637): the running sums of input, output, cache-read and cache-creation
    /// tokens across the whole record. INCLUDES archive rows - the same reason the model tally does: an
    /// all-time figure that dropped them would shrink as history is pruned. No context occupancy here; it is
    /// not spend and was never stored.
    /// </summary>
    public TokenSpendDto TokenSpend()
    {
        lock (_lock)
        {
            var dto = new TokenSpendDto();
            Read(@"SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                          COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                     FROM token_delta", r =>
            {
                dto.InputTokens = r.GetInt64(0);
                dto.OutputTokens = r.GetInt64(1);
                dto.CacheReadTokens = r.GetInt64(2);
                dto.CacheCreationTokens = r.GetInt64(3);
            });
            return dto;
        }
    }

    /// <summary>
    /// Token spend per UTC hour (issue #1637), for the working-day "what did I spend" series. EXCLUDES the
    /// archive marker, exactly as the hourly turn series does: an archive row is not a real hour and would
    /// grow a phantom bucket. Ordered by hour.
    /// </summary>
    public IReadOnlyList<TokenHourDto> TokenSpendByHour()
    {
        lock (_lock)
        {
            var list = new List<TokenHourDto>();
            Read(@"SELECT hour_utc,
                          COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                          COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                     FROM token_delta
                    WHERE hour_utc <> $marker
                    GROUP BY hour_utc", r => list.Add(new TokenHourDto
            {
                Hour = r.GetString(0),
                InputTokens = r.GetInt64(1),
                OutputTokens = r.GetInt64(2),
                CacheReadTokens = r.GetInt64(3),
                CacheCreationTokens = r.GetInt64(4),
            }), ("$marker", GatewayStatsDatabase.ArchiveMarker));
            list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return list;
        }
    }

    /// <summary>
    /// Token spend per model (issue #1637), ranked most-spent first, for "which model cost what". INCLUDES
    /// archive rows. The null-model bucket is a REAL bucket - spend folded before the session's model was
    /// recorded - and is reported, not filtered, so the per-model spend sums to the all-time total.
    /// </summary>
    public IReadOnlyList<ModelSpendDto> TokenSpendByModel()
    {
        lock (_lock)
        {
            var list = new List<ModelSpendDto>();
            Read(@"SELECT model_id,
                          COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                          COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                     FROM token_delta GROUP BY model_id", r =>
            {
                // A null model_id is the "not recorded" bucket and stays null to the page; the display
                // spelling comes from the identity mirror, never a SQL join over the string.
                string? display = r.IsDBNull(0)
                    ? null
                    : (_modelDisplay.TryGetValue(r.GetInt64(0), out var d) ? d : "");
                list.Add(new ModelSpendDto
                {
                    Model = display,
                    InputTokens = r.GetInt64(1),
                    OutputTokens = r.GetInt64(2),
                    CacheReadTokens = r.GetInt64(3),
                    CacheCreationTokens = r.GetInt64(4),
                });
            });
            list.Sort((a, b) =>
            {
                var byTotal = b.TotalTokens.CompareTo(a.TotalTokens);
                return byTotal != 0 ? byTotal : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
            });
            return list;
        }
    }

    // An explicit map, not a PascalCase splitter: "OpenCode" is the product's own spelling and must not
    // become "Open Code". An unrecognised token is shown verbatim - it is a real value we simply do not have
    // a nicer name for, so showing it is honest where hiding it behind "Other" would not be.
    private static string AgentDisplayName(string agent) => agent switch
    {
        "" => "(unknown)",
        "ClaudeCode" => "Claude Code",
        "RawCli" => "Raw CLI",
        _ => agent,
    };

    // The last segment of the grouping key, used as the row's short name. For a GitHub slug
    // ("thefrederiksen/devthrottle") this is the repository name ("devthrottle"); for a fallback path
    // ("D:\ReposFred\devthrottle") it is the folder name. The same split serves both because '/' and '\' are
    // both segment separators here.
    private static string RepoLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, System.Globalization.CultureInfo.InvariantCulture);

    // ---- Plumbing. Every statement passes through here so StatementsExecuted is honest. ----

    private void Execute(string sql, SqliteTransaction? tx, params (string Name, object Value)[] args)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
        StatementsExecuted++;
    }

    private long ExecuteScalarLong(string sql, SqliteTransaction? tx, params (string Name, object Value)[] args)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var result = cmd.ExecuteScalar();
        StatementsExecuted++;
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private void Read(string sql, Action<SqliteDataReader> onRow, params (string Name, object Value)[] args)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var reader = cmd.ExecuteReader();
        StatementsExecuted++;
        while (reader.Read()) onRow(reader);
    }

    private long ScalarLong(string sql) => ExecuteScalarLong(sql, null);

    private string? ReadScalarString(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        StatementsExecuted++;
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose()
    {
        if (_ownsDatabase) _db.Dispose();
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

/// <summary>One repository's all-time input tally for the private Repos page.</summary>
public sealed class RepoStatBucketDto
{
    /// <summary>The grouping key: the GitHub "owner/repo" slug the sessions' checkouts belong to
    /// (e.g. "thefrederiksen/devthrottle"), so every worktree and every per-machine clone of one repository
    /// is one row. Falls back to the local working-directory path for a checkout that has no github.com
    /// origin.</summary>
    public string Repo { get; set; } = "";

    /// <summary>The display leaf of <see cref="Repo"/> (its last segment): the repository name for a slug,
    /// e.g. "devthrottle", or the folder name for a fallback path.</summary>
    public string RepoName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }

    /// <summary>Distinct sessions that drove counted input into this repo.</summary>
    public int Sessions { get; set; }

    /// <summary>The local checkout paths (worktrees, per-machine clones) whose turns rolled up into this
    /// repository, sorted. Retained so the page can still show which working directories a repo's work came
    /// from; empty only for a legacy row written before the checkout dimension existed.</summary>
    public List<string> Checkouts { get; set; } = new();
}

/// <summary>
/// One model's all-time input tally for the private Models page (issue #1637).
///
/// The "unknown model" row is a REAL row here, not an omission: it is where every turn folded before its
/// session's agent had recorded a model lands, and it is the only bucket that can never shrink to zero -
/// each session's first turn is permanently in it. A page must show it and label it honestly rather than
/// filter it out, or the model turns will not add up to the total turns and the page will look broken.
/// </summary>
public sealed class ModelStatBucketDto
{
    /// <summary>The model the owning Director recorded, e.g. "claude-opus-4-8" - or null for the bucket of
    /// turns folded when no model had been recorded. Never an empty string: absence is null.</summary>
    public string? Model { get; set; }

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }
}

/// <summary>All-time token spend (issue #1637): the four cumulative, additive counts. No context occupancy -
/// that is a gauge, not spend, and is never summed.</summary>
public sealed class TokenSpendDto
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>Every token the work cost, cached or not - the single "how much did I spend" figure.</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>Token spend in one UTC hour (issue #1637), for the working-day series.</summary>
public sealed class TokenHourDto
{
    /// <summary>The hour key, "yyyy-MM-ddTHH".</summary>
    public string Hour { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>One model's all-time token spend (issue #1637), for "which model cost what". The null-model
/// bucket is a real answer - spend folded before the session's model was recorded - not an omission.</summary>
public sealed class ModelSpendDto
{
    /// <summary>The model the owning Director recorded, or null for spend folded when no model had been
    /// recorded. Never an empty string: absence is null.</summary>
    public string? Model { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>One agent CLI's all-time input tally for the private Agents page.</summary>
public sealed class AgentStatBucketDto
{
    /// <summary>The agent token the Director reported (the AgentKind name), or "" when none.</summary>
    public string Agent { get; set; } = "";

    /// <summary>The display name of <see cref="Agent"/>; "(unknown)" when empty.</summary>
    public string AgentName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }

    /// <summary>Turns OTHER agents drove into the sessions running this agent (issue #1636).</summary>
    public long AgentDrivenTurns { get; set; }

    /// <summary>Character volume other agents drove into the sessions running this agent.</summary>
    public long AgentDrivenCharacters { get; set; }

    /// <summary>Distinct sessions that drove counted input through this agent.</summary>
    public int Sessions { get; set; }
}
