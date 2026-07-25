using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// What a trial read established. THREE outcomes, for exactly the reason
/// <see cref="EntitlementOutcome"/> has three: "I looked and there is no live trial" and "I could not look"
/// demand opposite answers, and folding them together either denies a member who is inside their free window
/// or grants the product to someone who is not.
/// </summary>
public enum TrialOutcome
{
    /// <summary>The read succeeded and a trial is running right now.</summary>
    Active,

    /// <summary>
    /// The read SUCCEEDED and no trial covers this account now - either no trial was ever granted, or the one
    /// that was has ended. This is knowledge, and it justifies falling back to the paid answer.
    /// </summary>
    None,

    /// <summary>
    /// The read FAILED - connection, timeout, permission, anything. We do not KNOW whether a trial covers this
    /// account, so this must never be answered as a grant and never as a refusal.
    /// </summary>
    Unknown,
}

/// <summary>The result of one trial read: the three-way outcome and, on <see cref="TrialOutcome.Active"/>,
/// the exact instant the trial ends.</summary>
/// <param name="Outcome">The three-way outcome. Never fold the three into two.</param>
/// <param name="ExpiresAtUtc">The trial's end instant, present only on <see cref="TrialOutcome.Active"/>.
/// The entitlement decision carries it forward as the period end, so the hosted access lease clips to it and
/// caching can never extend a trial one moment past its end.</param>
public sealed record TrialDecision(TrialOutcome Outcome, DateTime? ExpiresAtUtc = null);

/// <summary>
/// The free-trial ledger: the Gateway's own record of which accounts were granted the 14-day Pro trial the
/// public pricing page promises, and when each trial ends.
///
/// WHY THE GATEWAY OWNS THIS AND NOT THE PAYMENT SIDE. The paid entitlement table belongs to the website's
/// payment webhook, which writes it as the service role while this Gateway holds SELECT and nothing more. A
/// trial is not a payment - no webhook produces one - so the Gateway cannot grant it by writing there. It
/// records the grant on its own side of the boundary, in <c>account_trials</c>, and the entitlement decision
/// reads BOTH.
///
/// WHO GETS A TRIAL, stated rather than implied. A trial is granted at an account's FIRST ARRIVAL at the
/// hosted Gateway and nowhere else: the account presents a verified token, holds no paid entitlement, has
/// never been granted a trial, and has no tenant - meaning this Gateway has never seen it before. An account
/// that already holds a tenant was already using hosted before this feature existed and is NOT granted a
/// trial; that is the owner's conservative rollout rule (issue #2117) expressed as something the Gateway can
/// actually observe, since account creation happens on the website and the Gateway never sees it. The
/// residual is stated plainly rather than hidden: an account that signed up before rollout and never once
/// reached the hosted Gateway is indistinguishable here from a brand-new one, and will be granted a trial on
/// its first arrival. It is granted nothing silently - only when the member actively tries to use hosted for
/// the first time - and no account that was already using hosted is granted anything at all.
///
/// ONE TRIAL PER ACCOUNT, EVER. <see cref="GrantIfFirstArrival"/> is idempotent: an existing row is returned
/// as it stands and is NEVER extended or re-stamped, so an expired trial row is what stops a member from
/// restarting the free window by re-enrolling.
///
/// THE READ IS NEVER A VERDICT WHEN IT FAILS. Every failure path returns <see cref="TrialOutcome.Unknown"/>,
/// and the caller turns that into a retry rather than a refusal or a grant. This type deliberately does not
/// throw. Failures are logged LOUD, because an unreadable trial ledger stops every new member from starting.
///
/// Nothing personally identifying is logged here - the subject never reaches the log.
/// </summary>
public sealed class TrialRegistry
{
    /// <summary>
    /// The length of the free trial: 14 days, the number the public pricing page states. It is a constant
    /// rather than a setting because the page says fourteen days and the two must not be able to disagree.
    /// </summary>
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(14);

    private readonly GatewayDatabase _db;
    private readonly object _writeLock = new();

    /// <param name="db">The Gateway database. The trial ledger is read and written through the UNSCOPED
    /// context: it is keyed by account subject and is read BEFORE any tenant exists, so scoping it to a
    /// tenant would be circular - the same reason the tenant mapping and entitlement tables are unscoped.</param>
    public TrialRegistry(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Read whether a trial covers this account at <paramref name="nowUtc"/>. READ-ONLY - it never grants,
    /// so the hot paths (the hosted access lease and its sweep) can call it on every check without the read
    /// itself creating an entitlement.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the trial window against. Injected so the expiry boundary is
    /// testable at an exact instant.</param>
    public TrialDecision Evaluate(string accountSubject, DateTime nowUtc)
    {
        // A blank subject is a caller error, not a failed read. Answering Unknown would invite a retry loop
        // that can never succeed. There is no trial for nobody.
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[TrialRegistry] Evaluate: NO TRIAL - no account subject was supplied");
            return new TrialDecision(TrialOutcome.None);
        }

        var subject = accountSubject.Trim();

        Data.Entities.AccountTrialEntity? row;
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            row = ctx.AccountTrials.AsNoTracking().FirstOrDefault(t => t.Subject == subject);
        }
        catch (Exception ex)
        {
            // IGNORANCE, NOT ABSENCE. A failed read does not tell us the account has no trial, so it may not
            // deny and it may not grant. Logged loud and by type: a persistent failure here stops every new
            // member from starting a trial and must not be quiet.
            FileLog.Write($"[TrialRegistry] Evaluate: READ FAILED ({ex.GetType().Name}) - answering UNKNOWN, " +
                          "which must be retried and must NEVER be treated as no-trial or as a live trial");
            return new TrialDecision(TrialOutcome.Unknown);
        }

