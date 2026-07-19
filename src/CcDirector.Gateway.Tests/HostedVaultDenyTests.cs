using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole <c>/vault/keys</c> group is DENIED on the hosted Gateway - reads, writes and delete alike.
///
/// The store behind those routes is ONE global key vault file at the shared storage root, with no tenant
/// in the file, the store, or the routes, and the group sits behind only the host-wide authentication
/// gate - which admits ANY enrolled device key from ANY account. So before this change any hosted
/// subscriber could read every other subscriber's provider credentials in cleartext (the single-key GET
/// returns the raw value), overwrite them, and delete them. That is credential THEFT and TAMPERING, not a
/// disclosure, which is why the whole surface closes and not only the value-returning read.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. It reads <see cref="GatewayHostedMode.IsHosted"/> directly, never
/// an optional boundary or tenant argument. A security branch that depends on an optional argument fails
/// OPEN the moment a caller omits it.
///
/// IT REFUSES, IT NEVER SERVES AN EMPTY ANSWER. An empty name list would be a false statement about the
/// vault; an absent one is merely absent.
///
/// SELF-HOST IS THE CONTROL. <see cref="SelfHostVaultControlTests"/> in this same file boots the same host
/// with hosted mode explicitly OFF and proves the owner can still list, read, write and delete his keys.
///
/// REVERT-PROOF RECIPE (run it, do not just describe it):
///   1. In <c>src/CcDirector.Gateway/Api/VaultEndpoints.cs</c>, delete the
///      <c>app.AddEndpointFilter(...)</c> block that calls <c>DenyOnHosted()</c>.
///   2. Run this test project. Every test in <see cref="HostedVaultDenyTests"/> goes RED - the key value,
///      the name list, the overwrite and the delete are all served to a tenant device key again - and
///      every test in <see cref="SelfHostVaultControlTests"/> stays GREEN, which is what proves the tests
///      are pinned to the guard and not to the vault working at all.
///   3. Restore the filter. Everything goes green again.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedVaultDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string RefusalMessage = "the key vault is not available on the hosted gateway";
    private const string SecretName = "DEVTHROTTLE_API_KEY";
    private const string SecretValue = "dt_live_another_tenants_key";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hosted-vault-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "cc-hosted-vault-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public HostedVaultDenyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-vault-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        // A real secret already in the vault, so a read that (wrongly) got through would have something
        // to hand back. A deny tested against an empty vault proves nothing.
        new KeyVault(_vaultPath).Set(SecretName, SecretValue);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that
        // even this one is refused: there is no credential that makes another account's key readable.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Every route in the group, in one theory rather than one test each, so a route added to the list is
    /// a one-line change and the shape of the assertion cannot drift between them.
    /// </summary>
    [Theory]
    [InlineData("GET", "vault/keys", null)]                                        // the name list
    [InlineData("GET", "vault/keys/" + SecretName, null)]                          // the raw value - the theft
    [InlineData("PUT", "vault/keys/" + SecretName, "{\"value\":\"attacker\"}")]     // the overwrite - the tampering
    [InlineData("DELETE", "vault/keys/" + SecretName, null)]                       // the destruction
    public async Task Every_vault_route_is_refused_to_an_enrolled_tenant(string method, string path, string? body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_read_did_not_leak_the_secret_value_anywhere_in_the_body()
    {
        var resp = await Send(HttpMethod.Get, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain(SecretValue, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refused_list_did_not_name_the_keys_and_is_not_an_empty_list()
    {
        // Refuse, never serve an empty list: an empty "names" array is a FALSE statement about a vault that
        // holds a key, where an absent one is merely absent. The allow-list assertion below is what proves
        // there is no "names" property at all, empty or otherwise.
        var resp = await Send(HttpMethod.Get, "vault/keys");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretName, body, StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_write_did_not_take_effect()
    {
        // The status code alone would not prove the tampering was prevented - a handler that ran and then
        // reported 404 would pass that. This reads the store back through a fresh vault instance.
        var resp = await Send(HttpMethod.Put, "vault/keys/" + SecretName, "{\"value\":\"attacker-owned\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        Assert.Equal(SecretValue, new KeyVault(_vaultPath).Get(SecretName));
    }

    [Fact]
    public async Task The_refused_write_of_a_new_name_did_not_create_it()
    {
        var resp = await Send(HttpMethod.Put, "vault/keys/PLANTED_KEY", "{\"value\":\"planted\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        Assert.Null(new KeyVault(_vaultPath).Get("PLANTED_KEY"));
    }

    [Fact]
    public async Task The_refused_delete_did_not_take_effect()
    {
        var resp = await Send(HttpMethod.Delete, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        Assert.Equal(SecretValue, new KeyVault(_vaultPath).Get(SecretName));
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the group up as a side effect of running before the
        // host-wide authentication gate. Without a key the middleware still refuses first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("vault/keys")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and the difference is the whole assertion.
    ///
    /// Asserting that a handful of known payload keys are ABSENT rots by construction: it protects against
    /// the payload as it is today, every field added later is unprotected until someone remembers this
    /// file, and a substring check silently misses siblings. Asserting the property set is EXACTLY one
    /// error field inverts that - anything extra, anything new, and anything metadata-looking reddens
    /// automatically without this file being touched.
    /// </summary>
    private static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL for the hosted key-vault deny, and the reason it can be trusted.
///
/// Self-host is single-tenant: the owner sets his own provider keys here from the Cockpit and his own
/// Director reads them back on demand. A deny scoped to the wrong signal would break that shipped product
/// to protect the unshipped one, so this class boots the SAME <see cref="GatewayHost"/> with hosted mode
/// explicitly OFF and drives the same list, read, write and delete through the same authenticated routes.
///
/// These tests must stay GREEN through the revert-proof recipe in <see cref="HostedVaultDenyTests"/> - both
/// with the guard in place and with it removed. That is what proves the deny tests are pinned to the guard
/// rather than to the vault being broken.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SelfHostVaultControlTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SecretName = "DEVTHROTTLE_API_KEY";
    private const string SecretValue = "dt_live_the_owners_own_key";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-selfhost-vault-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "cc-selfhost-vault-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public SelfHostVaultControlTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfhost-vault-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Hosted mode explicitly OFF - set here rather than assumed, so a value leaked by another test
        // cannot turn this control silently into a second hosted run.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);

        new KeyVault(_vaultPath).Set(SecretName, SecretValue);

        _gateway = new GatewayHost(port: HostedVaultDenyTests.FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _key = _gateway.Devices.Register("dev-owner", "MA").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task The_owner_can_still_list_his_key_names()
    {
        var resp = await Send(HttpMethod.Get, "vault/keys");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(SecretName, names);
    }

    [Fact]
    public async Task The_owner_can_still_read_his_key_value()
    {
        // This is the read a self-hosted Director makes on demand for hosted AI. It must keep working.
        var resp = await Send(HttpMethod.Get, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(SecretValue, doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task The_owner_can_still_write_a_key_and_it_takes_effect()
    {
        var resp = await Send(HttpMethod.Put, "vault/keys/OPENAI_API_KEY", "{\"value\":\"sk-owner-set\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("sk-owner-set", new KeyVault(_vaultPath).Get("OPENAI_API_KEY"));
    }

    [Fact]
    public async Task The_owner_can_still_delete_a_key_and_it_takes_effect()
    {
        var resp = await Send(HttpMethod.Delete, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Null(new KeyVault(_vaultPath).Get(SecretName));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }
}
