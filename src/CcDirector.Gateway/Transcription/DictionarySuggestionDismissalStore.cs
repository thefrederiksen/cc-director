using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway-owned, restart-surviving store of DISMISSED dictionary suggestions (devthrottle issue #2075):
/// one <c>dictation_suggestion_dismissals</c> row per term a tenant told us to stop suggesting. The mining
/// pass reads <see cref="DismissedTermNorms"/> to exclude these terms, so a dismissal is honored identically
/// by the Dictionary page and by the API; the "Dismissed terms" screen reads <see cref="List"/> to show them
/// with the snapshotted evidence and a Restore action.
///
/// EXPLICIT TENANT ON EVERY CALL, mirroring <see cref="TranscriptStore"/>: the tenant is the one the endpoint
/// already resolved from the authenticated device key and passed in - this layer performs NO ambient or
/// static tenant inference. It goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>, so the
/// global query filter scopes every read and write to that tenant and one tenant can never read, dismiss, or
/// restore another's terms.
///
/// Threading: the Gateway is a single writer; every operation runs under this store's write lock over a fresh
/// pooled context, preserving the single-writer invariant every store on this layer keeps.
/// </summary>
public sealed class DictionarySuggestionDismissalStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway EF database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public DictionarySuggestionDismissalStore(GatewayDatabase db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Record a dismissal for a tenant, upserting by normalized term so re-dismissing refreshes the evidence
    /// snapshot rather than duplicating the row. The evidence (<paramref name="suggestion"/>) is snapshotted
    /// so the "Dismissed terms" screen can still explain the term after its transcripts age out.
    /// </summary>
    /// <param name="tenant">The tenant the endpoint resolved. Required and valid.</param>
    /// <param name="suggestion">The suggestion being dismissed (its canonical term and evidence).</param>
    /// <param name="nowUtc">The dismissal time (UTC), injected so tests are deterministic.</param>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    /// <exception cref="ArgumentNullException">The suggestion is null.</exception>
    public void Dismiss(TenantId tenant, MistranscriptionSuggestion suggestion, DateTime nowUtc)
    {
        if (suggestion is null) throw new ArgumentNullException(nameof(suggestion));
        var norm = Normalize(suggestion.Term);
        if (norm.Length == 0) return;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.DictationSuggestionDismissals.FirstOrDefault(e => e.Term == norm);
            var variantsJson = JsonSerializer.Serialize(
                suggestion.Variants.Select(v => new { heard = v.Heard, count = v.Count }));
            if (existing is null)
            {
                ctx.DictationSuggestionDismissals.Add(new DictationSuggestionDismissalEntity
                {
                    TenantId = tenant.Value,
                    Term = norm,
                    DisplayTerm = suggestion.Term,
                    WrongCount = suggestion.WrongCount,
                    TotalCount = suggestion.TotalCount,
                    VariantsJson = variantsJson,
                    DismissedAtUtc = nowUtc.ToUniversalTime(),
                });
            }
            else
            {
                existing.DisplayTerm = suggestion.Term;
                existing.WrongCount = suggestion.WrongCount;
                existing.TotalCount = suggestion.TotalCount;
                existing.VariantsJson = variantsJson;
                existing.DismissedAtUtc = nowUtc.ToUniversalTime();
            }
            ctx.SaveChanges();
            // The term is dictation-derived customer content and stays out of the log (data-map promise).
            FileLog.Write($"[DismissalStore] Dismiss: tenant={tenant.ToLogString()} termLength={norm.Length} wrong={suggestion.WrongCount}/{suggestion.TotalCount}");
        }
    }

    /// <summary>
    /// Remove a dismissal for a tenant so the term is eligible again (the "Restore" action). Matched on the
    /// normalized term. Returns true when a row was removed, false when nothing matched (idempotent).
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public bool Restore(TenantId tenant, string term)
    {
        var norm = Normalize(term ?? "");
        if (norm.Length == 0) return false;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.DictationSuggestionDismissals.FirstOrDefault(e => e.Term == norm);
            if (existing is null) return false;
            ctx.DictationSuggestionDismissals.Remove(existing);
            ctx.SaveChanges();
            // Same rule as Dismiss: the term is dictation-derived customer content, log its length only.
            FileLog.Write($"[DismissalStore] Restore: tenant={tenant.ToLogString()} termLength={norm.Length}");
            return true;
        }
    }

    /// <summary>The set of dismissed terms for a tenant, in the NORMALIZED form the miner excludes on. Read by
    /// the suggestion service so a dismissed term is never mined back into the list.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyCollection<string> DismissedTermNorms(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.DictationSuggestionDismissals.AsNoTracking()
                .Select(e => e.Term)
                .ToList();
        }
    }

    /// <summary>The tenant's dismissed terms with their snapshotted evidence, newest dismissal first - what the
    /// "Dismissed terms" screen renders. Scoped to <paramref name="tenant"/> by the query filter.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyList<DismissedTerm> List(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.DictationSuggestionDismissals.AsNoTracking()
                .OrderByDescending(e => e.DismissedAtUtc)
                .ThenBy(e => e.Term)
                .ToList();
            return rows.Select(ToDismissedTerm).ToList();
        }
    }

    private static DismissedTerm ToDismissedTerm(DictationSuggestionDismissalEntity e)
    {
        List<MistranscriptionVariant> variants;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<VariantJson>>(e.VariantsJson) ?? new();
            variants = parsed
                .Where(v => !string.IsNullOrWhiteSpace(v.heard))
                .Select(v => new MistranscriptionVariant(v.heard!, v.count))
                .ToList();
        }
        catch (JsonException)
        {
            // A malformed snapshot must not break the whole list - the term and counts are still useful.
            variants = new List<MistranscriptionVariant>();
        }
        return new DismissedTerm(e.DisplayTerm, variants, e.WrongCount, e.TotalCount, e.DismissedAtUtc);
    }

    private sealed class VariantJson
    {
        public string? heard { get; set; }
        public int count { get; set; }
    }

    /// <summary>Lower-cased letters and digits only - identical to the miner's normalization, so a term the
    /// miner would fold and a term dismissed here compare on the exact same key.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

/// <summary>A dismissed term as the "Dismissed terms" screen shows it: the canonical spelling, its evidence
/// snapshot, and when it was dismissed.</summary>
public sealed record DismissedTerm(
    string Term,
    IReadOnlyList<MistranscriptionVariant> Variants,
    int WrongCount,
    int TotalCount,
    DateTime DismissedAtUtc);
