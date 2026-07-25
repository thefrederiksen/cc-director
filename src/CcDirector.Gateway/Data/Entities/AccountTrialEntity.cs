namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One free-trial grant for a hosted account - the row that records that this account was given the 14-day
/// Pro trial the public pricing page promises ("Every new account starts with 14 days of Pro"), and exactly
/// when that trial ends.
///
/// THIS TABLE IS OURS, and that is the whole reason it exists. The paid record
/// (<see cref="EntitlementEntity"/>) belongs to the payment side: the website's webhook writes it as the
/// service role and this Gateway holds SELECT and nothing more, so the Gateway cannot grant anything by
/// writing there. A trial is not a payment and no webhook produces one, so the grant has to be recorded on
/// the Gateway's own side of the boundary - here.
///
/// Keyed by the STABLE account subject, the same identity the tenant mapping and the entitlement read use,
/// never the email (which can change over an account's life).
///
/// ONE ROW PER ACCOUNT, WRITTEN ONCE. The expiry is stamped at grant time and is never extended or
/// re-stamped: an account gets one trial, ever. That is what stops a member from restarting the free window
/// by re-enrolling, and it is why the row is kept even after the trial has ended - an expired row is the
/// evidence that this account already had its trial.
///
/// Nothing on this row is ever logged: the subject is personally identifying.
/// </summary>
public sealed class AccountTrialEntity
{
    /// <summary>The verified account subject this trial belongs to. Primary key. Never logged.</summary>
    public string Subject { get; set; } = "";

    /// <summary>When the trial was granted (UTC) - the moment the account first arrived at the hosted Gateway.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When the trial ends (UTC), stamped once at grant time as start + the trial length. Stored rather than
    /// derived so the window is a fact on the row: a later change to the trial length does not silently move
    /// the end date of a trial already running, and a one-time goodwill grant can be written with any expiry
    /// without a schema change.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
