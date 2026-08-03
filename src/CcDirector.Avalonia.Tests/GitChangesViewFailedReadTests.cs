using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Source Control tab's Changes page says when it could NOT read, instead of leaving "No changes
/// detected" on the screen (devthrottle_internal issue #1048).
///
/// "No changes detected" is a statement about the repository. When the read fails nothing has been
/// established about the repository at all, so the page was asserting something it had not observed -
/// and the two states were indistinguishable to the user.
///
/// The failure injected here is a folder that is not a git checkout, because that fails the read on
/// a machine that HAS git and so can be reproduced anywhere. It is the same branch a machine with no
/// git takes: that git-is-absent reaches this branch as a failed read is proved separately, in
/// CcDirector.Core.Tests GitAbsentTests.
/// </summary>
public class GitChangesViewFailedReadTests
{
    [AvaloniaFact]
    public async Task AFailedRead_SaysSo_RatherThanClaimingThereAreNoChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-changes-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var view = new GitChangesView();
            view.Attach(dir);

            await view.RefreshAsync();

            Assert.True(view.ProblemText.IsVisible);
            Assert.Contains("could not be read", view.ProblemText.Text ?? "");
            Assert.False(view.EmptyText.IsVisible);
            view.Detach();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// A successful read clears the accusation. A machine that has been fixed must stop being told
    /// it is broken - and a problem line that never goes away is its own defect.
    /// </summary>
    [AvaloniaFact]
    public async Task AGoodReadAfterABadOne_ClearsTheProblemLine()
    {
        var repo = Path.Combine(Path.GetTempPath(), "cc-director-changes-view-tests", Guid.NewGuid().ToString("N"));
        var notARepo = Path.Combine(Path.GetTempPath(), "cc-director-changes-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(notARepo);
        try
        {
            await RunGitAsync(repo, "init");

            var view = new GitChangesView();

            view.Attach(notARepo);
            await view.RefreshAsync();
            Assert.True(view.ProblemText.IsVisible);

            view.Attach(repo);
            await view.RefreshAsync();

            Assert.False(view.ProblemText.IsVisible);
            Assert.True(view.EmptyText.IsVisible);
            view.Detach();
        }
        finally
        {
            TryDelete(repo);
            TryDelete(notARepo);
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* a git object store on Windows can hold a handle a moment longer; the temp folder is disposable */ }
    }
}
