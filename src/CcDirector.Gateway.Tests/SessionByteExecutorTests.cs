using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0: parity unit tests for the unary BYTE verbs the
/// <see cref="SessionByteExecutor"/> owns - <c>screenshots-list</c> (a directory read) and
/// <c>upload-image</c> (bytes down the payload, filed to disk). Each verb is exercised through the real
/// <see cref="SessionCommandExecutor.DispatchAsync"/> path (the same entry the Gateway tunnel uses), so the
/// tunnel verb and the REST route that now dispatches it cannot drift. The screenshots folder is pinned
/// (via CC_DIRECTOR_ROOT + config.json) into a temp dir so nothing touches the user's real
/// Pictures\Screenshots folder. In the "DirectorRoot" collection (serializes root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionByteExecutorTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A tiny valid 1x1 PNG (decodes cleanly), used as the image bytes.
    private static readonly byte[] OnePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root;
    private readonly string? _prevRoot;
    private string _shotsDir = null!;

    public SessionByteExecutorTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-byte-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Pin the screenshots folder into the temp root via config.json so CcStorage.Screenshots()
        // resolves to it instead of the real Pictures\Screenshots.
        _shotsDir = Path.Combine(_root, "shots");
        Directory.CreateDirectory(_shotsDir);
        var configDir = CcStorage.Config();
        Directory.CreateDirectory(configDir);
        var json = JsonSerializer.Serialize(new
        {
            screenshots = new { source_directory = _shotsDir },
        });
        await File.WriteAllTextAsync(CcStorage.ConfigJson(), json);
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session);
    }

    // ---------- screenshots-list ----------

    private void SeedScreenshot(string fileName, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_shotsDir, fileName);
        File.WriteAllBytes(path, OnePngBytes);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    private static DirectorCommand ScreenshotsListCommand(int? count) => new()
    {
        CommandId = "sl1",
        Verb = "screenshots-list",
        PayloadJson = JsonSerializer.Serialize(new ScreenshotListRequest { Count = count }, Json),
    };

    [Fact]
    public async Task DispatchAsync_ScreenshotsList_ReturnsOkWithNewestFirstItems()
    {
        SeedScreenshot("shot-older.png", DateTime.UtcNow.AddMinutes(-10));
        SeedScreenshot("shot-newer.png", DateTime.UtcNow);
        // A non-image file that must be ignored by the listing.
        await File.WriteAllTextAsync(Path.Combine(_shotsDir, "notes.txt"), "ignore me");

        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ScreenshotsListCommand(null));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("sl1", result.CommandId);

            var resp = JsonSerializer.Deserialize<ScreenshotListResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(_shotsDir, resp!.Directory);
            Assert.Equal(2, resp.Total);                              // notes.txt excluded
            Assert.Equal(2, resp.Items.Count);
            Assert.Equal("shot-newer.png", resp.Items[0].FileName);   // newest first
            Assert.Equal("shot-older.png", resp.Items[1].FileName);
            Assert.Equal(Path.Combine(_shotsDir, "shot-newer.png"), resp.Items[0].Path);
            Assert.All(resp.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.TimeLabel)));
            Assert.All(resp.Items, i => Assert.True(i.SizeBytes > 0));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ScreenshotsList_CountCapsItemsButTotalReportsFolder()
    {
        SeedScreenshot("shot-older.png", DateTime.UtcNow.AddMinutes(-10));
        SeedScreenshot("shot-newer.png", DateTime.UtcNow);

        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ScreenshotsListCommand(1));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<ScreenshotListResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Single(resp!.Items);                               // capped to the newest one
            Assert.Equal("shot-newer.png", resp.Items[0].FileName);
            Assert.Equal(2, resp.Total);                              // folder count, not the cap
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ScreenshotsList_EmptyFolder_ReturnsOkWithNoItems()
    {
        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ScreenshotsListCommand(null));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var resp = JsonSerializer.Deserialize<ScreenshotListResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal(0, resp!.Total);
            Assert.Empty(resp.Items);
        }
        finally { sm.Dispose(); }
    }

    // ---------- upload-image ----------

    private static DirectorCommand UploadImageCommand(string sid, string fileName, string bytesBase64) => new()
    {
        CommandId = "ui1",
        Verb = "upload-image",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new UploadImageRequest { FileName = fileName, BytesBase64 = bytesBase64 }, Json),
    };

    [Fact]
    public async Task DispatchAsync_UploadImage_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand("not-a-guid", "photo.png", Convert.ToBase64String(OnePngBytes)));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UploadImage_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand(Guid.NewGuid().ToString(), "photo.png", Convert.ToBase64String(OnePngBytes)));

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UploadImage_SavesBytesToScreenshotsFolderAndReturnsPath()
    {
        var (sm, session) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand(session.Id.ToString(), "photo.png", Convert.ToBase64String(OnePngBytes)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("ui1", result.CommandId);

            var resp = JsonSerializer.Deserialize<UploadImageResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            // Saved under a timestamped name that keeps the uploaded .png extension, inside the pinned folder.
            Assert.StartsWith("upload-", resp!.FileName);
            Assert.EndsWith(".png", resp.FileName);
            Assert.Equal(Path.Combine(_shotsDir, resp.FileName), resp.Path);
            Assert.True(File.Exists(resp.Path));
            Assert.Equal(OnePngBytes, await File.ReadAllBytesAsync(resp.Path));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UploadImage_UnsupportedExtension_ReturnsBadRequest()
    {
        var (sm, session) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand(session.Id.ToString(), "notes.txt", Convert.ToBase64String(OnePngBytes)));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UploadImage_EmptyBytes_ReturnsBadRequest()
    {
        var (sm, session) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand(session.Id.ToString(), "photo.png", ""));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UploadImage_NotBase64_ReturnsBadRequest()
    {
        var (sm, session) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                UploadImageCommand(session.Id.ToString(), "photo.png", "not valid base64 !!!"));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }
}