        if (row is null)
            return new TrialDecision(TrialOutcome.None);

        // STRICTLY LESS THAN, so the expiry instant itself is already outside the window - at exactly
        // ExpiresAtUtc the trial HAS ended, which is what the field means. An inclusive comparison would grant
        // on an expired trial: one tick, but a tick in the give-it-away direction, and boundaries are where a
        // policy is decided, so this one is pinned by its own test.
        if (nowUtc < row.ExpiresAtUtc)
            return new TrialDecision(TrialOutcome.Active, row.ExpiresAtUtc);

        FileLog.Write("[TrialRegistry] Evaluate: NO TRIAL - this account's trial has ended (it is never extended or re-granted)");
        return new TrialDecision(TrialOutcome.None);
    }

    /// <summary>
    /// Grant the 14-day Pro trial to an account arriving at the hosted Gateway for the FIRST time, and return
    /// what now covers it.
    ///
    /// This is the ONLY place a trial is created. It is called from the hosted enrollment path after the paid
    /// entitlement read has already answered NotEntitled, so a paying account never reaches it and can never
    /// be given a trial it does not need.
    ///
    /// IDEMPOTENT AND NEVER EXTENDING. An account that already has a trial row gets that row back exactly as
    /// it stands - live or expired - and nothing is written. That is what makes one trial per account a
    /// property of the code rather than a hope: re-enrolling after the window has closed re-reads the same
    /// expired row and is refused.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="alreadyKnownToGateway">True when this Gateway has seen this account before (it already
    /// holds a tenant). Such an account was using hosted before the trial existed and is granted NOTHING -
    /// the owner's conservative rollout rule. The caller supplies this because the tenant mapping is the
    /// tenant registry's table, not this one's.</param>
    /// <param name="nowUtc">The grant instant; the trial ends at this plus <see cref="TrialLength"/>.</param>
    public TrialDecision GrantIfFirstArrival(string accountSubject, bool alreadyKnownToGateway, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - no account subject was supplied");
            return new TrialDecision(TrialOutcome.None);
        }

        // An existing trial - live or expired - always wins over a new grant, and it is checked FIRST so the
        // never-extend rule holds no matter what the caller believes about the account.
        var existing = Evaluate(accountSubject, nowUtc);
        if (existing.Outcome != TrialOutcome.None)
            return existing;

        // Evaluate answered None. That is either "no row at all" or "the row has expired", and only the first
        // may be granted. Re-read the row itself rather than inferring, so an expired trial is never re-granted.
        var subject = accountSubject.Trim();

        lock (_writeLock)
        {
            try
            {
                using var ctx = _db.CreateUnscopedContext();

                if (ctx.AccountTrials.AsNoTracking().Any(t => t.Subject == subject))
                {
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - this account already had its trial and it has ended (never re-granted)");
                    return new TrialDecision(TrialOutcome.None);
                }

                if (alreadyKnownToGateway)
                {
                    // The conservative rollout rule (issue #2117): an account that already holds a tenant was
                    // using hosted before the trial existed, so it is granted nothing. A goodwill window for
                    // those accounts is a later, deliberate, one-time write - never something that happens by
                    // itself the first time they come back.
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - this account was already known to the hosted Gateway before the trial existed");
                    return new TrialDecision(TrialOutcome.None);
                }

                var expires = nowUtc + TrialLength;
                ctx.AccountTrials.Add(new Data.Entities.AccountTrialEntity
                {
                    Subject = subject,
                    StartedAtUtc = nowUtc,
                    ExpiresAtUtc = expires,
                });
                ctx.SaveChanges();

                FileLog.Write($"[TrialRegistry] GrantIfFirstArrival: GRANTED a {TrialLength.TotalDays:0}-day Pro trial to a first-seen account (no subject logged), ending {expires:O}");
                return new TrialDecision(TrialOutcome.Active, expires);
            }
            catch (DbUpdateException)
            {
                // A competing grant for the SAME subject won the primary key (a second instance, or a future
                // non-single-writer deployment - the in-process lock above does not cover those). One trial per
                // account is the rule, so the loser adopts the winner's row: re-read what the key enforced.
                // This is not a fallback that hides a problem - the primary key is the source of truth and we
                // are reading back the value it enforced. A still-absent row would be a genuine failure, so it
                // re-throws rather than granting again.
                var winner = Evaluate(subject, nowUtc);
                if (winner.Outcome != TrialOutcome.None)
                {
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: lost a grant race, adopting the trial the winner wrote");
                    return winner;
                }
                throw;
            }
            catch (Exception ex)
            {
                // The write failed for anything else. We did not grant, and we do not know what covers this
                // account - answering None here would refuse a brand-new member because the database hiccuped.
                FileLog.Write($"[TrialRegistry] GrantIfFirstArrival: WRITE FAILED ({ex.GetType().Name}) - answering UNKNOWN, which must be retried");
                return new TrialDecision(TrialOutcome.Unknown);
            }
        }
    }
}
