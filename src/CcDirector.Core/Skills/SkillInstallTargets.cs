using CcDirector.Core.Agents;

namespace CcDirector.Core.Skills;

/// <summary>
/// Where a skill has to appear for <paramref name="Kind"/> to find it.
/// </summary>
/// <param name="SharedRoot">The ONE directory a skill is written into - <c>~/.agents/skills</c>.
/// There is exactly one of these for every agent, because there is exactly one real copy.</param>
/// <param name="LinkRoot">The agent's own skills directory, when the agent does not read the shared
/// one and needs a link per skill pointing into it. Null when the agent reads the shared path
/// natively, which is six of the eight.</param>
public sealed record SkillInstallPaths(string SharedRoot, string? LinkRoot);

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
/// ONE DIRECTORY, USING THE AGENTS STANDARD. Every skill is materialized exactly once, into
/// <c>~/.agents/skills</c>. Codex, Gemini, Grok, pi, Copilot and opencode all scan that path natively
/// as an explicitly interoperable location and need nothing else at all. Claude Code does not scan it
/// today - that is an open, unshipped request on its own tracker - and Cursor documents only its own
/// directory, so those two get one LINK PER SKILL pointing into the shared directory. Inventing a
/// DevThrottle-owned directory instead would take six agents that need zero configuration and give
/// every one of them something that can be misconfigured.
///
/// A LINK PER SKILL, NEVER A LINK OVER THE DIRECTORY. <c>~/.claude/skills</c> is the owner's folder
/// and holds skills we did not write. We create <c>~/.claude/skills/&lt;id&gt;</c> and nothing above it.
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
    /// Where <paramref name="kind"/> needs skills to appear, absolute. Null when the agent has no
    /// skills mechanism to install into - a raw command line is a terminal, not an agent, and there is
    /// nowhere for a skill to go.
    /// </summary>
    public static SkillInstallPaths? For(AgentKind kind)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var shared = Path.Combine(home, ".agents", "skills");
        return kind switch
        {
            // Claude Code does not read the shared path, so each skill is linked into its own.
            AgentKind.ClaudeCode => new SkillInstallPaths(shared, Path.Combine(home, ".claude", "skills")),

            // Cursor documents only its own directory; the shared path is claimed for Cursor by
            // third-party summaries but not by Cursor, so it is linked rather than trusted.
            AgentKind.Cursor => new SkillInstallPaths(shared, Path.Combine(home, ".cursor", "skills")),

            AgentKind.Codex or AgentKind.Gemini or AgentKind.Grok
                or AgentKind.Pi or AgentKind.Copilot or AgentKind.OpenCode
                => new SkillInstallPaths(shared, null),

            // A user-supplied command line runs in raw terminal mode with no agent semantics at all.
            AgentKind.RawCli => null,

            // A kind added since this table was written. Installing into a guessed directory would
            // scatter files nowhere useful, so it installs nowhere until its path is looked up and
            // added here deliberately.
            _ => null,
        };
    }
}
