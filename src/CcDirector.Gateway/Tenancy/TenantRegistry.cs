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
            ctx.SaveChanges();

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
}
