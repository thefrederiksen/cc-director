using System.Text.Json;
using CCComputer.Agent;
using Xunit;

namespace CCComputer.Agent.Tests;

/// <summary>
/// Tests for the EvidenceChain data model classes:
/// EvidenceChain, EvidenceStep, ToolAction, ScreenshotEvidence, EvidenceConfig.
/// </summary>
public class EvidenceChainTests
{
    // ------------------------------------------------------------------
    //  EvidenceChain default properties
    // ------------------------------------------------------------------

    [Fact]
    public void EvidenceChain_DefaultProperties_AreInitialized()
    {
        var chain = new EvidenceChain();

        Assert.Equal("", chain.SessionId);
        Assert.Equal(default(DateTime), chain.StartedAt);
        Assert.Null(chain.CompletedAt);
        Assert.Equal("", chain.Request);
        Assert.NotNull(chain.Config);
        Assert.NotNull(chain.Steps);
        Assert.Empty(chain.Steps);
        Assert.Null(chain.Outcome);
        Assert.Null(chain.FinalResponse);
        Assert.Null(chain.TotalDurationMs);
    }

    [Fact]
    public void EvidenceChain_CanSetAllProperties()
    {
        var now = DateTime.UtcNow;
        var chain = new EvidenceChain
        {
            SessionId = "sess-001",
            StartedAt = now,
            CompletedAt = now.AddMinutes(5),
            Request = "Open Notepad",
            Outcome = "success",
            FinalResponse = "Notepad has been opened.",
            TotalDurationMs = 300_000
        };

        Assert.Equal("sess-001", chain.SessionId);
        Assert.Equal(now, chain.StartedAt);
        Assert.Equal(now.AddMinutes(5), chain.CompletedAt);
        Assert.Equal("Open Notepad", chain.Request);
        Assert.Equal("success", chain.Outcome);
        Assert.Equal("Notepad has been opened.", chain.FinalResponse);
        Assert.Equal(300_000L, chain.TotalDurationMs);
    }

    // ------------------------------------------------------------------
    //  EvidenceStep creation
    // ------------------------------------------------------------------

    [Fact]
    public void EvidenceStep_DefaultProperties_AreInitialized()
    {
        var step = new EvidenceStep();

        Assert.Equal(0, step.StepNumber);
        Assert.Equal(default(DateTime), step.StepStartedAt);
        Assert.Null(step.StepCompletedAt);
        Assert.Null(step.StepDurationMs);
        Assert.NotNull(step.Actions);
        Assert.Empty(step.Actions);
        Assert.Equal(0, step.SettleDelayMs);
        Assert.Null(step.Screenshot);
        Assert.Null(step.Observation);
        Assert.Null(step.ObservedAt);
    }

    [Fact]
    public void EvidenceStep_CanSetAllProperties()
    {
        var now = DateTime.UtcNow;
        var step = new EvidenceStep
        {
            StepNumber = 3,
            StepStartedAt = now,
            StepCompletedAt = now.AddSeconds(10),
            StepDurationMs = 10_000,
            SettleDelayMs = 500,
            Observation = "Notepad window is open.",
            ObservedAt = now.AddSeconds(11)
        };

        Assert.Equal(3, step.StepNumber);
        Assert.Equal(now, step.StepStartedAt);
        Assert.Equal(now.AddSeconds(10), step.StepCompletedAt);
        Assert.Equal(10_000L, step.StepDurationMs);
        Assert.Equal(500, step.SettleDelayMs);
        Assert.Equal("Notepad window is open.", step.Observation);
        Assert.Equal(now.AddSeconds(11), step.ObservedAt);
    }

    // ------------------------------------------------------------------
    //  ToolAction creation
    // ------------------------------------------------------------------

    [Fact]
    public void ToolAction_DefaultProperties_AreInitialized()
    {
        var action = new ToolAction();

        Assert.Equal("", action.ToolName);
        Assert.NotNull(action.Arguments);
        Assert.Empty(action.Arguments);
        Assert.Null(action.Result);
        Assert.False(action.Success);
        Assert.Equal(default(DateTime), action.StartedAt);
        Assert.Null(action.CompletedAt);
        Assert.Null(action.DurationMs);
        Assert.Null(action.ErrorMessage);
    }

