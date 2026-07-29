using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// Installing the fleet's skills where each agent looks for them.
///
/// The three properties worth pinning are the ones that go wrong SILENTLY: overwriting one of the
/// owner's own skills, leaving a withdrawn skill on disk, and writing a SKILL.md no agent will
/// actually accept.
/// </summary>
public sealed class SkillDirectoryInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "skill-install-tests-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "store");
    private string Target => Path.Combine(_root, "target");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static SkillBundle Bundle(
        string id = "demo-skill",
        string summary = "Does a useful thing.",
        IReadOnlyList<SkillFileBytes>? files = null) =>
        new(
            Id: id,
            Version: 3,
            ContentHash: "bundle-hash-3",
            Summary: summary,
            Triggers: new[] { "do the thing", "demo" },
            BodyMarkdown: "# Demo\n\nDo the thing.\n",
            Files: files ?? Array.Empty<SkillFileBytes>());

    [Fact]
    public void A_materialized_skill_is_a_standard_skill_directory()
    {
        Directory.CreateDirectory(Store);
        var raw = new byte[] { 0x00, 0xFF, 0x10 };

        var directory = SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/tracing.md", Encoding.UTF8.GetBytes("# Tracing\n"), false),
            new SkillFileBytes("assets/logo.png", raw, false),
            new SkillFileBytes("scripts/build.sh", Encoding.UTF8.GetBytes("echo hi\n"), true),
        }));

        // SKILL.md at the root and the files at their own paths - that IS the standard, and it is what
        // every agent this product supervises reads.
        var skillMd = File.ReadAllText(Path.Combine(directory, "SKILL.md"));
        Assert.StartsWith("---\n", skillMd);
        Assert.Contains("name: demo-skill", skillMd);
        Assert.Contains("Does a useful thing.", skillMd);
        Assert.Contains("# Demo", skillMd);
        Assert.Equal("# Tracing\n", File.ReadAllText(Path.Combine(directory, "references", "tracing.md")));
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(directory, "assets", "logo.png")));
        Assert.True(File.Exists(Path.Combine(directory, "scripts", "build.sh")));
    }

    [Fact]
    public void The_frontmatter_name_is_the_id_because_that_is_what_agents_validate()
    {
        // The standard requires a lowercase slug that MATCHES THE DIRECTORY NAME. A display name like
        // "Move a session" would fail validation in every agent, so the id is what goes in the file
        // even though it is not what a person would call the skill.
        var skillMd = SkillMarkdown.Compose(
            "move-session", "Relocate a live session: through the Gateway.", new[] { "move session" },
            "# Move\n");

        Assert.Contains("name: move-session", skillMd);
        // The summary contains a colon, which is why every scalar is quoted rather than only the ones
        // that look like they need it - unquoted, this line would change the document's meaning.
        Assert.Contains("description: \"Relocate a live session: through the Gateway.", skillMd);
        Assert.Contains("Use when the task involves: move session.", skillMd);
    }

    [Fact]
    public void Installing_puts_the_skill_where_the_agent_looks_and_marks_it_as_ours()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());
        Directory.CreateDirectory(Target);

        var installed = InstallInto(Target);

        Assert.Equal(1, installed);
        Assert.True(File.Exists(Path.Combine(Target, "demo-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Target, "demo-skill", SkillDirectoryInstaller.MarkerFileName)));
    }

    [Fact]
    public void A_skill_the_machine_already_had_is_never_overwritten()
    {
        // THE RULE THIS FEATURE MUST NOT BREAK. The library is an ADDITIONAL source of skills; a
        // machine's own skill wins a name clash. Copying over one of the owner's own skills would be
        // silent data loss dressed up as a feature.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());

        var mine = Path.Combine(Target, "demo-skill");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "SKILL.md"), "# MY OWN VERSION\n");

        InstallInto(Target);

        Assert.Equal("# MY OWN VERSION\n", File.ReadAllText(Path.Combine(mine, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(mine, SkillDirectoryInstaller.MarkerFileName)));
    }

    [Fact]
    public void A_withdrawn_skill_is_removed_and_a_skill_that_is_not_ours_is_left_alone()
    {
        // Reconcile, never add: a skill switched off on the Gateway must not keep working from disk.
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "keeper"));
        SkillDirectoryInstaller.Materialize(Store, Bundle(id: "withdrawn"));
        InstallInto(Target);
        Assert.True(Directory.Exists(Path.Combine(Target, "withdrawn")));

        // Somebody else's skill, sitting in the same directory. It is not ours to delete.
        var theirs = Path.Combine(Target, "not-ours");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "# theirs\n");

        // The Gateway stops serving 'withdrawn'.
        Directory.Delete(Path.Combine(Store, "withdrawn"), recursive: true);
        InstallInto(Target);

        Assert.False(Directory.Exists(Path.Combine(Target, "withdrawn")));
        Assert.True(Directory.Exists(Path.Combine(Target, "keeper")));
        Assert.True(File.Exists(Path.Combine(theirs, "SKILL.md")));
    }

    [Fact]
    public void A_file_removed_upstream_does_not_survive_inside_an_installed_skill()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/old.md", Encoding.UTF8.GetBytes("old"), false),
        }));
        InstallInto(Target);
        Assert.True(File.Exists(Path.Combine(Target, "demo-skill", "references", "old.md")));

        SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("references/new.md", Encoding.UTF8.GetBytes("new"), false),
        }));
        InstallInto(Target);

        Assert.False(File.Exists(Path.Combine(Target, "demo-skill", "references", "old.md")));
        Assert.True(File.Exists(Path.Combine(Target, "demo-skill", "references", "new.md")));
    }

    [Fact]
    public void A_path_that_escapes_the_skill_directory_is_refused()
    {
        // The Gateway validates paths on write; this is the same rule enforced again at the point
        // where bytes hit this disk, because a store that was ever wrong must not be able to write
        // wherever it likes.
        Directory.CreateDirectory(Store);
        Assert.Throws<InvalidOperationException>(() => SkillDirectoryInstaller.Materialize(Store, Bundle(files: new[]
        {
            new SkillFileBytes("../../escape.md", Encoding.UTF8.GetBytes("bad"), false),
        })));
    }

    [Fact]
    public void Nothing_is_installed_when_the_store_has_never_been_filled()
    {
        // A Director that has never reached the Gateway installs nothing and says so. It does NOT
        // fail, because a session must still launch.
        Assert.Equal(0, SkillDirectoryInstaller.InstallFor(AgentKind.ClaudeCode, Path.Combine(_root, "missing")));
    }

    [Fact]
    public void An_agent_with_no_skills_directory_installs_nothing()
    {
        Directory.CreateDirectory(Store);
        SkillDirectoryInstaller.Materialize(Store, Bundle());
        Assert.Equal(0, SkillDirectoryInstaller.InstallFor(AgentKind.RawCli, Store));
    }

    [Fact]
    public void Every_agent_kind_that_has_a_skills_directory_gets_one_that_is_documented()
    {
        // Claude Code is the exception that makes this table necessary: it reads ~/.claude/skills and
        // does NOT read the shared ~/.agents/skills path that the others do. Getting this backwards
        // would install everything into a directory Claude Code never looks in - and nothing would
        // fail, it would just silently not work.
        Assert.All(SkillInstallTargets.For(AgentKind.ClaudeCode),
            p => Assert.Contains(Path.Combine(".claude", "skills"), p));
        Assert.All(SkillInstallTargets.For(AgentKind.Cursor),
            p => Assert.Contains(Path.Combine(".cursor", "skills"), p));

        foreach (var kind in new[]
                 {
                     AgentKind.Codex, AgentKind.Gemini, AgentKind.Grok,
                     AgentKind.Pi, AgentKind.Copilot, AgentKind.OpenCode,
                 })
        {
            Assert.All(SkillInstallTargets.For(kind),
                p => Assert.Contains(Path.Combine(".agents", "skills"), p));
        }

        Assert.Empty(SkillInstallTargets.For(AgentKind.RawCli));
    }

    /// <summary>Run the REAL installer against one explicit directory. The target is overridden rather
    /// than the algorithm reimplemented: a test that reimplements what it is testing proves only that
    /// the copy agrees with itself. The override exists so a test never writes into the home directory
    /// of whoever is running it.</summary>
    private int InstallInto(string target) =>
        SkillDirectoryInstaller.InstallFor(AgentKind.ClaudeCode, Store, new[] { target });
}
