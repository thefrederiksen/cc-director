using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Handovers tab lists documents newest first. It used to order by filename, which sorted
/// every name beginning with a letter above every dated handover: a document written minutes
/// earlier appeared thirteenth of 143, below eleven move-* files and a rebuild manifest, in a
/// list whose visible date column then ran out of order.
/// </summary>
public class HandoverListOrderTests : IDisposable
{
    private readonly string _folder;

    public HandoverListOrderTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "handover-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string WriteHandover(string fileName, DateTime lastWrite)
    {
        var path = Path.Combine(_folder, fileName);
        File.WriteAllText(path, "---\ntitle: test\n---\n\nbody\n");
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void LoadOrdered_DatedFileNewerThanLetterNamedFile_PutsDatedFileFirst()
    {
        // The exact shape of the defect: a handover written today, against the machine-written
        // names that filename ordering floated to the top.
        WriteHandover("trial-handover-1dde12c2.md", new DateTime(2026, 7, 28, 15, 41, 0));
        WriteHandover("move-d8b4a5e6.md", new DateTime(2026, 7, 28, 21, 50, 0));
        WriteHandover("director-SOREN_NORTH-rebuild-manifest.md", new DateTime(2026, 7, 28, 9, 0, 0));
        WriteHandover("20260805_0613_cpmc-rehearsal-and-sort-defect.md", new DateTime(2026, 8, 5, 6, 13, 0));

        var ordered = HandoverViewModel.LoadOrdered(_folder);

        Assert.Equal(4, ordered.Count);
        Assert.Equal("20260805_0613_cpmc-rehearsal-and-sort-defect.md", Path.GetFileName(ordered[0].FilePath));
    }

    [Fact]
    public void LoadOrdered_MixedNamingConventions_DatesRunNewestToOldest()
    {
        // Every row displays FileDate, so that column must never run out of order - whether the
        // date comes from the filename convention or from the file's last write time.
        WriteHandover("20260304_0921_ai-examples-repo-status.md", new DateTime(2026, 3, 4, 9, 21, 0));
        WriteHandover("move-99509095.md", new DateTime(2026, 7, 28, 21, 50, 0));
        WriteHandover("20260805_0559_translation-mission.md", new DateTime(2026, 8, 5, 5, 59, 0));
        WriteHandover("2026-08-03-devthrottle-release-coordinator.md", new DateTime(2026, 8, 3, 8, 32, 0));
        WriteHandover("20260805_0613_cpmc-rehearsal-and-sort-defect.md", new DateTime(2026, 8, 5, 6, 13, 0));

        var ordered = HandoverViewModel.LoadOrdered(_folder);

        var dates = ordered.Select(h => h.FileDate).ToList();
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
    }

    [Fact]
    public void LoadOrdered_FilenameDateDiffersFromLastWrite_OrdersByTheDisplayedDate()
    {
        // A handover written at 06:00 but named for 05:59 must sort by what its row shows (05:59),
        // otherwise the visible dates disagree with the order again. This is the case that rules
        // out sorting on raw last-write time.
        WriteHandover("20260805_0559_earlier-by-name.md", new DateTime(2026, 8, 5, 6, 0, 30));
        WriteHandover("20260805_0613_later-by-name.md", new DateTime(2026, 8, 5, 6, 13, 45));

        var ordered = HandoverViewModel.LoadOrdered(_folder);

        Assert.Equal("20260805_0613_later-by-name.md", Path.GetFileName(ordered[0].FilePath));
        Assert.Equal(new DateTime(2026, 8, 5, 6, 13, 0), ordered[0].FileDate);
        Assert.Equal(new DateTime(2026, 8, 5, 5, 59, 0), ordered[1].FileDate);
    }

    [Fact]
    public void LoadOrdered_FolderAbsent_ReturnsEmpty()
    {
        var missing = Path.Combine(_folder, "no-such-folder");

        Assert.Empty(HandoverViewModel.LoadOrdered(missing));
    }
}
