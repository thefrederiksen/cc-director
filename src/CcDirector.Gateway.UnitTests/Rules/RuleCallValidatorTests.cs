using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// The write-time validator (Architect ruling A4). A rule holds a CALL - a primitive's name plus argument
/// values - and never a program, so the whole safety of the design rests on the call being checked against
/// the real signature before it is stored. These tests do that by writing REFUSED calls and reading the
/// reason, not by grepping a schema for the absence of a code column: an absence proves nothing about what
/// the writer would accept.
/// </summary>
public sealed class RuleCallValidatorTests
{
    private static readonly RulePrimitiveRegistry Registry = RulePrimitiveRegistry.Default;

    private static RulePrimitiveCall AGoodCall() => RulePrimitiveCall.To(
        "is_path_inside",
        RuleArgument.Literal("target", "D:\\ReposFred\\devthrottle\\src\\file.cs"),
        RuleArgument.FromInput("root", RuleInput.SessionRepositoryPath));

    [Fact]
    public void A_well_formed_call_is_accepted()
    {
        var result = RuleCallValidator.Validate(AGoodCall(), Registry);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("", result.Reason);
    }

    [Fact]
    public void Every_shipped_primitive_can_be_called_with_arguments_of_its_own_declared_kinds()
    {
        // Derived from the registry, so a primitive added later is covered without editing this test.
        Assert.NotEmpty(Registry.Primitives);
        foreach (var primitive in Registry.Primitives)
        {
            var call = RulePrimitiveCall.To(
                primitive.Name,
                primitive.Parameters.Select(SomeArgumentOfKind).ToArray());
            var result = RuleCallValidator.Validate(call, Registry);
            Assert.True(result.IsValid, primitive.Name + ": " + result.Reason);
        }
    }

    private static RuleArgument SomeArgumentOfKind(RulePrimitiveParameter parameter) => parameter.Kind switch
    {
        RuleValueKind.Text => RuleArgument.Literal(parameter.Name, "some text"),
        RuleValueKind.TextList => RuleArgument.LiteralList(parameter.Name, new[] { "usage limit" }),
        RuleValueKind.Timestamp => RuleArgument.FromInput(parameter.Name, RuleInput.Now),
        RuleValueKind.ExtractKind => RuleArgument.Literal(parameter.Name, "path"),
        _ => throw new InvalidOperationException(
            $"parameter '{parameter.Name}' is of kind {parameter.Kind}, which no argument can supply - " +
            "the test needs updating alongside whatever primitive introduced it"),
    };

    // ---- a malformed collection, which is what authoring output looks like when it goes wrong ------

