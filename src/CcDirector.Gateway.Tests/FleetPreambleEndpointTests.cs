using System.Net;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1357: HTTP-level proof of the Director's <c>GET /sessions/{sid}/fleet-preamble</c> endpoint.
/// It maps the real <see cref="ControlEndpoints"/> over a minimal Kestrel host with an adopted idle
/// session and a stub signed-in-user resolver, then reads the preamble over the wire - proving the
/// resolver is wired into the endpoint and the identity line appears (or is omitted) exactly as built.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetPreambleEndpointTests
{
    private static Session MakeIdleSession()
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\preamble-endpoint-test",
            workingDirectory: @"C:\test\preamble-endpoint-test",
            claudeArgs: null,
            backend: new StubBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "preamble-endpoint-test",
            customColor: null);
        session.MarkRunning();
        return session;
    }

    private static async Task<(WebApplication app, HttpClient http, Guid sessionId)> StartAsync(
        Func<CancellationToken, Task<SignedInUser?>>? resolver)
    {
        var sm = new SessionManager(new AgentOptions());
        var session = MakeIdleSession();
        sm.AdoptSession(session);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        ControlEndpoints.Map(app, sm, "dir-preamble-test", "1.0.0-test", () => Task.CompletedTask,
            signedInUserResolver: resolver);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http, session.Id);
    }

    [Fact]
    public async Task Preamble_WithSignedInUser_ContainsIdentityLine()
    {
        var (app, http, sid) = await StartAsync(
            _ => Task.FromResult<SignedInUser?>(new SignedInUser("star@example.com", "Starlord")));
        try
        {
            var resp = await http.GetAsync($"/sessions/{sid}/fleet-preamble");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("The user of this session is Starlord (star@example.com).", text);
            Assert.Contains("do not guess identity from usage or the database", text);
            // The base preamble is still present.
            Assert.Contains("cc-devthrottle", text);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Preamble_NoResolver_OmitsIdentityLine()
    {
        var (app, http, sid) = await StartAsync(resolver: null);
        try
        {
            var resp = await http.GetAsync($"/sessions/{sid}/fleet-preamble");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var text = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("The user of this session is", text);
            Assert.Contains("cc-devthrottle", text); // base preamble intact
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class StubBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Stub";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
