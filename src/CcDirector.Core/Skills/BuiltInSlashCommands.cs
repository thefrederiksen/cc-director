namespace CcDirector.Core.Skills;

/// <summary>
/// Static list of built-in Claude Code slash commands.
/// Source: https://code.claude.com/docs/en/interactive-mode#built-in-commands
/// Update this list when Claude Code ships new built-in commands.
/// </summary>
public static class BuiltInSlashCommands
{
    /// <summary>
    /// Claude Code version this list was last verified against.
    /// </summary>
    public const string CapturedFromVersion = "1.0.34";

    /// <summary>
    /// Official documentation URL for built-in commands.
    /// </summary>
    public const string DocsUrl = "https://code.claude.com/docs/en/interactive-mode#built-in-commands";

    public static IReadOnlyList<SlashCommandItem> All { get; } = new List<SlashCommandItem>
    {
        // Session management
        Cmd("/clear", "Clear conversation history and free up context", "Session", "Aliases: /reset, /new"),
        Cmd("/compact", "Compact conversation with optional focus instructions", "Session", "Usage: /compact [instructions]"),
        Cmd("/context", "Visualize current context usage as a colored grid", "Session"),
        Cmd("/cost", "Show token usage statistics", "Session"),
        Cmd("/exit", "Exit the CLI", "Session", "Alias: /quit"),
        Cmd("/export", "Export current conversation as plain text", "Session", "Usage: /export [filename]"),
        Cmd("/fork", "Create a fork of the current conversation at this point", "Session", "Usage: /fork [name]"),
        Cmd("/rename", "Rename current session", "Session", "Usage: /rename [name]. Without name, auto-generates from conversation history."),
        Cmd("/resume", "Resume a conversation by ID or name, or open session picker", "Session", "Alias: /continue"),
        Cmd("/rewind", "Rewind conversation and/or code to previous point", "Session", "Alias: /checkpoint"),
        Cmd("/tasks", "List and manage background tasks", "Session"),

        // Configuration
        Cmd("/config", "Open the Settings interface (Config tab)", "Config", "Alias: /settings"),
        Cmd("/fast", "Toggle fast mode on or off", "Config", "Usage: /fast [on|off]"),
        Cmd("/hooks", "Manage hook configurations for tool events", "Config"),
        Cmd("/keybindings", "Open or create keybindings configuration file", "Config"),
        Cmd("/memory", "Edit CLAUDE.md memory files and manage auto-memory", "Config"),
        Cmd("/model", "Select or change AI model", "Config", "Usage: /model [model]"),
        Cmd("/output-style", "Switch between output styles (Default, Explanatory, Learning)", "Config", "Usage: /output-style [style]"),
        Cmd("/permissions", "View or update permissions", "Config", "Alias: /allowed-tools"),
        Cmd("/privacy-settings", "View and update privacy settings (Pro and Max only)", "Config"),
        Cmd("/sandbox", "Toggle sandbox mode (supported platforms only)", "Config"),
        Cmd("/statusline", "Configure Claude Code's status line", "Config"),
        Cmd("/terminal-setup", "Configure terminal keybindings for Shift+Enter and other shortcuts", "Config"),
        Cmd("/theme", "Change color theme", "Config"),
        Cmd("/vim", "Toggle between Vim and Normal editing modes", "Config"),

        // Tools and integrations
        Cmd("/agents", "Manage agent/subagent configurations", "Tools"),
        Cmd("/chrome", "Configure Claude in Chrome settings", "Tools"),
        Cmd("/ide", "Manage IDE integrations and show status", "Tools"),
        Cmd("/mcp", "Manage MCP server connections and OAuth authentication", "Tools"),
        Cmd("/plugin", "Manage Claude Code plugins", "Tools"),
        Cmd("/reload-plugins", "Reload all active plugins to apply pending changes", "Tools"),
        Cmd("/skills", "List available skills", "Tools"),

        // Project and code
        Cmd("/add-dir", "Add a new working directory to the current session", "Project", "Usage: /add-dir <path>"),
        Cmd("/diff", "Open interactive diff viewer showing uncommitted changes", "Project"),
        Cmd("/init", "Initialize project with CLAUDE.md guide", "Project"),
        Cmd("/plan", "Enter plan mode directly from prompt", "Project"),
        Cmd("/pr-comments", "Fetch and display comments from GitHub pull request", "Project", "Usage: /pr-comments [PR]"),
        Cmd("/review", "Deprecated. Install the code-review plugin instead", "Project"),
        Cmd("/security-review", "Analyze pending changes on current branch for security vulnerabilities", "Project"),

        // Account and auth
        Cmd("/extra-usage", "Configure extra usage to keep working when rate limits are hit", "Account"),
        Cmd("/login", "Sign in to Anthropic account", "Account"),
        Cmd("/logout", "Sign out from Anthropic account", "Account"),
        Cmd("/passes", "Share free week of Claude Code with friends (if eligible)", "Account"),
        Cmd("/upgrade", "Open upgrade page to switch to higher plan tier", "Account"),
        Cmd("/usage", "Show plan usage limits and rate limit status", "Account"),

        // Info and help
        Cmd("/copy", "Copy the last assistant response to clipboard", "Info"),
        Cmd("/doctor", "Diagnose and verify Claude Code installation and settings", "Info"),
        Cmd("/feedback", "Submit feedback about Claude Code", "Info", "Alias: /bug"),
        Cmd("/help", "Show help and available commands", "Info"),
        Cmd("/insights", "Generate report analyzing Claude Code sessions", "Info"),
        Cmd("/release-notes", "View full changelog with most recent version closest to prompt", "Info"),
        Cmd("/stats", "Visualize daily usage, session history, streaks, and model preferences", "Info"),
        Cmd("/status", "Open Settings interface (Status tab)", "Info"),

        // Remote and device
        Cmd("/desktop", "Continue session in Claude Code Desktop app (macOS/Windows)", "Remote", "Alias: /app"),
        Cmd("/install-github-app", "Set up Claude GitHub Actions app for a repository", "Remote"),
        Cmd("/install-slack-app", "Install Claude Slack app", "Remote"),
        Cmd("/mobile", "Show QR code to download Claude mobile app", "Remote", "Aliases: /ios, /android"),
        Cmd("/remote-control", "Make session available for remote control from claude.ai", "Remote", "Alias: /rc"),
        Cmd("/remote-env", "Configure default remote environment for teleport sessions", "Remote"),

        // Fun
        Cmd("/stickers", "Order Claude Code stickers", "Fun"),

        // Bundled skills (ship with Claude Code, prompt-based)
        Cmd("/simplify", "Review changed code for reuse, quality, and efficiency, then fix issues", "Bundled Skill"),
        Cmd("/batch", "Orchestrate large-scale changes across a codebase in parallel", "Bundled Skill", "Usage: /batch <instruction>"),
        Cmd("/debug", "Troubleshoot current Claude Code session by reading the debug log", "Bundled Skill", "Usage: /debug [description]"),
        Cmd("/loop", "Run a prompt repeatedly on an interval", "Bundled Skill", "Usage: /loop [interval] <prompt>"),
        Cmd("/claude-api", "Load Claude API reference material for your project's language", "Bundled Skill"),
    };

    private static SlashCommandItem Cmd(string name, string description, string category, string documentation = "")
    {
        // Strip leading '/' so Name is consistent with custom skills (e.g. "config" not "/config").
        // Display code prepends '/' when rendering.
        var normalizedName = name.StartsWith('/') ? name[1..] : name;
        return new SlashCommandItem(normalizedName, description, "builtin", documentation, category);
    }
}
