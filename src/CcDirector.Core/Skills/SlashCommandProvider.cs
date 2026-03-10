using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// Discovers slash command skills from global and project skill directories.
/// </summary>
public sealed class SlashCommandProvider
{
    private static readonly string GlobalSkillsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "skills");

    private readonly Dictionary<string, List<SlashCommandItem>> _cache = new();

    /// <summary>
    /// Returns all slash commands: built-in + custom skills for the given repo path.
    /// Results are cached per repo path.
    /// </summary>
    public List<SlashCommandItem> GetCommands(string? repoPath)
    {
        FileLog.Write($"[SlashCommandProvider] GetCommands: repoPath={repoPath ?? "(null)"}");

        var cacheKey = repoPath ?? "__global__";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var commands = new Dictionary<string, SlashCommandItem>(StringComparer.OrdinalIgnoreCase);

        // Built-in commands first
        foreach (var cmd in BuiltInSlashCommands.All)
            commands[cmd.Name] = cmd;

        // Global skills (can shadow built-in names)
        ScanDirectory(GlobalSkillsPath, "global", commands);

        // Project skills (can shadow global)
        if (!string.IsNullOrEmpty(repoPath))
        {
            var projectSkillsPath = Path.Combine(repoPath, ".claude", "skills");
            ScanDirectory(projectSkillsPath, "project", commands);
        }

        var result = commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _cache[cacheKey] = result;

        FileLog.Write($"[SlashCommandProvider] GetCommands: found {result.Count} commands ({BuiltInSlashCommands.All.Count} built-in)");
        return result;
    }

    /// <summary>
    /// Returns only custom skill commands (global + project), excluding built-in.
    /// </summary>
    public List<SlashCommandItem> GetCustomSkills(string? repoPath)
    {
        return GetCommands(repoPath).Where(c => !c.IsBuiltIn).ToList();
    }

    /// <summary>
    /// Returns only built-in commands.
    /// </summary>
    public List<SlashCommandItem> GetBuiltInCommands()
    {
        return BuiltInSlashCommands.All.ToList();
    }

    /// <summary>
    /// Clears the cache, forcing re-scan on next call.
    /// </summary>
    public void InvalidateCache()
    {
        FileLog.Write("[SlashCommandProvider] InvalidateCache");
        _cache.Clear();
    }

    /// <summary>
    /// Clears the cache for a specific repo path.
    /// </summary>
    public void InvalidateCache(string? repoPath)
    {
        var cacheKey = repoPath ?? "__global__";
        _cache.Remove(cacheKey);
    }

    private static void ScanDirectory(string skillsDir, string source, Dictionary<string, SlashCommandItem> commands)
    {
        if (!Directory.Exists(skillsDir))
            return;

        foreach (var dir in Directory.GetDirectories(skillsDir))
        {
            var skillMd = Path.Combine(dir, "skill.md");
            if (!File.Exists(skillMd))
                continue;

            var item = ParseSkillFile(skillMd, source);
            if (item != null)
            {
                // Project skills override global skills with the same name
                commands[item.Name] = item;
            }
        }
    }

    private static SlashCommandItem? ParseSkillFile(string skillMdPath, string source)
    {
        try
        {
            var allText = File.ReadAllText(skillMdPath);
            var lines = allText.Split('\n');

            if (lines.Length < 3 || lines[0].Trim() != "---")
                return null;

            string? name = null;
            string? description = null;
            int frontmatterEnd = -1;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "---")
                {
                    frontmatterEnd = i;
                    break;
                }

                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = ExtractYamlValue(line, "name:");
                }
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = ExtractYamlValue(line, "description:");
                }
            }

            if (string.IsNullOrEmpty(name))
                return null;

            // Extract body after frontmatter
            var documentation = string.Empty;
            if (frontmatterEnd >= 0 && frontmatterEnd + 1 < lines.Length)
            {
                documentation = string.Join("\n", lines[(frontmatterEnd + 1)..]).Trim();
            }

            return new SlashCommandItem(name, description ?? string.Empty, source, documentation);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SlashCommandProvider] ParseSkillFile FAILED for {skillMdPath}: {ex.Message}");
            return null;
        }
    }

    private static string ExtractYamlValue(string line, string prefix)
    {
        var value = line.Substring(prefix.Length).Trim();
        // Remove surrounding quotes if present
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        return value;
    }
}
