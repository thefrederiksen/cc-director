namespace CcDirector.Gateway.Throttle;

/// <summary>
/// The Your Throttle figure as the library serves it - every count of TURNS the page shows, from one
/// definition (<see cref="ThrottleDefinition"/>) over one substrate (the submission ledger). Counts and
/// ratios only, never the text of anything typed or said. No character volume anywhere on it (ruling R16):
/// the ledger carries none, and a figure the page cannot vouch for is not shown with an apology attached.
/// </summary>
public sealed class ThrottleFigureDto
{
    /// <summary>The predicate, verbatim (<see cref="ThrottleDefinition.Predicate"/>), so a reader can check
    /// the number against the sentence.</summary>
    public string Definition { get; set; } = "";

    /// <summary>The unit of every share here (<see cref="ThrottleDefinition.Unit"/>).</summary>
    public string Unit { get; set; } = "";

    /// <summary>The window this figure describes. Stated on every answer: a number that does not say which
    /// stretch of time it covers is the ambiguity this mission exists to remove.</summary>
    public ThrottleWindowDto Window { get; set; } = new();

    /// <summary>What the ledger holds for this tenant, so the page can say where the record begins.</summary>
    public ThrottleLedgerDto Ledger { get; set; } = new();

    /// <summary>
    /// THE HEADLINE, FINISHED HERE (final inspection finding F-01). The two shares the reader is shown - spoken
    /// against typed, and from the phone - with their denominator, their rounded whole-number percentages and
    /// the empty state, computed ONCE in the library. A consumer renders these fields verbatim; it never divides
    /// the counts below or re-totals the buckets. Before this block existed the browser summed the buckets and
    /// divided, the mentor report divided the top-level counts, and a served answer whose counts and buckets
    /// disagreed would have printed two different headlines about one week.
    /// </summary>
    public ThrottleHeadlineDto Headline { get; set; } = new();

    /// <summary>Turns the predicate counted: every turn-submitted row in the window carrying an input origin.</summary>
    public long Turns { get; set; }

    public long VoiceTurns { get; set; }

    public long TypedTurns { get; set; }

    /// <summary>Distinct sessions the counted turns went into.</summary>
    public int Sessions { get; set; }

    /// <summary>The counted turns by (modality, surface), ordinal order.</summary>
    public List<ThrottleBucketDto> Buckets { get; set; } = new();

    /// <summary>The counted turns per UTC clock hour ("yyyy-MM-ddTHH"), oldest first, hours with none omitted.</summary>
    public List<ThrottleHourDto> HourlyTurns { get; set; } = new();

    /// <summary>The counted turns per agent kind the session was running, most-driven first.</summary>
    public List<ThrottleAgentDto> Agents { get; set; } = new();

    /// <summary>The counted turns per repository through the session-history join, most-driven first.</summary>
    public List<ThrottleRepoDto> Repos { get; set; } = new();

    /// <summary>Counted turns whose session history holds no repository for. Disclosed beside the split
    /// (R7), never folded into a guessed row.</summary>
    public long ReposUnattributedTurns { get; set; }

    /// <summary>The population the predicate left out, disclosed as counts beside the share (R7, R17).</summary>
    public ThrottleExcludedDto Excluded { get; set; } = new();

    /// <summary>Turns one session drove into another (the Agent send source) - the fleet driving itself.
    /// Reported beside the human figure from the same ledger, never inside it. Equal to
    /// <see cref="ThrottleExcludedDto.AgentDriven"/>, surfaced at the top level because the Agents page
    /// reads it as the leverage numerator.</summary>
    public long AgentDrivenTurns { get; set; }
}

public sealed class ThrottleWindowDto
{
    /// <summary>Inclusive start, UTC.</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>Exclusive end, UTC.</summary>
    public DateTime ToUtc { get; set; }

    /// <summary>True when the caller asked for no window and got the default (a rolling
    /// <see cref="ThrottleDefinition.DefaultWindowDays"/> days ending now).</summary>
    public bool IsDefault { get; set; }

    /// <summary>The Gateway's own plain-English name for the window, rendered verbatim by the clients (the
    /// dumb-client rule): "Last 7 days", the week's name in the caller's zone, or the explicit dates when a
    /// caller named them.</summary>
    public string Label { get; set; } = "";

    /// <summary>Which of the four query forms produced this window (<see cref="ThrottleWindowKinds"/>):
    /// <c>default</c>, <c>days</c>, <c>week</c> or <c>explicit</c>. The selector marks its choice from this.</summary>
    public string Kind { get; set; } = ThrottleWindowKinds.Explicit;

    /// <summary>The rolling length in days when <see cref="Kind"/> is <c>default</c> or <c>days</c>; null otherwise.</summary>
    public int? Days { get; set; }

    /// <summary>The ISO week ("2026-W35") when <see cref="Kind"/> is <c>week</c>; null otherwise.</summary>
    public string? Week { get; set; }

    /// <summary>The selector's options, served on EVERY answer in order
    /// (<see cref="ThrottleWindowChoices"/>), so no client keeps a list of lengths of its own.</summary>
    public List<ThrottleWindowChoiceDto> Choices { get; set; } = new();
}

