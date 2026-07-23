namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One paid-entitlement record for a hosted account - the row that decides whether an account may enroll a
/// device against the hosted Gateway at all.
///
/// THIS TABLE IS NOT OURS TO CREATE OR WRITE. It is the agreed cross-team hand-off point: the website's
/// payment webhook runs as the service role and UPSERTs it on subscription lifecycle events, while the
/// Gateway runs as a scoped role that holds SELECT and nothing more. The Gateway cannot read the website's
/// own member table - different schema, scoped role, and that isolation is deliberate - so this single
/// shared table exists precisely so the payment side can tell the Gateway one fact without widening the
/// Gateway's reach across the boundary. It is therefore mapped as EXCLUDED FROM MIGRATIONS: we describe
/// its shape so we can read it, and we never emit anything that would create, alter or seed it.
///
/// Keyed by the STABLE account subject - the same identity the tenant mapping resolves by - never the
/// email, which can change over an account's life.
///
/// Nothing on this row is ever logged: the subject is personally identifying, and the subscription
/// reference belongs to the payment provider.
/// </summary>
public sealed class EntitlementEntity
{
    /// <summary>The verified account subject this entitlement belongs to. Primary key. Never logged.</summary>
    public string Subject { get; set; } = "";

    /// <summary>
    /// The subscription state as the payment side computed it: active, past_due, or canceled. Read as an
    /// opaque string and compared exactly - the Gateway does not re-derive it, and a value it does not
    /// recognise is NOT entitled, because an unknown state is not an entitled one.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// The end of the paid period (UTC). This is what makes a past_due account's grace window finite: the
    /// payment provider retries a failed payment for a while, and this is when that window closes.
    /// </summary>
    public DateTime? CurrentPeriodEnd { get; set; }

    /// <summary>The payment provider's subscription reference. Read-only here, and never logged.</summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>
    /// Which paid plan this entitlement grants: <c>hosted</c> or <c>pro</c>. Written by the payment side and
    /// read-only here. NULLABLE on purpose: rows written before this column existed (or by a webhook that has
    /// not been updated) arrive null, and a null tier is not an error - it is an older row.
    ///
    /// The tier does NOT gate hosted ENROLLMENT: any live, paid entitlement of EITHER tier lets an account
    /// enroll a device, because enrolling is "may this account reach a hosted tenant at all", which both plans
    /// grant. The tier decides a CAPABILITY inside the tenant (the wingman), which is a separate concern read
    /// off this same value - so the reader exposes it, and the enrollment gate ignores it.
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>When the payment side last wrote this row (UTC).</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Whether this subscription is a LIVE one rather than a payment-provider TEST-mode one.
    ///
    /// A test-mode subscription costs nothing to create, so treating one as entitlement would be a paywall
    /// bypass - and a bypass in the deny-OPEN direction, which is the expensive direction. On the production
    /// hosted Gateway this must be explicitly true: a false value AND a NULL are both refused. Null matters
    /// as much as false, because a row written before this column existed, or by a webhook that forgot it,
    /// arrives as null - and "we did not record whether this was real money" is not evidence that it was.
    /// </summary>
    public bool? Livemode { get; set; }
}
