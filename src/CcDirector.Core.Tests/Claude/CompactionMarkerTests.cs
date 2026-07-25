using System.Text;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Issue #2150 - reading the COMPACTION mark out of a claude transcript, the signal compact-and-continue
/// waits on.
///
/// The sample lines here are the real shape claude writes (verified against live transcripts written by
/// claude 2.1.195 through 2.1.218). Two properties matter and are each tested: the mark carries its own
/// timestamp, and it is appended to the SAME file under the SAME session id - which is why compaction,
/// unlike clearing, needs no transcript re-link.
/// </summary>
public sealed class CompactionMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-compaction-" + Guid.NewGuid().ToString("N"));

    public CompactionMarkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not a test failure */ }
    }

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", Encoding.UTF8);
        return path;
    }

    private static string PlainLine(string timestamp, string sessionId = "s-1") =>
        $$"""{"type":"user","message":{"role":"user","content":"do the thing"},"timestamp":"{{timestamp}}","sessionId":"{{sessionId}}"}""";

    private static string CompactionLine(string timestamp, string sessionId = "s-1") =>
        $$"""{"type":"user","isVisibleInTranscriptOnly":true,"isCompactSummary":true,"message":{"role":"user","content":"This session is being continued from a previous conversation that ran out of context."},"timestamp":"{{timestamp}}","sessionId":"{{sessionId}}","version":"2.1.218"}""";

    [Fact]
    public void ReadLastCompactionUtc_ReturnsTheMarkTimestamp()
    {
        var path = WriteTranscript(
            PlainLine("2026-07-25T09:00:00.000Z"),
            CompactionLine("2026-07-25T09:14:22.500Z"),
            PlainLine("2026-07-25T09:15:00.000Z"));

        var stamp = CompactionMarker.ReadLastCompactionUtc(path);

        Assert.NotNull(stamp);
        Assert.Equal(new DateTime(2026, 7, 25, 9, 14, 22, 500, DateTimeKind.Utc), stamp!.Value);
        Assert.Equal(DateTimeKind.Utc, stamp.Value.Kind);
    }

    /// <summary>
    /// A long-lived session compacts repeatedly. The NEWEST mark is the one that answers "has it
    /// compacted since I asked" - reporting the first would make every later request look instantly done.
    /// </summary>
    [Fact]
    public void ReadLastCompactionUtc_ReturnsTheNewestMarkWhenTheSessionCompactedMoreThanOnce()
    {
        var path = WriteTranscript(
            CompactionLine("2026-07-25T08:00:00.000Z"),
            PlainLine("2026-07-25T08:30:00.000Z"),
            CompactionLine("2026-07-25T11:45:00.000Z"),
            PlainLine("2026-07-25T11:46:00.000Z"));

        var stamp = CompactionMarker.ReadLastCompactionUtc(path);

        Assert.Equal(new DateTime(2026, 7, 25, 11, 45, 0, DateTimeKind.Utc), stamp);
    }

    [Fact]
    public void ReadLastCompactionUtc_NullWhenTheSessionHasNeverCompacted()
    {
        var path = WriteTranscript(
            PlainLine("2026-07-25T09:00:00.000Z"),
            PlainLine("2026-07-25T09:05:00.000Z"));

        Assert.Null(CompactionMarker.ReadLastCompactionUtc(path));
    }

    [Fact]
    public void ReadLastCompactionUtc_NullWhenTheTranscriptDoesNotExist()
    {
        Assert.Null(CompactionMarker.ReadLastCompactionUtc(Path.Combine(_dir, "not-written-yet.jsonl")));
    }

    /// <summary>
    /// The words appear inside ordinary message text all the time - this very repository discusses
    /// isCompactSummary in prose. Only a top-level boolean true is a mark; a mention is not.
    /// </summary>
    [Fact]
    public void ReadLastCompactionUtc_IgnoresTheFieldNameMentionedInsideMessageText()
    {
        var path = WriteTranscript(
            """{"type":"user","message":{"role":"user","content":"explain what isCompactSummary means"},"timestamp":"2026-07-25T09:00:00.000Z","sessionId":"s-1"}""",
            """{"type":"assistant","message":{"role":"assistant","content":"isCompactSummary marks a compaction"},"timestamp":"2026-07-25T09:00:30.000Z","sessionId":"s-1"}""");

        Assert.Null(CompactionMarker.ReadLastCompactionUtc(path));
    }

    /// <summary>A mark written as the string "true" rather than the boolean is not a mark either.</summary>
    [Fact]
    public void ReadLastCompactionUtc_IgnoresANonBooleanMarkerValue()
    {
        var path = WriteTranscript(
            """{"type":"user","isCompactSummary":"true","timestamp":"2026-07-25T09:00:00.000Z","sessionId":"s-1"}""");

        Assert.Null(CompactionMarker.ReadLastCompactionUtc(path));
    }

    /// <summary>
    /// We read the file WHILE claude is appending to it, so the last line is regularly half-written.
    /// A torn line must be skipped and the next poll must still find the mark - a parse crash here would
    /// take down the rescue.
    /// </summary>
    [Fact]
    public void ReadLastCompactionUtc_SurvivesAHalfWrittenTrailingLine()
    {
        var path = WriteTranscript(
            CompactionLine("2026-07-25T09:14:00.000Z"),
            """{"type":"assistant","isCompactSummary":tr""");

        Assert.Equal(new DateTime(2026, 7, 25, 9, 14, 0, DateTimeKind.Utc), CompactionMarker.ReadLastCompactionUtc(path));
    }

    /// <summary>
    /// The transcript is open and being written by claude when we read it. Opening it exclusively would
    /// throw the moment a compaction actually landed - the one moment this must work.
    /// </summary>
    [Fact]
    public void ReadLastCompactionUtc_ReadsAFileThatIsStillOpenForWriting()
    {
        var path = WriteTranscript(CompactionLine("2026-07-25T09:14:00.000Z"));

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        Assert.NotNull(CompactionMarker.ReadLastCompactionUtc(path));
    }
}
