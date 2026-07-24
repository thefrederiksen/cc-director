using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The account-to-tenant resolver (Hosted Multi-Tenancy mission, increment 1). It owns the <c>tenants</c>
/// mapping table and answers the one question the hosted enrollment boundary asks: "what tenant does this
/// VERIFIED account belong to?" The mapping key is the STABLE Supabase subject (the token's <c>sub</c>),
/// never the email.
///
/// Mint-or-lookup is idempotent by subject: an account that already has a tenant gets that SAME tenant back
/// (so a second device of the same account resolves to the same tenant - the property that keeps 1:1-now /
/// many-accounts-per-tenant-later working), and a brand-new subject mints a fresh tenant id (a GUID
/// generated in code). Writes are serialized under a single write lock, preserving the Gateway's
/// single-writer invariant; the unique index on <c>account_subject</c> is the database-level backstop so
/// two racing mints can never split one account across two tenants.
///
/// Security: the account subject and email are personally identifying, so NEITHER is ever written to the
/// log - only the accept/mint decision is logged.
/// </summary>
public sealed class TenantRegistry
{
    private readonly GatewayDatabase _db;
    private readonly object _writeLock = new();

    /// <param name="db">The Gateway EF database. The registry reads and writes the global <c>tenants</c>
    /// table through its UNSCOPED context (the mapping table carries no tenant_id and no query filter).</param>
    public TenantRegistry(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Resolve the tenant for a verified account subject, minting one if the subject has none. Returns the
    /// EXISTING tenant when the subject is already mapped (idempotent - a second device of the same account
    /// resolves to the same tenant); mints a new tenant id (a code-generated GUID) only for a subject with no
    /// mapping. The email is stored as display metadata on a fresh mint only and is never the lookup key.
    /// </summary>
    /// <param name="accountSubject">The verified Supabase subject (<c>sub</c>). The caller must have already
    /// validated the token this came from; this method does not re-verify.</param>
    /// <param name="email">The account email for display metadata, or null. Never used as a key.</param>
    /// <returns>The resolved (existing or newly minted) tenant.</returns>
    public TenantId MintOrLookupBySubject(string accountSubject, string? email)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required to resolve a tenant.", nameof(accountSubject));

        var subject = accountSubject.Trim();

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();

            var existing = ctx.Tenants.AsNoTracking().FirstOrDefault(t => t.AccountSubject == subject);
            if (existing is not null)
            {
                FileLog.Write("[TenantRegistry] MintOrLookupBySubject: resolved an existing tenant for a known account");
                return new TenantId(existing.Id);
            }

            var tenantId = Guid.NewGuid().ToString();
            ctx.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                AccountSubject = subject,
                Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            });

            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // A competing mint for the SAME subject won the unique index on account_subject (a second
                // instance/process, or a future non-single-writer deployment - the in-process write lock above
                // does not cover those). The mapping is idempotent by subject, so the loser adopts the winner's
                // tenant: re-read what the index committed and return it. This is NOT a fallback that hides a
                // problem - the unique index is the source of truth and we are reading back the value it
                // enforced. A still-absent row would be a genuine failure, so it re-throws rather than mint again.
                using var reread = _db.CreateUnscopedContext();
                var winner = reread.Tenants.AsNoTracking().FirstOrDefault(t => t.AccountSubject == subject);
                if (winner is not null)
                {
                    FileLog.Write("[TenantRegistry] MintOrLookupBySubject: lost a mint race, adopting the winning tenant for the account");
                    return new TenantId(winner.Id);
                }
                throw;
            }

            FileLog.Write("[TenantRegistry] MintOrLookupBySubject: minted a new tenant for a first-seen account");
            return new TenantId(tenantId);
        }
    }

    /// <summary>
    /// Look a tenant up by verified account subject WITHOUT minting. Returns null when the subject has no
    /// tenant. Used where a missing mapping must be a deny (never a mint) - the read-only counterpart to
    /// <see cref="MintOrLookupBySubject"/>.
    /// </summary>
    public TenantId? LookupBySubject(string accountSubject)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            return null;

        var subject = accountSubject.Trim();
        using var ctx = _db.CreateUnscopedContext();
        var existing = ctx.Tenants.AsNoTracking().FirstOrDefault(t => t.AccountSubject == subject);
        return existing is null ? null : new TenantId(existing.Id);
    }

    /// <summary>
    /// The whole tenant census - every tenant id in the <c>tenants</c> mapping table. This is the fan-out
    /// source for <see cref="TenantScopedSweep"/>: a background worker enumerates it once per cycle and runs
    /// its per-tenant body inside each tenant's scope. Read through the UNSCOPED context because the mapping
    /// table carries no tenant_id and no query filter (reading it needs no ambient tenant), exactly as the
    /// mint/lookup paths do. Read-only; never mints. An empty census (no tenants yet) yields an empty list.
    /// </summary>
    public IReadOnlyList<TenantId> AllTenantIds()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.Tenants
            .AsNoTracking()
            .Select(t => t.Id)
            .ToList()
            .Select(id => new TenantId(id))
            .ToList();
    }

    /// <summary>
    /// The verified account subject a tenant maps to, or null when the tenant id is unknown. This is the
    /// REVERSE of <see cref="MintOrLookupBySubject"/> and the bridge the cancellation cutoff (MTR-15) needs:
    /// the lease and the sweep are keyed by <see cref="TenantId"/>, but the entitlement reader
    /// (<c>EntitlementRegistry</c>) reads by subject, so the cutoff resolves tenant -> subject here before it
    /// reads entitlement. Read through the UNSCOPED context (the mapping table carries no tenant_id). The
    /// subject is personally identifying and is never logged. A null means "no such tenant", never "not
    /// entitled" - the caller must not fold those together.
    /// </summary>
    public string? SubjectForTenant(TenantId tenant)
    {
        if (!tenant.IsValid)
            return null;

        var id = tenant.Value;
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.Tenants.AsNoTracking().FirstOrDefault(t => t.Id == id);
        return string.IsNullOrWhiteSpace(row?.AccountSubject) ? null : row!.AccountSubject;
    }

    /// <summary>What <see cref="LookupByAccount"/> concluded. The three outcomes are deliberately distinct:
    /// a caller that folded Ambiguous into NotFound would silently drop a real account, and one that folded
    /// it into Found would have to PICK one of two tenants - which is guessing about whose data to send.</summary>
    public enum AccountLookupOutcome
    {
        /// <summary>Exactly one tenant matched. <c>Tenant</c> carries it.</summary>
        Found,

        /// <summary>No tenant matched the identifier.</summary>
        NotFound,

        /// <summary>More than one tenant carries this email. There is no right answer, so there is no answer.</summary>
        Ambiguous,
    }

    /// <summary>
    /// Resolve the tenant an ACCOUNT identifier names, for a server-to-server caller that has no device key
    /// to be recognized by (the morning-report endpoint, issue #2119). The identifier is either the tenant
    /// id itself or the account's display email; the email match is case-insensitive because a person typing
    /// their own address does not preserve case.
    ///
    /// THIS IS NOT AN AUTHENTICATION PATH AND MUST NEVER BE USED AS ONE. The email is display metadata, not
    /// a credential - it is emphatically NOT the mapping key (the stable Supabase subject is; see
    /// <see cref="MintOrLookupBySubject"/>). A caller reaching this method has ALREADY been authorized by
    /// its own means (the report endpoint's service token); this only turns the account it named into the
    /// partition to read. Nothing here mints, and nothing here decides whether the caller may look.
    ///
    /// A duplicate email across two tenants returns <see cref="AccountLookupOutcome.Ambiguous"/> rather than
    /// picking one. Two accounts CAN share a display email (the mint records whatever the token carried), and
    /// sending one person's report to the other is the exact harm the tenant boundary exists to prevent.
    /// The subject and the email are personally identifying and are never logged.
    /// </summary>
    public (AccountLookupOutcome Outcome, TenantId Tenant) LookupByAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return (AccountLookupOutcome.NotFound, default);

        var needle = account.Trim();
        using var ctx = _db.CreateUnscopedContext();

        var byId = ctx.Tenants.AsNoTracking().FirstOrDefault(t => t.Id == needle);
        if (byId is not null)
        {
            FileLog.Write("[TenantRegistry] LookupByAccount: resolved a tenant by tenant id");
            return (AccountLookupOutcome.Found, new TenantId(byId.Id));
        }

        // Matched in memory rather than in SQL: case-insensitive string comparison is provider- and
        // collation-dependent (SQLite's NOCASE is ASCII-only, Postgres is case-sensitive by default), and a
        // report must not resolve to a different tenant depending on which database it runs against. The
        // tenant census is small - it is the account table, not a data table.
        var byEmail = ctx.Tenants.AsNoTracking()
            .Where(t => t.Email != null)
            .Select(t => new { t.Id, t.Email })
            .ToList()
            .Where(t => string.Equals(t.Email, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byEmail.Count == 1)
        {
            FileLog.Write("[TenantRegistry] LookupByAccount: resolved a tenant by account email");
            return (AccountLookupOutcome.Found, new TenantId(byEmail[0].Id));
        }
        if (byEmail.Count > 1)
        {
            FileLog.Write($"[TenantRegistry] LookupByAccount: AMBIGUOUS - {byEmail.Count} tenants carry the requested email; refusing to pick one");
            return (AccountLookupOutcome.Ambiguous, default);
        }

        FileLog.Write("[TenantRegistry] LookupByAccount: no tenant carries the requested account identifier");
        return (AccountLookupOutcome.NotFound, default);
    }

    /// <summary>
    /// The display email recorded on a tenant's row, or null when there is none (issue #1856). Read-only:
    /// it neither mints nor writes.
    ///
    /// NULL IS ORDINARY HERE, NOT AN ERROR. <see cref="MintOrLookupBySubject"/> records the email on a FRESH
    /// MINT ONLY, so a tenant minted before the email was captured, or from a token that carried none, simply
    /// has no email - while being a perfectly valid, fully enrolled tenant. A caller must therefore treat a
    /// null as "this identity is not available" and NEVER as "there is no such tenant" or "nobody is signed
    /// in": those are different answers and a caller that folds them together states a falsehood. The email
    /// is personally identifying and is never logged.
    /// </summary>
    public string? EmailForTenant(TenantId tenant)
    {
        if (!tenant.IsValid)
            return null;

        var id = tenant.Value;
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.Tenants.AsNoTracking().FirstOrDefault(t => t.Id == id);
        return string.IsNullOrWhiteSpace(row?.Email) ? null : row!.Email;
    }
}
