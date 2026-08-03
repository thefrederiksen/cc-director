using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway suggestion SCAN engine (devthrottle #2075, redesigned in #2115) wired to real per-tenant
/// stores and a stub screening brain: a scan mines the tenant's transcripts, sends never-judged candidates
/// to the model, persists the verdicts (a term is judged at most once, ever), and stores the approved
/// result; reads serve the stored result and never mine. The mining policy itself is proven in
/// MistranscriptionMinerTests and the prompt/parse in DictionarySuggestionScreenTests; these tests prove the
/// WIRING: judge-once persistence, rejected terms hidden, the loud screening-failure path, tenant scoping,
/// and the stored-result read model.
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

    /// <summary>A screening brain whose verdict policy is a delegate; counts AskAsync calls. The default
    /// policy approves everything (each prompt's terms are parsed back out of the candidate lines).</summary>
    private sealed class StubBrain : IAgentBrain
    {
        private readonly Func<string, string> _answer;
        public int Calls;

        public StubBrain(Func<string, string>? answer = null)
            => _answer = answer ?? (prompt => ApproveAll(prompt));

        public static string ApproveAll(string prompt) => AnswerFor(prompt, approved: true);
        public static string RejectAll(string prompt) => AnswerFor(prompt, approved: false);

        /// <summary>Build a well-formed verdict array for every candidate line in the prompt (lines like
        /// <c>1. "term" heard as: ...</c>), approving or rejecting all of them.</summary>
        private static string AnswerFor(string prompt, bool approved)
        {
            var verdicts = new List<object>();
            foreach (var line in prompt.Split('\n'))
            {
                var t = line.Trim();
                var dot = t.IndexOf(". \"", StringComparison.Ordinal);
                if (dot < 0 || dot > 4) continue;
                var start = dot + 3;
                var end = t.IndexOf('"', start);
                if (end < 0) continue;
                verdicts.Add(new { term = t[start..end], approved, reason = "stub" });
            }
            return JsonSerializer.Serialize(verdicts);
        }

        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AskResult { Text = _answer(prompt) });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    private static DictionarySuggestionService Build(
        GatewayDbTestHarness h, TranscriptStore transcripts, StubBrain brain,
        Func<TenantId, DictationDictionary>? glossary = null, Func<DateTime>? now = null)
        => new(
            transcripts,
            new DictionarySuggestionDismissalStore(h.Open()),
            new DictionarySuggestionVerdictStore(h.Open()),
            new DictionarySuggestionScanStore(h.Open()),
            glossary ?? (_ => EmptyDict),
            (_, _) => Task.FromResult<(IAgentBrain, string)>((brain, "stub-model")),
            now: now ?? (() => Base));

    [Fact]
    public async Task RunScan_MinesScreensAndStoresTheApprovedResult()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var brain = new StubBrain();
        var svc = Build(h, transcripts, brain);

        // Before any scan: nothing stored, nothing served, no mining behind the read.
        Assert.Null(svc.GetStored(TenantA));
        Assert.Empty(svc.GetSuggestions(TenantA));
        Assert.Equal(0, svc.GetSuggestionCount(TenantA));

        var result = await svc.RunScanAsync(TenantA);

        Assert.True(result.ScreeningOk);
        Assert.Equal(Base, result.ScannedAtUtc);
        var s = Assert.Single(result.Suggestions);
        Assert.Equal("mindzie", s.Term);
        Assert.Equal(53, s.WrongCount);
        Assert.Equal(97, s.TotalCount);

        // The stored result serves the reads (including after a service rebuild - it is in the database).
        Assert.Equal(1, svc.GetSuggestionCount(TenantA));
        var reloaded = Build(h, transcripts, brain);
        Assert.Equal("mindzie", Assert.Single(reloaded.GetSuggestions(TenantA)).Term);
    }

    [Fact]
    public async Task RunScan_RejectedTermIsNeverShown()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        // The screenshot regression: a cluster of DISTINCT common words the miner chains together.
        SeedTerm(transcripts, TenantA, Base,
            new[] { ("that", 50), ("then", 30), ("them", 25), ("there", 22) });
        var brain = new StubBrain(StubBrain.RejectAll);
        var svc = Build(h, transcripts, brain);

        var result = await svc.RunScanAsync(TenantA);

        Assert.True(result.ScreeningOk);
        Assert.Empty(result.Suggestions);
        Assert.Equal(0, svc.GetSuggestionCount(TenantA));
    }

    [Fact]
    public async Task RunScan_JudgesEachTermAtMostOnceEver()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var brain = new StubBrain();
        var svc = Build(h, transcripts, brain);

        await svc.RunScanAsync(TenantA);
        Assert.Equal(1, brain.Calls);

        // Same corpus, second scan: the verdict is stored, so NO second model call.
        await svc.RunScanAsync(TenantA);
        Assert.Equal(1, brain.Calls);

        // Even through a fresh service instance (the persistence, not the instance, is the memory).
        var svc2 = Build(h, transcripts, brain);
        await svc2.RunScanAsync(TenantA);
        Assert.Equal(1, brain.Calls);
    }

    [Fact]
    public async Task RunScan_ScreeningFailure_IsLoudAndHidesUnjudgedTerms()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var approving = new StubBrain();
        var svc = Build(h, transcripts, approving);
        await svc.RunScanAsync(TenantA); // mindzie is judged and approved

        // New evidence arrives for a NEW term, but the screening model is now down.
        SeedTerm(transcripts, TenantA, Base.AddHours(1),
            new[] { ("Kubernetes", 20), ("Kubernetis", 6), ("Kubernettes", 4) });
        var down = new StubBrain(_ => throw new InvalidOperationException("model unreachable"));
        var svcDown = Build(h, transcripts, down);

        var result = await svcDown.RunScanAsync(TenantA);

        // Loud: the stored result says screening failed and why. The unjudged term is HIDDEN (never shown
        // unscreened); the previously-approved term still serves - it WAS screened.
        Assert.False(result.ScreeningOk);
        Assert.Contains("model unreachable", result.ScreeningError);
        Assert.Equal("mindzie", Assert.Single(result.Suggestions).Term);
    }

    [Fact]
    public async Task RunScan_UnparseableModelAnswer_IsAScreeningFailure()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var svc = Build(h, transcripts, new StubBrain(_ => "I think these all look fine to me!"));

        var result = await svc.RunScanAsync(TenantA);

        Assert.False(result.ScreeningOk);
        Assert.NotEqual("", result.ScreeningError);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task RunScan_RejectedGarbageCannotCrowdARealTermOutOfTheCap()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        // Two high-frequency garbage clusters and one real term. With the miner capped at TWO suggestions,
        // the first mining round is ALL garbage - the real term is over the cap. The scan must loop: screen,
        // reject, exclude, re-mine - so the real term surfaces and is approved within ONE scan. This is the
        // crowding-out found on the owner's real corpus (50 of 50 capped candidates were garbage).
        SeedTerm(transcripts, TenantA, Base, new[] { ("wanted", 300), ("wanting", 200), ("wants", 150) });
        SeedTerm(transcripts, TenantA, Base.AddMinutes(30), new[] { ("started", 250), ("starting", 180), ("starts", 120) });
        SeedTerm(transcripts, TenantA, Base.AddHours(1), MindzieCorpus);

        // The stub rejects garbage and approves only mindzie's cluster.
        var brain = new StubBrain(prompt =>
        {
            var approveMindzie = StubBrain.RejectAll(prompt);
            return approveMindzie.Replace("\"term\":\"mindzie\",\"approved\":false", "\"term\":\"mindzie\",\"approved\":true");
        });
        var svc = new DictionarySuggestionService(
            transcripts,
            new DictionarySuggestionDismissalStore(h.Open()),
            new DictionarySuggestionVerdictStore(h.Open()),
            new DictionarySuggestionScanStore(h.Open()),
            _ => EmptyDict,
            (_, _) => Task.FromResult<(IAgentBrain, string)>((brain, "stub-model")),
            options: MistranscriptionMiner.Options.Default with { MaxSuggestions = 2 },
            now: () => Base);

        var result = await svc.RunScanAsync(TenantA);

        Assert.True(result.ScreeningOk);
        Assert.Equal("mindzie", Assert.Single(result.Suggestions).Term);
        Assert.True(brain.Calls >= 2); // it took more than one round to reach the real term
    }

    [Fact]
    public async Task RunScan_IsPerTenant()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        SeedTerm(transcripts, TenantB, Base,
            new[] { ("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12) });
        var brain = new StubBrain();
        var svc = Build(h, transcripts, brain);

        await svc.RunScanAsync(TenantA);
        await svc.RunScanAsync(TenantB);

        Assert.Equal("mindzie", Assert.Single(svc.GetSuggestions(TenantA)).Term);
        Assert.Equal("Frederiksen", Assert.Single(svc.GetSuggestions(TenantB)).Term);
    }

    [Fact]
    public async Task RunScan_ExcludesGlossaryAndDismissedTerms()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        var dismissals = new DictionarySuggestionDismissalStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        SeedTerm(transcripts, TenantA, Base.AddHours(1),
            new[] { ("Kubernetes", 20), ("Kubernetis", 6), ("Kubernettes", 4) });
        var brain = new StubBrain();
        var glossaryWithMindzie = new DictationDictionary(
            new[] { "mindzie" }, new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });
        var svc = new DictionarySuggestionService(
            transcripts, dismissals,
            new DictionarySuggestionVerdictStore(h.Open()),
            new DictionarySuggestionScanStore(h.Open()),
            _ => glossaryWithMindzie,
            (_, _) => Task.FromResult<(IAgentBrain, string)>((brain, "stub-model")),
            now: () => Base);

        // mindzie is already in the glossary; Kubernetes gets dismissed; a scan then offers neither.
        dismissals.Dismiss(TenantA,
            new MistranscriptionSuggestion("Kubernetes", Array.Empty<MistranscriptionVariant>(), 0, 0), Base);
        var result = await svc.RunScanAsync(TenantA);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task RemoveFromStored_ReflectsApplyOrDismissWithoutARescan()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        SeedTerm(transcripts, TenantA, Base.AddHours(1),
            new[] { ("Kubernetes", 20), ("Kubernetis", 6), ("Kubernettes", 4) });
        var brain = new StubBrain();
        var svc = Build(h, transcripts, brain);
        await svc.RunScanAsync(TenantA);
        Assert.Equal(2, svc.GetSuggestionCount(TenantA));

        svc.RemoveFromStored(TenantA, "MINDZIE"); // normalized match, like the endpoints use

        Assert.Equal("Kubernetes", Assert.Single(svc.GetSuggestions(TenantA)).Term);
    }

    [Fact]
    public async Task FindSuggestion_MatchesCaseAndPunctuationInsensitively()
    {
        using var h = new GatewayDbTestHarness();
        var transcripts = new TranscriptStore(h.Open());
        SeedTerm(transcripts, TenantA, Base, MindzieCorpus);
        var svc = Build(h, transcripts, new StubBrain());
        await svc.RunScanAsync(TenantA);

        Assert.NotNull(svc.FindSuggestion(TenantA, "MINDZIE"));
        Assert.Null(svc.FindSuggestion(TenantA, "nothinghere"));
    }
}
