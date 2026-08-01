using System.Net.Http.Headers;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// An HTTP client for a test <see cref="ControlApiHost"/>, carrying the credential the Control API
/// now requires on every route but <c>/healthz</c>.
///
/// It exists so the reason is written once rather than at each of the places that needed it. These
/// tests are about endpoint behaviour, not about authentication - but they run against the real
/// host, and the real host is the one a shipped install runs. Giving them the production posture
/// (authentication on, a valid credential presented) is strictly better coverage than switching
/// authentication off to keep them quiet, which would leave the whole surface exercised only in a
/// configuration nobody ships.
///
/// The secret is resolved exactly as the host resolves it - the shared fleet token when this
/// machine's config.json carries one, otherwise the storage root's own token file. A test class that
/// pins CC_DIRECTOR_ROOT to a temp directory therefore agrees with its host by construction; one
/// that does not will agree with the real machine, which is also what its host did.
/// </summary>
internal static class DirectorTestClient
{
    /// <summary>The machine secret the host under test accepts.</summary>
    public static string RootSecret() => DirectorAuth.ResolveAcceptedToken(GatewayConfig.Load().Token);

    /// <summary>A client for this port holding full authority (the admin scope).</summary>
    public static HttpClient Admin(int port) => WithToken(port, DirectorScopedToken.Mint(RootSecret(), ScopeNames.Admin));

    /// <summary>A client for this port holding a session-child credential bound to one session.</summary>
    public static HttpClient Child(int port, Guid sessionId)
        => WithToken(port, DirectorScopedToken.Mint(RootSecret(), ScopeNames.SessionChild, sessionId));

    private static HttpClient WithToken(int port, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
