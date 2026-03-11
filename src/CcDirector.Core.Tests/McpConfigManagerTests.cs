using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class McpConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly McpConfigManager _manager;

    public McpConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"McpConfigManagerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "mcp-settings.json");
        _manager = new McpConfigManager();
    }

    [Fact]
    public void LoadServers_ValidJson_ReturnsEntries()
    {
        File.WriteAllText(_configPath, """
            {
                "mcpServers": {
                    "myServer": {
                        "command": "node",
                        "args": ["server.js", "--port", "3000"],
                        "env": {
                            "API_KEY": "test123"
                        }
                    },
                    "sseServer": {
                        "url": "http://localhost:8080/sse"
                    }
                }
            }
            """);

        var servers = _manager.LoadServers(_configPath);

        Assert.Equal(2, servers.Count);

        var stdio = servers.Find(s => s.Name == "myServer");
        Assert.NotNull(stdio);
        Assert.Equal("node", stdio.Command);
        Assert.Equal(3, stdio.Args.Count);
        Assert.Equal("server.js", stdio.Args[0]);
        Assert.Equal("test123", stdio.Env["API_KEY"]);

        var sse = servers.Find(s => s.Name == "sseServer");
        Assert.NotNull(sse);
        Assert.Equal("sse", sse.TransportType);
        Assert.Equal("http://localhost:8080/sse", sse.Url);
    }

    [Fact]
    public void LoadServers_EmptyFile_ReturnsEmpty()
    {
        File.WriteAllText(_configPath, "{}");

        var servers = _manager.LoadServers(_configPath);

        Assert.Empty(servers);
    }

    [Fact]
    public void LoadServers_NoFile_ReturnsEmpty()
    {
        var servers = _manager.LoadServers(Path.Combine(_tempDir, "nonexistent.json"));

        Assert.Empty(servers);
    }

    [Fact]
    public void SaveServers_WritesValidJson()
    {
        var servers = new List<McpServerConfig>
        {
            new()
            {
                Name = "testServer",
                Command = "python",
                Args = new List<string> { "server.py" },
                Env = new Dictionary<string, string> { { "PORT", "8080" } },
            },
        };

        _manager.SaveServers(_configPath, servers);

        Assert.True(File.Exists(_configPath));
        var json = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(json);
        Assert.NotNull(root);
        var mcpServers = root["mcpServers"] as JsonObject;
        Assert.NotNull(mcpServers);
        Assert.True(mcpServers.ContainsKey("testServer"));
    }

    [Fact]
    public void SaveServers_PreservesOtherKeys()
    {
        File.WriteAllText(_configPath, """
            {
                "otherSetting": "keep-me",
                "mcpServers": {
                    "old": { "command": "old-cmd" }
                }
            }
            """);

        var servers = new List<McpServerConfig>
        {
            new() { Name = "newServer", Command = "new-cmd" },
        };

        _manager.SaveServers(_configPath, servers);

        var json = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(json);
        Assert.NotNull(root);
        Assert.Equal("keep-me", root["otherSetting"]?.GetValue<string>());

        var mcpServers = root["mcpServers"] as JsonObject;
        Assert.NotNull(mcpServers);
        Assert.True(mcpServers.ContainsKey("newServer"));
        Assert.False(mcpServers.ContainsKey("old"));
    }

    [Fact]
    public void AddServer_AppendsToExisting()
    {
        File.WriteAllText(_configPath, """
            {
                "mcpServers": {
                    "existing": { "command": "existing-cmd" }
                }
            }
            """);

        _manager.AddServer(_configPath, new McpServerConfig
        {
            Name = "added",
            Command = "added-cmd",
        });

        var servers = _manager.LoadServers(_configPath);
        Assert.Equal(2, servers.Count);
        Assert.NotNull(servers.Find(s => s.Name == "existing"));
        Assert.NotNull(servers.Find(s => s.Name == "added"));
    }

    [Fact]
    public void RemoveServer_RemovesEntry()
    {
        File.WriteAllText(_configPath, """
            {
                "mcpServers": {
                    "keep": { "command": "keep-cmd" },
                    "remove": { "command": "remove-cmd" }
                }
            }
            """);

        _manager.RemoveServer(_configPath, "remove");

        var servers = _manager.LoadServers(_configPath);
        Assert.Single(servers);
        Assert.Equal("keep", servers[0].Name);
    }

    [Fact]
    public void UpdateServer_ReplacesEntry()
    {
        File.WriteAllText(_configPath, """
            {
                "mcpServers": {
                    "myServer": { "command": "old-cmd" }
                }
            }
            """);

        _manager.UpdateServer(_configPath, "myServer", new McpServerConfig
        {
            Name = "myServer",
            Command = "new-cmd",
            Args = new List<string> { "--verbose" },
        });

        var servers = _manager.LoadServers(_configPath);
        Assert.Single(servers);
        Assert.Equal("new-cmd", servers[0].Command);
        Assert.Single(servers[0].Args);
        Assert.Equal("--verbose", servers[0].Args[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
