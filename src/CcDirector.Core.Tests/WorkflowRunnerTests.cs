using CcDirector.Core.Browser;
using Xunit;

namespace CcDirector.Core.Tests;

public class WorkflowRunnerTests
{
    // -------------------------------------------------------------------
    // BuildConditionJs tests
    // -------------------------------------------------------------------

    [Fact]
    public void BuildConditionJs_ElementExists_ReturnsSelectorCheck()
    {
        var condition = new WorkflowCondition
        {
            Check = "elementExists",
            Selector = "#login-form",
        };

        var js = WorkflowRunner.BuildConditionJs(condition);

        Assert.Equal("!!document.querySelector('#login-form')", js);
    }

    [Fact]
    public void BuildConditionJs_UrlContains_ReturnsLocationCheck()
    {
        var condition = new WorkflowCondition
        {
            Check = "urlContains",
            Value = "/dashboard",
        };

        var js = WorkflowRunner.BuildConditionJs(condition);

        Assert.Equal("window.location.href.includes('/dashboard')", js);
    }

    [Fact]
    public void BuildConditionJs_TextVisible_ReturnsInnerTextCheck()
    {
        var condition = new WorkflowCondition
        {
            Check = "textVisible",
            Value = "Welcome back",
        };

        var js = WorkflowRunner.BuildConditionJs(condition);

        Assert.Equal("!!document.querySelector('body').innerText.includes('Welcome back')", js);
    }

    [Fact]
    public void BuildConditionJs_UnknownCheck_ReturnsFalse()
    {
        var condition = new WorkflowCondition
        {
            Check = "unknownCheck",
        };

        var js = WorkflowRunner.BuildConditionJs(condition);

        Assert.Equal("false", js);
    }

    [Fact]
    public void BuildConditionJs_SelectorWithQuotes_EscapesCorrectly()
    {
        var condition = new WorkflowCondition
        {
            Check = "elementExists",
            Selector = "input[name='email']",
        };

        var js = WorkflowRunner.BuildConditionJs(condition);

        Assert.Equal("!!document.querySelector('input[name=\\'email\\']')", js);
    }

    // -------------------------------------------------------------------
    // Step structure tests
    // -------------------------------------------------------------------

    [Fact]
    public void WorkflowStep_ActionType_DefaultsCorrectly()
    {
        var step = new WorkflowStep();

        Assert.Equal("action", step.Type);
        Assert.Null(step.Action);
        Assert.Null(step.Condition);
    }

    [Fact]
    public void WorkflowStep_ConditionType_HasThenAndElse()
    {
        var step = new WorkflowStep
        {
            Type = "condition",
            Condition = new WorkflowCondition
            {
                Check = "elementExists",
                Selector = "#banner",
                ThenSteps = new List<WorkflowStep>
                {
                    new() { Type = "action", Action = new WorkflowAction { Command = "click" } },
                },
                ElseSteps = new List<WorkflowStep>
                {
                    new() { Type = "action", Action = new WorkflowAction { Command = "navigate" } },
                },
            },
        };

        Assert.Equal("condition", step.Type);
        Assert.NotNull(step.Condition);
        Assert.Single(step.Condition.ThenSteps);
        Assert.Single(step.Condition.ElseSteps);
        Assert.Equal("click", step.Condition.ThenSteps[0].Action!.Command);
        Assert.Equal("navigate", step.Condition.ElseSteps[0].Action!.Command);
    }

    [Fact]
    public void WorkflowCondition_NestedConditions_WorkCorrectly()
    {
        var step = new WorkflowStep
        {
            Type = "condition",
            Condition = new WorkflowCondition
            {
                Check = "elementExists",
                Selector = "#outer",
                ThenSteps = new List<WorkflowStep>
                {
                    new()
                    {
                        Type = "condition",
                        Condition = new WorkflowCondition
                        {
                            Check = "textVisible",
                            Value = "inner text",
                            ThenSteps = new List<WorkflowStep>
                            {
                                new() { Type = "action", Action = new WorkflowAction { Command = "type" } },
                            },
                        },
                    },
                },
            },
        };

        var innerCondition = step.Condition!.ThenSteps[0].Condition;
        Assert.NotNull(innerCondition);
        Assert.Equal("textVisible", innerCondition.Check);
        Assert.Single(innerCondition.ThenSteps);
    }
}