    [Fact]
    public void A_call_whose_argument_list_holds_a_null_is_refused_with_a_reason_and_does_not_crash()
    {
        // The arguments are a JSON-shaped mutable list, so a null element is exactly the shape malformed
        // authoring output arrives in. It used to reach the first GroupBy and throw, which turned a stated
        // refusal into an unhandled Gateway failure - a crash is not a reason anybody can act on.
        var call = new RulePrimitiveCall
        {
            Name = "is_path_inside",
            Arguments = new List<RuleArgument>
            {
                RuleArgument.Literal("target", "D:\repo\file.cs"),
                null!,
            },
        };

        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.NotEqual("", result.Reason);
        Assert.Contains("is_path_inside", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_of_calls_holding_a_null_call_is_refused_with_a_reason_and_does_not_crash()
    {
        var result = RuleCallValidator.ValidateAll(new[] { AGoodCall(), null! }, Registry);

        Assert.False(result.IsValid);
        Assert.NotEqual("", result.Reason);
    }

    [Fact]
    public void A_call_whose_argument_values_hold_a_null_is_refused_with_a_reason_and_does_not_crash()
    {
        var call = RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            new RuleArgument
            {
                Parameter = "terms",
                Source = "literal",
                Values = new List<string> { "usage limit", null! },
            });

        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.NotEqual("", result.Reason);
    }

    // ---- a primitive that does not exist ----------------------------------------------------------

    [Fact]
    public void A_call_naming_a_primitive_that_does_not_exist_is_refused_with_a_reason()
    {
        var call = RulePrimitiveCall.To("run_expression", RuleArgument.Literal("expr", "os.system('rm -rf /')"));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("run_expression", result.Reason, StringComparison.Ordinal);
        // The reason must say what DOES exist, derived from the registry, or it is not actionable.
        Assert.Contains("is_path_inside", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_with_no_name_at_all_is_refused_with_a_reason()
    {
        var result = RuleCallValidator.Validate(RulePrimitiveCall.To(""), Registry);
        Assert.False(result.IsValid);
        Assert.NotEqual("", result.Reason);
    }

    [Fact]
    public void A_dotnet_method_name_is_not_a_primitive_name()
    {
        var result = RuleCallValidator.Validate(RulePrimitiveCall.To("IsPathInside"), Registry);
        Assert.False(result.IsValid);
        Assert.Contains("IsPathInside", result.Reason, StringComparison.Ordinal);
    }

    // ---- the wrong arguments to a real primitive --------------------------------------------------

    [Fact]
    public void A_missing_argument_is_refused_and_the_reason_names_the_parameter()
    {
        var call = RulePrimitiveCall.To("is_path_inside", RuleArgument.Literal("target", "D:\\repo\\a.cs"));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("is_path_inside", result.Reason, StringComparison.Ordinal);
        Assert.Contains("root", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_argument_for_a_parameter_that_does_not_exist_is_refused()
    {
        var call = RulePrimitiveCall.To(
            "is_path_inside",
            RuleArgument.Literal("target", "D:\\repo\\a.cs"),
            RuleArgument.FromInput("root", RuleInput.SessionRepositoryPath),
            RuleArgument.Literal("depth", "3"));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("depth", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_parameter_supplied_twice_is_refused()
    {
        var call = RulePrimitiveCall.To(
            "is_path_inside",
            RuleArgument.Literal("target", "D:\\repo\\a.cs"),
            RuleArgument.Literal("target", "D:\\repo\\b.cs"),
            RuleArgument.FromInput("root", RuleInput.SessionRepositoryPath));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("target", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_argument_of_the_wrong_kind_is_refused_and_the_reason_says_what_was_wanted()
    {
        // "terms" takes a list of literal terms; "now" is a moment in time.
        var call = RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.FromInput("terms", RuleInput.Now));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("terms", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_value_supplied_where_the_primitive_wants_one_text_is_required_to_be_exactly_one()
    {
        var call = RulePrimitiveCall.To(
            "is_path_inside",
            RuleArgument.LiteralList("target", new[] { "D:\\repo\\a.cs", "D:\\repo\\b.cs" }),
            RuleArgument.FromInput("root", RuleInput.SessionRepositoryPath));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("target", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_term_list_is_refused()
    {
        var call = RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", Array.Empty<string>()));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("terms", result.Reason, StringComparison.Ordinal);
    }

    // ---- the closed sets --------------------------------------------------------------------------

    [Fact]
    public void An_extract_kind_outside_the_closed_set_is_refused_and_the_reason_lists_the_set()
    {
        var call = RulePrimitiveCall.To(
            "extract_first",
            RuleArgument.FromInput("screen_text", RuleInput.ScreenText),
            RuleArgument.Literal("kind", "regex"));
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("regex", result.Reason, StringComparison.Ordinal);
        Assert.Contains("path", result.Reason, StringComparison.Ordinal);
        Assert.Contains("duration", result.Reason, StringComparison.Ordinal);
        Assert.Contains("timestamp", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_extract_kind_in_the_closed_set_is_accepted()
    {
        Assert.NotEmpty(Enum.GetValues<RuleExtractKind>());
        foreach (var kind in Enum.GetValues<RuleExtractKind>())
        {
            var call = RulePrimitiveCall.To(
                "extract_first",
                RuleArgument.FromInput("screen_text", RuleInput.ScreenText),
                RuleArgument.Literal("kind", RuleWireNames.ToWireName(kind.ToString())));
            var result = RuleCallValidator.Validate(call, Registry);
            Assert.True(result.IsValid, kind + ": " + result.Reason);
        }
    }

    [Fact]
    public void An_input_that_does_not_exist_is_refused_and_the_reason_lists_the_inputs_that_do()
    {
        var call = new RulePrimitiveCall
        {
            Name = "matches_any",
            Arguments =
            {
                new RuleArgument { Parameter = "text", Source = "input", Values = { "the_whole_filesystem" } },
                RuleArgument.LiteralList("terms", new[] { "usage limit" }),
            },
        };
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("the_whole_filesystem", result.Reason, StringComparison.Ordinal);
        Assert.Contains("screen_text", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_is_neither_a_literal_nor_an_input_is_refused()
    {
        var call = new RulePrimitiveCall
        {
            Name = "is_path_inside",
            Arguments =
            {
                new RuleArgument { Parameter = "target", Source = "python", Values = { "open('/etc/passwd')" } },
                RuleArgument.FromInput("root", RuleInput.SessionRepositoryPath),
            },
        };
        var result = RuleCallValidator.Validate(call, Registry);

        Assert.False(result.IsValid);
        Assert.Contains("python", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_literal_must_be_a_real_moment()
    {
        var good = RulePrimitiveCall.To(
            "elapsed_since",
            RuleArgument.Literal("first_failure", "2026-09-02T09:44:00Z"),
            RuleArgument.FromInput("now", RuleInput.Now));
        Assert.True(RuleCallValidator.Validate(good, Registry).IsValid);

        var bad = RulePrimitiveCall.To(
            "elapsed_since",
            RuleArgument.Literal("first_failure", "whenever"),
            RuleArgument.FromInput("now", RuleInput.Now));
        var result = RuleCallValidator.Validate(bad, Registry);
        Assert.False(result.IsValid);
        Assert.Contains("whenever", result.Reason, StringComparison.Ordinal);
    }

    // ---- many calls at once -----------------------------------------------------------------------

    [Fact]
    public void ValidateAll_accepts_a_list_of_good_calls_and_refuses_on_the_first_bad_one()
    {
        var allGood = new[] { AGoodCall(), AGoodCall() };
        Assert.True(RuleCallValidator.ValidateAll(allGood, Registry).IsValid);

        var oneBad = new[] { AGoodCall(), RulePrimitiveCall.To("eval_python", RuleArgument.Literal("code", "1+1")) };
        var result = RuleCallValidator.ValidateAll(oneBad, Registry);
        Assert.False(result.IsValid);
        Assert.Contains("eval_python", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAll_accepts_an_empty_list_because_a_rule_may_need_no_check_at_all()
    {
        Assert.True(RuleCallValidator.ValidateAll(Array.Empty<RulePrimitiveCall>(), Registry).IsValid);
    }
}
