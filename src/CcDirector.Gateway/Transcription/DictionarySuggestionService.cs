using System.Collections.Concurrent;
using CcDirector.AgentBrain;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The server-side dictionary-suggestions engine, redesigned in devthrottle issue #2115: a SCAN - run daily
/// just after midnight in the tenant's own time zone, or on the Dictionary page's explicit "Scan now" - that
/// mines the tenant's stored transcripts for candidate terms, sends the never-judged candidates to the
/// screening model in one batch, persists the verdicts, and stores the approved suggestions. Reads (the page,
/// the navigation badge) serve the STORED result and never trigger mining or a model call.
///
/// WHY THE REDESIGN. The first version recomputed the mining every two minutes behind a badge poll and
/// surfaced whatever the heuristic clustering produced - which in practice was ordinary words ("that",
/// "want", "your") chained together by phonetic nearness. Two fixes, both here: judgment moved to a language
/// model that knows ordinary vocabulary in every language (see <see cref="DictionarySuggestionScreen"/>),
/// and the cadence moved to a daily scan whose result is stored (see <see cref="DictionarySuggestionScanStore"/>),
/// because suggestion-worthy evidence accumulates over days, not minutes.
///
/// SCREENING FAILURE IS LOUD, NOT SILENT: when the model cannot be reached the scan stores
/// "screening unavailable" with the reason and serves only PREVIOUSLY-approved suggestions - a candidate
/// that has never been judged is never shown. There is no heuristic fallback; the unscreened list is known
/// garbage.
///
/// TENANT-SCOPED THROUGHOUT, like every store on this layer: every input is read for the EXPLICIT tenant the
/// caller resolved, and the per-tenant scan lock keeps a button press and the daily sweep from mining the
/// same tenant twice concurrently.
/// </summary>
public sealed class DictionarySuggestionService
{
    /// <summary>The most-recent transcripts mined per scan. Bounds the mining cost; the store's own 90-day /
    /// 30,000-row retention already bounds what exists, and the newest slice is the relevant one
    /// for "what is the model getting wrong lately".</summary>
    public const int MaxTranscriptsMined = 5_000;

    /// <summary>Bound on screening rounds within ONE scan. A scan LOOPS - mine, screen, exclude the rejected,
    /// re-mine - because the miner caps its output (MaxSuggestions) and, on a first run, ordinary-word garbage
    /// fills that cap and would otherwise crowd every real term out of the model's sight (proven on the
    /// owner's real corpus: "mindzie" was rank ~55 behind 50 rejected clusters). The loop converges when a
    /// round yields no never-judged candidate; this bound is the cost ceiling for pathological corpora - a
    /// scan makes at most this many model calls, and later scans make almost none (verdicts persist).</summary>
    public const int MaxScreeningRoundsPerScan = 10;

    /// <summary>Resolves the screening brain and its model id for a tenant. Production builds the hosted
    /// inference brain the same way Wingman's fast leg does; tests pass a stub. The brain is disposed by the
    /// scan after the call.</summary>
    public delegate Task<(IAgentBrain Brain, string Model)> ScreeningBrainFactory(TenantId tenant, CancellationToken ct);

    private readonly TranscriptStore _transcripts;
    private readonly DictionarySuggestionDismissalStore _dismissals;
    private readonly DictionarySuggestionVerdictStore _verdicts;
    private readonly DictionarySuggestionScanStore _scans;
    private readonly Func<TenantId, DictationDictionary> _glossaryProvider;
    private readonly ScreeningBrainFactory _brainFactory;
    private readonly MistranscriptionMiner.Options _options;
    private readonly Func<DateTime> _now;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scanLocks = new(StringComparer.Ordinal);

