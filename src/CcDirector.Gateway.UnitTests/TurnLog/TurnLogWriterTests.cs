using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.TurnLog;
using Xunit;

namespace CcDirector.Gateway.Tests.TurnLog;

/// <summary>
/// The corpus on disk: that a record survives the round trip whole, that a bundle holding several records
/// still reads as one gzip stream, and that a caller-supplied name cannot climb out of the directory.
///
/// The round trip is the test that matters. Every question the turn log exists to answer is asked of a file
/// read months later, so a record that serializes but does not come back is not a record.
/// </summary>
public sealed class TurnLogWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "turnlog-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a test directory that will not delete is not a test failure */ }
    }

    [Fact]
    public void Append_OneRecord_RoundTripsWholeThroughTheBundle()
    {
        var writer = new TurnLogWriter(_root, writerId: "writer01");
        var record = NewRecord(sessionId: "sid-1", account: "acct-a", machine: "SOREN-NORTH");
        record.Terminal.Rows.Add("the screen line the judgement reads");

        var path = writer.Append(record);

        Assert.NotNull(path);
        var read = ReadBundle(path!);
        var only = Assert.Single(read);
        Assert.Equal(record.RecordId, only.RecordId);
        Assert.Equal("sid-1", only.Glance.SessionId);
        Assert.Equal("acct-a", only.Glance.Account);
        Assert.Equal("the screen line the judgement reads", Assert.Single(only.Terminal.Rows));
        Assert.Equal(TurnLogRecord.CurrentSchemaVersion, only.SchemaVersion);
    }

    [Fact]
    public void Append_SeveralRecords_AllReadBackFromTheOneBundle()
    {
        // Each record is its own gzip member, appended. This asserts the property that makes that safe:
        // concatenated members read back as one stream, so nothing is lost when the process stops between
        // two records.
        var writer = new TurnLogWriter(_root, writerId: "writer01");
        foreach (var sid in new[] { "sid-1", "sid-2", "sid-3" })
            writer.Append(NewRecord(sid, "acct-a", "SOREN-NORTH"));

        var path = writer.BundlePathFor(NewRecord("x", "acct-a", "SOREN-NORTH").CapturedAtUtc, "acct-a", "SOREN-NORTH");
        var read = ReadBundle(path);

        Assert.Equal(new[] { "sid-1", "sid-2", "sid-3" }, read.Select(r => r.Glance.SessionId));
    }

    [Fact]
    public void Append_TwoMachines_AreSeparateBundles()
    {
        var writer = new TurnLogWriter(_root, writerId: "writer01");
        writer.Append(NewRecord("sid-1", "acct-a", "SOREN-NORTH"));
        writer.Append(NewRecord("sid-2", "acct-a", "SOREN-SOUTH"));

        var bundles = Directory.GetFiles(_root, "*.jsonl.gz", SearchOption.AllDirectories);
        Assert.Equal(2, bundles.Length);
    }

    [Fact]
    public void Append_NothingIsWrittenUntilThereIsARecord()
    {
        // A Gateway with capture switched off must leave no trace on disk. Constructing the writer is not
        // a decision to record anything.
        _ = new TurnLogWriter(_root, writerId: "writer01");
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("../../etc", "etc-")]
    [InlineData("C:\\Windows", "C--Windows-")]
    [InlineData("a/b/c", "a-b-c-")]
    public void Sanitize_ReducesAPathSegmentToSomethingThatCannotClimb(string input, string expectedStem)
    {
        var result = TurnLogWriter.Sanitize(input);
        Assert.StartsWith(expectedStem, result);
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain(":", result);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Sanitize_ABlankName_IsNamedRatherThanEmpty(string input)
        => Assert.Equal("unknown", TurnLogWriter.Sanitize(input));

    [Theory]
    // Every one of these pairs collapsed to the SAME segment before the digest was added, which would have
    // appended two accounts' records into one bundle - two different accounts' terminals in one file.
    [InlineData("a/b", @"a\b")]
    [InlineData("Account-One", "account-one")]
    [InlineData(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-first",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-second")]
    public void Sanitize_TwoDifferentNames_NeverShareASegment(string left, string right)
        => Assert.NotEqual(TurnLogWriter.Sanitize(left), TurnLogWriter.Sanitize(right));

    [Fact]
    public void Append_TwoAccountsThatSanitizeAlike_StillGetSeparateBundles()
    {
        var writer = new TurnLogWriter(_root, writerId: "writer01");
        writer.Append(NewRecord("sid-1", "a/b", "SOREN-NORTH"));
        writer.Append(NewRecord("sid-2", @"a\b", "SOREN-NORTH"));

        var bundles = Directory.GetFiles(_root, "*.jsonl.gz", SearchOption.AllDirectories);
        Assert.Equal(2, bundles.Length);
    }

    [Fact]
    public void Append_AnAccountNameWithSeparators_StaysInsideTheRoot()
    {
        var writer = new TurnLogWriter(_root, writerId: "writer01");
        var path = writer.Append(NewRecord("sid-1", "../../escaped", "../../also-escaped"));

        Assert.NotNull(path);
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(path!), StringComparison.OrdinalIgnoreCase);
    }

    private static TurnLogRecord NewRecord(string sessionId, string account, string machine) => new()
    {
        CapturedAtUtc = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc),
        Glance = new TurnLogGlance
        {
            SessionId = sessionId,
            Account = account,
            Computer = machine,
        },
    };

    /// <summary>Read a bundle the way anything mining the corpus will: decompress the whole file as one
    /// gzip stream, then read one record per line.</summary>
    private static List<TurnLogRecord> ReadBundle(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var records = new List<TurnLogRecord>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            records.Add(JsonSerializer.Deserialize<TurnLogRecord>(line)!);
        }
        return records;
    }
}
