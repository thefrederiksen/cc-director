using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway-owned, restart-surviving store of screening-model VERDICTS on mined dictionary-suggestion
/// candidates (devthrottle issue #2115): one <c>dictation_suggestion_verdicts</c> row per tenant per term.
/// A term is judged by the model AT MOST ONCE per tenant, ever - the scan reads <see cref="VerdictsByNorm"/>
/// to split mined candidates into already-judged (serve the stored verdict) and new (send to the model), and
/// records the model's answers through <see cref="Record"/>. That persistence is what keeps the steady-state
/// screening cost per tenant near zero.
///
/// EXPLICIT TENANT ON EVERY CALL, mirroring <see cref="DictionarySuggestionDismissalStore"/>: the tenant is
/// passed in by the caller that resolved it; this layer performs NO ambient or static tenant inference. It
/// goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>, so the global query filter scopes
/// every read and write and one tenant can never read or shape another's verdicts.
///
/// Threading: the Gateway is a single writer; every operation runs under this store's write lock over a
/// fresh pooled context, preserving the single-writer invariant every store on this layer keeps.
/// </summary>
public sealed class DictionarySuggestionVerdictStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway EF database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public DictionarySuggestionVerdictStore(GatewayDatabase db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>One verdict as the scan consumes it: approved or not, with the model's reason for diagnosis.</summary>
    public sealed record Verdict(string Term, bool Approved, string Reason);

    /// <summary>
    /// Record a batch of model verdicts for a tenant, upserting by normalized term. Upsert (rather than
    /// insert-only) keeps a re-judge deliberate and possible - a future "re-screen" action can overwrite -
    /// while the normal scan path never re-submits a judged term in the first place.
    /// </summary>
    /// <param name="tenant">The tenant the caller resolved. Required and valid.</param>
    /// <param name="verdicts">The model's answers. Terms that normalize to empty are skipped.</param>
    /// <param name="model">The model id that judged the batch (stamped on each row for diagnosis).</param>
    /// <param name="nowUtc">The judgment time (UTC), injected so tests are deterministic.</param>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    /// <exception cref="ArgumentNullException">The verdicts are null.</exception>
    public void Record(TenantId tenant, IEnumerable<Verdict> verdicts, string model, DateTime nowUtc)
    {
        if (verdicts is null) throw new ArgumentNullException(nameof(verdicts));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var written = 0;
            foreach (var verdict in verdicts)
            {
                var norm = Normalize(verdict.Term);
                if (norm.Length == 0) continue;
                var existing = ctx.DictationSuggestionVerdicts.FirstOrDefault(e => e.Term == norm);
                if (existing is null)
                {
                    ctx.DictationSuggestionVerdicts.Add(new DictationSuggestionVerdictEntity
                    {
                        TenantId = tenant.Value,
                        Term = norm,
                        DisplayTerm = verdict.Term,
                        Approved = verdict.Approved,
                        Reason = verdict.Reason,
                        Model = model ?? "",
                        JudgedAtUtc = nowUtc.ToUniversalTime(),
                    });
                }
                else
                {
                    existing.DisplayTerm = verdict.Term;
                    existing.Approved = verdict.Approved;
                    existing.Reason = verdict.Reason;
                    existing.Model = model ?? "";
                    existing.JudgedAtUtc = nowUtc.ToUniversalTime();
                }
                written++;
            }
            ctx.SaveChanges();
            FileLog.Write($"[VerdictStore] Record: tenant={tenant.ToLogString()} verdicts={written} model={model}");
        }
    }

    /// <summary>Every stored verdict for a tenant, keyed by the NORMALIZED term (true = approved). The scan
    /// reads this once and splits mined candidates into judged and unjudged.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyDictionary<string, bool> VerdictsByNorm(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.DictationSuggestionVerdicts.AsNoTracking()
                .ToDictionary(e => e.Term, e => e.Approved, StringComparer.Ordinal);
        }
    }

    /// <summary>Lower-cased letters and digits only - identical to the miner's normalization, so a mined
    /// candidate and its stored verdict compare on the exact same key.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
