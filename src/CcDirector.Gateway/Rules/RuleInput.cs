using System.Reflection;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The closed set of RUNTIME INPUTS a stored rule may bind a primitive argument to. A rule is written
/// before the screen it will read exists, so <c>retry_delay_from(screen_text, now)</c> has to name the
/// screen and the clock rather than carry their values - and the set of things it may name is fixed here,
/// by us, exactly as the primitive set is. An argument bound to anything outside this enum is refused at
/// write time.
///
/// Each member is exactly one of the four things the five shipped primitives ask for; the set grows only
/// when a primitive needs something new, which is a product change.
/// </summary>
public enum RuleInput
{
    /// <summary>The terminal screen the rule is being evaluated against - the only input a rule watches
    /// (owner ruling 11).</summary>
    [RuleInputValue(RuleValueKind.Text)]
    ScreenText,

    /// <summary>The path of the repository the session is working in.</summary>
    [RuleInputValue(RuleValueKind.Text)]
    SessionRepositoryPath,

    /// <summary>The moment the rule is being evaluated, UTC.</summary>
    [RuleInputValue(RuleValueKind.Timestamp)]
    Now,

    /// <summary>When this session first showed the trouble the rule is about, UTC.</summary>
    [RuleInputValue(RuleValueKind.Timestamp)]
    FirstFailure,
}

/// <summary>Says what KIND of value a runtime input carries, so an argument bound to it can be checked
/// against the parameter it is being handed to. Written on the enum member itself so there is no second
/// table of input kinds to drift.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class RuleInputValueAttribute : Attribute
{
    public RuleInputValueAttribute(RuleValueKind kind) => Kind = kind;

    /// <summary>The kind of value this input carries.</summary>
    public RuleValueKind Kind { get; }
}

/// <summary>
/// The runtime inputs, DERIVED from <see cref="RuleInput"/> itself: the wire name from the member name and
/// the kind from the attribute on that member. Nothing here is hand-kept, so adding an input is one edit.
/// </summary>
public static class RuleInputs
{
    private static readonly Dictionary<string, (RuleInput Input, RuleValueKind Kind)> ByName = Build();

    private static Dictionary<string, (RuleInput, RuleValueKind)> Build()
    {
        var map = new Dictionary<string, (RuleInput, RuleValueKind)>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<RuleInput>())
        {
            var field = typeof(RuleInput).GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"rule input '{value}' has no backing field");
            var attribute = field.GetCustomAttribute<RuleInputValueAttribute>()
                ?? throw new InvalidOperationException(
                    $"rule input '{value}' does not say what kind of value it carries - " +
                    $"add [{nameof(RuleInputValueAttribute)}] to it.");
            map[RuleWireNames.ToWireName(value.ToString())] = (value, attribute.Kind);
        }
        return map;
    }

    /// <summary>Every runtime input's wire name, ordered so a read is stable.</summary>
    public static IReadOnlyList<string> Names { get; } = ByName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>The wire name for an input - the form a stored rule holds.</summary>
    public static string NameOf(RuleInput input) => RuleWireNames.ToWireName(input.ToString());

    /// <summary>Look a wire name up. Returns false when nothing ships under that name.</summary>
    public static bool TryFind(string name, out RuleInput input, out RuleValueKind kind)
    {
        if (name is not null && ByName.TryGetValue(name, out var found))
        {
            (input, kind) = found;
            return true;
        }
        input = default;
        kind = default;
        return false;
    }
}
