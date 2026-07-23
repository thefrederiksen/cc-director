using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

/// <summary>
/// Installs the Claude Code skill files. This is all that remains of the old per-tool installer:
/// the cc-* tools ship as one shared-venv bundle provisioned by the app on first launch, and the
/// app binaries are placed by the setup engine - the wizard itself only installs skills (the
/// analog of the Windows wizard's SkillInstaller).
/// </summary>
public class ToolInstaller
{
    private readonly string _skillsBaseDir;
    private readonly GitHubReleaseService _github = new();

    public static readonly string[] SkillNames =
    [
        // dev-throttle: the product's main skill (renamed from cc-director).
        "dev-throttle",
        // fleet-comms (issue #723): teaches an agent the cc-devthrottle session/message verbs
        // so every machine's agents know the capability, not just the CC_FLEET_TOOLS env hint.
        // Kept in sync with SkillInstaller.SkillNames in tools/cc-director-setup.
        "fleet-comms",
        // move-session: relocate a live session to another slot/Director via the Gateway handover,
        // with an approval gate and a verify-before-mark lifecycle.
        "move-session",
    ];

    public ToolInstaller()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _skillsBaseDir = Path.Combine(userProfile, ".claude", "skills");
    }

    public async Task InstallSkillsAsync(List<SkillItem> skillItems)
    {
        SetupLog.Write($"[ToolInstaller] InstallSkillsAsync: count={skillItems.Count}");

        foreach (var skill in skillItems)
        {
            var skillDir = Path.Combine(_skillsBaseDir, skill.Name);
            Directory.CreateDirectory(skillDir);
            var skillPath = Path.Combine(skillDir, "SKILL.md");

            // The canonical skill tree is .claude/skills/<name>/ (the stale root skills/ duplicate was
            // removed in issue #396; fetching from skills/ here 404'd every install).
            var success = await _github.DownloadSkillFileAsync(
                skillPath, $".claude/skills/{skill.Name}/SKILL.md");
            skill.Status = success ? "Done" : "Failed";
        }

        var done = skillItems.Count(s => s.Status == "Done");
        SetupLog.Write($"[ToolInstaller] InstallSkillsAsync: {done}/{skillItems.Count} installed");
    }
}