    /// <param name="transcripts">The store the raw transcripts are mined from. Required.</param>
    /// <param name="dismissals">The store the dismissed terms are read from. Required.</param>
    /// <param name="verdicts">The store the model verdicts are read from and recorded to. Required.</param>
    /// <param name="scans">The store the scan result is stored in and served from. Required.</param>
    /// <param name="glossaryProvider">Resolves a tenant's current glossary; production passes
    /// <see cref="TenantGlossary.Load"/>. Required.</param>
    /// <param name="brainFactory">Resolves the screening brain per tenant. Required.</param>
    /// <param name="options">Mining policy; <see cref="MistranscriptionMiner.Options.Default"/> when null.</param>
    /// <param name="now">Clock for scan timestamps; <see cref="DateTime.UtcNow"/> when null.</param>
    public DictionarySuggestionService(
        TranscriptStore transcripts,
        DictionarySuggestionDismissalStore dismissals,
        DictionarySuggestionVerdictStore verdicts,
        DictionarySuggestionScanStore scans,
        Func<TenantId, DictationDictionary> glossaryProvider,
        ScreeningBrainFactory brainFactory,
        MistranscriptionMiner.Options? options = null,
        Func<DateTime>? now = null)
    {
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _dismissals = dismissals ?? throw new ArgumentNullException(nameof(dismissals));
        _verdicts = verdicts ?? throw new ArgumentNullException(nameof(verdicts));
        _scans = scans ?? throw new ArgumentNullException(nameof(scans));
        _glossaryProvider = glossaryProvider ?? throw new ArgumentNullException(nameof(glossaryProvider));
        _brainFactory = brainFactory ?? throw new ArgumentNullException(nameof(brainFactory));
        _options = options ?? MistranscriptionMiner.Options.Default;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Run a full scan for a tenant: mine, screen the never-judged candidates, persist verdicts, store and
    /// return the result. Serialized per tenant - a concurrent second call waits, then runs (its screening is
    /// a no-op because the verdicts are already persisted).
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<DictionarySuggestionScanStore.ScanResult> RunScanAsync(TenantId tenant, CancellationToken ct = default)
    {
        var gate = _scanLocks.GetOrAdd(tenant.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RunScanCoreAsync(tenant, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DictionarySuggestionScanStore.ScanResult> RunScanCoreAsync(TenantId tenant, CancellationToken ct)
    {
        var raw = _transcripts.RawTexts(tenant, MaxTranscriptsMined);
        var dictionary = _glossaryProvider(tenant);
        var dismissed = _dismissals.DismissedTermNorms(tenant);

        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kv in _verdicts.VerdictsByNorm(tenant))
            verdicts[kv.Key] = kv.Value;

        // The mine-screen loop. Each round mines with the REJECTED terms excluded (they occupy the miner's
        // capped output otherwise - the crowding-out proven on the owner's real corpus), screens whatever is
        // still never-judged, and re-mines. Converges when a round has nothing unjudged; bounded by
        // MaxScreeningRoundsPerScan as the cost ceiling. Steady state is one round and zero model calls.
        var screeningOk = true;
        var screeningError = "";
        IReadOnlyList<MistranscriptionSuggestion> mined;
        var rounds = 0;
        while (true)
        {
            var excluded = dismissed
                .Concat(verdicts.Where(kv => !kv.Value).Select(kv => kv.Key))
                .ToList();
            mined = MistranscriptionMiner.Mine(raw, dictionary, excluded, _options);

            var unjudged = mined.Where(s => !verdicts.ContainsKey(Normalize(s.Term))).ToList();
            if (unjudged.Count == 0)
                break;
            if (rounds >= MaxScreeningRoundsPerScan)
            {
                // The bound hit with candidates still unjudged: say so rather than pretend the scan is
                // complete. The next scan resumes where this one stopped (verdicts persist).
                screeningOk = false;
                screeningError = $"stopped after {rounds} screening rounds with {unjudged.Count} candidates still unjudged; the next scan continues";
                FileLog.Write($"[SuggestionScan] tenant={tenant.ToLogString()} round cap hit: {screeningError}");
                break;
            }
            rounds++;

            try
            {
                var (brain, model) = await _brainFactory(tenant, ct);
                IReadOnlyList<DictionarySuggestionVerdictStore.Verdict> judged;
                using (brain)
                {
                    judged = await DictionarySuggestionScreen.JudgeAsync(brain, unjudged, ct);
                }
                _verdicts.Record(tenant, judged, model, _now().ToUniversalTime());
                foreach (var v in judged)
                    verdicts[Normalize(v.Term)] = v.Approved;
                FileLog.Write($"[SuggestionScan] tenant={tenant.ToLogString()} round={rounds} screened={judged.Count} approved={judged.Count(v => v.Approved)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail loud on the stored result: the page says screening is unavailable and the unjudged
                // candidates stay hidden. Previously-approved suggestions were screened, so they still serve.
                screeningOk = false;
                screeningError = ex.Message;
                FileLog.Write($"[SuggestionScan] tenant={tenant.ToLogString()} screening FAILED: {ex.Message}");
                break;
            }
        }

        var approved = mined
            .Where(s => verdicts.TryGetValue(Normalize(s.Term), out var ok) && ok)
            .ToList();

        var result = new DictionarySuggestionScanStore.ScanResult(
            _now().ToUniversalTime(), screeningOk, screeningError, approved);
        _scans.Save(tenant, result);
        return result;
    }

    /// <summary>The tenant's stored scan result, or null when no scan has ever run. Never mines, never calls
    /// the model - this is what the page and the badge read.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public DictionarySuggestionScanStore.ScanResult? GetStored(TenantId tenant) => _scans.Get(tenant);

    /// <summary>The stored suggestions for a tenant (empty when no scan has run).</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyList<MistranscriptionSuggestion> GetSuggestions(TenantId tenant)
        => GetStored(tenant)?.Suggestions ?? Array.Empty<MistranscriptionSuggestion>();

    /// <summary>The stored suggestion count for a tenant - the navigation badge number.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public int GetSuggestionCount(TenantId tenant) => GetSuggestions(tenant).Count;

    /// <summary>Find one stored suggestion by its (case/punctuation-insensitive) term, or null. The apply and
    /// dismiss endpoints resolve each requested term against this, so a caller can only act on what the scan
    /// actually offered.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public MistranscriptionSuggestion? FindSuggestion(TenantId tenant, string term)
    {
        var norm = Normalize(term ?? "");
        if (norm.Length == 0) return null;
        return GetSuggestions(tenant).FirstOrDefault(s => Normalize(s.Term) == norm);
    }

    /// <summary>Remove one term from the tenant's stored suggestions in place (after an apply or a dismiss),
    /// so the page and the badge reflect the action immediately without a rescan.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public void RemoveFromStored(TenantId tenant, string term) => _scans.RemoveSuggestion(tenant, term);

    /// <summary>Lower-cased letters and digits only - identical to the miner's clustering fold.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
