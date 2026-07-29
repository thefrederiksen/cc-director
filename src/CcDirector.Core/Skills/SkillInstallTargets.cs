using CcDirector.Core.Agents;

namespace CcDirector.Core.Skills;

/// <summary>
/// WHERE each agent looks for skills on this machine.
///
/// This is the entire per-agent cost of the skill library, and it is deliberately a table of PATHS
/// and nothing else. A skill is a directory in the Agent Skills open standard (agentskills.io,
/// stewarded by the Agentic AI Foundation) - SKILL.md plus any files and subdirectories - and every
/// agent DevThrottle supervises reads that same directory, byte for byte. So there is no per-agent
/// FORMAT to convert into and no translation layer to maintain: one materialized directory serves all
/// of them, and the only thing that differs is which folder it has to appear in.
///
/// Two facts drove this table, both read from each agent's own current documentation:
///
///  - <c>~/.agents/skills</c> is the SHARED path. Codex, Gemini, Grok, pi, Copilot and opencode all
///    scan it as an explicitly interoperable location, so one copy there reaches six of the eight.
///  - Claude Code is the exception. It scans <c>~/.claude/skills</c> and does NOT scan
///    <c>~/.agents/skills</c> today - that is an open, unshipped request on its tracker - so it needs
///    its own entry. Cursor likewise documents only its own <c>~/.cursor/skills</c>.
///
/// USER LEVEL, NEVER THE REPOSITORY. Skills install under the user's home directory rather than into
/// the repository being worked on, because writing into the repository would put untracked files in
/// the owner's working trees. The consequence, stated so it is not a surprise: an installed skill is
/// visible to every session on this machine, including ones the Director did not launch.
/// </summary>
public static class SkillInstallTargets
{
    /// <summary>The interoperable path six of the eight agents read. Relative to the user's home.</summary>
    public const string SharedRelativePath = ".agents/skills";

    /// <summary>
    /// The directories a skill must appear in to be discovered by <paramref name="kind"/>, absolute.
    /// Empty when the agent has no skills mechanism to install into - a raw command line is a
    /// terminal, not an agent, and there is nowhere for a skill to go.
    /// </summary>
    public static IReadOnlyList<string> For(AgentKind kind)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return Array.Empty<string>();

        var shared = Path.Combine(home, ".agents", "skills");
        return kind switch
        {
            // Claude Code does not read the shared path, so it gets its own and only its own.
            AgentKind.ClaudeCode => new[] { Path.Combine(home, ".claude", "skills") },

            // Cursor documents only its own directory; the shared path is claimed for Cursor by
            // third-party summaries but not by Cursor, so it is not relied on here.
            AgentKind.Cursor => new[] { Path.Combine(home, ".cursor", "skills") },

            AgentKind.Codex or AgentKind.Gemini or AgentKind.Grok
                or AgentKind.Pi or AgentKind.Copilot or AgentKind.OpenCode => new[] { shared },

            // A user-supplied command line runs in raw terminal mode with no agent semantics at all.
            AgentKind.RawCli => Array.Empty<string>(),

            // A kind added since this table was written. Installing into a guessed directory would
            // scatter files nowhere useful, so it installs nowhere until its path is looked up and
            // added here deliberately.
            _ => Array.Empty<string>(),
        };
    }
}
