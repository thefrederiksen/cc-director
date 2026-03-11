using CcDirector.Core.Browser;
using Xunit;

namespace CcDirector.Core.Tests;

public class WorkflowStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkflowStore _store;

    public WorkflowStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WorkflowStoreTests_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempDir);
        _store = new WorkflowStore();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------
    // Template tests
    // -------------------------------------------------------------------

    [Fact]
    public void SaveTemplate_ValidTemplate_CreatesJsonFile()
    {
        var template = MakeTemplate("login-flow", "test-conn");

        _store.SaveTemplate(template);

        var dir = Path.Combine(_tempDir, "connections", "test-conn", "workflows");
        var file = Path.Combine(dir, "login-flow.json");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void LoadTemplate_ExistingTemplate_ReturnsCorrectData()
    {
        var template = MakeTemplate("login-flow", "test-conn");
        template.Parameters.Add(new WorkflowParameter { Name = "url", Description = "Target URL", DefaultValue = "https://example.com" });
        _store.SaveTemplate(template);

        var loaded = _store.LoadTemplate("test-conn", "login-flow");

        Assert.NotNull(loaded);
        Assert.Equal("login-flow", loaded.Name);
        Assert.Equal("test-conn", loaded.Connection);
        Assert.Equal(2, loaded.Actions.Count);
        Assert.Single(loaded.Parameters);
        Assert.Equal("url", loaded.Parameters[0].Name);
    }

    [Fact]
    public void LoadTemplate_NonExistent_ReturnsNull()
    {
        var result = _store.LoadTemplate("no-conn", "no-workflow");

        Assert.Null(result);
    }

    [Fact]
    public void ListTemplates_MultipleTemplates_ReturnsAll()
    {
        _store.SaveTemplate(MakeTemplate("flow-a", "conn"));
        _store.SaveTemplate(MakeTemplate("flow-b", "conn"));
        _store.SaveTemplate(MakeTemplate("flow-c", "conn"));

        var templates = _store.ListTemplates("conn");

        Assert.Equal(3, templates.Count);
    }

    [Fact]
    public void ListTemplates_EmptyConnection_ReturnsEmpty()
    {
        var templates = _store.ListTemplates("empty-conn");

        Assert.Empty(templates);
    }

    [Fact]
    public void DeleteTemplate_ExistingTemplate_RemovesFileAndDir()
    {
        _store.SaveTemplate(MakeTemplate("doomed", "conn"));
        // Create some data in the workflow dir
        var recDir = _store.RecordingDir("conn", "doomed");
        File.WriteAllText(Path.Combine(recDir, "step-001.jpg"), "fake");

        _store.DeleteTemplate("conn", "doomed");

        var dir = Path.Combine(_tempDir, "connections", "conn", "workflows");
        Assert.False(File.Exists(Path.Combine(dir, "doomed.json")));
        Assert.False(Directory.Exists(Path.Combine(dir, "doomed")));
    }

    // -------------------------------------------------------------------
    // Run tests
    // -------------------------------------------------------------------

    [Fact]
    public void SaveRun_ValidRun_CreatesJsonFile()
    {
        var run = MakeRun("20260311T143000", "login-flow", "conn");

        _store.SaveRun(run);

        var file = Path.Combine(_tempDir, "connections", "conn", "workflows", "login-flow", "runs", "20260311T143000.json");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void LoadRun_ExistingRun_ReturnsCorrectData()
    {
        var run = MakeRun("20260311T143000", "login-flow", "conn");
        run.Steps.Add(new WorkflowRunStep
        {
            Index = 0,
            Command = "navigate",
            Status = "completed",
            DurationMs = 1200,
            ScreenshotFile = "step-001.jpg",
        });
        _store.SaveRun(run);

        var loaded = _store.LoadRun("conn", "login-flow", "20260311T143000");

        Assert.NotNull(loaded);
        Assert.Equal("20260311T143000", loaded.Id);
        Assert.Single(loaded.Steps);
        Assert.Equal("navigate", loaded.Steps[0].Command);
        Assert.Equal(1200, loaded.Steps[0].DurationMs);
    }

    [Fact]
    public void LoadRun_NonExistent_ReturnsNull()
    {
        var result = _store.LoadRun("conn", "flow", "no-such-id");

        Assert.Null(result);
    }

    [Fact]
    public void ListRuns_MultipleRuns_ReturnsMostRecentFirst()
    {
        _store.SaveRun(MakeRun("20260311T100000", "flow", "conn"));
        _store.SaveRun(MakeRun("20260311T120000", "flow", "conn"));
        _store.SaveRun(MakeRun("20260311T110000", "flow", "conn"));

        var runs = _store.ListRuns("conn", "flow");

        Assert.Equal(3, runs.Count);
        Assert.Equal("20260311T120000", runs[0].Id);
        Assert.Equal("20260311T110000", runs[1].Id);
        Assert.Equal("20260311T100000", runs[2].Id);
    }

    [Fact]
    public void DeleteRun_ExistingRun_RemovesJsonAndScreenshots()
    {
        var run = MakeRun("20260311T143000", "flow", "conn");
        _store.SaveRun(run);
        var ssDir = _store.RunScreenshotDir("conn", "flow", "20260311T143000");
        File.WriteAllText(Path.Combine(ssDir, "step-001.jpg"), "fake");

        _store.DeleteRun("conn", "flow", "20260311T143000");

        var runsDir = Path.Combine(_tempDir, "connections", "conn", "workflows", "flow", "runs");
        Assert.False(File.Exists(Path.Combine(runsDir, "20260311T143000.json")));
        Assert.False(Directory.Exists(Path.Combine(runsDir, "20260311T143000")));
    }

    [Fact]
    public void RecordingDir_CreatesDirectory()
    {
        var dir = _store.RecordingDir("conn", "flow");

        Assert.True(Directory.Exists(dir));
        Assert.EndsWith(Path.Combine("flow", "recording"), dir);
    }

    [Fact]
    public void RunScreenshotDir_CreatesDirectory()
    {
        var dir = _store.RunScreenshotDir("conn", "flow", "run-001");

        Assert.True(Directory.Exists(dir));
        Assert.EndsWith(Path.Combine("run-001"), dir);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static WorkflowTemplate MakeTemplate(string name, string connection)
    {
        return new WorkflowTemplate
        {
            Name = name,
            Connection = connection,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Actions = new List<WorkflowAction>
            {
                new() { Command = "navigate", Params = new Dictionary<string, object> { ["url"] = "https://example.com" } },
                new() { Command = "click", Params = new Dictionary<string, object> { ["text"] = "Login" } },
            },
        };
    }

    private static WorkflowRun MakeRun(string id, string workflowName, string connection)
    {
        return new WorkflowRun
        {
            Id = id,
            WorkflowName = workflowName,
            Connection = connection,
            StartedAt = DateTime.UtcNow.ToString("o"),
            Status = "completed",
        };
    }
}
