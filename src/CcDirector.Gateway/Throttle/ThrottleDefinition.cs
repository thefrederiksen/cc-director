namespace CcDirector.Gateway.Throttle;

/// <summary>
/// THE ONE DEFINITION of "how does this person drive DevThrottle" - the Your Throttle library
/// (mission "Clean up Your Throttle", 2026-09-05, rulings R7, R8, R9 and R17).
///
/// Every consumer of the figure - the Cockpit and mobile Your Throttle pages through <c>GET /stats/data</c>,
/// and the mentor report - anchors on the SUBMISSION LEDGER (<c>activity_events</c>), never on the second
/// cumulative tally the Directors push in <c>stat_delta</c>. The ledger is written at the same choke point
/// as that tally, it is append-only and idempotent on replay, and over the owner's measured week it agreed
/// with an independent reconstruction of the store to within one turn while the tally was 34 points off.
///
/// <see cref="Fold"/> is a PURE function over the narrow ledger projection so the predicate can be proven
/// without a database; <see cref="ThrottleLedgerReader"/> is the only thing that feeds it from the store.
/// There is deliberately no second implementation of any of this anywhere - a page or a report that needs
/// a turn count asks here.
/// </summary>
public static class ThrottleDefinition
{
    /// <summary>
    /// The predicate, stated exactly as ruling R17 states it. It is served on the feed as a sentence so the
    /// reader can check the number against it, and it is pinned by test so nobody paraphrases it.
    /// </summary>
    public const string Predicate =
        "The shared figure is computed over activity_events rows where EventType is turn-submitted and " +
        "InputOrigin is present, grouped by the origin's modality and surface.";

    /// <summary>The unit of every share on the page (ruling R8): submitted turns. Never words, never characters.</summary>
    public const string Unit = "submitted turns";

    /// <summary>The ledger event type the predicate reads. The same constant the producer writes.</summary>
    public const string TurnSubmitted = Contracts.ActivityEventTypes.TurnSubmitted;

    /// <summary>The send source stamped on a turn one session drove into another (a fleet message, ask or
    /// broadcast delivery). Out of the human figure BY RECORD, reported beside it from the same ledger.</summary>
    public const string AgentSendSource = "Agent";

    /// <summary>The send source stamped on text the product itself authored (a seed prompt, a handover, a
    /// queue drain). Never anybody's turn.</summary>
    public const string FrameworkSendSource = "Framework";

    /// <summary>
    /// The three consequences of the predicate, each of which is true in the code and proven by
    /// <c>ThrottleDefinitionTests</c>:
    ///
    ///  1. A turn typed at the desktop terminal carries a null SendSource and a present InputOrigin, so the
    ///     predicate TAKES it. Those turns were never missing from the ledger - only from the tally.
    ///  2. Agent traffic carries the Agent send source and no InputOrigin, so it is OUT by record - not out
    ///     because no surface happened to resolve, which is how it used to be excluded.
    ///  3. A submission with no InputOrigin is OUT and DISCLOSED as a count beside the share (R7). A share
    ///     computed over a subset publishes the size of the subset.
    /// </summary>
    public const string Consequences =
        "A turn typed at the terminal is in (null send source, present origin). Agent traffic is out by " +
        "record. A submission with no input origin is out and disclosed as a count beside the share.";

    /// <summary>The ledger's retention, in days - the owner's ruling of 2026-07-24. The widest window this
    /// definition can honestly answer, and the reason the feed's default window is exactly this long.</summary>
    public static readonly int RetentionDays = (int)Activity.ActivityRetentionSweep.RetentionPeriod.TotalDays;

