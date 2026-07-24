using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The per-tenant dismissed-suggestions store (devthrottle #2075): a dismissal persists with its evidence
/// snapshot, restore removes it, the normalized term is what both operations key on, and one tenant can never
/// read, dismiss, or restore another's terms. Drives two tenants over one SQLite database to prove the
/// isolation the hosted Gateway depends on.
/// </summary>
public sealed class DictionarySuggestionDismissalStoreTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private static MistranscriptionSuggestion Sug(string term, params (string heard, int count)[] variants)
        => new(term, variants.Select(v => new MistranscriptionVariant(v.heard, v.count)).ToList(),
            variants.Sum(v => v.count), variants.Sum(v => v.count) + 40);

    [Fact]
    public void Dismiss_PersistsTermAndEvidenceSnapshot()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());

        store.Dismiss(TenantA, Sug("Supabase", ("Superbase", 2), ("Super base", 1)), Base);

        var row = Assert.Single(store.List(TenantA));
        Assert.Equal("Supabase", row.Term);
        Assert.Equal(3, row.WrongCount);
        Assert.Equal(43, row.TotalCount);
        Assert.Equal(new[] { "Superbase", "Super base" }, row.Variants.Select(v => v.Heard).ToArray());
        Assert.Equal(Base, row.DismissedAtUtc);
    }

    [Fact]
    public void DismissedTermNorms_ReturnsNormalizedForm()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());

        store.Dismiss(TenantA, Sug("ConPty", ("Con-TY", 3)), Base);

        Assert.Equal(new[] { "conpty" }, store.DismissedTermNorms(TenantA).ToArray());
    }

    [Fact]
    public void Dismiss_SameTermDifferentCasing_UpsertsNotDuplicates()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());

        store.Dismiss(TenantA, Sug("Supabase", ("Superbase", 2)), Base);
        // Re-dismiss with a different casing and fresher evidence - one row, refreshed.
        store.Dismiss(TenantA, Sug("supabase", ("Superbase", 5), ("Soup abase", 2)), Base.AddDays(1));

        var row = Assert.Single(store.List(TenantA));
        Assert.Equal(7, row.WrongCount);
        Assert.Equal(Base.AddDays(1), row.DismissedAtUtc);
    }

    [Fact]
    public void Restore_RemovesTheDismissal()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());
        store.Dismiss(TenantA, Sug("Kubernetes", ("Cooper Netties", 2)), Base);

        Assert.True(store.Restore(TenantA, "kubernetes"));
        Assert.Empty(store.List(TenantA));
        Assert.Empty(store.DismissedTermNorms(TenantA));
    }

    [Fact]
    public void Restore_UnknownTerm_ReturnsFalse()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());

        Assert.False(store.Restore(TenantA, "neverdismissed"));
    }

    [Fact]
    public void List_NewestFirst()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());
        store.Dismiss(TenantA, Sug("Older", ("Oldarr", 3)), Base);
        store.Dismiss(TenantA, Sug("Newer", ("Newarr", 3)), Base.AddDays(2));

        var rows = store.List(TenantA);
        Assert.Equal(new[] { "Newer", "Older" }, rows.Select(r => r.Term).ToArray());
    }

    [Fact]
    public void OneTenant_CannotSeeOrRestoreAnothersDismissals()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());
        store.Dismiss(TenantA, Sug("Supabase", ("Superbase", 3)), Base);

        // Tenant B sees nothing, and cannot restore tenant A's dismissal.
        Assert.Empty(store.List(TenantB));
        Assert.Empty(store.DismissedTermNorms(TenantB));
        Assert.False(store.Restore(TenantB, "supabase"));

        // Tenant A's dismissal is untouched.
        Assert.Single(store.List(TenantA));
    }

    [Fact]
    public void Dismiss_InvalidTenant_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionDismissalStore(h.Open());

        Assert.Throws<ArgumentException>(() => store.Dismiss(default, Sug("X", ("Y", 3)), Base));
    }
}
