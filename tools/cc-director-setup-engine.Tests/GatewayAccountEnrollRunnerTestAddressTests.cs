using System.Net;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for <see cref="GatewayAccountEnrollRunner.TestGatewayAddressAsync"/> - the installer's mandatory
/// reachability Test gate (issue #1233). The person enters a computer name plus port (or pastes a full
/// address), hits Test, and the install may only proceed once the gateway actually answers GET /healthz.
/// A fake handler stands in for the gateway, so no live gateway is needed. Neither sign-in nor persist may
/// run for a reachability test (it happens before the person commits), which the injected throwers assert.
/// </summary>
public class GatewayAccountEnrollRunnerTestAddressTests
{
    private sealed class HealthzHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly bool _throw;
        public string? LastPath;

        public HealthzHandler(HttpStatusCode status, bool @throw = false)
        {
            _status = status;
            _throw = @throw;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            if (_throw) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private static GatewayAccountEnrollRunner Runner(HttpMessageHandler handler) =>
        new(signIn: _ => throw new InvalidOperationException("sign-in must not run for a reachability test"),
            handlerFactory: () => handler,
            persist: (_, _) => throw new InvalidOperationException("persist must not run for a reachability test"));

    [Fact]
    public async Task TestGatewayAddressAsync_ComputerNameAndPort_Reachable_ReturnsBuiltUrl()
    {
        var handler = new HealthzHandler(HttpStatusCode.OK);
        var runner = Runner(handler);

        var result = await runner.TestGatewayAddressAsync("SOREN-NORTH", 7878, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("http://SOREN-NORTH:7878", result.Value);
        Assert.Equal("/healthz", handler.LastPath);      // it really probed /healthz
    }

    [Fact]
    public async Task TestGatewayAddressAsync_FullPastedHttpsUrl_IsNormalizedAndProbed()
    {
        var handler = new HealthzHandler(HttpStatusCode.OK);
        var runner = Runner(handler);

        // A pasted Tailscale https address: the port argument is ignored; the address is used as-is.
        var result = await runner.TestGatewayAddressAsync("https://soren-north.tail1234.ts.net:7878", 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://soren-north.tail1234.ts.net:7878", result.Value);
    }

    [Fact]
    public async Task TestGatewayAddressAsync_GatewayAnswersNon2xx_Fails()
    {
        var handler = new HealthzHandler(HttpStatusCode.ServiceUnavailable);
        var runner = Runner(handler);

        var result = await runner.TestGatewayAddressAsync("MACHINE", 7878, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not reach", result.ErrorMessage);
    }

    [Fact]
    public async Task TestGatewayAddressAsync_TransportFails_Fails()
    {
        var handler = new HealthzHandler(HttpStatusCode.OK, @throw: true);
        var runner = Runner(handler);

        var result = await runner.TestGatewayAddressAsync("MACHINE", 7878, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("", 7878)]
    [InlineData("MACHINE", 0)]
    [InlineData("MACHINE", 70000)]
    [InlineData("has space", 7878)]
    public async Task TestGatewayAddressAsync_InvalidInput_FailsWithoutProbing(string name, int port)
    {
        var handler = new HealthzHandler(HttpStatusCode.OK);
        var runner = Runner(handler);

        var result = await runner.TestGatewayAddressAsync(name, port, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(handler.LastPath);                   // rejected before any network call
    }
}
