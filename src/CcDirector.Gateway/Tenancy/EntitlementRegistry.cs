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
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the paid period against. Injected so the grace-window
    /// boundary is testable at an exact instant rather than only in whichever direction the clock happens
    /// to be pointing when the suite runs.</param>
    public EntitlementOutcome LookupBySubject(string accountSubject, DateTime nowUtc)
    {
        // A blank subject is not a failed read - it is a caller error, and answering Unknown would invite a
        // retry loop that can never succeed. There is no entitlement for nobody.
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[EntitlementRegistry] LookupBySubject: NOT ENTITLED - no account subject was supplied");
            return EntitlementOutcome.NotEntitled;
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
            // persistent failure here stops every enrollment on the box and must not be quiet.
            FileLog.Write($"[EntitlementRegistry] LookupBySubject: READ FAILED ({ex.GetType().Name}) - answering UNKNOWN, " +
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
                FileLog.Write($"[EntitlementRegistry] LookupBySubject: PostgreSQL error SqlState={pg.SqlState} MessageText={pg.MessageText}");

            return EntitlementOutcome.Unknown;
        }

        // From here the read SUCCEEDED, so whatever we conclude is knowledge.
        if (row is null)
        {
            FileLog.Write("[EntitlementRegistry] LookupBySubject: NOT ENTITLED - the read succeeded and the account has no entitlement record");
            return EntitlementOutcome.NotEntitled;
        }

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
            FileLog.Write("[EntitlementRegistry] LookupBySubject: NOT ENTITLED - the entitlement is not a live-mode subscription (a test-mode or unrecorded one is not an entitlement)");
            return EntitlementOutcome.NotEntitled;
        }

        var status = (row.Status ?? "").Trim();

        if (string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase))
            return EntitlementOutcome.Entitled;

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
            FileLog.Write("[EntitlementRegistry] LookupBySubject: ENTITLED within the payment-retry grace window (payment is past due, the paid period has not ended)");
            return EntitlementOutcome.Entitled;
        }

        // Canceled, past_due with the period ended or with no period recorded, or any state this code does
        // not recognise. An unrecognised state is NOT entitled - we do not guess in the paying direction.
        FileLog.Write($"[EntitlementRegistry] LookupBySubject: NOT ENTITLED - the read succeeded and the record's state does not grant access (state='{status}')");
        return EntitlementOutcome.NotEntitled;
    }

    /// <summary>The state meaning a live, paid subscription.</summary>
    public const string StatusActive = "active";

    /// <summary>The state meaning a payment failed and is being retried - entitled only until the paid period ends.</summary>
    public const string StatusPastDue = "past_due";
}
