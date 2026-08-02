namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// When this tenant last exercised <c>DELETE /prompts</c>, in the <c>prompt_erasure_watermarks</c> table -
/// one row per tenant, or none if they never have.
///
/// WHY THIS EXISTS. The erasure clears the prompt-derived fields in three statements and returns. That is
/// only an erasure if nothing puts the material back afterwards, and three writers can: the summariser
/// takes a pending row, spends seconds or minutes asking a model, and writes the summary fields back; the
/// roll-up pass does the same for its cached paragraph; and the prompt ingest copies a session's first
/// prompt onto the history row. Each of those computes from material read BEFORE the delete and commits
/// AFTER it. The metadata reset makes the first one worse rather than better - it moves the summary kind
/// back to null, which is precisely the state that stops <c>StoreGeneratedSummary</c> refusing the write.
///
/// So a delete that reported success could be silently undone seconds later, with no error anywhere and
/// the member's own words back on the History page.
///
/// HOW IT IS USED. The erasure stamps this row. Every prompt-derived write then compares the material it
/// was computed from against the stamp and refuses to commit anything older. The comparison is on the
/// MATERIAL, not on the moment of writing: a write is only safe if what it is made of came into existence
/// after the member's delete.
///
/// WHY IT IS DURABLE RATHER THAN A FIELD IN MEMORY. Two of the three races are in-process and die with the
/// process, so memory would do for them. The third does not: the Director's ingest deliberately retries
/// records it previously failed to deliver (<c>ConversationIngestor</c>), so a push arriving tomorrow, or
/// after a Gateway restart, can carry prompts from before the delete - exactly the prompts the member
/// meant to erase. A watermark that a restart forgets would let those through, and the guard would fail
/// OPEN and silently.
///
/// WHAT IT DOES NOT COVER, stated because the gap is real. The prompt log now consults this watermark at
/// the door and refuses records DATED at or before the erasure, so the ordinary retry is kept out. What it
/// cannot judge is a record dated AFTER the erasure: the Gateway keeps no ledger of what it has already
/// seen, so such a record is indistinguishable from a prompt the member sent a second ago, and it is
/// admitted. A Director whose clock runs ahead produces exactly that shape. Closing it means the Director
/// honouring the delete rather than retrying at all - issue #2380.
/// </summary>
public sealed class PromptErasureWatermarkEntity : TenantScopedEntity
{
    /// <summary>When the erasure ran (UTC). Material older than this is refused by the derived-content
    /// writers. Only ever moves FORWARD - a later delete raises it, nothing lowers it.</summary>
    public DateTime ErasedAtUtc { get; set; }
}
