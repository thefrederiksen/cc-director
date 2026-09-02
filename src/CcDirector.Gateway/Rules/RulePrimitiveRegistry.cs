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
    /// <exception cref="InvalidOperationException">A primitive's signature uses a type outside
    /// <see cref="RuleValueKind"/>, or two primitives derive the same wire name.</exception>
    public static RulePrimitiveRegistry BuildFrom(Assembly assembly) => throw new NotImplementedException();

    /// <summary>The <see cref="RuleValueKind"/> for a CLR type - the ONE mapping between the two.</summary>
    /// <exception cref="InvalidOperationException">The type is outside the closed set.</exception>
    public static RuleValueKind KindOf(Type clrType) => throw new NotImplementedException();
}
