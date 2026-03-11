using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeConfigDiscoveryTests
{
    [Fact]
    public void Discover_NullRepoPath_ReturnsTreeWithGlobalItems()
    {
        var discovery = new ClaudeConfigDiscovery();

        var tree = discovery.Discover(null);

        Assert.NotNull(tree);
        // Global CLAUDE.md should exist (we know it does on this machine)
        // But even if it doesn't, the tree should be valid
        Assert.NotNull(tree.ClaudeMdFiles);
        Assert.NotNull(tree.GlobalSkills);
        Assert.NotNull(tree.ProjectSkills);
        Assert.NotNull(tree.McpServers);
        Assert.NotNull(tree.SettingsFiles);
    }

    [Fact]
    public void Discover_WithRepoPath_ReturnsTreeWithProjectItems()
    {
        var discovery = new ClaudeConfigDiscovery();
        // Use the cc-director repo path itself
        var repoPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));

        var tree = discovery.Discover(repoPath);

        Assert.NotNull(tree);
        // This repo has a CLAUDE.md at root and .claude/skills/
        Assert.True(tree.ClaudeMdFiles.Count > 0, "Should find at least one CLAUDE.md");
        Assert.True(tree.ProjectSkills.Count > 0, "Should find project skills");
    }

    [Fact]
    public void Discover_NonExistentRepoPath_ReturnsEmptyProjectItems()
    {
        var discovery = new ClaudeConfigDiscovery();

        var tree = discovery.Discover(@"C:\nonexistent\repo\path");

        Assert.NotNull(tree);
        Assert.Empty(tree.ProjectSkills);
    }

    [Fact]
    public void ConfigFileEntry_Properties_SetCorrectly()
    {
        var entry = new ConfigFileEntry("Test", @"C:\test\file.md", "A test file");

        Assert.Equal("Test", entry.Label);
        Assert.Equal(@"C:\test\file.md", entry.FilePath);
        Assert.Equal("A test file", entry.Description);
    }

    [Fact]
    public void SkillEntry_Properties_SetCorrectly()
    {
        var entry = new SkillEntry("commit", @"C:\skills\commit\skill.md", "Create commits", "global");

        Assert.Equal("commit", entry.Name);
        Assert.Equal("global", entry.Scope);
        Assert.Equal("Create commits", entry.Description);
    }

    [Fact]
    public void McpServerEntry_Properties_SetCorrectly()
    {
        var entry = new McpServerEntry("test-server", "node", new List<string> { "server.js" }, "global", @"C:\config.json");

        Assert.Equal("test-server", entry.Name);
        Assert.Equal("node", entry.Command);
        Assert.Single(entry.Args);
        Assert.Equal("server.js", entry.Args[0]);
        Assert.Equal("global", entry.Scope);
    }

    [Fact]
    public void RemoveMcpServer_NonExistentFile_ReturnsFalse()
    {
        var discovery = new ClaudeConfigDiscovery();

        var result = discovery.RemoveMcpServer(@"C:\nonexistent\mcp.json", "test");

        Assert.False(result);
    }

    [Fact]
    public void RemoveMcpServer_ValidFile_RemovesServer()
    {
        var discovery = new ClaudeConfigDiscovery();
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            {
                "mcpServers": {
                    "keep-this": { "command": "node", "args": ["a.js"] },
                    "remove-this": { "command": "python", "args": ["b.py"] }
                }
            }
            """);

            var result = discovery.RemoveMcpServer(tempFile, "remove-this");

            Assert.True(result);
            var content = File.ReadAllText(tempFile);
            Assert.Contains("keep-this", content);
            Assert.DoesNotContain("remove-this", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
