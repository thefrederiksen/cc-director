using System.Text.Json;
using CCComputer.Agent;
using Xunit;

namespace CCComputer.Agent.Tests;

/// <summary>
/// Tests for the EvidenceChainLogger state machine.
/// Each test creates a temp folder for the evidence chain JSON output,
/// then reads it back to verify the state transitions.
/// </summary>
public class EvidenceChainLoggerTests : IDisposable
{
    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _tempFolder;

    public EvidenceChainLoggerTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "EvidenceChainLoggerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
        {
            try { Directory.Delete(_tempFolder, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private EvidenceChain ReadChain()
    {
        var path = Path.Combine(_tempFolder, "evidence_chain.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EvidenceChain>(json, s_readOptions)
            ?? throw new InvalidOperationException("Failed to deserialize evidence chain from test output.");
    }

    // ------------------------------------------------------------------
    //  New logger starts with empty chain
    // ------------------------------------------------------------------

    [Fact]
    public void NewLogger_StartsWithEmptyChain()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-001", "Open Notepad", "computer-use-preview", 1000);

        logger.Save();
        var chain = ReadChain();

        Assert.Equal("sess-001", chain.SessionId);
        Assert.Equal("Open Notepad", chain.Request);
        Assert.Equal("computer-use-preview", chain.Config.ModelId);
        Assert.Equal(1000, chain.Config.SettleDelayMs);
        Assert.Empty(chain.Steps);
        Assert.Null(chain.Outcome);
        Assert.Null(chain.FinalResponse);
        Assert.Null(chain.CompletedAt);
    }

    // ------------------------------------------------------------------
    //  BeginStep creates a new step
    // ------------------------------------------------------------------

    [Fact]
    public void BeginStep_CreatesNewStep()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-002", "Click button", "test-model", 500);

        logger.BeginStep();
        logger.Save();
        var chain = ReadChain();

        Assert.Single(chain.Steps);
        Assert.Equal(1, chain.Steps[0].StepNumber);
        Assert.NotEqual(default, chain.Steps[0].StepStartedAt);
    }

    // ------------------------------------------------------------------
    //  RecordActionStart records tool action
    // ------------------------------------------------------------------

    [Fact]
    public void RecordActionStart_RecordsToolAction()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-003", "Click at (100, 200)", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("click", new Dictionary<string, object?>
        {
            ["x"] = 100,
            ["y"] = 200
        });
        logger.Save();
        var chain = ReadChain();

        Assert.Single(chain.Steps);
        Assert.Single(chain.Steps[0].Actions);

        var action = chain.Steps[0].Actions[0];
        Assert.Equal("click", action.ToolName);
        Assert.NotEqual(default, action.StartedAt);
        // Action not yet completed
        Assert.Null(action.CompletedAt);
        Assert.Null(action.Result);
    }

    [Fact]
    public void RecordActionStart_WithoutBeginStep_DoesNothing()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-guard", "Test guard clause", "test-model", 500);

