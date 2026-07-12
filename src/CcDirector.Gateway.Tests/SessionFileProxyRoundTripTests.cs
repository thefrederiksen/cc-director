using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Local Files mission (Phase 1) round-trip proof, modeled on
/// <see cref="ScreenshotProxyRoundTripTests"/>: a remote
/// <c>GET /sessions/{sid}/file?path=&lt;absolute path&gt;</c> on the Gateway is carried by the
/// generic per-session catch-all to the owning Director's <c>GET /sessions/{sid}/file</c> at the
/// same path, and the Director streams the file's bytes + content type back through unchanged. A
/// stub Director (real Kestrel host) implements just the two endpoints the proxy touches:
/// <c>GET /sessions/{sid}</c> (ownership resolution) and <c>GET /sessions/{sid}/file</c> (the bytes),
/// the latter replicating the real Director handler (content-type map, nosniff, range support). The
/// test proves an image and a text file BOTH come back byte-identical with the right content type,
/// that the URL-escaped path arrives decoded, and that a missing path passes the Director 404 through.
/// </summary>
public sealed class SessionFileProxyRoundTripTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private StubDirector _director = null!;

    // A tiny but real PNG header + payload and a text file. Byte-identity is what matters.
    private static readonly byte[] PngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0xDE, 0xAD, 0xBE, 0xEF, 0x13, 0x37, 0x42, 0x42,
    };
    private const string TextContent = "line one\nline two\nthe quick brown fox\n";

    // A "large-ish" binary blob so the range test proves the seek-and-resume guarantee for big
    // PDFs/images (Phase 4): each byte i holds (i * 31 + 7) mod 256, so any slice is independently
    // verifiable against the same formula without holding a second copy.
    private const int LargeSize = 300_000;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));
    private readonly string _filesDir =
        Path.Combine(Path.GetTempPath(), "cc-localfiles-" + Guid.NewGuid().ToString("N"));

    private string _pngPath = "";
    private string _textPath = "";
    private string _largePath = "";

    private static byte LargeByteAt(int i) => (byte)((i * 31 + 7) % 256);

    public async Task InitializeAsync()
    {
        // Real files on disk with a SPACE in the name, so the ?path= URL-escaping is exercised.
        Directory.CreateDirectory(_filesDir);
        _pngPath = Path.Combine(_filesDir, "a report.png");
        _textPath = Path.Combine(_filesDir, "notes 1.txt");
        _largePath = Path.Combine(_filesDir, "big scan.pdf");
        await File.WriteAllBytesAsync(_pngPath, PngBytes);
        await File.WriteAllTextAsync(_textPath, TextContent);

        var large = new byte[LargeSize];
        for (var i = 0; i < LargeSize; i++) large[i] = LargeByteAt(i);
        await File.WriteAllBytesAsync(_largePath, large);

        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        _director = new StubDirector(sessionId: "file-session-1");
        await _director.StartAsync();

        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4243,
            // Loopback stub on THIS machine -> register as same-machine (issue #457 refuses a
            // loopback endpoint advertised for a different machine).
            MachineName = Environment.MachineName,
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
        try { if (Directory.Exists(_filesDir)) Directory.Delete(_filesDir, true); }
        catch { }
    }

    [Fact]
    public async Task Image_bytes_round_trip_through_the_gateway_with_content_type()
    {
        var resp = await _http.GetAsync(
            $"sessions/file-session-1/file?path={Uri.EscapeDataString(_pngPath)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentType);
        Assert.Equal("image/png", resp.Content.Headers.ContentType!.MediaType);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(PngBytes, bytes);

        // The escaped path (it has a space) reached the Director decoded, intact.
        Assert.Equal(_pngPath, _director.LastRequestedPath);
        // The safety header set by the Director survives the proxy.
        Assert.True(resp.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", string.Join("", resp.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Text_bytes_round_trip_through_the_gateway_with_content_type()
    {
        var resp = await _http.GetAsync(
            $"sessions/file-session-1/file?path={Uri.EscapeDataString(_textPath)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentType);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType!.MediaType);

        var text = await resp.Content.ReadAsStringAsync();
        Assert.Equal(TextContent, text);
        Assert.Equal(_textPath, _director.LastRequestedPath);
    }

    [Fact]
    public async Task Missing_file_on_a_live_owner_passes_the_director_404_through()
    {
        var missing = Path.Combine(_filesDir, "does-not-exist.png");
        var resp = await _http.GetAsync(
            $"sessions/file-session-1/file?path={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // The large-file guarantee (Phase 4): a Range request must come back 206 Partial Content with only
    // the requested slice - byte-identical to that slice of the original - and a Content-Range whose
    // total is the full file size. This is what lets a big PDF/image seek and resume in the browser's
    // native viewer, and it is also the mechanism the download panel's fetchSessionFileSize relies on
    // (Range: bytes=0-0 -> the total is read from Content-Range). Proven through the Gateway proxy, so
    // the range header and the 206 both survive the round trip.
    [Fact]
    public async Task Range_request_returns_206_with_the_exact_slice_through_the_gateway()
    {
        const int from = 100_000;
        const int to = 100_255; // 256 bytes, inclusive on both ends (HTTP byte ranges)
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"sessions/file-session-1/file?path={Uri.EscapeDataString(_largePath)}");
        req.Headers.Range = new RangeHeaderValue(from, to);

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);

        // The slice is exactly the requested bytes, in the right place, byte-for-byte.
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(to - from + 1, slice.Length);
        for (var i = 0; i < slice.Length; i++)
            Assert.Equal(LargeByteAt(from + i), slice[i]);

        // Content-Range reports this slice out of the full size, so a client can compute total length.
        Assert.NotNull(resp.Content.Headers.ContentRange);
        Assert.Equal(from, resp.Content.Headers.ContentRange!.From);
        Assert.Equal(to, resp.Content.Headers.ContentRange.To);
        Assert.Equal(LargeSize, resp.Content.Headers.ContentRange.Length);
    }

    // The download panel's size probe (fetchSessionFileSize) in miniature: a one-byte Range: bytes=0-0
    // yields 206 and the FULL size in Content-Range's total, so the panel can show a real size without
    // downloading the file. This locks that exact contract at the proxy level.
    [Fact]
    public async Task Single_byte_range_reports_the_full_size_in_content_range()
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"sessions/file-session-1/file?path={Uri.EscapeDataString(_largePath)}");
        req.Headers.Range = new RangeHeaderValue(0, 0);

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Single(slice);
        Assert.Equal(LargeByteAt(0), slice[0]);
        Assert.NotNull(resp.Content.Headers.ContentRange);
        Assert.Equal(LargeSize, resp.Content.Headers.ContentRange!.Length);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>
    /// Minimal Kestrel host pretending to be a Director's Control API: owns exactly one session (so
    /// the Gateway's ownership fan-out resolves it) and serves any local file by absolute path,
    /// replicating the real <c>GET /sessions/{sid}/file</c> handler - the shared content-type map,
    /// the nosniff header, and range support - so the test proves the real behavior end to end.
    /// </summary>
    private sealed class StubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";
        public string? LastRequestedPath { get; private set; }

        private readonly string _sessionId;
        private WebApplication? _app;

        public StubDirector(string sessionId) => _sessionId = sessionId;

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "StubDirector",
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();
            _app.MapGet("/sessions/{sid}", (string sid) =>
                sid == _sessionId
                    ? Results.Json(new SessionDto { SessionId = _sessionId, Agent = "ClaudeCode", ActivityState = "Idle", StatusColor = "green" })
                    : Results.NotFound());
            _app.MapGet("/sessions/{sid}/file", (HttpContext ctx, string sid, string? path) =>
            {
                LastRequestedPath = path;
                if (string.IsNullOrWhiteSpace(path))
                    return Results.BadRequest(new { error = "path is required" });
                if (!File.Exists(path))
                    return Results.NotFound(new { error = "file not found: " + path });
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(path, ContentType(path), enableRangeProcessing: true);
            });

            await _app.StartAsync();
        }

        // The same extension -> content-type map the real Director uses (kept in step for the proof).
        private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".pdf"            => "application/pdf",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".svg"            => "image/svg+xml",
            ".css"            => "text/css; charset=utf-8",
            ".js"             => "text/javascript; charset=utf-8",
            ".json"           => "application/json; charset=utf-8",
            ".csv"            => "text/csv; charset=utf-8",
            ".md" or ".txt" or ".log" => "text/plain; charset=utf-8",
            _                 => "application/octet-stream",
        };

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
