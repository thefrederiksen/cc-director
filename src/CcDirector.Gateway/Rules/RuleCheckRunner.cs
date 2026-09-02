using System.Globalization;

namespace CcDirector.Gateway.Rules;

/// <summary>The things a check can be handed when a rule runs. One field per member of
/// <see cref="RuleInput"/>, so a new input is a compiler error here rather than a silent null.</summary>
public sealed record RuleRuntime(
    string ScreenText,
    string RepositoryPath,
    DateTime NowUtc,
    DateTime? FirstFailureUtc);

/// <summary>
/// What running the asked-for checks produced: one record per check - which one, with what arguments, what
/// it answered - and, when one of them stands in the way of acting, the reason.
/// </summary>
public sealed record RuleCheckOutcome(IReadOnlyList<RulePrimitiveRun> Runs, string? Problem);

/// <summary>
/// Runs the checks the agent asked for. The calls arriving here have already been validated against the
/// derived registry, so this executes a real method with real arguments - there is nothing to interpret and
/// no expression to evaluate (owner ruling 15).
///
/// A CHECK IS A CONDITION, NOT A DECORATION. The agent is told to name a check only when its answer is
/// something it is staking its decision on, so a check whose answer is a plain yes-or-no and that answers
/// NO stops the act, and so does a check that could not be run at all. Checks that hand back a value rather
/// than a verdict - a delay, an extracted path - are recorded as evidence and stop nothing. Without that,
/// "which checks ran and what they answered" would be a line in a report that changed no outcome.
/// </summary>
public static class RuleCheckRunner
{
    /// <summary>Run every asked-for check, in the order the agent named them.</summary>
    /// <exception cref="ArgumentNullException">The runtime or the registry is null.</exception>
    public static RuleCheckOutcome Run(
        IReadOnlyList<RulePrimitiveCall> calls,
        RuleRuntime runtime,
        RulePrimitiveRegistry registry)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var runs = new List<RulePrimitiveRun>();
        string? problem = null;

        foreach (var call in calls ?? Array.Empty<RulePrimitiveCall>())
        {
            var arguments = string.Join(", ", (call.Arguments ?? new List<RuleArgument>()).Select(a => a.Describe()));
            var signature = registry.Find(call.Name);
            if (signature is null)
            {
                // Unreachable through the evaluator - the validator refuses an unknown name before this -
                // but a check that cannot be found is never treated as a check that passed.
                runs.Add(new RulePrimitiveRun(call.Name, arguments, "could not run: there is no such check."));
                problem ??= $"the check '{call.Name}' does not exist, so nothing was done.";
                continue;
            }

            string answer;
            try
            {
                var values = Bind(signature, call, runtime);
                answer = Describe(signature.Method.Invoke(null, values));
            }
            catch (Exception ex)
            {
                var cause = (ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex).Message;
                runs.Add(new RulePrimitiveRun(signature.Name, arguments, "could not run: " + cause));
                problem ??= $"the check {call.Describe()} could not be run ({cause}), and a check that could " +
                            "not be run is not a check that passed.";
                continue;
            }

            runs.Add(new RulePrimitiveRun(signature.Name, arguments, answer));

            if (signature.Answer == RuleValueKind.Boolean && answer == "false")
                problem ??= $"the check {call.Describe()} answered no, and the agent staked its decision on it.";
        }

        return new RuleCheckOutcome(runs, problem);
    }

    /// <summary>The argument values for one call, in the method's own parameter order.</summary>
    private static object?[] Bind(RulePrimitiveSignature signature, RulePrimitiveCall call, RuleRuntime runtime)
    {
        var inputSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));
        var values = new object?[signature.Parameters.Count];

        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var parameter = signature.Parameters[i];
            var argument = (call.Arguments ?? new List<RuleArgument>())
                .First(a => string.Equals(a.Parameter, parameter.Name, StringComparison.Ordinal));

            values[i] = string.Equals(argument.Source, inputSource, StringComparison.Ordinal)
                ? FromRuntime(argument.Values[0], runtime)
                : FromLiteral(parameter.Kind, argument.Values);
        }

        return values;
    }

    /// <summary>The value of one runtime input.</summary>
    /// <exception cref="InvalidOperationException">The input is one this build does not yet supply.</exception>
    private static object FromRuntime(string name, RuleRuntime runtime)
    {
        if (!RuleInputs.TryFind(name, out var input, out _))
            throw new InvalidOperationException($"there is nothing called '{name}' to read when a rule runs.");

        return input switch
        {
            RuleInput.ScreenText => runtime.ScreenText,
            RuleInput.SessionRepositoryPath => runtime.RepositoryPath,
            RuleInput.Now => runtime.NowUtc,
            RuleInput.FirstFailure => runtime.FirstFailureUtc
                ?? throw new InvalidOperationException(
                    "this check needs to know when the trouble first appeared, and nothing is tracking that yet."),
            _ => throw new InvalidOperationException($"the runtime input '{name}' has no value in this build."),
        };
    }

    /// <summary>The value of one written-down argument, in the shape its parameter takes.</summary>
    private static object FromLiteral(RuleValueKind kind, IReadOnlyList<string> values) => kind switch
    {
        RuleValueKind.Text => values[0],
        RuleValueKind.TextList => (IReadOnlyList<string>)values.ToList(),
        RuleValueKind.Timestamp => DateTime.Parse(values[0], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        RuleValueKind.ExtractKind => Enum.GetValues<RuleExtractKind>()
            .First(k => string.Equals(RuleWireNames.ToWireName(k.ToString()), values[0], StringComparison.Ordinal)),
        _ => throw new InvalidOperationException($"'{kind}' is an answer a check gives back, not a value it takes."),
    };

    /// <summary>A check's answer as the one string that goes on the record.</summary>
    private static string Describe(object? answer) => answer switch
    {
        null => "nothing",
        bool yes => yes ? "true" : "false",
        double seconds => seconds.ToString("0.###", CultureInfo.InvariantCulture),
        string text => text.Length == 0 ? "nothing found" : text,
        _ => Convert.ToString(answer, CultureInfo.InvariantCulture) ?? "",
    };
}
