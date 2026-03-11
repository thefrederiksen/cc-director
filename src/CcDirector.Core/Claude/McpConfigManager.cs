using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Editable MCP server configuration entry for the management UI.
/// </summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>Transport type: "stdio" or "sse".</summary>
    public string TransportType { get; set; } = "stdio";

    /// <summary>For SSE transport: the URL endpoint.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// Reads and writes Claude MCP configuration files.
/// Supports both global (~/.claude/mcp-settings.json) and project-level (.mcp.json).
/// </summary>
public class McpConfigManager
{
    private static readonly string GlobalConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "mcp-settings.json");

    private static readonly string ClaudeDesktopConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", "claude_desktop_config.json");

    /// <summary>The path to the global MCP config file.</summary>
    public static string GlobalPath => GlobalConfigPath;

    /// <summary>The path to the Claude Desktop config file.</summary>
    public static string DesktopPath => ClaudeDesktopConfigPath;

    /// <summary>Load MCP servers from a config file.</summary>
    public List<McpServerConfig> LoadServers(string configPath)
    {
        FileLog.Write($"[McpConfigManager] LoadServers: path={configPath}");

        var servers = new List<McpServerConfig>();

        if (!File.Exists(configPath))
        {
            FileLog.Write($"[McpConfigManager] LoadServers: file not found, returning empty list");
            return servers;
        }

        var text = File.ReadAllText(configPath);
        var root = JsonNode.Parse(text);
        if (root is not JsonObject rootObj)
        {
            FileLog.Write($"[McpConfigManager] LoadServers: root is not a JSON object");
            return servers;
        }

        var mcpServers = rootObj["mcpServers"] as JsonObject;
        if (mcpServers == null)
        {
            FileLog.Write($"[McpConfigManager] LoadServers: no mcpServers key found");
            return servers;
        }

        foreach (var prop in mcpServers)
        {
            if (prop.Value is not JsonObject serverObj)
                continue;

            var entry = new McpServerConfig { Name = prop.Key };

            var command = serverObj["command"]?.GetValue<string>();
            if (command != null)
                entry.Command = command;

            var url = serverObj["url"]?.GetValue<string>();
            if (url != null)
            {
                entry.Url = url;
                entry.TransportType = "sse";
            }

            if (serverObj["args"] is JsonArray argsArray)
            {
                foreach (var arg in argsArray)
                {
                    var val = arg?.GetValue<string>();
                    if (val != null)
                        entry.Args.Add(val);
                }
            }

            if (serverObj["env"] is JsonObject envObj)
            {
                foreach (var envProp in envObj)
                {
                    var val = envProp.Value?.GetValue<string>();
                    if (val != null)
                        entry.Env[envProp.Key] = val;
                }
            }

            servers.Add(entry);
        }

        FileLog.Write($"[McpConfigManager] LoadServers: loaded {servers.Count} servers");
        return servers;
    }

    /// <summary>Load global MCP servers from ~/.claude/mcp-settings.json.</summary>
    public List<McpServerConfig> LoadGlobalServers() => LoadServers(GlobalConfigPath);

    /// <summary>Load project-level MCP servers from .mcp.json in a directory.</summary>
    public List<McpServerConfig> LoadProjectServers(string projectDir)
        => LoadServers(Path.Combine(projectDir, ".mcp.json"));

    /// <summary>Save servers back to a config file, preserving other top-level keys.</summary>
    public void SaveServers(string configPath, List<McpServerConfig> servers)
    {
        FileLog.Write($"[McpConfigManager] SaveServers: path={configPath}, count={servers.Count}");

        // Read existing file to preserve other top-level keys
        JsonObject rootObj;
        if (File.Exists(configPath))
        {
            var text = File.ReadAllText(configPath);
            var parsed = JsonNode.Parse(text);
            rootObj = parsed as JsonObject ?? new JsonObject();
        }
        else
        {
            rootObj = new JsonObject();
        }

        // Remove old mcpServers and rebuild
        rootObj.Remove("mcpServers");

        var mcpServers = new JsonObject();
        foreach (var server in servers)
        {
            var serverObj = new JsonObject();

            if (server.TransportType == "sse" && server.Url != null)
            {
                serverObj["url"] = server.Url;
            }
            else
            {
                serverObj["command"] = server.Command;
            }

            if (server.Args.Count > 0)
            {
                var argsArray = new JsonArray();
                foreach (var arg in server.Args)
                    argsArray.Add(JsonValue.Create(arg));
                serverObj["args"] = argsArray;
            }

            if (server.Env.Count > 0)
            {
                var envObj = new JsonObject();
                foreach (var kvp in server.Env)
                    envObj[kvp.Key] = kvp.Value;
                serverObj["env"] = envObj;
            }

            mcpServers[server.Name] = serverObj;
        }

        rootObj["mcpServers"] = mcpServers;

        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = rootObj.ToJsonString(options);
        File.WriteAllText(configPath, json);

        FileLog.Write($"[McpConfigManager] SaveServers: written {json.Length} bytes");
    }

    /// <summary>Save servers to the global config.</summary>
    public void SaveGlobalServers(List<McpServerConfig> servers) => SaveServers(GlobalConfigPath, servers);

    /// <summary>Save servers to a project config.</summary>
    public void SaveProjectServers(string projectDir, List<McpServerConfig> servers)
        => SaveServers(Path.Combine(projectDir, ".mcp.json"), servers);

    /// <summary>Import servers from Claude Desktop config.</summary>
    public List<McpServerConfig> ImportFromClaudeDesktop()
    {
        FileLog.Write("[McpConfigManager] ImportFromClaudeDesktop");
        return LoadServers(ClaudeDesktopConfigPath);
    }

    /// <summary>Add a server to the config.</summary>
    public void AddServer(string configPath, McpServerConfig server)
    {
        FileLog.Write($"[McpConfigManager] AddServer: path={configPath}, name={server.Name}");

        var servers = LoadServers(configPath);
        servers.Add(server);
        SaveServers(configPath, servers);
    }

    /// <summary>Remove a server by name from the config.</summary>
    public void RemoveServer(string configPath, string serverName)
    {
        FileLog.Write($"[McpConfigManager] RemoveServer: path={configPath}, name={serverName}");

        var servers = LoadServers(configPath);
        var removed = servers.RemoveAll(s => s.Name == serverName);
        if (removed == 0)
            FileLog.Write($"[McpConfigManager] RemoveServer: server '{serverName}' not found");

        SaveServers(configPath, servers);
    }

    /// <summary>Update a server in the config (match by old name, replace with new entry).</summary>
    public void UpdateServer(string configPath, string oldName, McpServerConfig server)
    {
        FileLog.Write($"[McpConfigManager] UpdateServer: path={configPath}, oldName={oldName}, newName={server.Name}");

        var servers = LoadServers(configPath);
        var index = servers.FindIndex(s => s.Name == oldName);
        if (index < 0)
        {
            FileLog.Write($"[McpConfigManager] UpdateServer: server '{oldName}' not found, adding as new");
            servers.Add(server);
        }
        else
        {
            servers[index] = server;
        }

        SaveServers(configPath, servers);
    }
}
