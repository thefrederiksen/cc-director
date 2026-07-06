using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests the shared browser credential hand-back page (issue #1082, absorbs #877) both callback surfaces use
/// to move the token pair off the URL query string. The page must carry no token itself and read the pair from
/// the URL fragment; its JSON body parser must accept a complete pair and reject anything incomplete so the
/// caller fails loud rather than storing a half-credential.
/// </summary>
public sealed class CredentialHandbackPageTests
{
    // The served page carries the fragment-reading script and NO token material of its own.
    [Fact]
    public void BuildHtml_CarriesFragmentReaderScript_AndNoToken()
    {
        var html = CredentialHandbackPage.BuildHtml();

        Assert.Contains("location.hash", html, StringComparison.Ordinal);
        Assert.Contains("access_token", html, StringComparison.Ordinal);   // the field name the script reads
        Assert.Contains("history.replaceState", html, StringComparison.Ordinal); // strips the fragment
        // It posts back rather than carrying the credential in the markup.
        Assert.Contains("method: \"POST\"", html, StringComparison.Ordinal);
    }

    // A body carrying both non-empty string tokens parses into the pair.
    [Fact]
    public void TryParseJsonBody_WithBothTokens_ParsesThePair()
    {
        var ok = CredentialHandbackPage.TryParseJsonBody(
            "{\"access_token\":\"acc\",\"refresh_token\":\"ref\"}", out var access, out var refresh);

        Assert.True(ok);
        Assert.Equal("acc", access);
        Assert.Equal("ref", refresh);
    }

    // Incomplete, empty, or malformed bodies are rejected (false) so the caller fails loud - never a
    // half-credential.
    [Theory]
    [InlineData("{\"access_token\":\"acc\"}")]                        // missing refresh
    [InlineData("{\"refresh_token\":\"ref\"}")]                       // missing access
    [InlineData("{\"access_token\":\"\",\"refresh_token\":\"ref\"}")]  // empty access
    [InlineData("{\"access_token\":\"acc\",\"refresh_token\":\"\"}")]  // empty refresh
    [InlineData("{}")]                                                 // no tokens
    [InlineData("not json")]                                           // malformed
    [InlineData("[1,2,3]")]                                            // not an object
    [InlineData("")]                                                   // empty
    [InlineData(null)]                                                 // null
    public void TryParseJsonBody_Incomplete_ReturnsFalse(string? json)
    {
        var ok = CredentialHandbackPage.TryParseJsonBody(json, out var access, out var refresh);

        Assert.False(ok);
        Assert.Equal(string.Empty, access);
        Assert.Equal(string.Empty, refresh);
    }
}
