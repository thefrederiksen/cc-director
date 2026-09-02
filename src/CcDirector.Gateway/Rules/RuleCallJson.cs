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
