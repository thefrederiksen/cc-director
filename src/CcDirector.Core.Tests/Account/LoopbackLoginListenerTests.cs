using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the loopback listener that captures the credential the sign-in completion hands back (issue
/// #581), hardened so no token rides in the callback URL (issue #1082, absorbs #877). It must bind
/// loopback only (security rule DT-07), capture both tokens from the NEW shape (a same-origin JSON body
/// the fragment hand-back page posts), still capture the OLD shape (the token pair in the query string)
/// during transition, serve the hand-back page on a bare GET, and fail loud (no fallback) on a callback
/// that yields the credential in neither shape.
/// </summary>
public sealed class LoopbackLoginListenerTests
{
    // The listener binds 127.0.0.1 only - never a routable address (DT-07).
    [Fact]
    public void CallbackUrl_IsLoopbackOnly()
    {
        using var listener = new LoopbackLoginListener();

        Assert.Equal("127.0.0.1", listener.CallbackUrl.Host);
        Assert.True(listener.CallbackUrl.Port > 0);
    }

    // NEW SHAPE (issue #1082): the credential arrives as a same-origin JSON body the hand-back page posts
    // (the token pair was in the URL fragment, never in the callback URL). Both tokens are captured.
    [Fact]
    public async Task WaitForCredentialAsync_CapturesBothTokensFromPostedBody()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostBodyCallbackAsync(listener.CallbackUrl, "access-xyz", "refresh-abc");
        var tokens = await wait;

        Assert.Equal("access-xyz", tokens.AccessToken);
        Assert.Equal("refresh-abc", tokens.RefreshToken);
    }

    // NEW SHAPE, full two-step flow: a bare GET (the browser landing with the pair in the fragment) is served
    // the hand-back page, then the same-origin POST the page makes captures the credential.
    [Fact]
    public async Task WaitForCredentialAsync_ServesHandbackPageThenCapturesPostedBody()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();

        using var http = new HttpClient();
        var page = await GetWithRetryAsync(http, listener.CallbackUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageHtml = await page.Content.ReadAsStringAsync();
        // The served page is the fragment hand-back page (carries the reader script), and NO token.
        Assert.Contains("Completing sign-in", pageHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("location.hash", pageHtml, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted); // still waiting for the POST the page makes

        await PostBodyCallbackAsync(listener.CallbackUrl, "access-2", "refresh-2");
        var tokens = await wait;

        Assert.Equal("access-2", tokens.AccessToken);
        Assert.Equal("refresh-2", tokens.RefreshToken);
    }

    // NEW SHAPE failure: a posted body missing a token fails loud (no half-credential is captured).
    [Fact]
    public async Task WaitForCredentialAsync_PostedBodyMissingRefreshToken_Throws()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostBodyCallbackAsync(listener.CallbackUrl, "access-only", refreshToken: null, expectSuccess: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
    }

    // TRANSITION (old shape): the token pair carried in the callback URL query string is still captured so
    // sign-in keeps working until the cloud completion migrates to the fragment shape.
    [Fact]
    public async Task WaitForCredentialAsync_CapturesBothTokensFromQueryString_Transition()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostCallbackAsync(listener.CallbackUrl, "access-xyz", "refresh-abc");
        var tokens = await wait;

        Assert.Equal("access-xyz", tokens.AccessToken);
        Assert.Equal("refresh-abc", tokens.RefreshToken);
    }

    // A query-string callback missing the refresh token fails loud (no half-credential is captured).
    [Fact]
    public async Task WaitForCredentialAsync_MissingRefreshToken_Throws()
    {
        using var listener = new LoopbackLoginListener();

        var wait = listener.WaitForCredentialAsync();
        await PostCallbackAsync(listener.CallbackUrl, "access-only", refreshToken: null, expectSuccess: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
    }

    // Posts the credential the NEW way: a same-origin JSON body to the callback, exactly as the hand-back
    // page's script does after reading the token pair from the URL fragment.
    private static async Task PostBodyCallbackAsync(Uri callbackUrl, string? accessToken, string? refreshToken, bool expectSuccess = true)
    {
        var payload = new Dictionary<string, string>();
        if (accessToken is not null)
            payload["access_token"] = accessToken;
        if (refreshToken is not null)
            payload["refresh_token"] = refreshToken;

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var response = await http.PostAsJsonAsync(callbackUrl, payload);
                if (expectSuccess)
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                else
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Could not reach the loopback callback.");
    }

    private static async Task<HttpResponseMessage> GetWithRetryAsync(HttpClient http, Uri callbackUrl)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return await http.GetAsync(callbackUrl);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Could not reach the loopback callback.");
    }

    private static async Task PostCallbackAsync(Uri callbackUrl, string? accessToken, string? refreshToken, bool expectSuccess = true)
    {
        var query = string.Empty;
        if (accessToken is not null)
            query += $"access_token={Uri.EscapeDataString(accessToken)}";
        if (refreshToken is not null)
            query += (query.Length > 0 ? "&" : "") + $"refresh_token={Uri.EscapeDataString(refreshToken)}";

        var builder = new UriBuilder(callbackUrl) { Query = query };
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var response = await http.GetAsync(builder.Uri);
                if (expectSuccess)
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                else
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Could not reach the loopback callback.");
    }
}
