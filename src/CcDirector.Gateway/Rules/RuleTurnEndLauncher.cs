using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// WHERE A RULE PASS IS STARTED FROM, AS A PIECE OF THIS FEATURE RATHER THAN A LAMBDA IN THE HOST.
///
/// This code used to sit inside the Gateway host's turn-end handler. Everything about it belongs to Session
/// Rules - the tenant scope it enters, the fire-and-forget it starts, the isolation that keeps a rule fault
/// away from the voice refresh beside it - but it lived in a type that also runs the whole rest of the
/// Gateway, and that had a consequence beyond tidiness: the guard which proves that only ONE type in this
/// feature can type into a session selects the feature's types, and the host is not one of them. The launch
/// was listed as a phase 2 feature piece and was outside the thing guarding the feature. The independent
/// inspection of landing B found that.
///
/// So it is a type of its own, carrying the feature marker, and the guard covers it like everything else.
///
/// THE TURN-END BOUNDARY IS THE SAFETY, and it is worth saying where it is enforced. This is called only
/// from the Working-to-idle transition, which is the only event that can wake a rule at all - so a session
/// that is working is out of a rule's reach by construction rather than by a check somebody has to
/// remember. The evaluator then re-reads the session's own activity immediately before the keystroke,
/// because this boundary says what was true when the pass STARTED and a model call sits in between.
///
/// FIRE AND FORGET, ON PURPOSE. The turn-end handler must not wait on a screen read, a model call and a
/// keystroke. The tenant scope is entered HERE so the background work inherits it: without it the pass
/// would run with no tenant in scope, the rule read would be denied, and the evaluator would wake up and
/// silently do nothing. Overlapping passes on one session are the evaluator's problem and it refuses them -
/// see its serialisation note.
/// </summary>
[RuleFeature]
internal sealed class RuleTurnEndLauncher
{
    private readonly RuleEvaluator _rules;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;

    /// <param name="rules">The evaluator to run.</param>
    /// <param name="enterTenantScope">Enters a tenant's storage scope for the life of the pass. Optional -
    /// self-host has one partition and the scope is inert.</param>
    /// <exception cref="ArgumentNullException">The evaluator is null.</exception>
    public RuleTurnEndLauncher(RuleEvaluator rules, Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _enterTenantScope = enterTenantScope;
    }

    /// <summary>A KNOWN-BAD PROBE, committed on purpose and removed in the commit that widens the guard.
    /// The launch is a feature piece, and until now it lived inside the Gateway host and was outside the
    /// guard that proves only one type in this feature can type into a session.</summary>
    internal static Task<Api.SessionVerbClient.PromptSendOutcome> ProbeTypeFromTheLauncher(
        Api.SessionVerbClient route, string sid) =>
        route.SendPromptAsync(sid, new Contracts.PromptRequest { Text = "/probe", AppendEnter = true });

    /// <summary>
    /// A session has just crossed into idle. Start one pass over this account's standing instructions.
    /// Never throws: a rule fault must not break whatever else hangs off the same boundary.
    /// </summary>
    public void OnTurnEnd(TenantId tenant, string directorId, string sessionId)
    {
        try
        {
            using (_enterTenantScope?.Invoke(tenant))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var pass = await _rules.EvaluateAsync(tenant, directorId, sessionId, CancellationToken.None)
                            .ConfigureAwait(false);
                        FileLog.Write($"[RuleTurnEndLauncher] sid={sessionId} outcome={pass.What}");
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[RuleTurnEndLauncher] sid={sessionId} FAILED: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RuleTurnEndLauncher] sid={sessionId} could not start: {ex.Message}");
        }
    }
}
