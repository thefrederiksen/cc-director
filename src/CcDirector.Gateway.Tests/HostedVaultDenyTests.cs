using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole <c>/vault/keys</c> group is DENIED on the hosted Gateway - reads, writes and delete alike.
///
/// The store behind those routes is ONE global key vault file at the shared storage root, with no tenant in
/// the file, the store, or the routes, and the group sits behind only the host-wide authentication gate -
/// which admits ANY enrolled device key from ANY account. So before this change any hosted subscriber could
/// read every other subscriber's provider credentials in cleartext (the single-key GET returns the raw
/// value), overwrite them, and delete them. That is credential THEFT and TAMPERING, not a disclosure, which
/// is why the whole surface closes and not only the value-returning read.
///
/// WHY A DENY AND NOT A PARTITION. There is nothing to partition by yet: the vault file has no tenant column
/// and no per-account namespace, so a per-account answer would have to be invented rather than read. That is
/// a half-partition, which is worse than an honest refusal because it looks like isolation.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. It reads <see cref="GatewayHostedMode.IsHosted"/> directly, never an
/// optional boundary or tenant argument. A security branch that depends on an optional argument fails OPEN
/// the moment a caller omits it.
///
/// IT REFUSES, IT NEVER SERVES AN EMPTY ANSWER. An empty name list would be a FALSE statement about a vault
/// that holds keys; an absent one is merely absent.
///
/// STATUS AND MEDIA TYPE ARE ASSERTED BEFORE ANY PARSE. Parsing is itself an unstated assertion about format:
/// on this Gateway a 404 is not necessarily JSON - the single-page-app fallback answers unmatched paths with
/// something else entirely - so a mutation that routed a denied path to the fallback would make the parse
/// THROW. That red is a crash, and a crash proves only that the mutation broke something upstream of the
/// claim; it cannot say what was served in place of the refusal, which is the entire claim a deny makes.
///
/// A 404 DENY IS INDISTINGUISHABLE FROM A ROUTE THAT DOES NOT EXIST, so every denied path also carries a
/// self-host HANDLER-POSITIVE receipt proving the route is really there and really does the thing:
/// <see cref="SelfHostVaultControlTests"/> drives all four verbs through the production host with the
/// authentication gate on, and <see cref="SelfHostVaultGroupControlTests"/> drives them on the group in both
/// non-hosted forms, asserting the real payloads and the real effects on the store.
///
/// SURVIVAL ASSERTIONS CARRY A DESTRUCTIBILITY CONTROL. Where a test here asserts a key SURVIVED a refused
/// write or delete, a no-op would pass identically, so the same operation is proven destructive on self-host
/// against the same seeded key - see <c>The_same_write_overwrites_an_existing_key_on_self_host</c> and
/// <c>The_same_delete_destroys_an_existing_key_on_self_host</c> in
/// <see cref="SelfHostVaultGroupControlTests"/>. Without those, "it survived" is a claim about a request that
/// might never have been capable of destroying anything.
///
/// ONE BYPASSABLE PRIMITIVE, BY DESIGN. The proof-run count is set by how many things can be individually
/// wrong while everything else stays correct and isolation still breaks. Here that number is ONE: a single
/// <c>AddEndpointFilter</c> on a single <c>MapGroup</c> created at a single <c>VaultEndpoints.Map</c> call
/// site (<c>GatewayHost.cs</c>, one call - checked, not assumed), with NO route carrying a guard of its own,
/// so deleting the filter fails all four routes together. The obvious second family - mapping one of the four
/// routes onto the UNGROUPED builder, bypassing the filter for that route alone - was four more independently
/// bypassable primitives, and it was removed by DESIGN rather than argued away: the routes now live in
/// <c>VaultEndpoints.MapRoutes</c>, which receives the guarded group and never receives the ungrouped builder,
/// so that mistake is not expressible. That is a compile-time property, which is why no test here asserts it;
/// a test could not. Removing the hosted check from <c>DenyOnHosted</c> is NOT a third primitive: it makes the
/// group refuse ALWAYS, which breaks the shipped self-host product rather than isolation, and the self-host
/// controls below are what redden on it.
///
/// REVERT-PROOF - the recipe to RUN, not to describe. In <c>src/CcDirector.Gateway/Api/VaultEndpoints.cs</c>
/// DELETE the <c>guarded.AddEndpointFilter(...)</c> block outright, leaving
/// <c>var guarded = outer.MapGroup("");</c> in place so the group still exists and the file still compiles,
/// with NO per-route guard put back - the
/// hosted deny is then absent entirely. Never <c>if (false)</c>: unreachable code is a build error here.
/// That is ONE primitive mutated and nothing else, so every red is attributable to it. Rebuild, CONFIRM ZERO
/// ERRORS (a run after a failed build executes the previous binary and reports a false pass), then run the
/// FULL Gateway suite and record every red BY NAME with the form it arrived in. A red only counts if it fails
/// WITH THE SYMPTOM - an assertion naming what was served instead of the refusal; crash-reds are UNPROVEN.
/// Then restore, rebuild, rerun the full suite, and confirm the counts reconcile arithmetically with total
/// and skipped unchanged.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedVaultDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    internal const string RefusalMessage = "the key vault is not available on the hosted gateway";
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
        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself and
        // proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        // A real secret already in the vault, so a read that (wrongly) got through would have something to
        // hand back. A deny tested against an empty vault proves nothing.
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
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every route in the group, in one theory rather than one test each, so a route added to the list is a
    /// one-line change and the shape of the assertion cannot drift between them.
    /// </summary>
    [Theory]
    [InlineData("GET", "vault/keys", null)]                                        // the name list
    [InlineData("GET", "vault/keys/" + SecretName, null)]                          // the raw value - the theft
    [InlineData("PUT", "vault/keys/" + SecretName, "{\"value\":\"attacker\"}")]     // the overwrite - the tampering
    [InlineData("DELETE", "vault/keys/" + SecretName, null)]                       // the destruction
    public async Task Every_vault_route_is_refused_to_an_enrolled_tenant(string method, string path, string? body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_read_did_not_leak_the_secret_value_anywhere_in_the_body()
    {
        var resp = await Send(HttpMethod.Get, "vault/keys/" + SecretName);

        // The refusal is asserted FIRST, and the absence check comes after it. On its own,
        // "the body does not contain the secret" is an absence-only claim that a 404 from the single-page-app
        // fallback - or any other body that simply is not the vault - would satisfy just as well as a working
        // deny. Pinning the exact refusal first is what makes this test a statement about the guard rather
        // than a statement about the string.
        await AssertBodyIsNothingButTheRefusal(resp);
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
        Assert.DoesNotContain(SecretName, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_write_did_not_take_effect()
    {
        // The status code alone would not prove the tampering was prevented - a handler that ran and then
        // reported 404 would pass that. This reads the store back through a fresh vault instance.
        //
        // DESTRUCTIBILITY CONTROL: the identical write DOES overwrite this same seeded key on self-host
        // (SelfHostVaultGroupControlTests.The_same_write_overwrites_an_existing_key_on_self_host), so this is
        // a capable operation being stopped, not a no-op passing by construction.
        var resp = await Send(HttpMethod.Put, "vault/keys/" + SecretName, "{\"value\":\"attacker-owned\"}");
        await AssertBodyIsNothingButTheRefusal(resp);

        Assert.Equal(SecretValue, new KeyVault(_vaultPath).Get(SecretName));
    }

    [Fact]
    public async Task The_refused_write_of_a_new_name_did_not_create_it()
    {
        var resp = await Send(HttpMethod.Put, "vault/keys/PLANTED_KEY", "{\"value\":\"planted\"}");
        await AssertBodyIsNothingButTheRefusal(resp);

        Assert.Null(new KeyVault(_vaultPath).Get("PLANTED_KEY"));
    }

    [Fact]
    public async Task The_refused_delete_did_not_take_effect()
    {
        // DESTRUCTIBILITY CONTROL: the identical delete DOES destroy this same seeded key on self-host
        // (SelfHostVaultGroupControlTests.The_same_delete_destroys_an_existing_key_on_self_host).
        var resp = await Send(HttpMethod.Delete, "vault/keys/" + SecretName);
        await AssertBodyIsNothingButTheRefusal(resp);

        Assert.Equal(SecretValue, new KeyVault(_vaultPath).Get(SecretName));
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the group up as a side effect of running before the
        // host-wide authentication gate. Without a key the middleware still refuses first. GREEN in both
        // directions of the revert on purpose - a control that moves with the change under test is not a
        // control.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("vault/keys")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and FORMAT FACTS BEFORE PARSING.
    ///
    /// Asserting that a handful of known payload keys are ABSENT rots by construction: it protects against
    /// the payload as it is today, every field added later is unprotected until someone remembers this file,
    /// and a substring check silently misses siblings. Asserting the property set is EXACTLY one error field
    /// inverts that - anything extra, anything new, and anything metadata-looking reddens automatically.
    ///
    /// The status and media type are asserted FIRST so a revert reddens as a STATEMENT - "expected NotFound,
    /// got OK", "expected application/json, got text/plain" - rather than as a parser exception. A crash-red
    /// proves the mutation broke something upstream; it cannot say what was served instead of the refusal,
    /// and that is exactly the claim being made here.
    /// </summary>
    internal static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// Boots ONLY the key-vault group on an ephemeral port, exactly as <see cref="VaultEndpointsTests"/> does,
/// and hands the caller the route group back so a test can map routes onto it. That is what makes the
/// future-route proof possible at all: the group is created inside <c>VaultEndpoints.Map</c>, so nothing
/// outside that method could otherwise state a property about routes added to it.
/// </summary>
internal static class VaultGroupProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        KeyVault vault,
        Action<RouteGroupBuilder>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = VaultEndpoints.Map(app, vault);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    /// <summary>
    /// The refusal, asserted as format facts first and then as an exact property set - same reasoning as
    /// <see cref="HostedVaultDenyTests.AssertBodyIsNothingButTheRefusal"/>, restated here so the probe host
    /// reads on its own.
    /// </summary>
    public static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(HostedVaultDenyTests.RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal is a filter on the key-vault route GROUP, so it covers
/// routes that have not been written yet.
///
/// A guard line repeated in every handler passes exactly the same tests as a group filter for the routes that
/// exist today, which is precisely why it is dangerous - the difference only shows up on the route somebody
/// adds NEXT, when it is open by default and nothing fails. That difference is not observable by driving the
/// four routes that exist, so this class maps a BRAND-NEW probe route onto the group and asserts it is
/// already refused with no deny of its own written anywhere.
///
/// The mirror half - the same probe path SERVED with hosted mode explicitly off, in both non-hosted forms -
/// is <see cref="SelfHostVaultGroupControlTests.A_route_added_to_the_group_still_serves_on_self_host"/>. One
/// direction alone cannot tell a working gate apart from a brick: a filter that refused everything
/// unconditionally would pass every hosted assertion in this file while having silently killed the routes for
/// self-host too.
/// </summary>
public sealed class HostedVaultGroupFilterTests : IDisposable
{
    private const string ProbePayloadSentinel = "probe-payload-that-must-never-be-served-on-hosted";
    private const string SecretName = "DEVTHROTTLE_API_KEY";
    private const string SecretValue = "dt_live_another_tenants_key";

    private readonly string _dir;
    private readonly string? _priorHosted;

    public HostedVaultGroupFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-vault-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private KeyVault SeededVault()
    {
        var vault = new KeyVault(Path.Combine(_dir, "v-" + Guid.NewGuid().ToString("N") + ".json"));
        vault.Set(SecretName, SecretValue);
        return vault;
    }

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in
    /// <c>VaultEndpoints</c> mentions this path, and no guard is written for it here - the only thing standing
    /// between the caller and the probe payload is the group filter. Delete the filter and this test serves
    /// the probe payload with a 200, which is the future-route hole stated out loud.
    /// </summary>
    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own()
    {
        var (app, http) = await VaultGroupProbeHost.StartAsync(
            SeededVault(),
            mapIntoGroup: group => group.MapGet("/vault/keys/added-after-the-deny-was-written",
                () => Results.Json(new { probe = ProbePayloadSentinel })));
        try
        {
            var resp = await http.GetAsync("/vault/keys/added-after-the-deny-was-written");

            await VaultGroupProbeHost.AssertBodyIsNothingButTheRefusal(resp);
            Assert.DoesNotContain(ProbePayloadSentinel, await resp.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// The four production routes, refused through that same filter rather than through a guard of their own.
    /// Every verb is here, because the write and the delete are the tampering half of this defect and a deny
    /// that closed only the reads would leave the damage path open.
    /// </summary>
    [Theory]
    [InlineData("GET", "/vault/keys", null)]
    [InlineData("GET", "/vault/keys/" + SecretName, null)]
    [InlineData("PUT", "/vault/keys/" + SecretName, "{\"value\":\"attacker\"}")]
    [InlineData("DELETE", "/vault/keys/" + SecretName, null)]
    public async Task Every_vault_route_is_refused_on_hosted_through_the_group_filter(
        string method, string path, string? body)
    {
        var vault = SeededVault();
        var (app, http) = await VaultGroupProbeHost.StartAsync(vault);
        try
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            await VaultGroupProbeHost.AssertBodyIsNothingButTheRefusal(await http.SendAsync(req));

            // The store is untouched by any of the four, including the two that would have changed it. Both
            // of those operations are proven CAPABLE of changing it in SelfHostVaultGroupControlTests.
            Assert.Equal(SecretValue, vault.Get(SecretName));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// CONTROL: the filter is scoped to the key-vault group, not a blanket refusal on the whole application.
    /// A route mapped OUTSIDE the group still serves on hosted, so the passing tests above are the filter
    /// doing its job and not the host refusing everything.
    /// </summary>
    [Fact]
    public async Task A_route_outside_the_group_still_serves_on_hosted()
    {
        var (app, http) = await VaultGroupProbeHost.StartAsync(
            SeededVault(),
            mapOutsideGroup: routes => routes.MapGet("/not-a-vault-route", () => Results.Json(new { ok = true })));
        try
        {
            var resp = await http.GetAsync("/not-a-vault-route");

            // Format facts before the parse, and then the real payload rather than a substring: "true"
            // appears in plenty of bodies that are not this handler's answer.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL ON THE GROUP, in BOTH non-hosted forms, with the effects proven.
///
/// Self-host is the control for this whole mission, so it is PROVEN rather than INHERITED.
/// <see cref="VaultEndpointsTests"/> does not prove it: those tests never mention <c>CC_GATEWAY_HOSTED</c>
/// and pass only because the runner happens to leave it unset. If that ambient default ever flipped they
/// would keep passing while self-host was completely broken, because they assert nothing about which mode
/// they are in. So this class sets the variable itself, to both non-hosted values that occur in practice -
/// absent, and present-but-not-"1" - and asserts the mode took before driving anything.
///
/// It asserts REAL PAYLOADS AND REAL EFFECTS, not the absence of the refusal string. An empty-but-successful
/// response would satisfy "the refusal is absent" while still being a broken self-host.
///
/// It also carries the DESTRUCTIBILITY CONTROLS for the hosted survival assertions: the same write overwrites
/// an existing key here, and the same delete destroys one. Without those, "the key survived the refusal" is
/// satisfied just as well by a request that could never have destroyed anything.
///
/// Every test here must stay GREEN through the revert described on <see cref="HostedVaultDenyTests"/>.
/// </summary>
public sealed class SelfHostVaultGroupControlTests : IDisposable
{
    private const string SecretName = "DEVTHROTTLE_API_KEY";
    private const string SecretValue = "dt_live_the_owners_own_key";

    private readonly string _dir;
    private readonly string? _priorHosted;

    public SelfHostVaultGroupControlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-vault-selfhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Puts the process into a STATED non-hosted mode and proves it took, so no test below can silently be
    /// running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    /// <summary>null = the variable is absent. "0" = present and explicitly not hosted. Both are real
    /// non-hosted deployments and both must serve.</summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    private KeyVault SeededVault()
    {
        var vault = new KeyVault(Path.Combine(_dir, "v-" + Guid.NewGuid().ToString("N") + ".json"));
        vault.Set(SecretName, SecretValue);
        return vault;
    }

    /// <summary>
    /// HANDLER-POSITIVE RECEIPT for the list route: the route really exists and really answers with the
    /// owner's key names. A 404 deny is indistinguishable from a route that was never mapped, so without a
    /// receipt like this the hosted 404 would prove nothing about a guard.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_still_gets_his_real_key_names_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await VaultGroupProbeHost.StartAsync(SeededVault());
        try
        {
            var resp = await http.GetAsync("/vault/keys");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains(SecretName, names);
            Assert.DoesNotContain("error", doc.RootElement.EnumerateObject().Select(p => p.Name));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>HANDLER-POSITIVE RECEIPT for the value read: the real key value comes back.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_still_gets_his_real_key_value_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await VaultGroupProbeHost.StartAsync(SeededVault());
        try
        {
            var resp = await http.GetAsync("/vault/keys/" + SecretName);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(SecretValue, doc.RootElement.GetProperty("value").GetString());
            Assert.Equal(SecretName, doc.RootElement.GetProperty("name").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// DESTRUCTIBILITY CONTROL for <c>HostedVaultDenyTests.The_refused_write_did_not_take_effect</c>: the
    /// identical request against the identical seeded key DOES overwrite it when the guard is not in the way.
    /// Without this, "the key survived" would be satisfied by an operation incapable of changing anything.
    /// It is also the handler-positive receipt for the write route.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_same_write_overwrites_an_existing_key_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var vault = SeededVault();
        var (app, http) = await VaultGroupProbeHost.StartAsync(vault);
        try
        {
            var resp = await http.PutAsync("/vault/keys/" + SecretName,
                new StringContent("{\"value\":\"attacker-owned\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            {
                Assert.Equal(SecretName, doc.RootElement.GetProperty("name").GetString());
                Assert.True(doc.RootElement.GetProperty("set").GetBoolean());
            }

            Assert.Equal("attacker-owned", vault.Get(SecretName));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// DESTRUCTIBILITY CONTROL for <c>HostedVaultDenyTests.The_refused_delete_did_not_take_effect</c>: the
    /// identical delete against the identical seeded key DOES destroy it when the guard is not in the way.
    /// It is also the handler-positive receipt for the delete route.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_same_delete_destroys_an_existing_key_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var vault = SeededVault();
        var (app, http) = await VaultGroupProbeHost.StartAsync(vault);
        try
        {
            var resp = await http.DeleteAsync("/vault/keys/" + SecretName);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            {
                Assert.Equal(SecretName, doc.RootElement.GetProperty("name").GetString());
                Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
            }

            Assert.Null(vault.Get(SecretName));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// THE SECOND HALF OF THE FUTURE-ROUTE PROOF: the probe route must be REFUSED on hosted and SERVED with
    /// hosted mode EXPLICITLY off, in both non-hosted forms. The hosted half is
    /// <see cref="HostedVaultGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own"/>;
    /// this is its mirror, on the SAME probe path.
    ///
    /// Without this half, "the filter refuses everything, always" would pass every hosted test in this file
    /// while having silently killed the vault for self-host too.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

        var (app, http) = await VaultGroupProbeHost.StartAsync(
            SeededVault(),
            mapIntoGroup: group => group.MapGet("/vault/keys/added-after-the-deny-was-written",
                () => Results.Json(new { probe = "served" })));
        try
        {
            var resp = await http.GetAsync("/vault/keys/added-after-the-deny-was-written");

            // Format facts before the parse, then the probe handler's OWN payload. A substring check would
            // also pass on a body that merely mentioned the word, which is not the claim being made.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("served", doc.RootElement.GetProperty("probe").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE SELF-HOST RECEIPT ON THE PRODUCTION WIRING, with authentication.
///
/// <see cref="SelfHostVaultGroupControlTests"/> proves the group's behaviour by mapping it directly; this
/// class proves the same four routes are actually reachable and correct through a real
/// <see cref="GatewayHost"/> with the authentication gate ON, hosted mode explicitly OFF and asserted off,
/// and a registered device key - the shipped self-host configuration. A change that quietly unmapped the
/// routes in production wiring would pass the group tests and still break the shipped product; this is what
/// catches that.
///
/// Must stay GREEN through the revert described on <see cref="HostedVaultDenyTests"/>.
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
        // Hosted mode explicitly OFF and PROVEN off - set here rather than assumed, so a value leaked by
        // another test cannot turn this control silently into a second hosted run.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        Assert.False(GatewayHostedMode.IsHosted);

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
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task The_owner_can_still_list_his_key_names()
    {
        var resp = await Send(HttpMethod.Get, "vault/keys");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(SecretName, names);
    }

    [Fact]
    public async Task The_owner_can_still_read_his_key_value()
    {
        // This is the read a self-hosted Director makes on demand for hosted AI. It must keep working.
        var resp = await Send(HttpMethod.Get, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(SecretValue, doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task The_owner_can_still_write_a_key_and_it_takes_effect()
    {
        var resp = await Send(HttpMethod.Put, "vault/keys/OPENAI_API_KEY", "{\"value\":\"sk-owner-set\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("set").GetBoolean());

        Assert.Equal("sk-owner-set", new KeyVault(_vaultPath).Get("OPENAI_API_KEY"));
    }

    [Fact]
    public async Task The_owner_can_still_delete_a_key_and_it_takes_effect()
    {
        var resp = await Send(HttpMethod.Delete, "vault/keys/" + SecretName);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());

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
