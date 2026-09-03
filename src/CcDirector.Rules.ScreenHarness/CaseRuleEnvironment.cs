using System.Diagnostics;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Rules.ScreenHarness;

/// <summary>One firing as the evaluator wrote it down, with the id the environment answered.</summary>
public sealed record RecordedFiring(Guid FiringId, RuleFiringDraft Draft);

/// <summary>
/// THE EVALUATOR'S ENVIRONMENT FOR ONE CASE ON ONE MODEL. Every read comes from the case: the rules are the
/// corpus rules, the facts are the case's facts, the screen is the case's screen and is the same on every
/// read, so the evaluator's re-read before the keystroke passes exactly as it would on an unchanged live
/// screen. The one model call is the production <see cref="HostedInferenceBrain"/> on the model under test,
/// and it is timed here because this is the only place the call is made.
///
/// It mirrors <c>GatewayRuleEnvironment</c> where the production wiring has a shape worth keeping: a model
/// that throws is caught, logged as the reason, and answered as null, which the evaluator records as a
/// refusal - the report then counts it as "no answer" and names the exception, and a
/// <see cref="TimeoutException"/> is counted as a timeout. It never degrades into an assumption that the
/// rule may act.
///
/// IT CANNOT TYPE. Every corpus rule is a dry run, so the evaluator never reaches the send; if it ever
/// does, <see cref="TypeIntoSessionAsync"/> throws and the whole run fails loudly rather than recording a
/// phantom keystroke.
/// </summary>
public sealed class CaseRuleEnvironment : IRuleEnvironment
{
    private readonly IReadOnlyList<SessionRule> _rules;
    private readonly ScreenCase _case;
    private readonly IncludedModelId _model;
    private readonly string _apiKey;
    private readonly Action<string> _log;
    private readonly List<RecordedFiring> _firings = new();

    /// <param name="rules">The corpus rules, every one in dry run.</param>
    /// <param name="screenCase">The case whose facts and screen every read answers with.</param>
    /// <param name="model">The model under test, by its included id.</param>
    /// <param name="apiKey">The DevThrottle account key the Gateway itself reads. Never logged.</param>
    /// <param name="log">Where a line about the model call goes.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The key is blank.</exception>
    public CaseRuleEnvironment(
        IReadOnlyList<SessionRule> rules,
        ScreenCase screenCase,
        IncludedModelId model,
        string apiKey,
        Action<string> log)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _case = screenCase ?? throw new ArgumentNullException(nameof(screenCase));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("the DevThrottle account key is blank", nameof(apiKey));
        _apiKey = apiKey;
    }

    /// <summary>Every firing the evaluator wrote, completed where it completed one.</summary>
    public IReadOnlyList<RecordedFiring> Firings => _firings;

    /// <summary>How long the one model call took, or null when the model was never asked.</summary>
    public TimeSpan? ModelCallTime { get; private set; }

    /// <summary>What the model call threw, or null when it answered (or was never asked).</summary>
    public Exception? ModelFailure { get; private set; }

    /// <summary>How many times the model was asked. The evaluator asks once per pass.</summary>
    public int ModelCalls { get; private set; }

    /// <summary>What the model said, verbatim, or null when it was never asked or threw. Kept so a refusal
    /// in the report can be argued with: "the agent gave no reason" is a fact about the reply, and the
    /// reply is the evidence.</summary>
    public string? RawReply { get; private set; }

    /// <inheritdoc />
    public DateTime NowUtc => DateTime.UtcNow;

    /// <inheritdoc />
    public IReadOnlyList<SessionRule> Rules(TenantId tenant) => _rules;

    /// <inheritdoc />
    public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId) => Array.Empty<SessionRuleFiring>();

    /// <inheritdoc />
    public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId) => _case.SessionFacts();

    /// <inheritdoc />
    public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(_case.ScreenRows);

    /// <inheritdoc />
    public async Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
    {
        ModelCalls++;
        var sw = Stopwatch.StartNew();
        try
        {
            // AT THE PRODUCTION TEMPERATURE. The harness measures what the Gateway does, and the Gateway
            // asks this question at RuleAgentContract.JudgementTemperature.
            using var brain = new HostedInferenceBrain(
                TranscriptionEndpointResolver.DevThrottleBaseUrl, _apiKey, _model, log: _log,
                temperature: RuleAgentContract.JudgementTemperature);
            var result = await brain.AskAsync(prompt, ct).ConfigureAwait(false);
            sw.Stop();
            ModelCallTime = sw.Elapsed;
            RawReply = result?.Text;
            return result?.Text;
        }
        catch (Exception ex)
        {
            sw.Stop();
            ModelCallTime = sw.Elapsed;
            ModelFailure = ex;
            _log("[CaseRuleEnvironment] case " + _case.Id + " model " + _model.Value +
                 ": the agent could not be asked: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<RuleSendResult> TypeIntoSessionAsync(
        TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct) =>
        throw new InvalidOperationException("the harness was asked to type; a dry-run rule must never reach the send");

    /// <inheritdoc />
    public Guid RecordFiring(TenantId tenant, RuleFiringDraft draft)
    {
        var id = Guid.NewGuid();
        _firings.Add(new RecordedFiring(id, draft));
        return id;
    }

    /// <inheritdoc />
    public void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome)
    {
        var index = _firings.FindIndex(f => f.FiringId == firingId);
        if (index < 0)
            throw new InvalidOperationException("firing " + firingId + " was completed but never recorded");
        var existing = _firings[index];
        _firings[index] = existing with { Draft = existing.Draft with { TypedText = typedText, Outcome = outcome } };
    }
}
