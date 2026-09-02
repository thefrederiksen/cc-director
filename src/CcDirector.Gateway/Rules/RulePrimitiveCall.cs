namespace CcDirector.Gateway.Rules;

/// <summary>Where an argument's value comes from. Two ways, and no third: a value written down when the
/// rule was built, or one of the closed set of runtime inputs.</summary>
public enum RuleArgumentSource
{
    /// <summary>A value fixed when the rule was built.</summary>
    Literal,

    /// <summary>One of the <see cref="RuleInput"/> values, supplied when the rule runs.</summary>
    Input,
}

/// <summary>
/// One argument of a stored primitive call: which parameter it fills, where its value comes from, and the
/// value itself. The value is always carried as a list of strings - one entry for a single value, several
/// for a list of terms - so the stored shape is uniform and there is nowhere for a program to hide.
///
/// This is DATA and only data. There is no field here that an interpreter could read, and none that the
/// account ever types into: the account writes English, and this is derived from it and validated against
/// a real primitive signature before it is stored (owner ruling 15).
/// </summary>
public sealed class RuleArgument
{
    /// <summary>The wire name of the parameter this argument fills, e.g. "root".</summary>
    public string Parameter { get; set; } = "";

    /// <summary>Where the value comes from - the wire name of a <see cref="RuleArgumentSource"/>.</summary>
    public string Source { get; set; } = "";

    /// <summary>The value: one entry for a single value, several for a list of literal terms. For an input
    /// argument, exactly one entry - the input's wire name.</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>An argument whose value was fixed when the rule was built.</summary>
    public static RuleArgument Literal(string parameter, string value) => new()
    {
        Parameter = parameter,
        Source = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Literal)),
        Values = new List<string> { value },
    };

    /// <summary>An argument whose value is a list of literal terms.</summary>
    public static RuleArgument LiteralList(string parameter, IEnumerable<string> values) => new()
    {
        Parameter = parameter,
        Source = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Literal)),
        Values = values?.ToList() ?? new List<string>(),
    };

    /// <summary>An argument taken from one of the runtime inputs when the rule runs.</summary>
    public static RuleArgument FromInput(string parameter, RuleInput input) => new()
    {
        Parameter = parameter,
        Source = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input)),
        Values = new List<string> { RuleInputs.NameOf(input) },
    };

    /// <summary>A readable one-line rendering for the firing record: "root=/repo" or "now=&lt;now&gt;".</summary>
    public string Describe() =>
        Source == RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input))
            ? Parameter + "=<" + string.Join(",", Values) + ">"
            : Parameter + "=" + string.Join(",", Values);
}

/// <summary>
/// A stored call to one verified primitive: the primitive's wire name and its arguments. This is what a
/// rule holds instead of code - a NAME plus argument values, checked against the real signature by
/// <see cref="RuleCallValidator"/> before it can be written (Architect ruling A4).
/// </summary>
public sealed class RulePrimitiveCall
{
    /// <summary>The verified primitive's wire name, e.g. "is_path_inside".</summary>
    public string Name { get; set; } = "";

    /// <summary>The arguments, one per parameter of that primitive.</summary>
    public List<RuleArgument> Arguments { get; set; } = new();

    /// <summary>Build a call to a primitive with the given arguments.</summary>
    public static RulePrimitiveCall To(string name, params RuleArgument[] arguments) => new()
    {
        Name = name,
        Arguments = arguments?.ToList() ?? new List<RuleArgument>(),
    };

    /// <summary>A readable one-line rendering for the firing record.</summary>
    public string Describe() => Name + "(" + string.Join(", ", Arguments.Select(a => a.Describe())) + ")";
}
