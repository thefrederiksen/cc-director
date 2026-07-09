using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="GatewayConfig"/>, focused on the same-machine credential resolution: a
/// Gateway-role install never pairs its own Director, so config.json carries no gateway.token; when
/// the configured Gateway is THIS machine's own Gateway, <see cref="GatewayConfig.Load"/> presents the
/// local shared machine token from gateway-token.txt instead of sending an empty bearer (which host-wide
/// auth enforcement rejects with 401). The resolution is deliberately scoped to a LOCAL Gateway URL.
/// All methods share an isolated CC_DIRECTOR_ROOT set in the constructor; xUnit runs a class's methods
/// sequentially.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class GatewayConfigTests : IDisposable
{
    private const string LocalToken = "local-shared-machine-token-abc123";
    private readonly string _root;
    private readonly string? _prevRoot;

    public GatewayConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-gwconfig-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static void SeedGatewayTokenFile(string token)
    {
        // Compute the path fresh (GatewayCredentialStore.CredentialFile is a cached static that would
        // capture the first test's CC_DIRECTOR_ROOT); this is the same file the production code reads.
        var path = Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);
    }

    // ===== IsLocalGatewayHost (pure) =====

    [Theory]
    [InlineData("http://127.0.0.1:7878")]
    [InlineData("http://localhost:7878")]
    [InlineData("https://LOCALHOST:7878")]
    public void IsLocalGatewayHost_true_for_loopback_and_localhost(string url)
        => Assert.True(GatewayConfig.IsLocalGatewayHost(url));

    [Fact]
    public void IsLocalGatewayHost_true_for_this_machine_name()
        => Assert.True(GatewayConfig.IsLocalGatewayHost($"http://{Environment.MachineName}:7878"));

    [Fact]
    public void IsLocalGatewayHost_true_for_this_machines_tailscale_magicdns_name()
    {
        // Tailscale lowercases the hostname and turns '_' into '-'.
        var normalized = Environment.MachineName.ToLowerInvariant().Replace('_', '-');
        Assert.True(GatewayConfig.IsLocalGatewayHost($"http://{normalized}.example-tailnet.ts.net:7878"));
    }

    [Theory]
    [InlineData("http://some-other-host.example-tailnet.ts.net:7878")]
    [InlineData("http://192.168.1.50:7878")]
    [InlineData("")]
    [InlineData("not a url")]
    public void IsLocalGatewayHost_false_for_remote_or_invalid(string url)
        => Assert.False(GatewayConfig.IsLocalGatewayHost(url));

    // ===== Load: same-machine token resolution =====

    [Fact]
    public void Load_resolves_local_token_when_token_empty_and_gateway_is_local()
    {
        SeedGatewayTokenFile(LocalToken);
        SeedConfig("""
        { "gateway": { "url": "http://127.0.0.1:7878", "token": "" } }
        """);

        var cfg = GatewayConfig.Load();

        Assert.Equal(LocalToken, cfg.Token);
        Assert.True(cfg.IsEnabled);
    }

    [Fact]
    public void Load_does_not_resolve_local_token_for_a_remote_gateway()
    {
        // A stale/foreign gateway-token.txt must never be presented to a DIFFERENT Gateway.
        SeedGatewayTokenFile(LocalToken);
        SeedConfig("""
        { "gateway": { "url": "http://some-other-host.example-tailnet.ts.net:7878", "token": "" } }
        """);

        var cfg = GatewayConfig.Load();

        Assert.Equal("", cfg.Token);
    }

    [Fact]
    public void Load_keeps_configured_token_and_never_overrides_it()
    {
        SeedGatewayTokenFile(LocalToken);
        SeedConfig("""
        { "gateway": { "url": "http://127.0.0.1:7878", "token": "per-device-key-xyz" } }
        """);

        var cfg = GatewayConfig.Load();

        Assert.Equal("per-device-key-xyz", cfg.Token);
    }

    [Fact]
    public void Load_leaves_token_empty_when_local_gateway_but_no_token_file()
    {
        SeedConfig("""
        { "gateway": { "url": "http://127.0.0.1:7878", "token": "" } }
        """);

        var cfg = GatewayConfig.Load();

        Assert.Equal("", cfg.Token);
    }

    // ===== Issue #1176 (Phase 1a): streamMode + staleAfterSeconds =====

    [Fact]
    public void Load_streamMode_defaults_off_when_absent()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw:7878" } }""");

        var cfg = GatewayConfig.Load();

        Assert.False(cfg.StreamMode);
        Assert.Equal(GatewayConfig.DefaultStreamStaleAfterSeconds, cfg.StreamStaleAfterSeconds);
    }

    [Fact]
    public void Load_streamMode_true_when_configured()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw:7878", "streamMode": true } }""");

        var cfg = GatewayConfig.Load();

        Assert.True(cfg.StreamMode);
    }

    [Fact]
    public void Load_streamMode_false_for_non_boolean_value()
    {
        // Only a JSON boolean true enables it; a string "true" must not.
        SeedConfig("""{ "gateway": { "url": "http://gw:7878", "streamMode": "true" } }""");

        var cfg = GatewayConfig.Load();

        Assert.False(cfg.StreamMode);
    }

    [Fact]
    public void Load_staleAfterSeconds_reads_positive_value()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw:7878", "staleAfterSeconds": 45 } }""");

        var cfg = GatewayConfig.Load();

        Assert.Equal(45, cfg.StreamStaleAfterSeconds);
    }

    [Fact]
    public void Load_staleAfterSeconds_ignores_non_positive_value()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw:7878", "staleAfterSeconds": 0 } }""");

        var cfg = GatewayConfig.Load();

        Assert.Equal(GatewayConfig.DefaultStreamStaleAfterSeconds, cfg.StreamStaleAfterSeconds);
    }
}
