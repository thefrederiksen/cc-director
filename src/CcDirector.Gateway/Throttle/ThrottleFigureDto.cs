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

    /// <summary>True when the caller asked for no window and got the default (the ledger's whole retention).</summary>
    public bool IsDefault { get; set; }

    /// <summary>The Gateway's own plain-English name for the window, rendered verbatim by the clients (the
    /// dumb-client rule): "Last 30 days", or the explicit dates when a caller named them.</summary>
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
