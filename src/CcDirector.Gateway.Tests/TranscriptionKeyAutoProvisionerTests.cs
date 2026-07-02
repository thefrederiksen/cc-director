using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #881: after sign-in the Gateway mints a DevThrottle inference key and stores it where the
/// transcription owner reads it, so a signed-in user transcribes with zero configuration. These tests
/// cover the mint response parsing, the mint HTTP call, and the ensure-logic precedence (manual
/// override / reuse across restarts vs. mint-when-absent).
/// </summary>
public sealed class TranscriptionKeyAutoProvisionerTests : IDisposable
{
    private readonly string _vaultPath;

    public TranscriptionKeyAutoProvisionerTests()
    {
        _vaultPath = Path.Combine(Path.GetTempPath(), "ccd-autowire-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
    }

    // ----- Response parsing -----

    [Theory]
    [InlineData("{\"key\":\"dt_live_ABC123\"}", "dt_live_ABC123")]
    [InlineData("{\"id\":\"uuid\",\"api_key\":\"dt_test_XYZ789\"}", "dt_test_XYZ789")]
    [InlineData("{\"secret\":\"dt_live_deadBEEF\"}", "dt_live_deadBEEF")]
    // The REAL live mint shape: the full key at data.key (with base64url '-'/'_' chars), alongside a
    // data.record.masked that also looks like a key - must return the full key, never the masked one.
    [InlineData("{\"data\":{\"key\":\"dt_live_aB-cD_eF123456\",\"record\":{\"masked\":\"dt_live_...3456\",\"prefix\":\"dt_live\",\"last4\":\"3456\"}}}", "dt_live_aB-cD_eF123456")]
    // A shape we don't name a field for still works via the pattern scan.
    [InlineData("{\"data\":{\"newKey\":\"dt_live_nested00\"}}", "dt_live_nested00")]
    public void ExtractKey_FindsKey(string body, string expected)
        => Assert.Equal(expected, AccountInferenceKeyProvisioner.ExtractKey(body));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"error\":\"nope\"}")]
    [InlineData("not json and no key")]
    public void ExtractKey_NoKey_ReturnsNull(string body)
        => Assert.Null(AccountInferenceKeyProvisioner.ExtractKey(body));

    // ----- Mint HTTP call -----

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? SeenAuth { get; private set; }
        public StatusHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("{\"data\":{\"key\":\"dt_live_full_KEY\",\"record\":{\"id\":\"key-uuid-123\"}}}", "key-uuid-123")]
    [InlineData("{\"data\":{\"key\":\"dt_live_full_KEY\",\"id\":\"data-id-9\"}}", "data-id-9")]
    [InlineData("{\"id\":\"root-id-7\",\"key\":\"dt_live_full_KEY\"}", "root-id-7")]
    [InlineData("{\"data\":{\"key\":\"dt_live_full_KEY\"}}", null)]
    public void ExtractKeyId_FindsRecordId(string body, string? expected)
        => Assert.Equal(expected, AccountInferenceKeyProvisioner.ExtractKeyId(body));

    [Fact]
    public async Task MintAsync_201_ReturnsKeyAndId_AndBearsTheJwt()
    {
        // The real live shape: full key at data.key, id at data.record.id.
        var body = "{\"data\":{\"key\":\"dt_live_minted01\",\"record\":{\"id\":\"the-key-id\"}}}";
        var handler = new StatusHandler(HttpStatusCode.Created, body);
        var minter = new AccountInferenceKeyProvisioner(new HttpClient(handler));

        var minted = await minter.MintAsync("the.jwt.token", "cc-director TESTBOX");

        Assert.NotNull(minted);
        Assert.Equal("dt_live_minted01", minted!.Key);
        Assert.Equal("the-key-id", minted.Id);
        Assert.Equal("Bearer the.jwt.token", handler.SeenAuth);
    }

    [Fact]
    public async Task MintAsync_Non2xx_ReturnsNull()
    {
        var minter = new AccountInferenceKeyProvisioner(new HttpClient(new StatusHandler(HttpStatusCode.Forbidden, "{\"error\":\"denied\"}")));
        Assert.Null(await minter.MintAsync("jwt", "label"));
    }

    [Fact]
    public async Task MintAsync_NoAccessToken_ReturnsNull_WithoutCalling()
    {
        var minter = new AccountInferenceKeyProvisioner(new HttpClient(new StatusHandler(HttpStatusCode.Created, "{\"data\":{\"key\":\"dt_live_x\"}}")));
        Assert.Null(await minter.MintAsync("", "label"));
    }

    // ----- Ensure logic -----

    private sealed class FakeMinter : IInferenceKeyMinter
    {
        private readonly MintedInferenceKey? _minted;
        public int Calls { get; private set; }
        public int RevokeCalls { get; private set; }
        public string? RevokedId { get; private set; }
        public FakeMinter(string? key, string? id = "key-id") { _minted = key is null ? null : new MintedInferenceKey(key, id); }
        public Task<MintedInferenceKey?> MintAsync(string accessToken, string label, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_minted);
        }
        public Task<bool> RevokeAsync(string accessToken, string keyId, CancellationToken ct = default)
        {
            RevokeCalls++;
            RevokedId = keyId;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task EnsureAsync_VaultAlreadyHasKey_DoesNotMint()
    {
        var vault = new KeyVault(_vaultPath);
        vault.Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_manual_or_reused");
        var minter = new FakeMinter("dt_live_should_not_be_used");
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => "jwt", minter);

        var minted = await sut.EnsureAsync();

        Assert.False(minted);
        Assert.Equal(0, minter.Calls);
        Assert.Equal("dt_live_manual_or_reused", new KeyVault(_vaultPath).Get(TranscriptionEndpointResolver.DevThrottleKeyName));
    }

    [Fact]
    public async Task EnsureAsync_NotSignedIn_DoesNotMint()
    {
        var vault = new KeyVault(_vaultPath);
        var minter = new FakeMinter("dt_live_x");
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => null, minter);

        var minted = await sut.EnsureAsync();

        Assert.False(minted);
        Assert.Equal(0, minter.Calls);
    }

    [Fact]
    public async Task EnsureAsync_SignedIn_EmptyVault_MintsAndStoresKeyAndId()
    {
        var vault = new KeyVault(_vaultPath);
        var minter = new FakeMinter("dt_live_freshly_minted", id: "minted-key-id");
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => "the.jwt", minter);

        var minted = await sut.EnsureAsync();

        Assert.True(minted);
        Assert.Equal(1, minter.Calls);
        var reread = new KeyVault(_vaultPath);
        Assert.Equal("dt_live_freshly_minted", reread.Get(TranscriptionEndpointResolver.DevThrottleKeyName));
        // The id is recorded so sign-out can revoke THIS auto-minted key.
        Assert.Equal("minted-key-id", reread.Get(TranscriptionKeyAutoProvisioner.InferenceKeyIdVaultName));
    }

    // ----- Revoke-on-sign-out (issue #881) -----

    [Fact]
    public async Task RevokeMintedKeyAsync_AutoMintedKey_RevokesAndClearsBoth()
    {
        var vault = new KeyVault(_vaultPath);
        var minter = new FakeMinter("dt_live_x", id: "the-id");
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => "the.jwt", minter);
        await sut.EnsureAsync(); // mints + records the id

        var revoked = await sut.RevokeMintedKeyAsync();

        Assert.True(revoked);
        Assert.Equal(1, minter.RevokeCalls);
        Assert.Equal("the-id", minter.RevokedId);
        var reread = new KeyVault(_vaultPath);
        Assert.Null(reread.Get(TranscriptionEndpointResolver.DevThrottleKeyName));
        Assert.Null(reread.Get(TranscriptionKeyAutoProvisioner.InferenceKeyIdVaultName));
    }

    [Fact]
    public async Task RevokeMintedKeyAsync_ManualKey_LeavesItUntouched()
    {
        // A manual key has no recorded id; sign-out must NOT revoke or clear it (it is the user's key).
        var vault = new KeyVault(_vaultPath);
        vault.Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_manual");
        var minter = new FakeMinter("dt_live_unused");
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => "the.jwt", minter);

        var revoked = await sut.RevokeMintedKeyAsync();

        Assert.False(revoked);
        Assert.Equal(0, minter.RevokeCalls);
        Assert.Equal("dt_live_manual", new KeyVault(_vaultPath).Get(TranscriptionEndpointResolver.DevThrottleKeyName));
    }

    [Fact]
    public async Task EnsureAsync_MintReturnsNull_LeavesVaultUnset()
    {
        var vault = new KeyVault(_vaultPath);
        var minter = new FakeMinter(null);
        var sut = new TranscriptionKeyAutoProvisioner(vault, () => "the.jwt", minter);

        var minted = await sut.EnsureAsync();

        Assert.False(minted);
        Assert.Equal(1, minter.Calls);
        Assert.Null(new KeyVault(_vaultPath).Get(TranscriptionEndpointResolver.DevThrottleKeyName));
    }
}
