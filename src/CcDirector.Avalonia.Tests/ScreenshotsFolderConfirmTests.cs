using Avalonia.Headless.XUnit;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The wizard's Screenshots step must both PERSIST the folder and RE-POINT the live screenshots
/// panel at it. Persisting alone shipped as a dead end: the panel resolves its folder once at
/// startup, so a user who set the folder in first-run saw an empty panel until the next restart -
/// and Refresh could not save them either, because it re-listed the folder resolved at startup.
///
/// This drives the production write path (<c>ConfirmScreenshotsFolderAsync</c>, what the step's
/// primary button invokes) and asserts BOTH halves of the seam: what the panel's own resolver
/// (<see cref="CcStorage.Screenshots"/>) reports afterwards, and that the reload actually ran.
///
/// Constructed headless and never shown. Config is redirected to a temp root via CC_DIRECTOR_ROOT;
/// the assembly runs sequentially (TestParallelization), so the process-global env var is not raced.
/// </summary>
public class ScreenshotsFolderConfirmTests
{
    [AvaloniaFact]
    public async Task ConfirmScreenshotsFolder_PersistsFolder_AndReloadsThePanel()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-shots-confirm-tests", Guid.NewGuid().ToString("N"));
        var chosen = Path.Combine(root, "my-screenshots");
        Directory.CreateDirectory(chosen);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            // Before the step runs, the resolver reports the fallback folder - not the user's.
            Assert.NotEqual(chosen, CcStorage.Screenshots());

            var reloads = 0;
            var dialog = new FirstRunWizardDialog(new AgentOptions(), () =>
            {
                reloads++;
                return Task.CompletedTask;
            });

            await dialog.ConfirmScreenshotsFolderAsync(chosen);

            // Persisted: the same resolver the screenshots panel uses now answers with the chosen folder.
            Assert.Equal(chosen, CcStorage.Screenshots());

            // And the live panel was told, so the payoff lands while the wizard is still open.
            Assert.Equal(1, reloads);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
