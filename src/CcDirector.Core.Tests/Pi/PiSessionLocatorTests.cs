using System.Text.Json;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

/// <summary>
/// The Pi transcript is found BY THE SESSION ID the Director launched pi with, never by which file in
/// the repo is newest (issue #2670: the newest file at launch is always the previous session's, and a
/// fresh session was shown - and narrated - with five-week-old conversation).
/// </summary>
public class PiSessionLocatorTests
{
    private const string Repo = @"C:\target\repo";
    private const string OtherRepo = @"C:\other\repo";

    [Fact]
    public void Resolve_FindsTheFileNamedByTheId_NotTheNewestInTheRepo()
    {
        using var dir = new TempSessions();
        var own = dir.Write("sub-a", "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa", Repo, created: "2026-07-31T04:08:28.319Z", mtimeSeconds: 100);
        dir.Write("sub-a", "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb", Repo, created: "2026-09-03T10:00:00.000Z", mtimeSeconds: 900); // newer, same repo

        var found = PiSessionLocator.Resolve("11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa", dir.Path);

        Assert.Equal(own, found, ignoreCase: true);
    }

    [Fact]
    public void Resolve_NoFileForTheIdYet_ReturnsNull_EvenThoughTheRepoHasOlderSessions()
    {
        // THE regression: a session whose pi has not written its file (pi writes on the first message)
        // must answer "nothing yet", not "here is the previous session's file".
        using var dir = new TempSessions();
        dir.Write("sub-a", "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb", Repo, created: "2026-07-31T04:08:28.319Z", mtimeSeconds: 100);

        Assert.Null(PiSessionLocator.Resolve("33333333-cccc-4ccc-8ccc-cccccccccccc", dir.Path));
    }

    [Fact]
    public void Resolve_NoId_ReturnsNull()
    {
        using var dir = new TempSessions();
        dir.Write("sub-a", "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb", Repo, created: "2026-07-31T04:08:28.319Z", mtimeSeconds: 100);

        Assert.Null(PiSessionLocator.Resolve(null, dir.Path));
        Assert.Null(PiSessionLocator.Resolve("", dir.Path));
    }

    [Fact]
    public void FindById_SkipsArchivedFolders()
    {
        using var dir = new TempSessions();
        dir.Write("sub-a/_archived_2026", "44444444-dddd-4ddd-8ddd-dddddddddddd", Repo, created: "2026-07-31T04:08:28.319Z", mtimeSeconds: 100);

        Assert.Null(PiSessionLocator.FindById("44444444-dddd-4ddd-8ddd-dddddddddddd", dir.Path));
    }

    [Fact]
    public void FindCreatedAfter_PicksTheFileCreatedAfterTheClear_InThisRepo_UnderAnotherId()
    {
        using var dir = new TempSessions();
        var cleared = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        const string ownId = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        // The session's own file: created before the clear (and still being written after it).
        dir.Write("sub-a", ownId, Repo, created: "2026-09-03T11:00:00.000Z", mtimeSeconds: 900);
        // A second Pi session in the same repo: created before the clear, written after it. A last-write
        // test would pick this one - it must not.
        dir.Write("sub-a", "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb", Repo, created: "2026-09-03T11:30:00.000Z", mtimeSeconds: 950);
        // Created after the clear, but in another repo.
        dir.Write("sub-b", "33333333-cccc-4ccc-8ccc-cccccccccccc", OtherRepo, created: "2026-09-03T12:05:00.000Z", mtimeSeconds: 960);
        // The file pi started for this session after /new.
        var started = dir.Write("sub-a", "44444444-dddd-4ddd-8ddd-dddddddddddd", Repo, created: "2026-09-03T12:01:00.000Z", mtimeSeconds: 10);

        var found = PiSessionLocator.FindCreatedAfter(Repo, cleared, ownId, dir.Path);

        Assert.NotNull(found);
        Assert.Equal("44444444-dddd-4ddd-8ddd-dddddddddddd", found!.Id);
        Assert.Equal(started, found.Path, ignoreCase: true);
        Assert.Equal(new DateTime(2026, 9, 3, 12, 1, 0, DateTimeKind.Utc), found.CreatedUtc);
    }

    [Fact]
    public void FindCreatedAfter_NewestCreatedWins_WhenSeveralWereStartedAfterTheClear()
    {
        using var dir = new TempSessions();
        var cleared = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        dir.Write("sub-a", "55555555-eeee-4eee-8eee-eeeeeeeeeeee", Repo, created: "2026-09-03T12:01:00.000Z", mtimeSeconds: 990);
        dir.Write("sub-a", "66666666-ffff-4fff-8fff-ffffffffffff", Repo, created: "2026-09-03T12:02:00.000Z", mtimeSeconds: 10);

        var found = PiSessionLocator.FindCreatedAfter(Repo, cleared, "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa", dir.Path);

        Assert.Equal("66666666-ffff-4fff-8fff-ffffffffffff", found?.Id);
    }

    [Fact]
    public void FindCreatedAfter_NothingStartedYet_ReturnsNull()
    {
        using var dir = new TempSessions();
        var cleared = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        const string ownId = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        dir.Write("sub-a", ownId, Repo, created: "2026-09-03T11:00:00.000Z", mtimeSeconds: 900);

        Assert.Null(PiSessionLocator.FindCreatedAfter(Repo, cleared, ownId, dir.Path));
    }

    /// <summary>A throwaway pi sessions directory. Files are laid out as pi lays them out: a per-cwd
    /// subdirectory holding <c>&lt;timestamp&gt;_&lt;id&gt;.jsonl</c> whose first line is the session record.</summary>
    private sealed class TempSessions : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi-sessions-" + Guid.NewGuid().ToString("N"));

        public TempSessions() => Directory.CreateDirectory(Path);

        public string Write(string sub, string id, string cwd, string created, int mtimeSeconds)
        {
            var dir = System.IO.Path.Combine(Path, sub);
            Directory.CreateDirectory(dir);
            var stamp = created.Replace(':', '-').Replace('.', '-');
            var path = System.IO.Path.Combine(dir, $"{stamp}_{id}.jsonl");
            var session = "{\"type\":\"session\",\"version\":3,\"id\":\"" + id + "\",\"timestamp\":\"" + created
                          + "\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}";
            File.WriteAllLines(path, new[]
            {
                session,
                "{\"type\":\"message\",\"id\":\"u1\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}",
            });
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtimeSeconds));
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