    /// <summary>
    /// THE ONE RULE FOR WHAT WINDOW THE LEDGER CAN HONESTLY ANSWER (final inspection finding F-04), applied to
    /// EVERY window form - the feed's explicit and week forms and the library's command line alike. A window
    /// must end after it starts, must not be longer than the ledger keeps, must not BEGIN before the oldest
    /// instant the ledger can still hold (now minus retention), and must have begun. Duration is not age: an
    /// eight-day window from six years ago is short enough and still unanswerable, and it used to be served
    /// as silent zeroes. Returns the refusal, in the words the caller shows, or null when the window is
    /// answerable. The boundary is inclusive: a window beginning exactly at now minus retention is served.
    /// </summary>
    public static string? WindowRefusal(DateTime fromUtc, DateTime toUtc, DateTime nowUtc)
    {
        if (toUtc <= fromUtc) return "'to' must be later than 'from'";
        if (toUtc - fromUtc > TimeSpan.FromDays(RetentionDays))
            return $"the window is longer than the {RetentionDays} days the submission ledger keeps";
        if (fromUtc < nowUtc.AddDays(-RetentionDays))
            return $"the window begins before the {RetentionDays} days the submission ledger keeps; the oldest instant it can answer is {nowUtc.AddDays(-RetentionDays):yyyy-MM-dd'T'HH:mm:ss'Z'}";
        if (fromUtc > nowUtc) return "the window has not begun yet";
        return null;
    }

    /// <summary>
    /// The window the feed answers when none is asked for: a ROLLING seven days ending now (ruling R5, landed
    /// directly per R15). Seven because the owner asked for "seven days, so it lines up with the report";
    /// rolling rather than the report's calendar week because Your Throttle is a live dashboard that refreshes
    /// while he works, and a Monday-to-Sunday default would show a nearly empty page every Monday morning. The
    /// report's own link names its exact week instead (<c>week=YYYY-Www</c>), so following it shows the
    /// identical number. Always one of <see cref="ThrottleWindowChoices.Days"/>.
    /// </summary>
    public const int DefaultWindowDays = 7;

    /// <summary>
    /// One turn-submitted row, projected to the five facts the definition reads. Anything else on the row
    /// (terminal diffs, detector fields) is never loaded.
    /// </summary>
    /// <param name="OccurredUtc">When the submission happened, UTC.</param>
    /// <param name="SessionId">The session it went into.</param>
    /// <param name="AgentKind">The agent running that session, when recorded.</param>
    /// <param name="InputOrigin">"modality/surface" when a human surface tagged the submission; null otherwise.</param>
    /// <param name="SendSource">Who drove it (UserInput, Delivery, Agent, Framework), or null on the raw-byte
    /// terminal path.</param>
    public readonly record struct LedgerSubmission(
        DateTime OccurredUtc, string SessionId, string? AgentKind, string? InputOrigin, string? SendSource);

    /// <summary>What session history knows about one session, for the per-repository split (R9: the ledger
    /// carries no repository; session history carries the resolved name and the checkout path).</summary>
    public readonly record struct SessionFacts(string? RepoName, string? RepoPath);

