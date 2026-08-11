namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One administrator extension of a free Pro trial - the audit record of a human deciding to hand somebody
/// paid product for free, and why.
///
/// WHY THE LEDGER LIVES HERE, BESIDE THE TABLE IT DESCRIBES. The extension itself is an UPDATE of
/// <see cref="AccountTrialEntity.ExpiresAtUtc"/>, which only this Gateway's role may write. Putting the
/// record of that write anywhere else - in the website's schema, say - would mean two systems each holding
/// half of one fact, joined by nothing, and the two halves would be written in separate transactions across
/// a network. Then an extension whose audit row failed to reach the other side would be free product handed
/// out with nothing saying who did it, and an audit row whose extension failed would name a change that
/// never happened. Both are lies, in opposite directions. Here the row and the extension are written in ONE
/// transaction by the one role that owns both, so neither can exist without the other.
///
/// IT IDENTIFIES THE RECIPIENT, and that is a deliberate departure from the rule this Gateway follows in its
/// LOGS, where a subject never appears. A log is diagnostic exhaust read by anyone debugging; this is a
/// restricted ledger whose entire purpose is to say who received something. An audit that cannot name the
/// recipient is not an audit. Nothing on this row is ever logged.
///
/// APPEND-ONLY. A row is written once and never updated or deleted: an audit trail you can rewrite is not an
/// audit trail. It is deliberately NOT keyed on the subject - one account may be extended more than once,
/// and each decision is its own record.
///
/// GLOBAL, not tenant-scoped, for exactly the reason <see cref="AccountTrialEntity"/> is: it is keyed by the
/// account subject, which is the identity the trial ledger itself uses, and a trial exists before and
/// independently of any tenant.
/// </summary>
public sealed class TrialExtensionEntity
{
    /// <summary>
    /// The row's identity, minted by the Gateway. A private setter for the same reason
    /// <see cref="GatewayMintedKeyEntity"/> has one: no caller supplies this value, so writing
    /// <c>new TrialExtensionEntity { Id = ... }</c> does not compile. EF still materializes persisted rows
    /// normally - it writes the backing field directly on load - so only NEW rows take a minted value.
    ///
    /// A Guid rather than a database-generated counter so the key means the same thing on both providers
    /// (Postgres identity and SQLite AUTOINCREMENT are not the same mechanism) and the ledger does not depend
    /// on one of them behaving like the other.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>The account subject whose trial was extended - the same key
    /// <see cref="AccountTrialEntity.Subject"/> uses. Never logged.</summary>
    public string Subject { get; set; } = "";

    /// <summary>
    /// The member's email as it read at the moment of the decision, for human eyes. Nullable and NOT the
    /// identity: it is kept alongside the subject so the ledger stays readable after an account is deleted or
    /// its address changes, and it is never used to find anything.
    /// </summary>
    public string? MemberEmail { get; set; }

    /// <summary>When the trial being extended originally began. Copied onto the row so the record still reads
    /// correctly if the trial row is later removed.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>The end instant the trial carried BEFORE this decision. Half of what makes the row an audit
    /// rather than a note: without it, nobody can tell how much was given away.</summary>
    public DateTime PreviousExpiresAtUtc { get; set; }

    /// <summary>The end instant the trial carries AFTER this decision.</summary>
    public DateTime NewExpiresAtUtc { get; set; }

    /// <summary>Who made the decision - the administrator's identity as the calling surface knows it.
    /// Required: "who decided" is the question this ledger exists to answer.</summary>
    public string Actor { get; set; } = "";

    /// <summary>Why the decision was made. Required by the capability, not merely by a screen: a ledger of
    /// blank reasons answers no question anybody will actually ask ("why does this account have six
    /// weeks?").</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the Gateway wrote this row - server-stamped, never caller-supplied.</summary>
    public DateTime RecordedUtc { get; set; }
}
