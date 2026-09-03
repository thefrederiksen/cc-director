using System.Text.Json;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The ONE place a check written as JSON becomes a <see cref="RulePrimitiveCall"/>. The agent's reply and
/// the rule-writing route both arrive as JSON and both must mean the same thing by it, so there is one
/// reader rather than two that drift.
///
/// Nothing here decides whether a call is LEGAL - a name that does not exist and an argument of the wrong
/// shape both come through with their text intact, so that <see cref="RuleCallValidator"/> can refuse them
/// BY NAME. A reader that quietly dropped what it did not recognise would turn a refusal into a silence.
/// </summary>
public static class RuleCallJson
{
    private static readonly string InputSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));
    private static readonly string LiteralSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Literal));

    /// <summary>
    /// THE CHECKS OFF A DOCUMENT, STRICTLY - the one reader both the agent's reply and the rule-writing
    /// route use, so a check written as JSON means one thing in this feature and not two.
    ///
    /// Returns null and sets <paramref name="problem"/> when the collection is not a collection of checks.
    /// It used to be read only when the property was an ARRAY, and every other shape - a missing property,
    /// an object, a number - quietly became an empty list; the rule-writing route additionally dropped any
    /// array member that was not an object. So a malformed safety check DISAPPEARED and the act went ahead
    /// as though none had been asked for. Path containment, freshness and failure detection are exactly the
    /// checks that shape would swallow.
    ///
    /// An EMPTY ARRAY is legal and means what it says: no checks were asked for. That is the difference the
    /// strictness preserves - "I want none" is a statement, and "I said nothing" is not.
    /// </summary>
    /// <param name="root">The document the checks hang off.</param>
    /// <param name="property">The property holding them.</param>
    /// <param name="required">Whether the property must be there at all. It must whenever something will
    /// ACT on the strength of it - an act that goes ahead with a swallowed check is the whole defect - and
    /// need not when nothing will follow either way, which is a decline. A MALFORMED collection is refused
    /// in both cases: that one is never harmless, because it means somebody meant to say something.</param>
    /// <param name="problem">Why they could not be read, in plain English, or null when they could.</param>
    public static IReadOnlyList<RulePrimitiveCall>? ReadChecks(
        JsonElement root, string property, bool required, out string? problem)
    {
        problem = null;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var array))
        {
            if (!required) return Array.Empty<RulePrimitiveCall>();
            problem =
                $"'{property}' has to be given, as a list. An empty list means no checks were asked for; " +
                "leaving it out means nobody said, and a check that goes missing takes its refusal with it.";
            return null;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            problem =
                $"'{property}' has to be a list of checks, and this one is {Shape(array)}. A check that is " +
                "not read is a check that did not run, and nothing downstream can tell that from a reply " +
                "that asked for none.";
            return null;
        }

        var calls = new List<RulePrimitiveCall>();
        var position = 0;
        foreach (var entry in array.EnumerateArray())
        {
            position++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problem =
                    $"check {position} in '{property}' is {Shape(entry)}, not a check. Every entry has to be " +
                    "a check with a name and its arguments.";
                return null;
            }
            calls.Add(ReadCall(entry));
        }
        return calls;
    }

    /// <summary>What a JSON value is, in words a person reads.</summary>
    private static string Shape(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "a piece of text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a yes-or-no value",
        JsonValueKind.Null => "null",
        _ => "not a value this reader knows",
    };

    /// <summary>One check, written as <c>{ "name": "...", "arguments": { "parameter": value } }</c>.</summary>
    public static RulePrimitiveCall ReadCall(JsonElement entry)
    {
        var call = new RulePrimitiveCall { Name = (Text(entry, "name") ?? "").Trim() };

        if (!entry.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            return call;

        foreach (var argument in arguments.EnumerateObject())
            call.Arguments.Add(ReadArgument(argument.Name, argument.Value));

        return call;
    }

    /// <summary>
    /// One argument's value. A string in angle brackets is a request to read something when the rule runs;
    /// a list is a list of literal terms; anything else is one written-down value. The angle-bracket form is
    /// the same rendering the firing record uses, so what is written and what a person later reads are the
    /// same notation.
    /// </summary>
    public static RuleArgument ReadArgument(string parameter, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return new RuleArgument
            {
                Parameter = parameter,
                Source = LiteralSource,
                Values = value.EnumerateArray().Select(Scalar).ToList(),
            };

        var text = Scalar(value);
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>')
            return new RuleArgument
            {
                Parameter = parameter,
                Source = InputSource,
                Values = new List<string> { text[1..^1].Trim() },
            };

        return RuleArgument.Literal(parameter, text);
    }

    /// <summary>A JSON value as the one string this feature carries values in.</summary>
    public static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.ToString(),
    };

    /// <summary>One named string off an object, or null when it is not there.</summary>
    public static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? Scalar(value) : null;
}
