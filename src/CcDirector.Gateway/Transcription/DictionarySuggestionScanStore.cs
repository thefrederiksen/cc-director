using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway-owned, restart-surviving store of each tenant's LATEST dictionary-suggestion scan result
/// (devthrottle issue #2115): one <c>dictation_suggestion_scans</c> row per tenant, overwritten by each scan.
/// The navigation badge and the Dictionary page read this row - they never trigger mining or screening. The
/// row's <see cref="ScanResult.ScannedAtUtc"/> doubles as the daily sweep's durable "last ran" marker, so a
/// Gateway restart never double-runs or skips a tenant's day.
///
/// EXPLICIT TENANT ON EVERY CALL, mirroring <see cref="DictionarySuggestionDismissalStore"/>; every operation
/// goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/> under this store's write lock.
/// </summary>
public sealed class DictionarySuggestionScanStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway EF database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public DictionarySuggestionScanStore(GatewayDatabase db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>A stored scan result: when it ran, whether screening completed, and the approved suggestions.</summary>
    public sealed record ScanResult(
        DateTime ScannedAtUtc,
        bool ScreeningOk,
        string ScreeningError,
        IReadOnlyList<MistranscriptionSuggestion> Suggestions);

    /// <summary>
    /// Overwrite the tenant's stored scan result with a fresh one (one row per tenant).
    /// </summary>
    /// <param name="tenant">The tenant the caller resolved. Required and valid.</param>
    /// <param name="result">The scan outcome to store. Required.</param>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public void Save(TenantId tenant, ScanResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var json = SerializeSuggestions(result.Suggestions);
            var existing = ctx.DictationSuggestionScans.FirstOrDefault();
            if (existing is null)
            {
                ctx.DictationSuggestionScans.Add(new DictationSuggestionScanEntity
                {
                    TenantId = tenant.Value,
                    ScannedAtUtc = result.ScannedAtUtc.ToUniversalTime(),
                    ScreeningOk = result.ScreeningOk,
                    ScreeningError = result.ScreeningError,
                    SuggestionsJson = json,
                });
            }
            else
            {
                existing.ScannedAtUtc = result.ScannedAtUtc.ToUniversalTime();
                existing.ScreeningOk = result.ScreeningOk;
                existing.ScreeningError = result.ScreeningError;
                existing.SuggestionsJson = json;
            }
            ctx.SaveChanges();
            FileLog.Write($"[SuggestionScanStore] Save: tenant={tenant.ToLogString()} suggestions={result.Suggestions.Count} screeningOk={result.ScreeningOk}");
        }
    }

    /// <summary>The tenant's stored scan result, or null when no scan has ever run for it.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public ScanResult? Get(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.DictationSuggestionScans.AsNoTracking().FirstOrDefault();
            if (row is null) return null;
            return new ScanResult(
                DateTime.SpecifyKind(row.ScannedAtUtc, DateTimeKind.Utc),
                row.ScreeningOk,
                row.ScreeningError,
                DeserializeSuggestions(row.SuggestionsJson));
        }
    }

    /// <summary>
    /// Remove one term from the tenant's stored suggestion list in place (after an apply or a dismiss), so the
    /// page and the badge reflect the action immediately without waiting for the next scan. Matched on the
    /// normalized term; a no-op when there is no stored scan or the term is not in it.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public void RemoveSuggestion(TenantId tenant, string term)
    {
        var norm = Normalize(term ?? "");
        if (norm.Length == 0) return;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.DictationSuggestionScans.FirstOrDefault();
            if (existing is null) return;
            var suggestions = DeserializeSuggestions(existing.SuggestionsJson);
            var kept = suggestions.Where(s => Normalize(s.Term) != norm).ToList();
            if (kept.Count == suggestions.Count) return;
            existing.SuggestionsJson = SerializeSuggestions(kept);
            ctx.SaveChanges();
            FileLog.Write($"[SuggestionScanStore] RemoveSuggestion: tenant={tenant.ToLogString()} term={norm} remaining={kept.Count}");
        }
    }

    private static string SerializeSuggestions(IReadOnlyList<MistranscriptionSuggestion> suggestions)
        => JsonSerializer.Serialize(suggestions.Select(s => new SuggestionJson
        {
            term = s.Term,
            wrong = s.WrongCount,
            total = s.TotalCount,
            variants = s.Variants.Select(v => new VariantJson { heard = v.Heard, count = v.Count }).ToList(),
        }));

    private static IReadOnlyList<MistranscriptionSuggestion> DeserializeSuggestions(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SuggestionJson>>(json) ?? new();
            return parsed
                .Where(s => !string.IsNullOrWhiteSpace(s.term))
                .Select(s => new MistranscriptionSuggestion(
                    s.term!,
                    (s.variants ?? new()).Where(v => !string.IsNullOrWhiteSpace(v.heard))
                        .Select(v => new MistranscriptionVariant(v.heard!, v.count)).ToList(),
                    s.wrong,
                    s.total))
                .ToList();
        }
        catch (JsonException)
        {
            // A malformed stored row must not break the page; the next scan overwrites it wholesale.
            return Array.Empty<MistranscriptionSuggestion>();
        }
    }

    private sealed class SuggestionJson
    {
        public string? term { get; set; }
        public int wrong { get; set; }
        public int total { get; set; }
        public List<VariantJson>? variants { get; set; }
    }

    private sealed class VariantJson
    {
        public string? heard { get; set; }
        public int count { get; set; }
    }

    /// <summary>Lower-cased letters and digits only - identical to the miner's normalization.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
