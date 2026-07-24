using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// What the entitlement read actually established. THREE outcomes, not two, and keeping them apart is the
/// whole point of this type.
///
/// A boolean would collapse "I looked and there is no entitlement" together with "I could not look", and
/// those demand OPPOSITE answers: the first is a refusal the caller must act on, the second is a temporary
/// condition the caller must retry. Collapsing them either locks out a paying customer when the database
/// hiccups, or - far worse - hands the product away free the moment a read fails. An unresolvable state and
/// a negative state are different answers and must never share a code path.
/// </summary>
public enum EntitlementOutcome
{
    /// <summary>The read succeeded and the account holds a currently-valid entitlement.</summary>
    Entitled,

    /// <summary>
    /// The read SUCCEEDED and the account holds no currently-valid entitlement - either no row at all, or a
    /// row whose state or period says it is not valid now. This is knowledge, and it justifies a refusal.
    /// </summary>
    NotEntitled,

    /// <summary>
    /// The read FAILED - connection, timeout, permission, malformed data, anything at all. We do not KNOW.
    /// This must never be answered as a refusal and must never be answered as a grant: the only honest reply
    /// is "ask again".
    /// </summary>
    Unknown,
}

/// <summary>
/// The full result of one entitlement read: the three-way <see cref="EntitlementOutcome"/> and the plan
/// <see cref="Tier"/> the row carries (<c>hosted</c>, <c>pro</c>, or null on an older row / a read that found
/// or read nothing).
///
/// The two fields answer DIFFERENT questions and must be read together. <see cref="Outcome"/> answers "may
/// this account reach hosted at all" - the enrollment gate's question, which is tier-agnostic. <see cref="Tier"/>
/// answers "which plan" - a capability question (the wingman) - and it is meaningful ONLY when paired with an
/// <see cref="EntitlementOutcome.Entitled"/> outcome: a tier next to <see cref="EntitlementOutcome.NotEntitled"/>
/// is the plan of a non-granting row and grants nothing, and a tier is always null on
/// <see cref="EntitlementOutcome.Unknown"/> because a failed read establishes nothing.
/// </summary>
/// <param name="Outcome">The three-way entitlement outcome. Never fold the three into two.</param>
/// <param name="Tier">The plan tier the row records (hosted|pro), or null. Never gates enrollment.</param>
/// <param name="CurrentPeriodEnd">The paid-period boundary from the row, present only on an
/// <see cref="EntitlementOutcome.Entitled"/> outcome. The cancellation cutoff (MTR-15) CLIPS a positive lease
/// to this instant (<c>expiry = min(now + ttl, CurrentPeriodEnd)</c>) so caching can never extend access one
/// moment past the paid boundary. Null on NotEntitled/Unknown, and null on an Entitled row that recorded no
/// period end (an active row need not carry one; the lease then falls back to the plain ttl).</param>
public sealed record EntitlementDecision(EntitlementOutcome Outcome, string? Tier, DateTime? CurrentPeriodEnd = null);

/// <summary>
/// Reads the paid-entitlement record for a hosted account, at enrollment time.
///
/// This is the gate that makes hosted a paid product: without it, enrolling is free and the billing side
/// sells what anyone can take for nothing. It sits between subject-verification and tenant-mint in the
/// hosted enrollment path, so an account with no entitlement never gets a tenant and never gets a device
/// key - and with no device key there is no tunnel, no cockpit and no mobile. That is the literal meaning
/// of "approval before you can use a hosted tenant".
///
/// THE POLICY, stated rather than implied. An account is entitled when its record says <c>active</c>, OR
/// when it says <c>past_due</c> and the paid period has not yet ended - the payment provider retries a
/// failed payment for a while, and cutting a customer off during that window would refuse someone who has
/// paid and simply had a card decline. A <c>past_due</c> record whose period HAS ended is not entitled, so
/// the grace window is finite rather than open-ended. Any other state - including one this code does not
/// recognise - is NOT entitled, because an unknown state is not an entitled one.
///
/// THE READ IS NEVER A VERDICT WHEN IT FAILS. Every failure path returns <see cref="EntitlementOutcome.Unknown"/>,
/// and the caller is responsible for turning that into a retry rather than a refusal. This type deliberately
/// does not throw: a caller that has to catch will eventually catch in the wrong place and turn ignorance
/// into a denial. The failure is logged LOUD, because a persistent inability to read this table means
/// nobody can enroll and that must not be silent.
///
/// Nothing personally identifying is logged here - not the subject, not the subscription reference.
/// </summary>
public sealed class EntitlementRegistry
{
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway database. The entitlement table is read through the UNSCOPED context:
    /// it is keyed by account subject and is read BEFORE any tenant exists, so scoping it to a tenant would
    /// be circular - the same reason the tenant mapping table is unscoped.</param>
    /// <param name="requireLivemode">
    /// Whether a live-mode subscription is required. Defaults to the hosted deployment signal, so production
    /// hosted demands real money and nothing else has to remember to. A test may pass false to exercise the
    /// rest of the policy without a live row.
    /// </param>
    public EntitlementRegistry(GatewayDatabase db, bool? requireLivemode = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _requireLivemode = requireLivemode ?? GatewayHostedMode.IsHosted;
    }

