using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The morning report for ONE account and ONE calendar day (issue #2119, slice 2 of #2096) - the JSON the
/// website's 7:00 cron reads, renders into the approved email design, and sends. Three headline numbers, the
/// needs-your-attention list, and the exact window every number was measured over.
///
/// THE HONESTY RULE IS STRUCTURAL, NOT EDITORIAL. Anything the Gateway has no data for is ABSENT from the
/// JSON - never zero-filled, never estimated. A missing <see cref="MorningReportStatsDto.SessionsRan"/> means
/// "this Gateway holds no session history for this account", which is a different statement from "no sessions
/// ran", and the email must be able to tell them apart. Every optional member below is therefore
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>: a null does not serialize at all.
///
/// The field names here are the CONTRACT the website sender is coded against (owner-relayed, 24 July 2026).
/// They are camelCase on the wire (minimal-API web defaults). Renaming one breaks the email.
/// </summary>
public sealed class MorningReportDto
{
    /// <summary>The account this report is about, echoed back exactly as the caller asked for it, so the
    /// sender can prove the report it is about to email belongs to the recipient it is emailing.</summary>
    public string Account { get; set; } = "";

    /// <summary>The exact coordinates every number below was measured over - the resolved UTC range, plus
    /// the calendar day and zone it came from. Every number carries its coordinates.</summary>
    public MorningReportWindowDto Window { get; set; } = new();

    /// <summary>The three headline numbers. Each is individually optional (see the honesty rule).</summary>
    public MorningReportStatsDto Stats { get; set; } = new();

    /// <summary>How the account's microphones are doing, ranked best first. NULL - the whole section
    /// absent - when nothing has been measured yet, per the honesty rule.</summary>
    public MorningMicrophonesDto? Microphones { get; set; }

    /// <summary>
    /// The needs-your-attention list, one typed item per row the email renders. ALWAYS PRESENT, possibly
    /// empty: an empty list is real knowledge ("nothing is waiting on you"), unlike an absent stat.
    /// Items are polymorphic and discriminated by their <c>type</c> string; the sender tolerates a type it
    /// does not know (it logs and does not render it), so a new item type never breaks a sent email.
    /// </summary>
    public List<MorningAttentionItemDto> Attention { get; set; } = new();

    /// <summary>
    /// An optional single-sentence observation about the day. The Gateway EMITS NOTHING HERE TODAY - it
    /// reports measurements and invents no prose. The member exists because the contract reserves the key;
    /// it is absent from the JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Observation { get; set; }
}

/// <summary>The resolved reporting window: the UTC range the calendar day covers in the caller's zone.</summary>
/// <summary>
/// How the account's microphones are doing, ranked best first, for the daily report.
///
/// This is a SECTION rather than an attention row on purpose. The attention list answers "what needs
/// you today"; this answers "which of your microphones should you be using", which is worth seeing even
/// when nothing is wrong - it is the comparison that makes the advice actionable, and a user cannot
/// make it for themselves because the defect that matters most sounds merely dull to a human ear.
///
/// THE HONESTY RULE APPLIES: this whole section is ABSENT when the Gateway holds no measurements for the
/// account, or too few for any device to be judged. "We have never measured your microphones" and "your
/// microphones are fine" are different statements and only one of them has been established.
/// </summary>
public sealed class MorningMicrophonesDto
{
    /// <summary>One line naming the best microphone, or saying they are all fine.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What to change, present ONLY when switching or fixing something would actually help.
    /// Absent when the microphones are all good - a daily email that always has advice is one nobody
    /// reads.</summary>
    public string? Advice { get; set; }

    /// <summary>The devices, best first. Never empty when this section is present.</summary>
    public List<MorningMicrophoneDto> Devices { get; set; } = new();
}

/// <summary>One microphone's standing in the daily report.</summary>
public sealed class MorningMicrophoneDto
{
    /// <summary>The name the operating system gave it, or "Unnamed microphone".</summary>
    public string Device { get; set; } = "";

    /// <summary>How many dictations this verdict rests on.</summary>
    public int Samples { get; set; }

    /// <summary>"good" or "bad" - the same fold the Cockpit renders.</summary>
    public string Status { get; set; } = "";

    /// <summary>A plain sentence about this device, already written. The email prints it verbatim.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Share of this device's dictations that arrived band-limited (0..1).</summary>
    public double NarrowbandShare { get; set; }

    /// <summary>Share of this device's dictations that were distorting (0..1).</summary>
    public double ClippingShare { get; set; }

    /// <summary>Typical level of the voice, in dBFS.</summary>
    public double SpeechLevelDb { get; set; }

    /// <summary>Typical margin of the voice over the room, in dB.</summary>
    public double SignalToNoiseDb { get; set; }
}

public sealed class MorningReportWindowDto
{
    /// <summary>Inclusive start of the reported day, in UTC.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>EXCLUSIVE end of the reported day, in UTC. A day is [start, end).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>The calendar day being reported on, as the caller supplied it (yyyy-MM-dd).</summary>
    public string Date { get; set; } = "";

    /// <summary>The IANA zone the calendar day was resolved in, as the caller supplied it.</summary>
    public string Tz { get; set; } = "";
}

/// <summary>
/// The three headline numbers. Each is null - and therefore ABSENT from the JSON - when this Gateway holds
/// no backing data at all for the account. A present zero is a measured zero and may be rendered as such.
/// </summary>
public sealed class MorningReportStatsDto
{
    /// <summary>Distinct sessions that recorded at least one state transition inside the window.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SessionsRan { get; set; }

    /// <summary>Workflow runs ACCEPTED in the window - the outcome ledger's delivered count.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WorkDelivered { get; set; }

    /// <summary>Hosted-AI service dollars spent in the window, CEIL-rounded to the cent so the figure can
    /// never undercount real money.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? HostedAiSpendUsd { get; set; }
}

/// <summary>
/// One row of the needs-your-attention list. The <c>type</c> string is the discriminator the email renderer
/// switches on; the derived shapes carry the fields that row needs plus its deep-link target.
///
/// Polymorphic serialization is declared WITHOUT a type-discriminator name, so System.Text.Json writes the
/// runtime type's properties and adds no synthetic <c>$type</c> key - this class's own
/// <see cref="Type"/> property is the discriminator, and it is the one the contract names.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(WaitingSessionAttentionDto))]
[JsonDerivedType(typeof(StaleWorktreesAttentionDto))]
[JsonDerivedType(typeof(UnmergedBranchesAttentionDto))]
public abstract class MorningAttentionItemDto
{
    /// <summary>The item's discriminator: "waiting-session", "stale-worktrees", "unmerged-branches".</summary>
    public abstract string Type { get; }
}

/// <summary>Type strings for <see cref="MorningAttentionItemDto"/>, so producer and tests share one spelling.</summary>
public static class MorningAttentionTypes
{
    public const string WaitingSession = "waiting-session";
    public const string StaleWorktrees = "stale-worktrees";
    public const string UnmergedBranches = "unmerged-branches";
}

/// <summary>A session whose last recorded state is waiting on the human, and how long it has been there.</summary>
public sealed class WaitingSessionAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.WaitingSession;

    /// <summary>The session's friendly name when the Gateway can see it live, otherwise its session id -
    /// never blank, so the email always has something to name the row with.</summary>
    public string Session { get; set; } = "";

    /// <summary>The session's repository path, when a live record supplies one. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; set; }

    /// <summary>When the session entered its current waiting state, from the durable event ledger.</summary>
    public DateTime WaitingSinceUtc { get; set; }

    /// <summary>How long it has been waiting, in hours, at the moment the report was built.</summary>
    public double AgeHours { get; set; }
}

/// <summary>Stale worktrees in one repository. Only worktrees whose branch is MERGED and whose tip is older
/// than the staleness bar are ever marked <see cref="SafeToRemove"/>.</summary>
public sealed class StaleWorktreesAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.StaleWorktrees;

    public string Repo { get; set; } = "";
    public int Count { get; set; }

    /// <summary>The worktrees' directory BASE NAMES - never full paths.</summary>
    public List<string> Worktrees { get; set; } = new();

    public double OldestAgeDays { get; set; }

    /// <summary>True only when every worktree in this item is old AND its branch is merged into the
    /// default branch. An unmerged stale worktree is reported as its own item with this false.</summary>
    public bool SafeToRemove { get; set; }
}

/// <summary>Branches in one repository that are unmerged to the default branch, oldest first.</summary>
public sealed class UnmergedBranchesAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.UnmergedBranches;

    public string Repo { get; set; } = "";
    public List<UnmergedBranchDto> Branches { get; set; } = new();
}

/// <summary>One unmerged branch: its name, how old its tip is, and how many commits it carries.</summary>
public sealed class UnmergedBranchDto
{
    public string Name { get; set; } = "";
    public double AgeDays { get; set; }
    public int Commits { get; set; }
}