    /// <summary>
    /// The fold: apply the predicate to a window of ledger rows and produce every turn figure the page
    /// shows. Rows outside [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) are ignored so a caller
    /// can hand in a superset. Rows whose EventType is not turn-submitted must not be passed - the reader
    /// filters them at the query and the fold does not re-check, because the row projection carries no
    /// event type on purpose.
    /// </summary>
    /// <exception cref="InvalidOperationException">A row carries an InputOrigin that is not
    /// "modality/surface". That is a producer defect and it is surfaced, never counted into a guessed
    /// bucket - the mentor harness's own reader exits on the same row, so both consumers fail the same
    /// way.</exception>
    public static ThrottleFigureDto Fold(
        IEnumerable<LedgerSubmission> rows,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyDictionary<string, SessionFacts> sessions)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sessions);
        if (toUtc <= fromUtc)
            throw new ArgumentException("The window must end after it starts.", nameof(toUtc));

        var buckets = new Dictionary<(string Modality, string Surface), long>();
        var hours = new Dictionary<string, (long Voice, long Typed)>(StringComparer.Ordinal);
        var agents = new Dictionary<string, AgentTally>(StringComparer.Ordinal);
        var repos = new Dictionary<string, RepoTally>(StringComparer.Ordinal);
        var countedSessions = new HashSet<string>(StringComparer.Ordinal);

        long counted = 0, voice = 0, typed = 0;
        long noOrigin = 0, noOriginAgent = 0, noOriginFramework = 0;
        long repoUnattributed = 0;

        foreach (var row in rows)
        {
            if (row.OccurredUtc < fromUtc || row.OccurredUtc >= toUtc) continue;

            var agentKey = row.AgentKind ?? "";

            // THE PREDICATE: InputOrigin present. Nothing about SendSource decides membership - a null
            // send source with a present origin is the terminal-typed turn and it is IN (consequence 1).
            if (string.IsNullOrWhiteSpace(row.InputOrigin))
            {
                noOrigin++;
                if (string.Equals(row.SendSource, AgentSendSource, StringComparison.Ordinal))
                {
                    noOriginAgent++;
                    // Consequence 2: agent traffic is out of the human figure, reported beside it, and
                    // attributed to the agent RUNNING the session it was driven into.
                    Tally(agents, agentKey).AgentDrivenTurns++;
                }
                else if (string.Equals(row.SendSource, FrameworkSendSource, StringComparison.Ordinal))
                {
                    noOriginFramework++;
                }
                continue;
            }

            var (modality, surface) = ParseOrigin(row.InputOrigin!, row);
            var isVoice = modality == "voice";

            counted++;
            if (isVoice) voice++; else typed++;
            buckets[(modality, surface)] = buckets.TryGetValue((modality, surface), out var b) ? b + 1 : 1;

            var hour = row.OccurredUtc.ToString("yyyy-MM-dd'T'HH", System.Globalization.CultureInfo.InvariantCulture);
            var h = hours.TryGetValue(hour, out var existing) ? existing : (0, 0);
            hours[hour] = isVoice ? (h.Voice + 1, h.Typed) : (h.Voice, h.Typed + 1);

            countedSessions.Add(row.SessionId);

            var agent = Tally(agents, agentKey);
            agent.Turns++;
            if (isVoice) agent.VoiceTurns++; else agent.TypedTurns++;
            agent.Sessions.Add(row.SessionId);

            // Consequence 3 applied to the repository split too: a session that history holds no repository
            // for is disclosed as unattributed, never folded into a guessed row.
            if (RepoKeyOf(row.SessionId, sessions) is { } repoKey)
            {
                var repo = repos.TryGetValue(repoKey.Key, out var r) ? r : repos[repoKey.Key] = new RepoTally(repoKey.Key, repoKey.Leaf);
                repo.Turns++;
                if (isVoice) repo.VoiceTurns++; else repo.TypedTurns++;
                repo.Sessions.Add(row.SessionId);
                if (!string.IsNullOrWhiteSpace(repoKey.Checkout)) repo.Checkouts.Add(repoKey.Checkout);
            }
            else
            {
                repoUnattributed++;
            }
        }

        var dto = new ThrottleFigureDto
        {
            Definition = Predicate,
            Unit = Unit,
            Window = new ThrottleWindowDto { FromUtc = fromUtc, ToUtc = toUtc },
            Turns = counted,
            VoiceTurns = voice,
            TypedTurns = typed,
            Sessions = countedSessions.Count,
            Excluded = new ThrottleExcludedDto
            {
                NoInputOrigin = noOrigin,
                AgentDriven = noOriginAgent,
                Framework = noOriginFramework,
                Unresolved = noOrigin - noOriginAgent - noOriginFramework,
            },
            AgentDrivenTurns = noOriginAgent,
            ReposUnattributedTurns = repoUnattributed,
        };

        foreach (var kv in buckets.OrderBy(k => k.Key.Modality, StringComparer.Ordinal).ThenBy(k => k.Key.Surface, StringComparer.Ordinal))
            dto.Buckets.Add(new ThrottleBucketDto { Modality = kv.Key.Modality, Surface = kv.Key.Surface, Turns = kv.Value });

        // THE HEADLINE IS FINISHED HERE, from the same tallies the counts above were written from, and served as
        // fields a consumer prints. Nothing downstream divides (finding F-01).
        dto.Headline = Headline(counted, voice, typed, dto.Buckets);

        foreach (var kv in hours.OrderBy(k => k.Key, StringComparer.Ordinal))
            dto.HourlyTurns.Add(new ThrottleHourDto
            {
                Hour = kv.Key, VoiceTurns = kv.Value.Voice, TypedTurns = kv.Value.Typed, Turns = kv.Value.Voice + kv.Value.Typed,
            });

        foreach (var a in agents.Values.OrderByDescending(a => a.Turns).ThenByDescending(a => a.AgentDrivenTurns).ThenBy(a => a.Key, StringComparer.Ordinal))
            dto.Agents.Add(new ThrottleAgentDto
            {
                Agent = a.Key,
                AgentName = AgentDisplayName(a.Key),
                Turns = a.Turns,
                VoiceTurns = a.VoiceTurns,
                TypedTurns = a.TypedTurns,
                Sessions = a.Sessions.Count,
                AgentDrivenTurns = a.AgentDrivenTurns,
            });

        foreach (var r in repos.Values.OrderByDescending(r => r.Turns).ThenBy(r => r.Key, StringComparer.Ordinal))
            dto.Repos.Add(new ThrottleRepoDto
            {
                Repo = r.Key,
                RepoName = r.Leaf,
                Turns = r.Turns,
                VoiceTurns = r.VoiceTurns,
                TypedTurns = r.TypedTurns,
                Sessions = r.Sessions.Count,
                Checkouts = r.Checkouts.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            });

        return dto;
    }

    /// <summary>The surfaces the headline reports, in the order every page draws them, with the Gateway's own
    /// display name for each. Every one is served on every answer, zero or not, so no consumer keeps a list.</summary>
    public static readonly IReadOnlyList<(string Surface, string Label)> Surfaces = new[]
    {
        ("desktop", "Desktop"),
        ("cockpit", "Cockpit"),
        ("phone", "Phone"),
        ("unknown", "Unknown"),
    };

    /// <summary>
    /// THE ONE COMPUTATION OF THE FINAL RATIOS (finding F-01). The spoken, typed and per-surface shares of the
    /// counted turns, each with the whole-number percentage the reader is shown, rounded half up - the rounding
    /// both consumers used to do for themselves, now done once. With nothing counted, <c>HasData</c> is false
    /// and every share and percent is null: the empty state is decided here, and a consumer renders it rather
    /// than printing 0%.
    /// </summary>
    /// <exception cref="InvalidOperationException">A bucket carries a surface this definition does not know.
    /// Refused rather than folded into a guessed surface, the same way a malformed origin is refused.</exception>
    public static ThrottleHeadlineDto Headline(long counted, long voice, long typed, IEnumerable<ThrottleBucketDto> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var bySurface = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var b in buckets)
        {
            if (!Surfaces.Any(s => string.Equals(s.Surface, b.Surface, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"A counted bucket carries the surface '{b.Surface}', which is none of " +
                    string.Join(", ", Surfaces.Select(s => s.Surface)) + ". The headline is refused rather than " +
                    "folding an unknown surface into a guessed one; a new surface is a definition change made here on purpose.");
            bySurface[b.Surface] = bySurface.TryGetValue(b.Surface, out var t) ? t + b.Turns : b.Turns;
        }

        var headline = new ThrottleHeadlineDto
        {
            Denominator = counted,
            HasData = counted > 0,
            Voice = ShareOf(voice, counted),
            Typed = ShareOf(typed, counted),
        };
        foreach (var (surface, label) in Surfaces)
        {
            var turns = bySurface.TryGetValue(surface, out var t) ? t : 0;
            var entry = ShareOf(turns, counted);
            headline.Surfaces.Add(new ThrottleSurfaceShareDto
            {
                Surface = surface, Label = label, Turns = entry.Turns, Share = entry.Share, Percent = entry.Percent,
            });
            if (surface == "phone") headline.Phone = entry;
        }
        return headline;
    }

    /// <summary>One share of the denominator, with its rounding done: half up to a whole percent, the same
    /// rule for every share on every consumer. Null share and percent when the denominator is zero.</summary>
    private static ThrottleShareDto ShareOf(long turns, long denominator)
    {
        if (denominator <= 0) return new ThrottleShareDto { Turns = turns, Share = null, Percent = null };
        var share = (double)turns / denominator;
        return new ThrottleShareDto
        {
            Turns = turns,
            Share = share,
            Percent = (int)Math.Floor(share * 100.0 + 0.5),
        };
    }

    /// <summary>"modality/surface" into its two tokens. Malformed is a producer defect and fails loud.</summary>
    private static (string Modality, string Surface) ParseOrigin(string origin, LedgerSubmission row)
    {
        var slash = origin.IndexOf('/');
        if (slash <= 0 || slash == origin.Length - 1)
            throw new InvalidOperationException(
                $"A turn-submitted row for session {row.SessionId} at {row.OccurredUtc:O} carries the InputOrigin " +
                $"'{origin}', which is not '<modality>/<surface>'. The producer wrote a malformed origin; the " +
                "figure is refused rather than guessed.");
        var modality = origin[..slash].Trim().ToLowerInvariant();
        var surface = origin[(slash + 1)..].Trim().ToLowerInvariant();
        if (modality != "typed" && modality != "voice")
            throw new InvalidOperationException(
                $"A turn-submitted row for session {row.SessionId} at {row.OccurredUtc:O} carries the modality " +
                $"'{modality}', which is neither typed nor voice. The figure is refused rather than guessed.");
        return (modality, surface);
    }

    /// <summary>The grouping key for one counted turn's repository, or null when history holds nothing that
    /// names one. The resolved "owner/repo" name wins; a checkout path alone folds by its folder name so the
    /// same repository worked from two machines is one row; a session history does not hold, or holds with
    /// neither, is unattributed.</summary>
    private static (string Key, string Leaf, string? Checkout)? RepoKeyOf(string sessionId, IReadOnlyDictionary<string, SessionFacts> sessions)
    {
        if (!sessions.TryGetValue(sessionId, out var facts)) return null;
        if (!string.IsNullOrWhiteSpace(facts.RepoName))
            return (facts.RepoName!, Leaf(facts.RepoName!), facts.RepoPath);
        if (!string.IsNullOrWhiteSpace(facts.RepoPath))
        {
            var leaf = Leaf(facts.RepoPath!);
            return (leaf, leaf, facts.RepoPath);
        }
        return null;
    }

    private static string Leaf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    /// <summary>The same display spelling the private Agents page has always used.</summary>
    public static string AgentDisplayName(string agent) => agent switch
    {
        "" => "(unknown)",
        "ClaudeCode" => "Claude Code",
        "RawCli" => "Raw CLI",
        _ => agent,
    };

    private static AgentTally Tally(Dictionary<string, AgentTally> agents, string key)
        => agents.TryGetValue(key, out var t) ? t : agents[key] = new AgentTally(key);

    private sealed class AgentTally(string key)
    {
        public string Key { get; } = key;
        public long Turns, VoiceTurns, TypedTurns, AgentDrivenTurns;
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RepoTally(string key, string leaf)
    {
        public string Key { get; } = key;
        public string Leaf { get; } = leaf;
        public long Turns, VoiceTurns, TypedTurns;
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Checkouts { get; } = new(StringComparer.Ordinal);
    }
}