        // No BeginStep called -- RecordActionStart should be a no-op
        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 10 });
        logger.Save();
        var chain = ReadChain();

        Assert.Empty(chain.Steps);
    }

    // ------------------------------------------------------------------
    //  RecordActionComplete completes the action
    // ------------------------------------------------------------------

    [Fact]
    public void RecordActionComplete_CompletesTheAction()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-004", "Type hello", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("type", new Dictionary<string, object?> { ["text"] = "hello" });
        logger.RecordActionComplete("Typed text", success: true);
        logger.Save();
        var chain = ReadChain();

        var action = chain.Steps[0].Actions[0];
        Assert.True(action.Success);
        Assert.Equal("Typed text", action.Result);
        Assert.NotNull(action.CompletedAt);
        Assert.NotNull(action.DurationMs);
        Assert.Null(action.ErrorMessage);
    }

    [Fact]
    public void RecordActionComplete_WithError_RecordsErrorMessage()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-005", "Click missing element", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0 });
        logger.RecordActionComplete("", success: false, errorMessage: "Element not found at coordinates");
        logger.Save();
        var chain = ReadChain();

        var action = chain.Steps[0].Actions[0];
        Assert.False(action.Success);
        Assert.Equal("Element not found at coordinates", action.ErrorMessage);
    }

    [Fact]
    public void RecordActionComplete_TruncatesLongResult()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-trunc", "Long result test", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("scrape", new Dictionary<string, object?>());

        // Create a string longer than 2000 characters
        var longResult = new string('A', 2500);
        logger.RecordActionComplete(longResult, success: true);
        logger.Save();
        var chain = ReadChain();

        var action = chain.Steps[0].Actions[0];
        Assert.NotNull(action.Result);
        Assert.True(action.Result.Length < 2500, "Result should be truncated");
        Assert.EndsWith("... (truncated)", action.Result);
    }

    [Fact]
    public void RecordActionComplete_WithoutRecordActionStart_DoesNothing()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-guard2", "Test guard clause", "test-model", 500);

        logger.BeginStep();
        // No RecordActionStart called -- RecordActionComplete should be a no-op
        logger.RecordActionComplete("result", success: true);
        logger.Save();
        var chain = ReadChain();

        Assert.Single(chain.Steps);
        Assert.Empty(chain.Steps[0].Actions);
    }

    // ------------------------------------------------------------------
    //  RecordScreenshot captures screenshot metadata
    // ------------------------------------------------------------------

    [Fact]
    public void RecordScreenshot_CapturesMetadata()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-006", "Screenshot test", "test-model", 750);

        logger.BeginStep();
        logger.RecordScreenshot(@"C:\sessions\step_001.png", 245_760);
        logger.Save();
        var chain = ReadChain();

        var screenshot = chain.Steps[0].Screenshot;
        Assert.NotNull(screenshot);
        Assert.Equal(@"C:\sessions\step_001.png", screenshot.Path);
        Assert.Equal(245_760L, screenshot.SizeBytes);
        Assert.NotEqual(default, screenshot.CapturedAt);
        Assert.Equal(750, chain.Steps[0].SettleDelayMs);
    }

    // ------------------------------------------------------------------
    //  RecordObservation fills in the observation on previous step
    // ------------------------------------------------------------------

    [Fact]
    public void RecordObservation_FillsObservationOnPreviousStep()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-007", "Observation test", "test-model", 500);

        // Step 1: action + screenshot but no observation yet
        logger.BeginStep();
        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 50 });
        logger.RecordActionComplete("Clicked", success: true);
        logger.RecordScreenshot(@"C:\sessions\step_001.png", 100_000);

        // Step 2 begins, then we record the observation for step 1
        logger.BeginStep();
        logger.RecordObservation("The button was clicked successfully.");
        logger.Save();
        var chain = ReadChain();

        Assert.Equal(2, chain.Steps.Count);
        Assert.Equal("The button was clicked successfully.", chain.Steps[0].Observation);
        Assert.NotNull(chain.Steps[0].ObservedAt);
        Assert.NotNull(chain.Steps[0].StepCompletedAt);
    }

    // ------------------------------------------------------------------
    //  CompleteSuccess sets result
    // ------------------------------------------------------------------

    [Fact]
    public void CompleteSuccess_SetsOutcomeAndFinalResponse()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-008", "Success test", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("type", new Dictionary<string, object?> { ["text"] = "done" });
        logger.RecordActionComplete("Typed text", success: true);
        logger.CompleteSuccess("Task completed successfully.");

        var chain = ReadChain();

        Assert.Equal("success", chain.Outcome);
        Assert.Equal("Task completed successfully.", chain.FinalResponse);
        Assert.NotNull(chain.CompletedAt);
        Assert.NotNull(chain.TotalDurationMs);
    }

    // ------------------------------------------------------------------
    //  CompleteError sets error
    // ------------------------------------------------------------------

    [Fact]
    public void CompleteError_SetsOutcomeAndErrorMessage()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-009", "Error test", "test-model", 500);

        logger.CompleteError("Something went wrong.");

        var chain = ReadChain();

        Assert.Equal("error", chain.Outcome);
        Assert.Equal("Something went wrong.", chain.FinalResponse);
        Assert.NotNull(chain.CompletedAt);
        Assert.NotNull(chain.TotalDurationMs);
    }

    // ------------------------------------------------------------------
    //  CompleteMaxIterations sets outcome
    // ------------------------------------------------------------------

    [Fact]
    public void CompleteMaxIterations_SetsOutcome()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-010", "Max iterations test", "test-model", 500);

        logger.BeginStep();
        logger.CompleteMaxIterations("Reached maximum iterations.");

        var chain = ReadChain();

        Assert.Equal("max_iterations", chain.Outcome);
        Assert.Equal("Reached maximum iterations.", chain.FinalResponse);
    }

    // ------------------------------------------------------------------
    //  CompleteCancelled sets outcome
    // ------------------------------------------------------------------

    [Fact]
    public void CompleteCancelled_SetsOutcome()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-011", "Cancel test", "test-model", 500);

        logger.CompleteCancelled();

        var chain = ReadChain();

        Assert.Equal("cancelled", chain.Outcome);
        Assert.Null(chain.FinalResponse);
    }

    // ------------------------------------------------------------------
    //  Sequential steps increment step number
    // ------------------------------------------------------------------

    [Fact]
    public void SequentialSteps_IncrementStepNumber()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-012", "Multi-step test", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 10 });
        logger.RecordActionComplete("Clicked", success: true);

        logger.BeginStep();
        logger.RecordActionStart("type", new Dictionary<string, object?> { ["text"] = "hello" });
        logger.RecordActionComplete("Typed", success: true);

        logger.BeginStep();
        logger.RecordActionStart("screenshot", new Dictionary<string, object?>());
        logger.RecordActionComplete("Captured", success: true);

        logger.CompleteSuccess("All steps done.");

        var chain = ReadChain();

        Assert.Equal(3, chain.Steps.Count);
        Assert.Equal(1, chain.Steps[0].StepNumber);
        Assert.Equal(2, chain.Steps[1].StepNumber);
        Assert.Equal(3, chain.Steps[2].StepNumber);
    }

    // ------------------------------------------------------------------
    //  Finalize is idempotent
    // ------------------------------------------------------------------

    [Fact]
    public void Finalize_IsIdempotent_SecondCallIgnored()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-013", "Idempotent test", "test-model", 500);

        logger.CompleteSuccess("First completion.");
        var chain1 = ReadChain();

        // Second call should be ignored
        logger.CompleteError("This should not overwrite.");
        var chain2 = ReadChain();

        Assert.Equal("success", chain1.Outcome);
        Assert.Equal("First completion.", chain1.FinalResponse);
        // The chain should still show the first outcome since Finalize guards against double-call
        Assert.Equal("success", chain2.Outcome);
        Assert.Equal("First completion.", chain2.FinalResponse);
    }

    // ------------------------------------------------------------------
    //  Dispose finalizes incomplete chain
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_FinalizesIncompleteChain()
    {
        var logger = new EvidenceChainLogger(
            _tempFolder, "sess-014", "Dispose test", "test-model", 500);

        logger.BeginStep();
        logger.Dispose();

        var chain = ReadChain();

        Assert.Equal("incomplete", chain.Outcome);
        Assert.Null(chain.FinalResponse);
        Assert.NotNull(chain.CompletedAt);
    }

    [Fact]
    public void Dispose_DoesNotOverwrite_AlreadyFinalizedChain()
    {
        var logger = new EvidenceChainLogger(
            _tempFolder, "sess-015", "Dispose after finalize test", "test-model", 500);

        logger.CompleteSuccess("Already done.");
        logger.Dispose();

        var chain = ReadChain();

        Assert.Equal("success", chain.Outcome);
        Assert.Equal("Already done.", chain.FinalResponse);
    }

    // ------------------------------------------------------------------
    //  Open step is closed on finalize
    // ------------------------------------------------------------------

    [Fact]
    public void Finalize_ClosesOpenStep()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-016", "Close open step test", "test-model", 500);

        logger.BeginStep();
        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 5 });
        logger.RecordActionComplete("Clicked", success: true);
        // Step not explicitly completed -- finalize should close it
        logger.CompleteSuccess("Done with open step.");

        var chain = ReadChain();

        Assert.Single(chain.Steps);
        Assert.NotNull(chain.Steps[0].StepCompletedAt);
        Assert.NotNull(chain.Steps[0].StepDurationMs);
    }

    // ------------------------------------------------------------------
    //  Multiple actions in a single step
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleActions_InSingleStep()
    {
        using var logger = new EvidenceChainLogger(
            _tempFolder, "sess-017", "Multi-action test", "test-model", 500);

        logger.BeginStep();

        logger.RecordActionStart("click", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 20 });
        logger.RecordActionComplete("Clicked", success: true);

        logger.RecordActionStart("type", new Dictionary<string, object?> { ["text"] = "hello" });
        logger.RecordActionComplete("Typed", success: true);

        logger.RecordActionStart("key", new Dictionary<string, object?> { ["key"] = "Enter" });
        logger.RecordActionComplete("Key pressed", success: true);

        logger.CompleteSuccess("All actions done.");

        var chain = ReadChain();

        Assert.Single(chain.Steps);
        Assert.Equal(3, chain.Steps[0].Actions.Count);
        Assert.Equal("click", chain.Steps[0].Actions[0].ToolName);
        Assert.Equal("type", chain.Steps[0].Actions[1].ToolName);
        Assert.Equal("key", chain.Steps[0].Actions[2].ToolName);
    }
}
