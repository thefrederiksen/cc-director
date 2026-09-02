using System.Reflection;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// The registry is DERIVED, never hand-kept (Architect ruling A2). These tests check that as a PRESENCE:
/// the registry must be non-empty, and every method in the assembly carrying
/// <see cref="RulePrimitiveAttribute"/> must be reachable through it. The expected set is discovered by
/// this test's OWN reflection scan rather than typed out here, so a primitive added to the product without
/// being registered fails immediately, and a registry that came back empty fails rather than passing
/// vacuously.
/// </summary>
public sealed class RulePrimitiveRegistryTests
{
    private static readonly Assembly GatewayAssembly = typeof(RulePrimitives).Assembly;

    /// <summary>Every attributed public static method in the Gateway assembly, found independently of the
    /// registry - this is the instrument the completeness check is measured against.</summary>
    private static IReadOnlyList<MethodInfo> AttributedMethods() =>
        GatewayAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<RulePrimitiveAttribute>() is not null)
            .ToList();

    [Fact]
    public void The_assembly_actually_carries_attributed_primitives()
    {
        // The instrument first: if this scan finds nothing, every check below would pass over an empty
        // set and prove nothing at all.
        Assert.NotEmpty(AttributedMethods());
    }

    [Fact]
    public void The_registry_is_non_empty()
    {
        Assert.NotEmpty(RulePrimitiveRegistry.Default.Primitives);
    }

    [Fact]
    public void Every_attributed_primitive_is_reachable_through_the_registry()
    {
        var attributed = AttributedMethods();
        Assert.NotEmpty(attributed);

        foreach (var method in attributed)
        {
            var wireName = RuleWireNames.ToWireName(method.Name);
            var found = RulePrimitiveRegistry.Default.Find(wireName);
            Assert.True(found is not null, $"primitive '{wireName}' ({method.Name}) is not in the registry");
            Assert.Same(method, found!.Method);
        }

        Assert.Equal(attributed.Count, RulePrimitiveRegistry.Default.Primitives.Count);
    }

    /// <summary>
    /// THE FIVE THE RULING NAMES, and the ONE hand-written list in this file. They are the owner's ruling
    /// 15 and Architect ruling A3 - an EXTERNAL contract, which is exactly the kind of thing that cannot
    /// be derived from our own code, because deriving it from the code would make it agree with the code
    /// by construction and prove nothing at all. Everything it is compared against is derived.
    /// </summary>
    private static readonly (string Name, string[] Parameters, RuleValueKind Answer)[] TheApprovedFive =
    {
        ("is_path_inside",    new[] { "target", "root" },        RuleValueKind.Boolean),
        ("retry_delay_from",  new[] { "screen_text", "now" },    RuleValueKind.OptionalSeconds),
        ("elapsed_since",     new[] { "first_failure", "now" },  RuleValueKind.Seconds),
        ("matches_any",       new[] { "text", "terms" },         RuleValueKind.Boolean),
        ("extract_first",     new[] { "screen_text", "kind" },   RuleValueKind.Text),
    };

    [Fact]
    public void The_registry_ships_exactly_the_approved_checks_and_no_others()
    {
        // THE HALF THAT WAS MISSING. Both existing checks compared sets derived from the same attributes,
        // and the external one only asked whether each approved name was PRESENT - so a sixth attributed
        // method with supported parameter types was legal and left every test green. It was run against
        // exactly that: a sixth check committed on purpose, which the suite passed.
        //
        // A new general-purpose check is the route by which an interpreter returns under another name
        // (owner ruling 15), so adding one has to be a visible act. This is what makes it visible.
        var approved = TheApprovedFive.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var shipped = RulePrimitiveRegistry.Default.Primitives
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(shipped);
        Assert.Equal(approved, shipped);
    }

    [Fact]
    public void The_five_primitives_the_ruling_names_are_present_with_their_stated_signatures()
    {
        var expected = TheApprovedFive;

        foreach (var (name, parameters, answer) in expected)
        {
            var sig = RulePrimitiveRegistry.Default.Find(name);
            Assert.True(sig is not null, $"the ruling names '{name}' but the registry does not ship it");
            Assert.Equal(parameters, sig!.Parameters.Select(p => p.Name).ToArray());
            Assert.Equal(answer, sig.Answer);
            Assert.False(string.IsNullOrWhiteSpace(sig.Summary));
        }
    }

    [Fact]
    public void Argument_kinds_are_read_off_the_clr_signature()
    {
        var isPathInside = RulePrimitiveRegistry.Default.Find("is_path_inside")!;
        Assert.All(isPathInside.Parameters, p => Assert.Equal(RuleValueKind.Text, p.Kind));

        var matchesAny = RulePrimitiveRegistry.Default.Find("matches_any")!;
        Assert.Equal(RuleValueKind.Text, matchesAny.Parameters[0].Kind);
        Assert.Equal(RuleValueKind.TextList, matchesAny.Parameters[1].Kind);

        var elapsedSince = RulePrimitiveRegistry.Default.Find("elapsed_since")!;
        Assert.All(elapsedSince.Parameters, p => Assert.Equal(RuleValueKind.Timestamp, p.Kind));

        var extractFirst = RulePrimitiveRegistry.Default.Find("extract_first")!;
        Assert.Equal(RuleValueKind.ExtractKind, extractFirst.Parameters[1].Kind);
    }

    [Fact]
    public void A_primitive_whose_signature_uses_an_unsupported_type_is_refused_when_the_registry_is_built()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RulePrimitiveRegistry.BuildFrom(typeof(UnsupportedPrimitiveHolder).Assembly));
        Assert.Contains("bad_primitive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_names_are_simply_absent()
    {
        Assert.Null(RulePrimitiveRegistry.Default.Find("run_expression"));
        Assert.Null(RulePrimitiveRegistry.Default.Find("IsPathInside"));
    }
}

/// <summary>A deliberately WRONG primitive, living only in the test assembly, so the registry's refusal of
/// an unsupported signature is proved against a real bad input rather than asserted.</summary>
public static class UnsupportedPrimitiveHolder
{
    [RulePrimitive("A primitive taking a type outside the closed set - must be refused.")]
    public static bool BadPrimitive(Uri notAValueKind) => notAValueKind is not null;
}
