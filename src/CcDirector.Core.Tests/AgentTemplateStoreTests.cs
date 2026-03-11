using System.Text.Json;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class AgentTemplateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public AgentTemplateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AgentTemplateStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "agent-templates.json");
    }

    [Fact]
    public void Load_NoFile_SeedsDefaults()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        Assert.True(store.Templates.Count > 0);
        Assert.True(File.Exists(_filePath));
    }

    [Fact]
    public void Load_ExistingFile_ReadsTemplates()
    {
        var templates = new List<AgentTemplate>
        {
            new() { Name = "MyAgent", Description = "Test agent", Model = "sonnet" },
        };
        var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);

        var store = new AgentTemplateStore(_filePath);
        store.Load();

        Assert.Single(store.Templates);
        Assert.Equal("MyAgent", store.Templates[0].Name);
        Assert.Equal("sonnet", store.Templates[0].Model);
    }

    [Fact]
    public void Save_WritesValidJson()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        // Should have seeded defaults and saved
        var json = File.ReadAllText(_filePath);
        var parsed = JsonSerializer.Deserialize<List<AgentTemplate>>(json);
        Assert.NotNull(parsed);
        Assert.True(parsed.Count > 0);
    }

    [Fact]
    public void Add_IncreasesCount()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var before = store.Templates.Count;
        store.Add(new AgentTemplate { Name = "New Agent" });

        Assert.Equal(before + 1, store.Templates.Count);
    }

    [Fact]
    public void Remove_DecreasesCount()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var template = new AgentTemplate { Name = "ToRemove" };
        store.Add(template);
        var countAfterAdd = store.Templates.Count;

        store.Remove(template.Id);

        Assert.Equal(countAfterAdd - 1, store.Templates.Count);
    }

    [Fact]
    public void GetById_ExistingId_ReturnsTemplate()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var template = new AgentTemplate { Name = "FindMe" };
        store.Add(template);

        var found = store.GetById(template.Id);

        Assert.NotNull(found);
        Assert.Equal("FindMe", found.Name);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var found = store.GetById("nonexistent-id");

        Assert.Null(found);
    }

    [Fact]
    public void Duplicate_CreatesNewIdAndCopyName()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var original = new AgentTemplate
        {
            Name = "Original",
            Description = "Original description",
            Model = "opus",
        };
        store.Add(original);

        var clone = store.Duplicate(original.Id);

        Assert.NotEqual(original.Id, clone.Id);
        Assert.Equal("Copy of Original", clone.Name);
        Assert.Equal("Original description", clone.Description);
        Assert.Equal("opus", clone.Model);
    }

    [Fact]
    public void Update_ModifiesExisting()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var template = new AgentTemplate { Name = "Before" };
        store.Add(template);

        template.Name = "After";
        store.Update(template);

        var found = store.GetById(template.Id);
        Assert.NotNull(found);
        Assert.Equal("After", found.Name);
    }

    [Fact]
    public void ExportToJson_ReturnsValidJson()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var template = new AgentTemplate { Name = "Exportable", Model = "sonnet" };
        store.Add(template);

        var json = store.ExportToJson(template.Id);

        var deserialized = JsonSerializer.Deserialize<AgentTemplate>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("Exportable", deserialized.Name);
        Assert.Equal("sonnet", deserialized.Model);
    }

    [Fact]
    public void ImportFromJson_AddsTemplate()
    {
        var store = new AgentTemplateStore(_filePath);
        store.Load();

        var countBefore = store.Templates.Count;

        var json = JsonSerializer.Serialize(new AgentTemplate
        {
            Name = "Imported",
            Model = "haiku",
        });

        var imported = store.ImportFromJson(json);

        Assert.NotNull(imported);
        Assert.Equal("Imported", imported.Name);
        Assert.Equal(countBefore + 1, store.Templates.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
