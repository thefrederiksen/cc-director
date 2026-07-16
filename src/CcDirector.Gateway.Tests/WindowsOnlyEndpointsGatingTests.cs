using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Two Gateway endpoints only make sense on a Windows dev or desktop machine and are gated off every
/// other platform (the port to a headless Linux and macOS Gateway):
///   - the whole /exes surface builds developer slot exes by shelling out to
///     powershell.exe scripts/local-build-avalonia.ps1, which exists only on a Windows dev box;
///   - POST /directors launches the Windows desktop cc-director.exe via ShellExecute.
/// Off Windows these routes are simply not mapped. These facts assert the absence off Windows; they
/// no-op on Windows, where the routes are present exactly as before (invoking them there has real
/// side effects - launching processes - so this test never calls them on Windows). The macOS and
/// Linux runs are where the absence is actually proven.
/// </summary>
public sealed class WindowsOnlyEndpointsGatingTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-gate-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task ExesSurface_IsAbsentOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        // /exes/list has no non-Windows mapping at all, so an unmapped path answers 404.
        var resp = await _http.GetAsync("/exes/list");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task LaunchDirectorPost_IsAbsentOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        // GET /directors still exists (the roster), so the gated-away POST answers 405 Method Not
        // Allowed; a host that dropped the path entirely would answer 404. Either proves the launch
        // action is unreachable off Windows - what it must never be is a 2xx that started a process.
        var resp = await _http.PostAsync("/directors", content: null);
        Assert.True(
            resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"expected 405 or 404 for POST /directors off Windows, got {(int)resp.StatusCode}");
    }

    private static int FreePort()
    {
        using var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
