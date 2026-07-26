using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2190: every image-upload rejection must leave a line in the Director log.
///
/// Before this, only the SUCCESS paths logged. The three upload verbs returned genuinely useful sentences -
/// an unsupported image type with the accepted list, an incomplete upload with both byte counts, an
/// out-of-order chunk with both sequence numbers - and wrote nothing anywhere. The reason existed for
/// exactly as long as the response took to travel, then vanished.
///
/// The existing <see cref="SessionByteExecutorTests"/> cover the same rejections but assert only the
/// returned STATUS, so they passed the whole time the log line was missing. Asserting the status is not
/// asserting the record; these tests assert the record.
///
/// They are written to FAIL against the pre-fix code: each one requires a "REJECTED" line that simply did
/// not exist before, and each also asserts the line carries the REASON, so a bare
/// "[SessionByteExecutor] rejected" would not satisfy them either.
///
/// In the "DirectorRoot" collection because the upload verbs resolve the screenshots folder through
/// CC_DIRECTOR_ROOT, and that collection serializes root-touching tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionByteExecutorRejectionLoggingTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A tiny valid 1x1 PNG, so a rejection can only come from the guard under test and never from the bytes.
    private static readonly byte[] OnePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root;
    private readonly string? _prevRoot;
    private string _shotsDir = null!;

    public SessionByteExecutorRejectionLoggingTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-reject-log-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _shotsDir = Path.Combine(_root, "shots");
        Directory.CreateDirectory(_shotsDir);
        Directory.CreateDirectory(CcStorage.Config());
        await File.WriteAllTextAsync(CcStorage.ConfigJson(),
            JsonSerializer.Serialize(new { screenshots = new { source_directory = _shotsDir } }));
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static (SessionManager, Session) NewSession()
    {
        var sm = new SessionManager(new AgentOptions());
        var session = sm.CreateSession(Path.GetTempPath());
        return (sm, session);
    }

    private static DirectorCommand Command(string verb, string sid, object payload) => new()
    {
        CommandId = "rl1",
        Verb = verb,
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(payload, Json),
    };

    /// <summary>Dispatch one command with FileLog captured, and return the result plus the lines written.</summary>
    private static async Task<(DirectorCommandResult Result, IReadOnlyList<string> Lines)> DispatchCapturing(
        SessionManager sm, DirectorCommand command)
    {
        using var capture = CcDirector.Core.Utilities.FileLog.RedirectForTests();
        var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);
        return (result, capture.DrainAndReadLines());
    }

    private static void AssertRejectionLogged(
        IReadOnlyList<string> lines, string verb, params string[] reasonFragments)
    {
        var rejections = lines
            .Where(l => l.Contains("[SessionByteExecutor]", StringComparison.Ordinal)
                     && l.Contains("REJECTED", StringComparison.Ordinal)
                     && l.Contains(verb, StringComparison.Ordinal))
            .ToList();

        Assert.True(rejections.Count > 0,
            $"expected a logged rejection for '{verb}'. Lines written:{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines));

        // The line must carry the REASON, not merely the fact of a rejection. A record that says something
        // failed without saying what is the same dead end as the bare status code this work removed.
        foreach (var fragment in reasonFragments)
        {
            Assert.Contains(fragment, string.Join(Environment.NewLine, rejections), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- upload-image-begin ----

    [Fact]
    public async Task UploadImageBegin_UnsupportedExtension_LogsTheRejectionWithTheAcceptedList()
    {
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-begin", session.Id.ToString(),
                new UploadImageBeginRequest { UploadId = "u1", FileName = "diagram.svg", TotalBytes = 100 }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            // The user chose this file, so the record must name the extension AND what would have worked.
            AssertRejectionLogged(lines, "upload-image-begin", "unsupported image type", ".svg", ".png");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task UploadImageBegin_OversizeDeclaredTotal_LogsTheRejectionWithBothNumbers()
    {
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-begin", session.Id.ToString(),
                new UploadImageBeginRequest { UploadId = "u2", FileName = "photo.jpg", TotalBytes = 40L * 1024 * 1024 }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image-begin", "out of range", "41943040");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task UploadImageBegin_UnknownSession_LogsTheRejection()
    {
        var sm = new SessionManager(new AgentOptions());
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-begin", Guid.NewGuid().ToString(),
                new UploadImageBeginRequest { UploadId = "u3", FileName = "photo.png", TotalBytes = 100 }));

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
            AssertRejectionLogged(lines, "upload-image-begin", "session not found");
        }
        finally { sm.Dispose(); }
    }

    // ---------------------------------------------------------------- upload-image-chunk ----

    [Fact]
    public async Task UploadImageChunk_UnknownUploadId_LogsTheRejection()
    {
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-chunk", session.Id.ToString(),
                new UploadImageChunkRequest { UploadId = "never-opened", Seq = 0, BytesBase64 = "AAAA" }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image-chunk", "unknown or expired upload id");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task UploadImageChunk_OutOfOrder_LogsTheRejectionWithBothSequenceNumbers()
    {
        var (sm, session) = NewSession();
        try
        {
            var sid = session.Id.ToString();
            // Open a real upload first, so the rejection under test is the ordering guard and nothing else.
            var begin = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("upload-image-begin", sid,
                new UploadImageBeginRequest { UploadId = "u4", FileName = "photo.png", TotalBytes = OnePngBytes.Length }));
            Assert.Equal(DirectorCommandStatus.Ok, begin.Status);

            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-chunk", sid,
                new UploadImageChunkRequest { UploadId = "u4", Seq = 5, BytesBase64 = Convert.ToBase64String(OnePngBytes) }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image-chunk", "out-of-order chunk", "expected 0", "got 5");
        }
        finally { sm.Dispose(); }
    }

    // ---------------------------------------------------------------- upload-image-complete ----

    [Fact]
    public async Task UploadImageComplete_ByteCountMismatch_LogsTheRejectionWithBothCounts()
    {
        var (sm, session) = NewSession();
        try
        {
            var sid = session.Id.ToString();
            // Declare more bytes than are ever sent, then complete: the classic truncated upload.
            var begin = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("upload-image-begin", sid,
                new UploadImageBeginRequest { UploadId = "u5", FileName = "photo.png", TotalBytes = OnePngBytes.Length + 500 }));
            Assert.Equal(DirectorCommandStatus.Ok, begin.Status);
            var chunk = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Command("upload-image-chunk", sid,
                new UploadImageChunkRequest { UploadId = "u5", Seq = 0, BytesBase64 = Convert.ToBase64String(OnePngBytes) }));
            Assert.Equal(DirectorCommandStatus.Ok, chunk.Status);

            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-complete", sid,
                new UploadImageCompleteRequest { UploadId = "u5" }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image-complete", "incomplete upload",
                OnePngBytes.Length.ToString(), (OnePngBytes.Length + 500).ToString());
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task UploadImageComplete_UnknownUploadId_LogsTheRejection()
    {
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image-complete", session.Id.ToString(),
                new UploadImageCompleteRequest { UploadId = "never-opened" }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image-complete", "unknown or expired upload id");
        }
        finally { sm.Dispose(); }
    }

    // ---------------------------------------------------------------- the single-shot verb ----

    [Fact]
    public async Task UploadImage_UnsupportedExtension_LogsTheRejection()
    {
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image", session.Id.ToString(),
                new UploadImageRequest { FileName = "notes.txt", BytesBase64 = Convert.ToBase64String(OnePngBytes) }));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            AssertRejectionLogged(lines, "upload-image", "unsupported image type", ".txt");
        }
        finally { sm.Dispose(); }
    }

    // ---------------------------------------------------------------- the positive control ----

    [Fact]
    public async Task UploadImage_Success_LogsNoRejection()
    {
        // The other arm. Without it, an AssertRejectionLogged helper that matched too loosely - or a change
        // that logged "REJECTED" on every dispatch - would leave every test above green while saying nothing.
        var (sm, session) = NewSession();
        try
        {
            var (result, lines) = await DispatchCapturing(sm, Command("upload-image", session.Id.ToString(),
                new UploadImageRequest { FileName = "photo.png", BytesBase64 = Convert.ToBase64String(OnePngBytes) }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.DoesNotContain(lines, l => l.Contains("REJECTED", StringComparison.Ordinal));
            // And the success record is still there, so this is not passing because logging is off entirely.
            Assert.Contains(lines, l => l.Contains("upload-image saved", StringComparison.Ordinal));
        }
        finally { sm.Dispose(); }
    }
}
