using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The two things that made the terminal recorder a data-collection defect rather than a feature:
/// it ran on every install unless somebody found an environment variable named only in a source
/// comment, and what it wrote outlived the session it was about, forever.
///
/// CC_DIRECTOR_ROOT is pinned to a temp directory so config.json and the recordings folder read and
/// written here are this test's, never the real machine's.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class TerminalSessionRecorderPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevEnv;

    public TerminalSessionRecorderPolicyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-recorder-policy-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // The override is process-wide and the machine running this suite may well have it set -
        // it is how corpus collection was switched on before there was a setting - so a test about
        // the DEFAULT has to clear it, or it would read the machine's opinion and call it the default.
        _prevEnv = Environment.GetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, _prevEnv);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Recording_is_off_on_a_clean_install()
    {
        Assert.False(SessionRecordingConfig.IsEnabled());
    }

    [Fact]
    public void Recording_is_on_only_when_the_visible_setting_says_so()
    {
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionRecordingConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true },
        });

        Assert.True(SessionRecordingConfig.IsEnabled());
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void The_environment_override_wins_in_both_directions(string value, bool expected)
    {
        // Config says the opposite of the override, so a test that passed by accident - because the
        // config default happened to agree - cannot pass here.
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject
        {
            [SessionRecordingConfig.SectionName] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = !expected },
        });
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, value);

        Assert.Equal(expected, SessionRecordingConfig.IsEnabled());
    }

    [Fact]
    public void A_nonsense_override_says_so_rather_than_guessing()
    {
        Environment.SetEnvironmentVariable(SessionRecordingConfig.EnvironmentVariable, "maybe");

        var ex = Assert.Throws<InvalidOperationException>(() => SessionRecordingConfig.IsEnabled());
        Assert.Contains(SessionRecordingConfig.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing a session deletes what was recorded of it. Before this, removal disposed the writer
    /// and left the file: an install with recording switched on accumulated the screens of sessions
    /// that no longer existed, in a directory nothing ever swept.
    ///
    /// The directory is created here with the SAME spelling the recorder writes with, and the
    /// arrangement is asserted before the act - a purge aimed at a directory that never existed
    /// would otherwise pass this test while deleting nothing, which is exactly the mistake the
    /// first version of the purge made (it used the dashed spelling of the id).
    /// </summary>
    [Fact]
    public void Removing_a_session_purges_its_recording()
    {
        var recordings = Path.Combine(_root, "recordings-under-test");
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        using var recorder = new TerminalSessionRecorder(manager, root: recordings);
        try
        {
            recorder.Start();
            var session = manager.CreateSession(Path.GetTempPath());

            var sessionDir = Path.Combine(recordings, session.Id.ToString("N"));
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "grid.jsonl"), "{\"rows\":[\"a secret on a screen\"]}\n");
            Assert.True(Directory.Exists(sessionDir), "the arrangement did not create the recording it is about to remove");

            manager.RemoveSession(session.Id);

            Assert.False(Directory.Exists(sessionDir),
                "the session was removed but its recorded screens are still on disk");
        }
        finally { manager.Dispose(); }
    }
}
