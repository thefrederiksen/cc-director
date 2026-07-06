using System.Text.Json;

namespace CcDirector.Core.Account;

/// <summary>
/// The secure browser credential hand-back page shared by both sign-in callback surfaces (issue #1082,
/// which absorbs the standalone security issue #877): the host-local <see cref="LoopbackLoginListener"/>
/// and the Gateway reachable front-door callback (<c>AccountSignInCallbackEndpoint</c>).
///
/// The sign-in completion used to redirect the browser to the callback with the access-plus-refresh token
/// pair in the URL QUERY STRING (<c>?access_token=...&amp;refresh_token=...</c>). A URL is not a secret-safe
/// place: it lands in the browser history, appears in screenshots, and is readable straight from the address
/// bar - and the refresh token is the long-lived credential. So the hand-back moves off the query string:
/// the completion puts the token pair in the URL FRAGMENT (the part after <c>#</c>), which the browser NEVER
/// sends to any server - the exact pattern the phone enrollment already uses
/// (<c>packages/client-core/src/auth/DeviceCallback.tsx</c>). The callback serves this small page; its script
/// reads the fragment, POSTs the pair back to the SAME callback path as a same-origin request BODY, strips the
/// fragment from the address bar, and shows a close-this-tab message. The server then captures the credential
/// from the request body, so no token ever rides in a URL the server can log (security rule DT-05).
/// </summary>
public static class CredentialHandbackPage
{
    /// <summary>The access-token field name in both the URL fragment and the JSON body the script posts back.</summary>
    public const string AccessTokenField = "access_token";

    /// <summary>The refresh-token field name in both the URL fragment and the JSON body the script posts back.</summary>
    public const string RefreshTokenField = "refresh_token";

    /// <summary>
    /// Builds the credential hand-back HTML page. The page's script reads the token pair from the URL
    /// FRAGMENT and POSTs it, as a same-origin JSON body, back to the current path (<c>location.pathname</c>) -
    /// so the same page serves both callback surfaces without embedding a path. It strips the fragment from the
    /// address bar after reading it (mirroring the phone's <c>history.replaceState</c>) so the token does not
    /// linger in the address bar or history. The page carries NO token itself and shares the DevThrottle dark
    /// theme so the sign-in web surfaces read as one.
    /// </summary>
    public static string BuildHtml() => HandbackHtml;

    /// <summary>
    /// Parses the JSON body the hand-back script posts back (<c>{"access_token":"...","refresh_token":"..."}</c>)
    /// into the token pair. Returns false - rather than throwing - when the body is missing, is not a JSON
    /// object, or does not carry both non-empty string tokens, so the caller fails loud on an incomplete
    /// credential instead of storing a half-credential. This is the <c>TryParse</c> idiom (a false result is a
    /// clear "not a complete credential", not a swallowed error); the caller surfaces the failure.
    /// </summary>
    public static bool TryParseJsonBody(string? json, out string accessToken, out string refreshToken)
    {
        accessToken = string.Empty;
        refreshToken = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var access = ReadStringField(document.RootElement, AccessTokenField);
            var refresh = ReadStringField(document.RootElement, RefreshTokenField);

            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
                return false;

            accessToken = access;
            refreshToken = refresh;
            return true;
        }
        catch (JsonException)
        {
            // A malformed body is simply not a complete credential; the TryParse contract reports that as
            // false and the caller fails loud. This is the one place the parse can throw on untrusted input.
            return false;
        }
    }

    private static string? ReadStringField(JsonElement root, string field)
        => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The hand-back page. The script (ASCII only) reads the fragment, posts the pair back same-origin, and
    // strips the fragment. No token is ever placed in this markup.
    private const string HandbackHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DevThrottle - Sign in</title>
<style>
  body {
    margin: 0;
    background: #1e1e1e;
    color: #ddd;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .panel {
    width: 100%;
    max-width: 420px;
    background: #252526;
    border: 1px solid #3c3c3c;
    border-radius: 6px;
    padding: 24px;
    margin: 16px;
    text-align: center;
  }
  h1 { margin: 0 0 4px; font-size: 18px; letter-spacing: 0.5px; }
  p { color: #aaa; font-size: 13px; margin: 0; line-height: 1.4; }
</style>
</head>
<body>
  <div class="panel">
    <h1>DevThrottle</h1>
    <p id="status">Completing sign-in...</p>
  </div>
  <script>
  (function () {
    var status = document.getElementById("status");
    function fail() {
      status.textContent = "Sign-in did not complete. Please return to your browser and sign in again.";
    }
    function stripFragment() {
      try { history.replaceState(null, "", location.pathname); } catch (e) { /* address bar left as is */ }
    }
    var raw = location.hash && location.hash.charAt(0) === "#" ? location.hash.substring(1) : (location.hash || "");
    var params = new URLSearchParams(raw);
    var access = params.get("access_token");
    var refresh = params.get("refresh_token");
    if (!access || !refresh) {
      stripFragment();
      fail();
      return;
    }
    fetch(location.pathname, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ access_token: access, refresh_token: refresh })
    }).then(function (response) {
      stripFragment();
      if (response.ok) {
        status.textContent = "You are signed in to DevThrottle. You can close this tab.";
      } else {
        fail();
      }
    }).catch(function () {
      stripFragment();
      fail();
    });
  })();
  </script>
</body>
</html>
""";
}
