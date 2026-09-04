namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One decision to record turn ends - the switch that turns the turn log on, and the record of who turned
/// it on, for whom, and why.
///
/// GLOBAL, NOT TENANT-SCOPED, and that is deliberate. This is an operator setting rather than customer
/// data: an administrator switches capture on for an account that is not their own, so a row scoped to the
/// account it names could not be written by the person entitled to write it, nor read by the recorder,
/// which runs outside any one account's partition when it decides whether to record at all.
///
/// IT NAMES THE ACCOUNT, and that is a deliberate departure from this Gateway's rule that a raw tenant id
/// never reaches a log. The same reasoning applies here as to the trial ledger: a record that cannot name
/// whose terminal is being captured cannot answer the only questions that matter about it - who agreed to
/// this, and what has to be deleted if they withdraw. A capture we cannot attribute is a capture we cannot
/// stop.
///
/// SCOPE IS TWO DIMENSIONS AND THE MOST SPECIFIC ONE WINS. <see cref="Account"/> and
/// <see cref="Machine"/> each hold either an exact identifier or <see cref="Any"/>, so capture can be
/// switched on for one machine, for a whole account, or for the whole fleet - and, because an exact row
/// beats a wildcard row, a single noisy machine can be switched back OFF inside an account that is
/// otherwise on. A switch turned off is a row saying so, not a row deleted: "we decided not to" and "nobody
/// ever decided" are different facts and the corpus has to be able to tell them apart.
/// </summary>
public sealed class TurnLogSwitchEntity
{
    /// <summary>The wildcard both scope columns accept, meaning every account or every machine. A literal
    /// rather than null so the unique index over the pair actually constrains: two null-scoped rows would
    /// both be allowed by most providers, and then which one applies is a coin toss.</summary>
    public const string Any = "*";

    /// <summary>The row's identity, minted here. Private setter for the same reason the trial ledger has
    /// one: no caller supplies it, so it cannot be written by hand, while a persisted row still
    /// materializes normally.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>The account this decision covers - a tenant identifier, or <see cref="Any"/>.</summary>
    public string Account { get; set; } = Any;

    /// <summary>The machine this decision covers - a Director identifier, or <see cref="Any"/>.</summary>
    public string Machine { get; set; } = Any;

    /// <summary>Whether capture is ON for that scope. False is a real decision and is stored as one.</summary>
    public bool Enabled { get; set; }

    /// <summary>Who decided, as the calling surface knows them. Required: the whole point of keeping this
    /// beside the switch is that somebody can be asked whether permission was obtained.</summary>
    public string Actor { get; set; } = "";

    /// <summary>Why - and for another account, this is where the permission itself is recorded.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the Gateway wrote this row. Server-stamped, never caller-supplied.</summary>
    public DateTime RecordedUtc { get; set; }
}