    private readonly bool _requireLivemode;

    /// <summary>
    /// Look up whether a verified account subject may enroll. See <see cref="EntitlementOutcome"/> for why
    /// the answer has three states; the caller MUST handle all three and must not collapse them.
    ///
    /// This is the enrollment gate's read, and it is deliberately TIER-AGNOSTIC: a live, paid entitlement of
    /// EITHER tier (hosted or pro) enrolls, because enrolling is "may this account reach a hosted tenant at
    /// all", which both plans grant. The tier is read and exposed by <see cref="Evaluate"/> for the capability
    /// that cares about it (the wingman); it never changes this outcome. This method returns only the outcome
    /// so an enrollment caller cannot accidentally start gating on tier.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the paid period against. Injected so the grace-window
    /// boundary is testable at an exact instant rather than only in whichever direction the clock happens
    /// to be pointing when the suite runs.</param>
    public EntitlementOutcome LookupBySubject(string accountSubject, DateTime nowUtc)
        => Evaluate(accountSubject, nowUtc).Outcome;

    /// <summary>
    /// The full entitlement read: the three-way <see cref="EntitlementOutcome"/> AND the plan
    /// <see cref="EntitlementDecision.Tier"/> the row carries. One read, one place - the enrollment gate takes
    /// the outcome (tier-agnostic) and the wingman capability takes the tier, so the two can never disagree
    /// about the same row.
    ///
    /// The tier is exposed as the row records it, and is meaningful ONLY paired with the outcome: a tier
    /// alongside <see cref="EntitlementOutcome.NotEntitled"/> is the plan of a NON-granting row and confers
    /// nothing, and a failed read carries no tier at all (null) because we read nothing.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the paid period against, injected for exact-boundary tests.</param>
    public EntitlementDecision Evaluate(string accountSubject, DateTime nowUtc)
    {
        // A blank subject is not a failed read - it is a caller error, and answering Unknown would invite a
        // retry loop that can never succeed. There is no entitlement for nobody, and no tier.
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - no account subject was supplied");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        var subject = accountSubject.Trim();

        Data.Entities.EntitlementEntity? row;
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            row = ctx.Entitlements.AsNoTracking().FirstOrDefault(e => e.Subject == subject);
        }
        catch (Exception ex)
        {
            // IGNORANCE, NOT ABSENCE. Every failure lands here - connection refused, timeout, the scoped
            // role losing its SELECT grant, a malformed row. None of them tell us the account is unpaid, so
            // none of them may deny, and none of them may grant. Logged loud and by TYPE, because a
            // persistent failure here stops every enrollment on the box and must not be quiet. No tier: we
            // read nothing, so there is nothing to expose.
            FileLog.Write($"[EntitlementRegistry] Evaluate: READ FAILED ({ex.GetType().Name}) - answering UNKNOWN, " +
                          "which must be retried and must NEVER be treated as unpaid or as paid");

            // DIAGNOSTIC. On a PostgreSQL failure, add the two fields that let the server's OWN error be read
            // from the log - the SQLSTATE code and the server's message text - whether the PostgresException
            // arrived directly or wrapped by EF. These are the difference between "the box is down" and "the
            // read reached the server but the row/column/relation was not what the query expected" (for
            // example SQLSTATE 42P01 undefined_table, 42703 undefined_column, or 42804 datatype_mismatch).
            // Without them the generic type name alone cannot tell those apart. NEITHER field carries PII for
            // a schema, relation or column error - no subject, no email, no row data - so the
            // never-log-the-subject rule is preserved: the subject is still never written here.
            var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
            if (pg is not null)
                FileLog.Write($"[EntitlementRegistry] Evaluate: PostgreSQL error SqlState={pg.SqlState} MessageText={pg.MessageText}");

            return new EntitlementDecision(EntitlementOutcome.Unknown, null);
        }