/// <summary>One length the period selector offers, with the Gateway's name for it.</summary>
public sealed class ThrottleWindowChoiceDto
{
    public int Days { get; set; }

    public string Label { get; set; } = "";
}

public sealed class ThrottleLedgerDto
{
    /// <summary>How long the ledger keeps a submission (<see cref="ThrottleDefinition.RetentionDays"/>).</summary>
    public int RetentionDays { get; set; }

    /// <summary>The oldest turn-submitted row the ledger holds for this tenant, or null when it holds none.
    /// When this is later than the window's start, the record does not reach the whole window and the page
    /// says so.</summary>
    public DateTime? EarliestUtc { get; set; }
}

public sealed class ThrottleExcludedDto
{
    /// <summary>Every turn-submitted row in the window with no input origin - the literal R17 population.</summary>
    public long NoInputOrigin { get; set; }

    /// <summary>Of those, the ones stamped Agent: another session prompting this one. Attributed, and
    /// reported as the fleet driving itself.</summary>
    public long AgentDriven { get; set; }

    /// <summary>Of those, the ones stamped Framework: text the product authored (a seed prompt, a handover).
    /// Nobody's turn.</summary>
    public long Framework { get; set; }

    /// <summary>The remainder: a person's submission the product could not place on a surface. These are the
    /// rows disclosed beside the share as "outside every number here".</summary>
    public long Unresolved { get; set; }
}

public sealed class ThrottleBucketDto
{
    public string Modality { get; set; } = "";
    public string Surface { get; set; } = "";
    public long Turns { get; set; }
}

public sealed class ThrottleHourDto
{
    public string Hour { get; set; } = "";
    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
}

public sealed class ThrottleAgentDto
{
    /// <summary>The agent token the ledger recorded (the AgentKind name), or "" when none.</summary>
    public string Agent { get; set; } = "";

    /// <summary>The display name; "(unknown)" when empty.</summary>
    public string AgentName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }

    /// <summary>Distinct sessions the counted turns went into under this agent.</summary>
    public int Sessions { get; set; }

    /// <summary>Turns OTHER sessions drove into the sessions running this agent.</summary>
    public long AgentDrivenTurns { get; set; }
}

public sealed class ThrottleRepoDto
{
    /// <summary>The grouping key: the resolved "owner/repo" name, or the checkout's folder name when the
    /// session history holds a path and no name.</summary>
    public string Repo { get; set; } = "";

    /// <summary>The display leaf of <see cref="Repo"/>.</summary>
    public string RepoName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }

    /// <summary>Distinct sessions the counted turns went into in this repository.</summary>
    public int Sessions { get; set; }

    /// <summary>The checkout paths those sessions ran in, sorted.</summary>
    public List<string> Checkouts { get; set; } = new();
}

/// <summary>
/// The library's finished headline (finding F-01). Every ratio a page or a report prints as THE figure is here,
/// with its denominator and its rounding done: a consumer reads <see cref="Voice"/>.<c>Percent</c> and prints it.
/// <see cref="HasData"/> is the empty state - false when nothing was counted, and then every share and percent
/// is null so no consumer can print a fabricated 0%. <see cref="Surfaces"/> is every surface the figure knows,
/// in one fixed order, each with its own share; <see cref="Phone"/> is the same phone entry surfaced at the top
/// because it is the second ring.
/// </summary>
public sealed class ThrottleHeadlineDto
{
    /// <summary>The denominator of every share here: the counted turns (<see cref="ThrottleFigureDto.Turns"/>).</summary>
    public long Denominator { get; set; }

    /// <summary>False when nothing was counted. Then every share and percent below is null: the empty state is
    /// the library's ruling, and a consumer renders it rather than a number.</summary>
    public bool HasData { get; set; }

    public ThrottleShareDto Voice { get; set; } = new();

    public ThrottleShareDto Typed { get; set; } = new();

    /// <summary>The phone's entry of <see cref="Surfaces"/>, at the top because it is the second ring.</summary>
    public ThrottleShareDto Phone { get; set; } = new();

    /// <summary>Every surface, in the order the pages draw them, each with its turns and its share of the
    /// denominator. Always all four known surfaces, zero or not, so a consumer never keeps a list of its own.</summary>
    public List<ThrottleSurfaceShareDto> Surfaces { get; set; } = new();
}

/// <summary>One share of the headline denominator: the count, the fraction, and the whole-number percentage
/// the reader sees, rounded half up. Fraction and percent are null when the denominator is zero.</summary>
public class ThrottleShareDto
{
    public long Turns { get; set; }

    /// <summary>Turns over the denominator, in [0, 1]; null when nothing was counted.</summary>
    public double? Share { get; set; }

    /// <summary>The percentage the reader is shown, rounded half up to a whole number; null when nothing was
    /// counted. THIS is the number a ring prints - never a consumer's own rounding of <see cref="Share"/>.</summary>
    public int? Percent { get; set; }
}

public sealed class ThrottleSurfaceShareDto : ThrottleShareDto
{
    /// <summary>The surface token the ledger recorded: desktop, cockpit, phone or unknown.</summary>
    public string Surface { get; set; } = "";

    /// <summary>The Gateway's own display name for the surface, rendered verbatim (the dumb-client rule).</summary>
    public string Label { get; set; } = "";
}
