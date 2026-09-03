using System.Globalization;

namespace CcDirector.Gateway.Rules;

/// <summary>The answer to "may this call be stored?": yes, or no with a stated reason in plain English.</summary>
public sealed record RuleCallValidation(bool IsValid, string Reason)
{
    /// <summary>A call that may be stored.</summary>
    public static RuleCallValidation Ok { get; } = new(true, "");

    /// <summary>A refusal, with the reason the account and the record will both read.</summary>
    public static RuleCallValidation Refused(string reason) => new(false, reason);
}

/// <summary>
/// THE WRITE-TIME VALIDATOR. Nothing gets into the rule store without passing through here (Architect
/// ruling A4): a call naming a primitive that does not exist is REFUSED, and a call supplying the wrong
/// arguments to a real primitive is REFUSED, both with a reason a person can act on.
///
/// This is where "the model cannot name anything outside the set" stops being a hope. The set it is
/// checked against is <see cref="RulePrimitiveRegistry"/>, derived from the reviewed code, so a refusal
/// here is a fact about what the product ships rather than about what a document says. Every list a reason
/// quotes - the primitives, the runtime inputs, the extract kinds - is likewise read off the code, so a
/// refusal can never advertise something that is not there.
/// </summary>
public static class RuleCallValidator
{
    private static readonly string LiteralSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Literal));
    private static readonly string InputSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));

    /// <summary>Every extract kind's wire name, derived from the closed set itself.</summary>
    private static readonly IReadOnlyList<string> ExtractKindNames =
        Enum.GetValues<RuleExtractKind>().Select(k => RuleWireNames.ToWireName(k.ToString())).ToList();

    /// <summary>Check one call against the primitives the product actually ships.</summary>
    public static RuleCallValidation Validate(RulePrimitiveCall call) => Validate(call, RulePrimitiveRegistry.Default);

    /// <summary>Check one call against a given registry.</summary>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    public static RuleCallValidation Validate(RulePrimitiveCall call, RulePrimitiveRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        if (call is null)
            return RuleCallValidation.Refused("a rule cannot hold an empty check.");

        if (string.IsNullOrWhiteSpace(call.Name))
            return RuleCallValidation.Refused(
                "a check has to name one of the checks we ship. It named nothing. " + WhatWeShip(registry));

        var primitive = registry.Find(call.Name);
        if (primitive is null)
            return RuleCallValidation.Refused(
                $"there is no check called '{call.Name}'. " + WhatWeShip(registry));

        var arguments = call.Arguments ?? new List<RuleArgument>();

        // A NULL ELEMENT IS A REFUSAL, NOT A CRASH. The arguments are a JSON-shaped mutable list, so a null
        // element is exactly the shape malformed authoring output arrives in - and it used to reach the
        // GroupBy below and throw, which turned a stated refusal into an unhandled Gateway failure. A
        // refusal is a reason somebody can act on; an exception is not.
        if (arguments.Any(a => a is null))
            return RuleCallValidation.Refused(
                $"the check '{primitive.Name}' was given a value that is nothing at all. Every value a check " +
                "is given has to say which parameter it fills and where it comes from.");

        var duplicate = arguments
            .GroupBy(a => a.Parameter ?? "", StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return RuleCallValidation.Refused(
                $"the check '{primitive.Name}' was given '{duplicate.Key}' more than once; each value is given once.");

        foreach (var argument in arguments)
        {
            var parameterName = argument.Parameter ?? "";
            if (!primitive.Parameters.Any(p => string.Equals(p.Name, parameterName, StringComparison.Ordinal)))
                return RuleCallValidation.Refused(
                    $"the check '{primitive.Name}' has no value called '{parameterName}'. It takes: " +
                    string.Join(", ", primitive.Parameters.Select(p => p.Name)) + ".");
        }

        foreach (var parameter in primitive.Parameters)
        {
            var argument = arguments.FirstOrDefault(
                a => string.Equals(a.Parameter, parameter.Name, StringComparison.Ordinal));
            if (argument is null)
                return RuleCallValidation.Refused(
                    $"the check '{primitive.Name}' needs a value for '{parameter.Name}' and was not given one.");

            var problem = ProblemWithArgument(primitive.Name, parameter, argument);
            if (problem is not null)
                return RuleCallValidation.Refused(problem);
        }

        return RuleCallValidation.Ok;
    }

    /// <summary>Check every call, answering with the FIRST refusal so the reason names one real problem
    /// rather than a list the reader has to sift.</summary>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    public static RuleCallValidation ValidateAll(IEnumerable<RulePrimitiveCall> calls, RulePrimitiveRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (calls is null) return RuleCallValidation.Ok;

        foreach (var call in calls)
        {
            var result = Validate(call, registry);
            if (!result.IsValid) return result;
        }
        return RuleCallValidation.Ok;
    }

    /// <summary>What is wrong with one argument, or null when nothing is.</summary>
    private static string? ProblemWithArgument(
        string primitiveName, RulePrimitiveParameter parameter, RuleArgument argument)
    {
        var values = (argument.Values ?? new List<string>()).Select(v => v ?? "").ToList();
        var source = argument.Source ?? "";

        if (string.Equals(source, InputSource, StringComparison.Ordinal))
        {
            if (values.Count != 1)
                return $"'{parameter.Name}' on the check '{primitiveName}' has to name exactly one thing to read " +
                       $"when the rule runs; it named {values.Count}.";

            var inputName = values[0];
            if (!RuleInputs.TryFind(inputName, out _, out var inputKind))
                return $"'{parameter.Name}' on the check '{primitiveName}' asks to read '{inputName}' when the rule " +
                       "runs, and there is no such thing. What a rule can read is: " +
                       string.Join(", ", RuleInputs.Names) + ".";

            if (inputKind != parameter.Kind)
                return $"'{parameter.Name}' on the check '{primitiveName}' wants {Describe(parameter.Kind)}, " +
                       $"but '{inputName}' is {Describe(inputKind)}.";

            return null;
        }

        if (string.Equals(source, LiteralSource, StringComparison.Ordinal))
            return ProblemWithLiteral(primitiveName, parameter, values);

        return $"'{parameter.Name}' on the check '{primitiveName}' says its value comes from '{source}', which is " +
               $"not something a rule can hold. A value is either written down ('{LiteralSource}') or read when " +
               $"the rule runs ('{InputSource}').";
    }

    /// <summary>What is wrong with a written-down value, or null when nothing is.</summary>
    private static string? ProblemWithLiteral(
        string primitiveName, RulePrimitiveParameter parameter, IReadOnlyList<string> values)
    {
        switch (parameter.Kind)
        {
            case RuleValueKind.Text:
                if (values.Count != 1)
                    return $"'{parameter.Name}' on the check '{primitiveName}' is one piece of text; " +
                           $"{values.Count} were given.";
                if (string.IsNullOrWhiteSpace(values[0]))
                    return $"'{parameter.Name}' on the check '{primitiveName}' is one piece of text, and it " +
                           "was empty.";
                return null;

            case RuleValueKind.TextList:
                if (values.Count == 0)
                    return $"'{parameter.Name}' on the check '{primitiveName}' is a list of words to look for, " +
                           "and it was empty.";
                if (values.Any(string.IsNullOrWhiteSpace))
                    return $"'{parameter.Name}' on the check '{primitiveName}' contains an empty word.";
                return null;

            case RuleValueKind.Timestamp:
                if (values.Count != 1)
                    return $"'{parameter.Name}' on the check '{primitiveName}' is one moment in time; " +
                           $"{values.Count} were given.";
                if (!DateTime.TryParse(values[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                    return $"'{parameter.Name}' on the check '{primitiveName}' is a moment in time, and " +
                           $"'{values[0]}' is not one.";
                return null;

            case RuleValueKind.ExtractKind:
                if (values.Count != 1)
                    return $"'{parameter.Name}' on the check '{primitiveName}' is one kind of thing to look for; " +
                           $"{values.Count} were given.";
                if (!ExtractKindNames.Contains(values[0], StringComparer.Ordinal))
                    return $"'{parameter.Name}' on the check '{primitiveName}' cannot look for '{values[0]}'. " +
                           "It can look for: " + string.Join(", ", ExtractKindNames) + ".";
                return null;

            default:
                return $"'{parameter.Name}' on the check '{primitiveName}' is {Describe(parameter.Kind)}, " +
                       "which is an answer a check gives back, not a value a rule can supply.";
        }
    }

    /// <summary>The account never reads a type name, so every kind has words.</summary>
    private static string Describe(RuleValueKind kind) => kind switch
    {
        RuleValueKind.Text => "a piece of text",
        RuleValueKind.TextList => "a list of words",
        RuleValueKind.Timestamp => "a moment in time",
        RuleValueKind.ExtractKind => "a kind of thing to look for",
        RuleValueKind.Boolean => "a yes or no answer",
        RuleValueKind.Seconds => "a number of seconds",
        RuleValueKind.OptionalSeconds => "a number of seconds, or nothing",
        _ => kind.ToString(),
    };

    /// <summary>The checks the product ships, read off the registry so a reason can never name one we do
    /// not have.</summary>
    private static string WhatWeShip(RulePrimitiveRegistry registry) =>
        "The checks we ship are: " + string.Join(", ", registry.Primitives.Select(p => p.Name)) + ".";
}
