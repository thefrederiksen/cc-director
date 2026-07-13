using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): the Director's screenshots-gallery HTTP routes
/// (GET /screenshots, GET /screenshots/file, DELETE /screenshots/file) are DELETED. The list,
/// delete, and traversal-safe name resolution now live in the shared command cores the tunnel
/// verbs dispatch to - <see cref="SessionByteExecutor.ScreenshotsList"/> (the "screenshots-list"
/// verb), <see cref="SessionByteExecutor.ScreenshotDelete"/> (the "screenshot-delete" verb) and
/// <see cref="ControlEndpoints.ResolveScreenshot"/> (the shared traversal-safe resolver). The
/// Gateway forwards these verbs over the tunnel (proven end to end in SessionWsProxyEndpointsTests);
/// this file pins the DIRECTOR-SIDE listing/delete LOGIC - newest-first ordering, the count cap
/// vs. full-folder total, image-only filtering, the time label, and traversal-safe delete - by
/// driving those cores directly against a real on-disk screenshots folder.
///
/// The screenshots folder is pinned (via config.json under an isolated CC_DIRECTOR_ROOT) into a
/// temp dir, so nothing touches the user's real Pictures\Screenshots folder. In the "DirectorRoot"
/// collection (serializes root-touching tests).
///
/// The screenshot-BYTES leg (the old GET /screenshots/file, with its image/png content type and the
/// CORS + cache-control headers) is NOT a unary verb: it rides the up-stream producer over the
/// tunnel now, exercised end to end in the whole-surface real-exe proof. There is no faithful unary
/// rewrite for it here, so its test is removed with the deleted HTTP route rather than faked.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ScreenshotEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;
    private string _shotsDir = null!;

    // A tiny valid 1x1 PNG (decodes cleanly), used as the seeded screenshot bytes.
    private static readonly byte[] OnePngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public ScreenshotEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-shots-test-" + Guid.NewGuid().ToString("N"));
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

        // Two seeded screenshots with distinct write times so newest-first ordering is testable.
        var older = Path.Combine(_shotsDir, "shot-older.png");
        var newer = Path.Combine(_shotsDir, "shot-newer.png");
        await File.WriteAllBytesAsync(older, OnePngBytes);
        await File.WriteAllBytesAsync(newer, OnePngBytes);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        // A non-image file that must be ignored by the listing.
        await File.WriteAllTextAsync(Path.Combine(_shotsDir, "notes.txt"), "ignore me");
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ---- helpers: drive the shared command cores the tunnel verbs dispatch to ----

    private static ScreenshotListResponse List(int? count)
    {
        var payload = count is null ? "" : JsonSerializer.Serialize(new ScreenshotListRequest { Count = count }, Web);
        var result = SessionByteExecutor.ScreenshotsList(new DirectorCommand { Verb = "screenshots-list", PayloadJson = payload });
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var body = JsonSerializer.Deserialize<ScreenshotListResponse>(result.BodyJson!, Web);
        Assert.NotNull(body);
        return body!;
    }

    private static DirectorCommandResult Delete(string name) =>
        SessionByteExecutor.ScreenshotDelete(new DirectorCommand
        {
            Verb = "screenshot-delete",
            PayloadJson = JsonSerializer.Serialize(new ScreenshotDeleteRequest { Name = name }, Web),
        });

    [Fact]
    public void List_returns_only_images_newest_first_with_labels()
    {
        var list = List(null);
        Assert.Equal(2, list.Items.Count);                       // notes.txt excluded
        Assert.Equal(2, list.Total);
        Assert.Equal("shot-newer.png", list.Items[0].FileName);  // newest first
        Assert.Equal("shot-older.png", list.Items[1].FileName);
        Assert.All(list.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.TimeLabel)));
        Assert.All(list.Items, i => Assert.True(i.SizeBytes > 0));
        // Each item carries its absolute on-disk path (injected into the composer on tap).
        Assert.Equal(Path.Combine(_shotsDir, "shot-newer.png"), list.Items[0].Path);
    }

    [Fact]
    public void List_count_caps_items_but_total_reports_the_folder()
    {
        var list = List(1);
        Assert.Single(list.Items);                               // capped to the newest one
        Assert.Equal("shot-newer.png", list.Items[0].FileName);
        Assert.Equal(2, list.Total);                             // folder count, not the cap
    }

    [Theory]
    [InlineData(0)]     // <=0 falls back to the default cap
    [InlineData(-5)]
    [InlineData(999)]   // cap above the folder size
    public void List_count_edge_values_are_safe(int count)
    {
        var list = List(count);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.Total);
    }

    [Fact]
    public async Task List_omitted_count_caps_at_default_and_ignores_older_files()
    {
        // Seed enough images to exceed the default cap; all OLDER than the two fixtures so
        // the newest-first cap keeps the fixtures and drops the tail.
        var extra = ControlEndpoints.DefaultScreenshotCount + 3;
        for (var i = 0; i < extra; i++)
        {
            var p = Path.Combine(_shotsDir, $"bulk-{i:D3}.png");
            await File.WriteAllBytesAsync(p, OnePngBytes);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-60 - i));
        }

        var list = List(null);
        Assert.Equal(ControlEndpoints.DefaultScreenshotCount, list.Items.Count); // capped
        Assert.Equal(extra + 2, list.Total);                                     // full folder count
        Assert.Equal("shot-newer.png", list.Items[0].FileName);                  // newest still first
    }

    [Fact]
    public void File_resolver_returns_null_for_unknown_name()
    {
        // The old GET /screenshots/file 404 came from the shared traversal-safe resolver returning
        // null; that resolver still guards the delete verb, so it is the piece worth pinning.
        Assert.Null(ControlEndpoints.ResolveScreenshot("nope.png"));
    }

    [Theory]
    [InlineData("../../secret.png")]   // parent traversal
    [InlineData("sub/shot.png")]        // subdir
    [InlineData("notes.txt")]           // non-image extension
    public void File_resolver_rejects_traversal_and_non_images(string name)
    {
        Assert.Null(ControlEndpoints.ResolveScreenshot(name));
    }

    [Fact]
    public void Delete_removes_the_file_off_disk()
    {
        var result = Delete("shot-older.png");
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.False(File.Exists(Path.Combine(_shotsDir, "shot-older.png")));

        var list = List(null);
        Assert.Single(list.Items);
        Assert.Equal("shot-newer.png", list.Items[0].FileName);
    }

    [Fact]
    public void Delete_returns_not_found_for_unknown_name()
    {
        var result = Delete("ghost.png");
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }
}
