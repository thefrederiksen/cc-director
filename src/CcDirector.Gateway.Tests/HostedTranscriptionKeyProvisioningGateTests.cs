using CcDirector.Core;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins that a HOSTED Gateway cannot run either of the two key-vault operations in
/// <see cref="TranscriptionKeyAutoProvisioner"/>.
///
/// THE REPORTED DEFECT DOES NOT HOLD ON HOSTED TODAY, AND THAT IS THE POINT OF THIS FILE. A future reader
/// who finds only the gate will assume there was a live cross-tenant hole in production. There was not. As
/// deployed, the hosted Gateway is a Linux container, <c>GatewayHost</c> builds its DevThrottle credential
/// service only on Windows and macOS, and the provisioner and the sign-in service are both built only when
/// that credential service exists - so on hosted no provisioner is ever constructed, no sign-in flow is ever
/// mapped, and <c>POST /account/logout</c> short-circuits at "no credential service" before it reaches the
/// sign-out hook. Neither the write nor the delete can execute. This work was done because that safety was
/// UNASSERTED, not because it was absent.
///
/// SAFE ONLY BY WIRING is exactly the problem. The property held because of which platform hosted happens
/// to run on, and nothing anywhere said so. The day hosted gains a credential service - a product
/// direction, not a hypothesis - both harms below arm themselves and NOTHING GOES RED:
///
///   - BILLING INTEGRITY. <c>EnsureAsync</c> writes the shared, tenant-less key vault with SET-IF-ABSENT, so
///     the first tenant to sign in owns the key and every later tenant silently transcribes on it, spending
///     the first tenant's credits. Nobody is refused; nothing errors.
///   - AVAILABILITY. <c>RevokeMintedKeyAsync</c> revokes and deletes that same shared entry, so one tenant's
///     entirely ordinary sign-out breaks transcription for every other tenant on the box.
///
/// <see cref="Two_tenants_on_one_shared_vault_is_a_real_harm_when_the_provisioner_exists"/> DEMONSTRATES
/// both harms rather than describing them, on a provisioner explicitly told it is not hosted. It is also the
/// DESTRUCTIBILITY CONTROL for the hosted arm: without it, "the other tenant's key survived on hosted" would
/// be satisfied by a revoke that never destroys anything at all.
///
/// The gate under proof is a private constructor plus <see cref="TranscriptionKeyAutoProvisioner.CreateUnlessHosted"/>
/// as the only construction route. That is one removable line rather than a guard at the top of each method:
/// with two guards, either could be individually removed while the other stayed correct, so each would need
/// its own proof. With one construction route there is no second way to obtain an instance to bypass.
///
/// Revert-prove: delete the hosted branch from <c>CreateUnlessHosted</c> and
/// <see cref="Hosted_yields_no_provisioner_so_neither_shared_vault_operation_can_run"/>,
/// <see cref="On_hosted_a_second_tenant_cannot_inherit_or_destroy_a_first_tenants_key"/> and
/// <see cref="A_hosted_gateway_wires_no_transcription_key_provisioner"/> all go RED, while the two-tenant
/// harm demonstration and the self-host control stay GREEN - they are what must not move.
/// </summary>
public sealed class HostedTranscriptionKeyProvisioningGateTests : IDisposable
{
    /// <summary>One vault file for the whole class - which is the production shape being reasoned about:
    /// the key vault is ONE file per box, with no tenant dimension.</summary>
    private readonly string _sharedVaultPath =
        Path.Combine(Path.GetTempPath(), "ccd-hosted-gate-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_sharedVaultPath)) File.Delete(_sharedVaultPath); } catch { /* best effort */ }
    }

    /// <summary>Mints a distinct key per tenant and records what it was asked to revoke.</summary>
    private sealed class TenantMinter : IInferenceKeyMinter
    {
        private readonly string _key;
        private readonly string _id;
        public int MintCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public string? RevokedId { get; private set; }

        public TenantMinter(string key, string id) { _key = key; _id = id; }