        // From here the read SUCCEEDED, so whatever we conclude is knowledge.
        if (row is null)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - the read succeeded and the account has no entitlement record");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        // The plan the row records (hosted|pro), normalized to null-if-blank. Exposed with WHATEVER outcome
        // follows - it is the row's data. It never influences the outcome below.
        var tier = string.IsNullOrWhiteSpace(row.Tier) ? null : row.Tier!.Trim();

        // LIVE MONEY ONLY on the production hosted Gateway. A payment-provider TEST-mode subscription costs
        // nothing to create, so honouring one is a paywall bypass in the deny-OPEN direction - the expensive
        // direction, because it is silent. A NULL is refused exactly as a false is: a row written before this
        // column existed, or by a webhook that forgot it, arrives null, and "we did not record whether this
        // was real money" is not evidence that it was.
        //
        // This composes with the three outcomes rather than adding a fourth: the read SUCCEEDED, so a
        // non-live row is a successful read that returned no VALID entitlement - absence, not ignorance. It
        // earns the 402 and it mints nothing. Keyed off the same hosted signal as everything else, so
        // self-host - which has no billing at all - is untouched.
        if (_requireLivemode && row.Livemode != true)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - the entitlement is not a live-mode subscription (a test-mode or unrecorded one is not an entitlement)");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, tier);
        }

        var status = (row.Status ?? "").Trim();

        if (string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase))
            return new EntitlementDecision(EntitlementOutcome.Entitled, tier, row.CurrentPeriodEnd);

        // The dunning grace window: a payment that failed is being retried, and the customer has paid for
        // the period already. Entitled until that period ends, and not one moment after.
        //
        // The comparison is STRICTLY LESS THAN, so the end instant itself is already outside the window. At
        // exactly CurrentPeriodEnd the paid period HAS ended - that is what the field means - and an
        // inclusive comparison would grant on an expired entitlement. It is one tick of access, but it is a
        // grant in the deny-OPEN direction, and this gate's whole job is to never guess in the paying
        // direction. Boundaries are where a policy is decided, so this one is pinned by its own test.
        if (string.Equals(status, StatusPastDue, StringComparison.OrdinalIgnoreCase)
            && row.CurrentPeriodEnd is { } periodEnd
            && nowUtc < periodEnd)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: ENTITLED within the payment-retry grace window (payment is past due, the paid period has not ended)");
            return new EntitlementDecision(EntitlementOutcome.Entitled, tier, periodEnd);
        }

        // Canceled, past_due with the period ended or with no period recorded, or any state this code does
        // not recognise. An unrecognised state is NOT entitled - we do not guess in the paying direction.
        FileLog.Write($"[EntitlementRegistry] Evaluate: NOT ENTITLED - the read succeeded and the record's state does not grant access (state='{status}')");
        return new EntitlementDecision(EntitlementOutcome.NotEntitled, tier);
    }

    /// <summary>The state meaning a live, paid subscription.</summary>
    public const string StatusActive = "active";

    /// <summary>The state meaning a payment failed and is being retried - entitled only until the paid period ends.</summary>
    public const string StatusPastDue = "past_due";

    /// <summary>The hosted plan tier.</summary>
    public const string TierHosted = "hosted";

    /// <summary>The pro plan tier.</summary>
    public const string TierPro = "pro";
}