    [Fact]
    public void ToolAction_CanSetAllProperties()
    {
        var now = DateTime.UtcNow;
        var action = new ToolAction
        {
            ToolName = "click",
            Arguments = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 200 },
            Result = "Clicked at (100, 200)",
            Success = true,
            StartedAt = now,
            CompletedAt = now.AddMilliseconds(150),
            DurationMs = 150,
            ErrorMessage = null
        };

        Assert.Equal("click", action.ToolName);
        Assert.Equal(2, action.Arguments.Count);
        Assert.Equal("Clicked at (100, 200)", action.Result);
        Assert.True(action.Success);
        Assert.Equal(now, action.StartedAt);
        Assert.Equal(now.AddMilliseconds(150), action.CompletedAt);
        Assert.Equal(150L, action.DurationMs);
        Assert.Null(action.ErrorMessage);
    }

    [Fact]
    public void ToolAction_ErrorMessage_CanBeSet()
    {
        var action = new ToolAction
        {
            ToolName = "click",
            Success = false,
            ErrorMessage = "Element not found"
        };

        Assert.False(action.Success);
        Assert.Equal("Element not found", action.ErrorMessage);
    }

    // ------------------------------------------------------------------
    //  ScreenshotEvidence creation
    // ------------------------------------------------------------------

    [Fact]
    public void ScreenshotEvidence_DefaultProperties_AreInitialized()
    {
        var evidence = new ScreenshotEvidence();

        Assert.Equal("", evidence.Path);
        Assert.Equal(default(DateTime), evidence.CapturedAt);
        Assert.Equal(0L, evidence.SizeBytes);
    }

    [Fact]
    public void ScreenshotEvidence_CanSetAllProperties()
    {
        var now = DateTime.UtcNow;
        var evidence = new ScreenshotEvidence
        {
            Path = @"C:\sessions\step_001.png",
            CapturedAt = now,
            SizeBytes = 245_760
        };

        Assert.Equal(@"C:\sessions\step_001.png", evidence.Path);
        Assert.Equal(now, evidence.CapturedAt);
        Assert.Equal(245_760L, evidence.SizeBytes);
    }

    // ------------------------------------------------------------------
    //  EvidenceConfig creation
    // ------------------------------------------------------------------

    [Fact]
    public void EvidenceConfig_DefaultProperties_AreInitialized()
    {
        var config = new EvidenceConfig();

        Assert.Equal("", config.ModelId);
        Assert.Equal(0, config.SettleDelayMs);
    }

    [Fact]
    public void EvidenceConfig_CanSetAllProperties()
    {
        var config = new EvidenceConfig
        {
            ModelId = "computer-use-preview",
            SettleDelayMs = 1500
        };

        Assert.Equal("computer-use-preview", config.ModelId);
        Assert.Equal(1500, config.SettleDelayMs);
    }

    // ------------------------------------------------------------------
    //  JSON round-trip serialization
    // ------------------------------------------------------------------

    [Fact]
    public void EvidenceChain_JsonRoundTrip_PreservesAllFields()
    {
        var now = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var original = new EvidenceChain
        {
            SessionId = "sess-roundtrip",
            StartedAt = now,
            CompletedAt = now.AddMinutes(2),
            Request = "Type hello world",
            Config = new EvidenceConfig
            {
                ModelId = "computer-use-preview",
                SettleDelayMs = 1000
            },
            Outcome = "success",
            FinalResponse = "Typed hello world into the editor.",
            TotalDurationMs = 120_000
        };

        var step = new EvidenceStep
        {
            StepNumber = 1,
            StepStartedAt = now,
            StepCompletedAt = now.AddSeconds(5),
            StepDurationMs = 5000,
            SettleDelayMs = 1000,
            Observation = "Text editor is showing.",
            ObservedAt = now.AddSeconds(6)
        };

        step.Actions.Add(new ToolAction
        {
            ToolName = "type",
            Arguments = new Dictionary<string, object?> { ["text"] = "hello world" },
            Result = "Typed text",
            Success = true,
            StartedAt = now.AddSeconds(1),
            CompletedAt = now.AddSeconds(2),
            DurationMs = 1000
        });

        step.Screenshot = new ScreenshotEvidence
        {
            Path = @"C:\sessions\step_001.png",
            CapturedAt = now.AddSeconds(3),
            SizeBytes = 102_400
        };

        original.Steps.Add(step);

        // Serialize
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(original, options);

        // Deserialize
        var deserialized = JsonSerializer.Deserialize<EvidenceChain>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.StartedAt, deserialized.StartedAt);
        Assert.Equal(original.CompletedAt, deserialized.CompletedAt);
        Assert.Equal(original.Request, deserialized.Request);
        Assert.Equal(original.Config.ModelId, deserialized.Config.ModelId);
        Assert.Equal(original.Config.SettleDelayMs, deserialized.Config.SettleDelayMs);
        Assert.Equal(original.Outcome, deserialized.Outcome);
        Assert.Equal(original.FinalResponse, deserialized.FinalResponse);
        Assert.Equal(original.TotalDurationMs, deserialized.TotalDurationMs);

        // Verify step
        Assert.Single(deserialized.Steps);
        var deserializedStep = deserialized.Steps[0];
        Assert.Equal(step.StepNumber, deserializedStep.StepNumber);
        Assert.Equal(step.StepStartedAt, deserializedStep.StepStartedAt);
        Assert.Equal(step.StepCompletedAt, deserializedStep.StepCompletedAt);
        Assert.Equal(step.StepDurationMs, deserializedStep.StepDurationMs);
        Assert.Equal(step.SettleDelayMs, deserializedStep.SettleDelayMs);
        Assert.Equal(step.Observation, deserializedStep.Observation);
        Assert.Equal(step.ObservedAt, deserializedStep.ObservedAt);

        // Verify action
        Assert.Single(deserializedStep.Actions);
        var deserializedAction = deserializedStep.Actions[0];
        Assert.Equal("type", deserializedAction.ToolName);
        Assert.True(deserializedAction.Success);
        Assert.Equal("Typed text", deserializedAction.Result);
        Assert.Equal(1000L, deserializedAction.DurationMs);

        // Verify screenshot
        Assert.NotNull(deserializedStep.Screenshot);
        Assert.Equal(@"C:\sessions\step_001.png", deserializedStep.Screenshot.Path);
        Assert.Equal(102_400L, deserializedStep.Screenshot.SizeBytes);
    }

    [Fact]
    public void EvidenceChain_JsonRoundTrip_NullFieldsArePreserved()
    {
        var original = new EvidenceChain
        {
            SessionId = "sess-nulls",
            Request = "Test null fields"
            // CompletedAt, Outcome, FinalResponse, TotalDurationMs all null
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<EvidenceChain>(json, options);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.CompletedAt);
        Assert.Null(deserialized.Outcome);
        Assert.Null(deserialized.FinalResponse);
        Assert.Null(deserialized.TotalDurationMs);
        Assert.Empty(deserialized.Steps);
    }

    [Fact]
    public void EvidenceChain_StepsList_CanHoldMultipleSteps()
    {
        var chain = new EvidenceChain();

        chain.Steps.Add(new EvidenceStep { StepNumber = 1 });
        chain.Steps.Add(new EvidenceStep { StepNumber = 2 });
        chain.Steps.Add(new EvidenceStep { StepNumber = 3 });

        Assert.Equal(3, chain.Steps.Count);
        Assert.Equal(1, chain.Steps[0].StepNumber);
        Assert.Equal(2, chain.Steps[1].StepNumber);
        Assert.Equal(3, chain.Steps[2].StepNumber);
    }

    [Fact]
    public void EvidenceStep_ActionsList_CanHoldMultipleActions()
    {
        var step = new EvidenceStep();

        step.Actions.Add(new ToolAction { ToolName = "click" });
        step.Actions.Add(new ToolAction { ToolName = "type" });

        Assert.Equal(2, step.Actions.Count);
        Assert.Equal("click", step.Actions[0].ToolName);
        Assert.Equal("type", step.Actions[1].ToolName);
    }
}