        public Task<MintedInferenceKey?> MintAsync(string accessToken, string label, CancellationToken ct = default)
        {
            MintCalls++;
            return Task.FromResult<MintedInferenceKey?>(new MintedInferenceKey(_key, _id));
        }

        public Task<bool> RevokeAsync(string accessToken, string keyId, CancellationToken ct = default)
        {
            RevokeCalls++;
            RevokedId = keyId;
            return Task.FromResult(true);
        }
    }

    // ----- The gate itself -----

    [Fact]
    public void Hosted_yields_no_provisioner_so_neither_shared_vault_operation_can_run()
    {
        var provisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            new KeyVault(_sharedVaultPath), () => "an.account.jwt", new TenantMinter("dt_live_a", "id-a"),
            isHosted: true);

        // Null is the refusal, and it is the ONLY way to refuse: the constructor is private, so there is no
        // instance in existence on which EnsureAsync or RevokeMintedKeyAsync could be called.
        Assert.Null(provisioner);
    }

    [Fact]
    public void Self_host_still_gets_a_provisioner()
    {
        // The control for the gate: the refusal is specific to hosted and has not simply disabled the
        // feature everywhere. Without this, the hosted assertion above would also pass if CreateUnlessHosted
        // returned null unconditionally.
        var provisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            new KeyVault(_sharedVaultPath), () => "an.account.jwt", new TenantMinter("dt_live_a", "id-a"),
            isHosted: false);

        Assert.NotNull(provisioner);
    }

    // ----- The harm, demonstrated, and the destructibility control -----

    [Fact]
    public async Task Two_tenants_on_one_shared_vault_is_a_real_harm_when_the_provisioner_exists()
    {
        // Two tenants, both signing in, both reaching the SAME vault file - the hosted shape. Told
        // isHosted:false so the provisioner exists and its real behaviour is observable. This test asserts
        // the DEFECT, not the fix: it is what makes the hosted arms mean something.
        var vault = new KeyVault(_sharedVaultPath);
        var alice = new TenantMinter("dt_live_ALICE", "id-alice");
        var bob = new TenantMinter("dt_live_BOB", "id-bob");

        var aliceProvisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            vault, () => "alice.jwt", alice, isHosted: false)!;
        var bobProvisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            vault, () => "bob.jwt", bob, isHosted: false)!;

        // Alice signs in first and mints. She owns the shared entry.
        Assert.True(await aliceProvisioner.EnsureAsync());
        Assert.Equal("dt_live_ALICE", new KeyVault(_sharedVaultPath).Get(TranscriptionEndpointResolver.DevThrottleKeyName));

        // HARM 1 - BILLING. Bob signs in second. Set-if-absent means first writer wins: Bob never mints, and
        // the key the vault now serves HIM is Alice's, so Bob's transcription spends Alice's credits.
        Assert.False(await bobProvisioner.EnsureAsync());
        Assert.Equal(0, bob.MintCalls);
        Assert.Equal("dt_live_ALICE", new KeyVault(_sharedVaultPath).Get(TranscriptionEndpointResolver.DevThrottleKeyName));

        // HARM 2 - AVAILABILITY, and the DESTRUCTIBILITY CONTROL. Bob does something entirely ordinary: he
        // signs out. The revoke reads the shared id entry - Alice's - revokes it in the cloud with BOB's
        // token, and deletes the shared key. Alice is still signed in and now has no key at all.
        Assert.True(await bobProvisioner.RevokeMintedKeyAsync());
        Assert.Equal(1, bob.RevokeCalls);
        Assert.Equal("id-alice", bob.RevokedId);

        var afterBobSignedOut = new KeyVault(_sharedVaultPath);
        Assert.Null(afterBobSignedOut.Get(TranscriptionEndpointResolver.DevThrottleKeyName));
        Assert.Null(afterBobSignedOut.Get(TranscriptionKeyAutoProvisioner.InferenceKeyIdVaultName));
    }

    [Fact]
    public async Task On_hosted_a_second_tenant_cannot_inherit_or_destroy_a_first_tenants_key()
    {
        // The same two tenants, the same shared vault, hosted this time. Neither can obtain a provisioner,
        // so the shared-vault path is closed at the only door: there is no key for a second tenant to
        // inherit and no key for a sign-out to destroy. The vault is never written at all.
        var vault = new KeyVault(_sharedVaultPath);
        var alice = new TenantMinter("dt_live_ALICE", "id-alice");
        var bob = new TenantMinter("dt_live_BOB", "id-bob");

        var aliceProvisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            vault, () => "alice.jwt", alice, isHosted: true);
        var bobProvisioner = TranscriptionKeyAutoProvisioner.CreateUnlessHosted(
            vault, () => "bob.jwt", bob, isHosted: true);

        Assert.Null(aliceProvisioner);
        Assert.Null(bobProvisioner);

        // Nothing minted, and the shared vault holds no transcription credential for anyone to spend or
        // revoke. Read from a fresh KeyVault so this observes the FILE, not an in-memory instance.
        Assert.Equal(0, alice.MintCalls);
        Assert.Equal(0, bob.MintCalls);
        var reread = new KeyVault(_sharedVaultPath);
        Assert.Null(reread.Get(TranscriptionEndpointResolver.DevThrottleKeyName));
        Assert.Null(reread.Get(TranscriptionKeyAutoProvisioner.InferenceKeyIdVaultName));
    }

    // ----- The wiring: the gate is fed by the REAL hosted signal -----

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A Gateway credential service over an in-memory store. Injected deliberately rather than left to the
    /// platform: it is the whole point of these two tests to model the day HOSTED HAS A CREDENTIAL SERVICE,
    /// which is precisely when the accidental safety stops holding. Injecting it also makes the tests
    /// platform-independent instead of quietly passing on Linux because no credential service exists there.
    /// </summary>
    private static DevThrottleAccountService MakeAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, "a-test-signing-secret-for-the-hosted-gate");
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-hosted-gate-" + Guid.NewGuid().ToString("N") + ".jsonl");
            return GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    /// <summary>
    /// Constructs a real GatewayHost with a real credential service under a stated hosted setting, and hands
    /// back what it wired. The wiring happens in the constructor, so nothing is started and no port is bound.
    /// The storage root is isolated so this never writes the running user's real vault. The assembly runs
    /// sequentially, so toggling CC_GATEWAY_HOSTED here is safe; it is restored in the finally.
    /// </summary>
    private static async Task<bool> WiresAProvisionerAsync(bool hosted)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-hosted-gate-root-" + Guid.NewGuid().ToString("N"));
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            await using var gateway = new GatewayHost(
                port: 0, token: "test-token", authEnabled: true,
                instancesDirectory: Path.Combine(root, "instances"),
                keyVaultPath: Path.Combine(root, "keyvault.json"),
                workListsPath: Path.Combine(root, "worklists", "worklists.json"),
                snoozePath: Path.Combine(root, "snooze", "snooze.json"),
                account: MakeAccount());

            return gateway.TranscriptionKeyProvisioner is not null;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", priorRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_hosted_gateway_wires_no_transcription_key_provisioner()
    {
        // The credential service is PRESENT here, so this is not passing for the incidental reason hosted is
        // safe today. It is the real CC_GATEWAY_HOSTED signal that must close it, which is what pins the gate
        // to the production hosted flag rather than to a boolean a test hands it.
        Assert.False(await WiresAProvisionerAsync(hosted: true));
    }

    [Fact]
    public async Task A_self_hosted_gateway_still_wires_the_provisioner()
    {
        // The control for the wiring assertion: the same construction with the same credential service and
        // ONLY CC_GATEWAY_HOSTED differing still wires a provisioner, so the hosted result above is caused by
        // the hosted signal and not by the account, the paths, or the isolated root.
        Assert.True(await WiresAProvisionerAsync(hosted: false));
    }
}
