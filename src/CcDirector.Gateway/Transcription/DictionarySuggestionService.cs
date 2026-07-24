using System.Collections.Concurrent;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The server-side dictionary-suggestions engine (devthrottle issue #2075): for a tenant, read what the
/// speech model heard (the stored transcripts), read that tenant's current glossary and its dismissed terms,
/// run the pure <see cref="MistranscriptionMiner"/>, and return the ranked suggestions with evidence. It is a
/// thin read-compute-cache shell around the miner - all the mining policy lives in the pure miner, so this
/// class only wires the three tenant-scoped inputs together and caches the result.
///
/// TENANT-SCOPED THROUGHOUT. Every input is read for the EXPLICIT tenant the endpoint resolved: the
/// transcripts through <see cref="TranscriptStore.RawTexts"/> (global query filter), the glossary through
/// <see cref="TenantGlossary.Load"/> (per-tenant file), the dismissals through
/// <see cref="DictionarySuggestionDismissalStore.DismissedTermNorms"/> (global query filter). The cache is
/// keyed by tenant. So a suggestion computed for one tenant can never be built from, or served to, another.
///
/// CACHED WITH A SHORT TTL, INVALIDATED ON CHANGE. Mining scans up to <see cref="MaxTranscriptsMined"/>
/// transcripts, so the result is memoized per tenant for <see cref="CacheTtl"/> to keep the nav-badge poll
/// and the page load cheap. The cache is invalidated for a tenant whenever its glossary or dismissals change
/// (apply, dismiss, restore), so a change is reflected on the next read rather than after the TTL. There is
/// NO clock injection into the miner (it is pure); the only clock here is the injected <see cref="_now"/>
/// used for the TTL, so tests stay deterministic.
/// </summary>
public sealed class DictionarySuggestionService
{
    /// <summary>The most-recent transcripts mined per tenant. Bounds the mining cost; the store's own 30-day /
    /// 10,000-row retention (issue #509) already bounds what exists, and the newest slice is the relevant one
    /// for "what is the model getting wrong lately".</summary>
    public const int MaxTranscriptsMined = 5_000;

    /// <summary>How long a computed suggestion set is memoized per tenant before it is recomputed.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly TranscriptStore _transcripts;
    private readonly DictionarySuggestionDismissalStore _dismissals;
    private readonly Func<TenantId, DictationDictionary> _glossaryProvider;
    private readonly MistranscriptionMiner.Options _options;
    private readonly Func<DateTime> _now;

    private sealed record CacheEntry(IReadOnlyList<MistranscriptionSuggestion> Suggestions, DateTime ComputedUtc);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <param name="transcripts">The store the raw transcripts are mined from. Required.</param>
    /// <param name="dismissals">The store the dismissed terms are read from. Required.</param>
    /// <param name="glossaryProvider">Resolves a tenant's current glossary; production passes
    /// <see cref="TenantGlossary.Load"/>. Required.</param>
    /// <param name="options">Mining policy; <see cref="MistranscriptionMiner.Options.Default"/> when null.</param>
    /// <param name="now">Clock for the cache TTL; <see cref="DateTime.UtcNow"/> when null.</param>
    public DictionarySuggestionService(
        TranscriptStore transcripts,
        DictionarySuggestionDismissalStore dismissals,
        Func<TenantId, DictationDictionary> glossaryProvider,
        MistranscriptionMiner.Options? options = null,
        Func<DateTime>? now = null)
    {
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _dismissals = dismissals ?? throw new ArgumentNullException(nameof(dismissals));
        _glossaryProvider = glossaryProvider ?? throw new ArgumentNullException(nameof(glossaryProvider));
        _options = options ?? MistranscriptionMiner.Options.Default;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The ranked suggestions for a tenant, from cache when fresh, otherwise recomputed. Never throws
    /// for a mining problem - an empty transcript set simply yields an empty list.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyList<MistranscriptionSuggestion> GetSuggestions(TenantId tenant)
    {
        var nowUtc = _now().ToUniversalTime();
        if (_cache.TryGetValue(tenant.Value, out var cached) && nowUtc - cached.ComputedUtc < CacheTtl)
            return cached.Suggestions;

        var raw = _transcripts.RawTexts(tenant, MaxTranscriptsMined);
        var dictionary = _glossaryProvider(tenant);
        var dismissed = _dismissals.DismissedTermNorms(tenant);
        var suggestions = MistranscriptionMiner.Mine(raw, dictionary, dismissed, _options);

        _cache[tenant.Value] = new CacheEntry(suggestions, nowUtc);
        FileLog.Write($"[SuggestionService] computed tenant={tenant.ToLogString()} transcripts={raw.Count} suggestions={suggestions.Count}");
        return suggestions;
    }

    /// <summary>The number of pending suggestions for a tenant - the nav-badge count. Reuses the cached
    /// computation, so polling the badge is as cheap as reading the list.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public int GetSuggestionCount(TenantId tenant) => GetSuggestions(tenant).Count;

    /// <summary>Find one pending suggestion by its (case/punctuation-insensitive) term - what the dismiss route
    /// needs to snapshot the evidence being dismissed. Null when the term is not currently suggested.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public MistranscriptionSuggestion? FindSuggestion(TenantId tenant, string term)
    {
        var norm = Normalize(term ?? "");
        if (norm.Length == 0) return null;
        return GetSuggestions(tenant).FirstOrDefault(s => Normalize(s.Term) == norm);
    }

    /// <summary>Drop a tenant's cached suggestions so the next read recomputes. Called after any change that
    /// alters the result - a glossary edit (apply), a dismissal, or a restore.</summary>
    public void Invalidate(TenantId tenant) => _cache.TryRemove(tenant.Value, out _);

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
