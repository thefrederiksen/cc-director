using CcDirector.Gateway.TurnLog;
using Xunit;

namespace CcDirector.Gateway.Tests.TurnLog;

/// <summary>
/// Retention on the captured records.
///
/// THE TEST THAT MATTERS MOST is that a directory it does not understand is LEFT ALONE. The failure mode
/// of a retention sweep must be "kept too long", never "deleted something we needed" - and a sweep that
/// guesses what a directory is, or that treats an unparseable name as expired, is how a tidy-up becomes an
/// incident.
/// </summary>
public sealed class TurnLogRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "turnlog-retention-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Day(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "account", "machine"));
        File.WriteAllText(Path.Combine(path, "account", "machine", "bundle.jsonl.gz"), "x");
        return path;
    }

    private TurnLogRetention NewSweep() => new(_root, () => Now);

    [Fact]
    public void ADayOlderThanTheWindow_IsRemoved()
    {
        var old = Day("2026-09-01");   // 19 days before Now

        var removed = NewSweep().Sweep();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void ADayInsideTheWindow_IsKept()
    {
        var recent = Day("2026-09-15");   // 5 days before Now

        NewSweep().Sweep();

        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void ADirectoryWhoseNameIsNotADate_IsLeftALONE()
    {
        // The one that matters. An unparseable name is something this sweep does not understand, and it
        // must not be treated as expired just because it cannot be dated.
        var strange = Path.Combine(_root, "labelled-keep-forever");
        Directory.CreateDirectory(strange);
        File.WriteAllText(Path.Combine(strange, "note.txt"), "x");

        var removed = NewSweep().Sweep();

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(strange));
    }

    [Fact]
    public void TheBoundaryDayIsKeptRatherThanCut()
    {
        // Exactly at the window. Keeping it is the safe side of an off-by-one on a delete.
        var boundary = Day(Now.Date.AddDays(-TurnLogRetention.KeepFor.TotalDays).ToString("yyyy-MM-dd"));

        NewSweep().Sweep();

        Assert.True(Directory.Exists(boundary));
    }

    [Fact]
    public void NoTurnLogDirectoryAtAll_IsNotAnError()
    {
        // A Gateway that has never had capture switched on has no directory, and a sweep must not care.
        var sweep = new TurnLogRetention(Path.Combine(_root, "never-created"), () => Now);
        Assert.Equal(0, sweep.Sweep());
    }
}
