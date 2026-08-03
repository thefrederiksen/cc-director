using System;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The tenant registry's account lookup and its display-email backfill (issue #2119).
///
/// WHY THE BACKFILL EXISTS. The email was recorded on a FRESH MINT ONLY, so every tenant minted before the
/// email was captured - or from a token that carried none - was left permanently without one. That was
/// harmless while nothing looked an account up by email. The morning report does exactly that, and the
/// website's 07:00 cron sends to the same string it asks for, so a null email turns a real, fully enrolled
/// account into "no such account" - a 404 about the data, dressed as a 404 about the endpoint.
///
/// It is a BACKFILL and not an overwrite, and the difference is the whole safety of it: an existing email
/// is never replaced, so this cannot silently re-point an account's display identity, and the mapping key
/// remains the subject and only the subject.
/// </summary>
public sealed class TenantRegistryEmailBackfillTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private TenantRegistry NewRegistry() => new(_h.Open());

    private static string Subject() => "sub-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_tenant_minted_without_an_email_gets_one_recorded_when_the_account_next_resolves()
    {
        var registry = NewRegistry();
        var subject = Subject();

        var minted = registry.MintOrLookupBySubject(subject, email: null);
        Assert.Null(registry.EmailForTenant(minted));

        // The same account resolves again, this time carrying its email.
        var again = registry.MintOrLookupBySubject(subject, "soren@example.com");

        Assert.Equal(minted, again);   // same tenant - the subject is still the only key
        Assert.Equal("soren@example.com", registry.EmailForTenant(minted));
    }

    [Fact]
    public void An_existing_email_is_NEVER_overwritten()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "first@example.com");

        registry.MintOrLookupBySubject(subject, "second@example.com");

        // A backfill fills a hole; it does not re-point an identity. Overwriting here would let a later
        // token silently change where an account's morning report is addressed.
        Assert.Equal("first@example.com", registry.EmailForTenant(tenant));
    }

    [Fact]
    public void Resolving_without_an_email_leaves_a_null_alone_rather_than_blanking_anything()
    {
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, "kept@example.com");

        registry.MintOrLookupBySubject(subject, email: null);

        Assert.Equal("kept@example.com", registry.EmailForTenant(tenant));
    }

    [Fact]
    public void The_backfilled_email_is_what_makes_the_account_findable_by_email()
    {
        // The end-to-end point of the backfill, stated as the property the morning report depends on.
        var registry = NewRegistry();
        var subject = Subject();
        var tenant = registry.MintOrLookupBySubject(subject, email: null);

        var before = registry.LookupByAccount("findable@example.com");
        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound, before.Outcome);

        registry.MintOrLookupBySubject(subject, "findable@example.com");

        var after = registry.LookupByAccount("findable@example.com");
        Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, after.Outcome);
        Assert.Equal(tenant, after.Tenant);
    }

    // ---- the lookup itself -----------------------------------------------------------------------------

    [Fact]
    public void An_account_is_findable_by_its_tenant_id_and_by_its_email_in_any_casing()
    {
        var registry = NewRegistry();
        var tenant = registry.MintOrLookupBySubject(Subject(), "Mixed.Case@Example.com");

        foreach (var identifier in new[] { tenant.Value, "mixed.case@example.com", "MIXED.CASE@EXAMPLE.COM", "  Mixed.Case@Example.com  " })
        {
            var (outcome, resolved) = registry.LookupByAccount(identifier);
            Assert.Equal(TenantRegistry.AccountLookupOutcome.Found, outcome);
            Assert.Equal(tenant, resolved);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nobody@example.com")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void An_unknown_or_empty_identifier_is_NotFound(string? identifier)
    {
        var registry = NewRegistry();
        registry.MintOrLookupBySubject(Subject(), "somebody@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound,
            registry.LookupByAccount(identifier).Outcome);
    }

    [Fact]
    public void Two_accounts_sharing_a_display_email_are_AMBIGUOUS_never_resolved_to_one_of_them()
    {
        // Two DIFFERENT accounts (different subjects - the real key) that happen to carry the same display
        // email. Picking either would email one person the other person's day, so there is no answer.
        var registry = NewRegistry();
        registry.MintOrLookupBySubject(Subject(), "shared@example.com");
        registry.MintOrLookupBySubject(Subject(), "shared@example.com");

        var (outcome, resolved) = registry.LookupByAccount("shared@example.com");

        Assert.Equal(TenantRegistry.AccountLookupOutcome.Ambiguous, outcome);
        Assert.False(resolved.IsValid);
    }

    [Fact]
    public void The_lookup_never_mints()
    {
        var registry = NewRegistry();

        Assert.Equal(TenantRegistry.AccountLookupOutcome.NotFound,
            registry.LookupByAccount("ghost@example.com").Outcome);
        // Asking about an account must not bring it into existence - otherwise a service token could
        // populate the census one guess at a time.
        Assert.Empty(registry.AllTenantIds());
    }
}
