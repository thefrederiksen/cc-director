using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway suggestion service (devthrottle #2075) wired to real per-tenant stores: it mines a tenant's
/// stored transcripts against that tenant's glossary and dismissed terms, caches the result on a TTL, and
/// keeps every tenant's suggestions built only from - and served only to - that tenant. The mining policy
/// itself is proven in MistranscriptionMinerTests; these tests prove the WIRING: tenant scoping, dismissal
/// honoring, cache TTL and invalidation.
/// </summary>
public sealed class DictionarySuggestionServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DictationDictionary EmptyDict = new(
        Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });

    private static void SeedTerm(TranscriptStore store, TenantId tenant, DateTime at,
        (string spelling, int times)[] spellings)
    {
        var i = 0;
        foreach (var (spelling, times) in spellings)
            for (var n = 0; n < times; n++)
                store.Append(tenant, "dictation", $"we shipped the {spelling} change", null, false,
                    turnId: null, nowUtc: at.AddSeconds(i++));
    }

    // The canonical "mindzie" corpus: said 44 times right, heard wrong 53 times.
    private static readonly (string, int)[] MindzieCorpus =
        { ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12), ("Mindsea", 6) };

    [Fact]
    public void GetSuggestions_MinesTheTenantsTranscripts()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);

        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => EmptyDict, now: () => Base);

        var s = Assert.Single(svc.GetSuggestions(TenantA));
        Assert.Equal("mindzie", s.Term);
        Assert.Equal(53, s.WrongCount);
        Assert.Equal(97, s.TotalCount);
        Assert.Equal(1, svc.GetSuggestionCount(TenantA));
    }

    [Fact]
    public void GetSuggestions_IsPerTenant()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        // Tenant B has its own, different mistranscription and none of A's.
        SeedTerm(transcripts, TenantB, Base,
            new[] { ("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12) });

        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => EmptyDict, now: () => Base);

        Assert.Equal("mindzie", Assert.Single(svc.GetSuggestions(TenantA)).Term);
        Assert.Equal("Frederiksen", Assert.Single(svc.GetSuggestions(TenantB)).Term);
    }

    [Fact]
    public void GetSuggestions_ExcludesTermsAlreadyInTheGlossary()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);

        var dictWithTerm = new DictationDictionary(
            new[] { "mindzie" }, new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });

        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => dictWithTerm, now: () => Base);

        Assert.Empty(svc.GetSuggestions(TenantA));
    }

    [Fact]
    public void GetSuggestions_ExcludesDismissedTerms()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => EmptyDict, now: () => Base);

        // Dismiss the term, invalidate, and it is gone; restore, invalidate, and it returns.
        dismissals.Dismiss(TenantA, svc.FindSuggestion(TenantA, "mindzie")!, Base);
        svc.Invalidate(TenantA);
        Assert.Empty(svc.GetSuggestions(TenantA));

        dismissals.Restore(TenantA, "mindzie");
        svc.Invalidate(TenantA);
        Assert.Single(svc.GetSuggestions(TenantA));
    }

    [Fact]
    public void GetSuggestions_CachesWithinTtl_AndInvalidateForcesRecompute()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);

        var clock = Base;
        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => EmptyDict, now: () => clock);

        Assert.Single(svc.GetSuggestions(TenantA)); // computes and caches

        // Add a NEW batch that would create a second suggestion; still within the TTL, the cache hides it.
        SeedTerm(transcripts, TenantA, Base.AddHours(1),
            new[] { ("Kubernetes", 20), ("Kubernetis", 6), ("Kubernettes", 4) });
        clock = Base.AddSeconds(30);
        Assert.Single(svc.GetSuggestions(TenantA)); // cached

        // Past the TTL, it recomputes and sees both.
        clock = Base + DictionarySuggestionService.CacheTtl + TimeSpan.FromSeconds(1);
        Assert.Equal(2, svc.GetSuggestions(TenantA).Count);
    }

    [Fact]
    public void FindSuggestion_MatchesCaseAndPunctuationInsensitively()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var svc = new DictionarySuggestionService(transcripts, dismissals, _ => EmptyDict, now: () => Base);

        Assert.NotNull(svc.FindSuggestion(TenantA, "MINDZIE"));
        Assert.Null(svc.FindSuggestion(TenantA, "nothinghere"));
    }
}
