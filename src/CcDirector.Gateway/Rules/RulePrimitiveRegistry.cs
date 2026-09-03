using System.Reflection;

namespace CcDirector.Gateway.Rules;

/// <summary>One parameter of a verified primitive: its wire name and the kind of value it takes.</summary>
public sealed record RulePrimitiveParameter(string Name, RuleValueKind Kind);

/// <summary>
/// One verified primitive's whole contract, READ OFF THE METHOD (Architect ruling A2): its wire name, the
/// plain-English summary the account reads, its parameters in order, the kind of answer it gives, and the
/// method itself so a validated call can be run without a lookup table.
/// </summary>
public sealed record RulePrimitiveSignature(
    string Name,
    string Summary,
    IReadOnlyList<RulePrimitiveParameter> Parameters,
    RuleValueKind Answer,
    MethodInfo Method);

/// <summary>
/// The registry of verified primitives, DERIVED BY REFLECTION and never hand-kept. It scans the assembly
/// for public static methods carrying <see cref="RulePrimitiveAttribute"/> and reads each one's contract
/// off its own signature, so the set of legal names, arities and argument kinds has exactly one source:
/// the reviewed code itself. There is no constant, switch, JSON file or test holding a second copy of the
/// list, which is what makes "the model cannot name anything outside the set" a fact about the code rather
/// than a promise about a document.
///
/// A method whose parameter or return type is outside <see cref="RuleValueKind"/> is refused when the
/// registry is built - loudly, at first use - rather than being mapped onto something close.
/// </summary>
public sealed class RulePrimitiveRegistry
{
    private readonly Dictionary<string, RulePrimitiveSignature> _byName;

    private RulePrimitiveRegistry(IReadOnlyList<RulePrimitiveSignature> primitives)
    {
        Primitives = primitives;
        _byName = primitives.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>The registry derived from the Gateway assembly - the set the product actually ships.</summary>
    public static RulePrimitiveRegistry Default { get; } = BuildFrom(typeof(RulePrimitiveRegistry).Assembly);

    /// <summary>Every shipped primitive, ordered by wire name so a read is stable.</summary>
    public IReadOnlyList<RulePrimitiveSignature> Primitives { get; }

    /// <summary>The primitive with this wire name, or null when nothing ships under that name.</summary>
    public RulePrimitiveSignature? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out var found) ? found : null;

    /// <summary>Derive the registry from every attributed public static method in an assembly.</summary>
    /// <exception cref="ArgumentNullException">The assembly is null.</exception>
    /// <exception cref="InvalidOperationException">A primitive's signature uses a type outside
    /// <see cref="RuleValueKind"/>, or two primitives derive the same wire name.</exception>
    public static RulePrimitiveRegistry BuildFrom(Assembly assembly)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        var signatures = new List<RulePrimitiveSignature>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attribute = method.GetCustomAttribute<RulePrimitiveAttribute>();
                if (attribute is null) continue;
                signatures.Add(Describe(method, attribute));
            }
        }

        var duplicate = signatures.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"two verified primitives derive the same name '{duplicate.Key}': " +
                string.Join(", ", duplicate.Select(s => s.Method.DeclaringType?.Name + "." + s.Method.Name)) +
                ". A primitive's name is its method name, so the methods must differ.");

        signatures.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return new RulePrimitiveRegistry(signatures);
    }

    /// <summary>Read one attributed method's contract off its own signature.</summary>
    private static RulePrimitiveSignature Describe(MethodInfo method, RulePrimitiveAttribute attribute)
    {
        var name = RuleWireNames.ToWireName(method.Name);

        var parameters = new List<RulePrimitiveParameter>();
        foreach (var parameter in method.GetParameters())
        {
            var parameterName = RuleWireNames.ToWireName(parameter.Name
                ?? throw new InvalidOperationException($"verified primitive '{name}' has an unnamed parameter"));
            parameters.Add(new RulePrimitiveParameter(parameterName, KindOfMember(parameter.ParameterType, name, parameterName)));
        }

        var answer = KindOfMember(method.ReturnType, name, "the answer");
        return new RulePrimitiveSignature(name, attribute.Summary, parameters, answer, method);
    }

    /// <summary>The kind for one member of a primitive's signature, naming what was wrong when it is not
    /// one of ours - the message has to say which primitive and which part, or a refusal at startup is a
    /// puzzle rather than an instruction.</summary>
    private static RuleValueKind KindOfMember(Type clrType, string primitiveName, string member)
    {
        try
        {
            return KindOf(clrType);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"verified primitive '{primitiveName}' cannot ship: {member} is {clrType.Name}, " +
                "which is outside the closed set of rule value kinds. " + ex.Message, ex);
        }
    }

    /// <summary>The <see cref="RuleValueKind"/> for a CLR type - the ONE mapping between the two. A type
    /// outside the closed set is refused rather than approximated: a primitive is only as safe as the
    /// promise that its arguments are validated DATA, and that promise needs a finite set of shapes.</summary>
    /// <exception cref="InvalidOperationException">The type is outside the closed set.</exception>
    public static RuleValueKind KindOf(Type clrType)
    {
        if (clrType is null) throw new ArgumentNullException(nameof(clrType));

        if (clrType == typeof(string)) return RuleValueKind.Text;
        if (clrType == typeof(IReadOnlyList<string>)) return RuleValueKind.TextList;
        if (clrType == typeof(DateTime)) return RuleValueKind.Timestamp;
        if (clrType == typeof(RuleExtractKind)) return RuleValueKind.ExtractKind;
        if (clrType == typeof(bool)) return RuleValueKind.Boolean;
        if (clrType == typeof(double)) return RuleValueKind.Seconds;
        if (clrType == typeof(double?)) return RuleValueKind.OptionalSeconds;

        throw new InvalidOperationException(
            $"'{clrType.FullName}' is not a rule value kind. The closed set is: string, " +
            "IReadOnlyList<string>, DateTime, RuleExtractKind, bool, double, double?.");
    }
}
