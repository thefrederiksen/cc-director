namespace CcDirector.Core.Skills;

/// <summary>
/// Represents a discovered slash command skill.
/// </summary>
public sealed class SlashCommandItem
{
    public string Name { get; }
    public string Description { get; }
    public string Source { get; } // "builtin", "global", or "project"
    public string Documentation { get; } // Body content from skill.md (after frontmatter)
    public string Category { get; } // For built-in commands: "Session", "Config", "Navigation", etc.

    public SlashCommandItem(string name, string description, string source, string documentation, string category = "")
    {
        Name = name;
        Description = description;
        Source = source;
        Documentation = documentation;
        Category = category;
    }

    public bool IsBuiltIn => Source == "builtin";
}
