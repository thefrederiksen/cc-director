using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Persists agent templates as a JSON file.
/// File: config/director/agent-templates.json
/// </summary>
public class AgentTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private List<AgentTemplate> _templates = new();

    public IReadOnlyList<AgentTemplate> Templates => _templates;

    public AgentTemplateStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(CcStorage.ToolConfig("director"), "agent-templates.json");
    }

    public void Load()
    {
        FileLog.Write($"[AgentTemplateStore] Load: path={_filePath}");

        var dir = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_filePath}");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(_filePath))
        {
            FileLog.Write("[AgentTemplateStore] Load: file not found, seeding defaults");
            SeedDefaults();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<AgentTemplate>>(json, JsonOptions);
            if (loaded != null)
            {
                _templates.Clear();
                _templates.AddRange(loaded);
            }
            FileLog.Write($"[AgentTemplateStore] Load: loaded {_templates.Count} templates");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AgentTemplateStore] Load FAILED: {ex.Message}");
            throw;
        }
    }

    public void Save()
    {
        FileLog.Write($"[AgentTemplateStore] Save: writing {_templates.Count} templates");

        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_templates, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    public void Add(AgentTemplate template)
    {
        FileLog.Write($"[AgentTemplateStore] Add: name={template.Name}, id={template.Id}");
        template.CreatedAt = DateTime.UtcNow;
        template.ModifiedAt = DateTime.UtcNow;
        _templates.Add(template);
        Save();
    }

    public void Update(AgentTemplate template)
    {
        FileLog.Write($"[AgentTemplateStore] Update: id={template.Id}, name={template.Name}");
        var index = _templates.FindIndex(t => t.Id == template.Id);
        if (index < 0)
            throw new InvalidOperationException($"Template not found: {template.Id}");

        template.ModifiedAt = DateTime.UtcNow;
        _templates[index] = template;
        Save();
    }

    public void Remove(string id)
    {
        FileLog.Write($"[AgentTemplateStore] Remove: id={id}");
        var index = _templates.FindIndex(t => t.Id == id);
        if (index >= 0)
        {
            _templates.RemoveAt(index);
            Save();
        }
    }

    public AgentTemplate? GetById(string id)
    {
        FileLog.Write($"[AgentTemplateStore] GetById: id={id}");
        return _templates.Find(t => t.Id == id);
    }

    public AgentTemplate Duplicate(string id)
    {
        FileLog.Write($"[AgentTemplateStore] Duplicate: id={id}");
        var source = _templates.Find(t => t.Id == id)
            ?? throw new InvalidOperationException($"Template not found: {id}");

        var clone = new AgentTemplate
        {
            Name = $"Copy of {source.Name}",
            Description = source.Description,
            Model = source.Model,
            FallbackModel = source.FallbackModel,
            MaxTurns = source.MaxTurns,
            MaxBudgetUsd = source.MaxBudgetUsd,
            SystemPrompt = source.SystemPrompt,
            AppendSystemPrompt = source.AppendSystemPrompt,
            PermissionMode = source.PermissionMode,
            SkipPermissions = source.SkipPermissions,
            Tools = source.Tools,
            AllowedTools = source.AllowedTools,
            DisallowedTools = source.DisallowedTools,
            McpConfigPath = source.McpConfigPath,
        };

        Add(clone);
        FileLog.Write($"[AgentTemplateStore] Duplicate: created id={clone.Id}, name={clone.Name}");
        return clone;
    }

    public string ExportToJson(string id)
    {
        FileLog.Write($"[AgentTemplateStore] ExportToJson: id={id}");
        var template = _templates.Find(t => t.Id == id)
            ?? throw new InvalidOperationException($"Template not found: {id}");

        return JsonSerializer.Serialize(template, JsonOptions);
    }

    public AgentTemplate? ImportFromJson(string json)
    {
        FileLog.Write("[AgentTemplateStore] ImportFromJson");

        var template = JsonSerializer.Deserialize<AgentTemplate>(json, JsonOptions);
        if (template == null)
        {
            FileLog.Write("[AgentTemplateStore] ImportFromJson: deserialization returned null");
            return null;
        }

        // Assign a new Id to avoid collisions
        template.Id = Guid.NewGuid().ToString("N");
        Add(template);
        FileLog.Write($"[AgentTemplateStore] ImportFromJson: imported as id={template.Id}, name={template.Name}");
        return template;
    }

    private void SeedDefaults()
    {
        FileLog.Write("[AgentTemplateStore] SeedDefaults: creating 4 starter templates");

        _templates.Add(new AgentTemplate
        {
            Name = "Code Reviewer",
            Description = "Reviews code for bugs, security issues, and style violations.",
            Model = "sonnet",
            AppendSystemPrompt = "Review code for bugs, security issues, and style. Provide specific actionable feedback.",
            PermissionMode = "plan",
        });

        _templates.Add(new AgentTemplate
        {
            Name = "Doc Writer",
            Description = "Writes clear technical documentation, API docs, and README files.",
            Model = "sonnet",
            AppendSystemPrompt = "Write clear technical documentation. Focus on API docs, README files, and code comments.",
            AllowedTools = "Read,Write,Glob,Grep",
        });

        _templates.Add(new AgentTemplate
        {
            Name = "Test Writer",
            Description = "Writes comprehensive unit tests with edge case coverage.",
            Model = "sonnet",
            AppendSystemPrompt = "Write comprehensive unit tests. Use Arrange-Act-Assert pattern. Cover edge cases.",
            AllowedTools = "Read,Write,Edit,Glob,Grep,Bash",
        });

        _templates.Add(new AgentTemplate
        {
            Name = "Security Scanner",
            Description = "Scans code for security vulnerabilities and hardcoded secrets.",
            Model = "opus",
            AppendSystemPrompt = "Scan code for security vulnerabilities. Check OWASP Top 10, hardcoded secrets, injection risks.",
            PermissionMode = "plan",
        });
    }
}
