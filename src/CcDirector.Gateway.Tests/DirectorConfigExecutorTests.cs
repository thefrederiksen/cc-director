using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup CUT RESTORATION: parity tests for the Director-level CONFIG verbs
/// (<see cref="DirectorConfigExecutor"/>) behind the Cockpit's Director Settings editor. The cut dropped the
/// Gateway's leg for <c>/directors/{id}/settings</c> but left the Cockpit calling it, so the request fell
/// through to the single-page-app fallback and the editor was handed the HTML shell at status 200.
///
/// Each verb is exercised through the real <see cref="SessionCommandExecutor.DispatchAsync"/> path (verb map
/// -&gt; area -&gt; core), the same way the Gateway stream down-channel reaches it, and asserts the behaviour
/// the Director's old SettingsEndpoint route had (deleted with the listener): the read returns the config verbatim, the
/// write deep-merges without dropping sibling sections, a non-object body is refused with the route's exact
/// wording, and a gateway patch re-applies the Gateway live.
///
/// Every method runs under an isolated CC_DIRECTOR_ROOT set in the constructor, so a test can never read or
/// write the real config.json on this machine.
/// </summary>
[Collection("DirectorRoot")] // serializes the classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class DirectorConfigExecutorTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;

    public DirectorConfigExecutorTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-dircfg-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static DirectorCommand Cmd(string verb, string payloadJson = "") =>
        new() { CommandId = "cfg", Verb = verb, SessionId = "", PayloadJson = payloadJson };

    /// <summary>The settings-put payload envelope: the patch travels as an opaque object under "settings".</summary>
    private static string PutPayload(string settingsJson) =>
        $"{{\"settings\":{settingsJson}}}";

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir!);
        File.WriteAllText(path, json);
    }

    private static string ReadConfigOnDisk() => File.ReadAllText(CcStorage.ConfigJson());

    // ---------- settings-get ----------

    [Fact]
    public async Task DispatchAsync_SettingsGet_ReturnsTheConfigOnDisk()
    {
        SeedConfig("""{ "addressing_mode": "lan", "gateway": { "url": "http://gw.example:7878" } }""");
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg", Cmd("settings-get"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var body = JsonNode.Parse(result.BodyJson ?? "")!.AsObject();
            Assert.Equal("lan", body["addressing_mode"]!.GetValue<string>());
            Assert.Equal("http://gw.example:7878", body["gateway"]!["url"]!.GetValue<string>());
        }
        finally { sm.Dispose(); }
    }

    // ---------- settings-put ----------

    [Fact]
    public async Task DispatchAsync_SettingsPut_MergesPatchAndPreservesSiblingSections()
    {
        // The data-loss guard the Director's own PUT /settings route has: a targeted patch must never drop a
        // block it did not mention.
        SeedConfig("""{ "gateway": { "url": "http://gw.example:7878" }, "screenshots": { "source_directory": "C:/old" } }""");
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg",
                Cmd("settings-put", PutPayload("""{ "screenshots": { "source_directory": "D:/new" } }""")));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var merged = JsonNode.Parse(result.BodyJson ?? "")!.AsObject();
            Assert.Equal("D:/new", merged["screenshots"]!["source_directory"]!.GetValue<string>());
            Assert.Equal("http://gw.example:7878", merged["gateway"]!["url"]!.GetValue<string>());

            // and it actually landed on disk, not just in the reply
            var onDisk = JsonNode.Parse(ReadConfigOnDisk())!.AsObject();
            Assert.Equal("D:/new", onDisk["screenshots"]!["source_directory"]!.GetValue<string>());
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SettingsPut_NonObjectBody_ReturnsBadRequestWithTheRoutesWording()
    {
        SeedConfig("""{ "gateway": { "url": "http://gw.example:7878" } }""");
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg",
                Cmd("settings-put", PutPayload("[1,2,3]")));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal("request body must be a JSON object", result.Error);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SettingsPut_MissingPayload_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg", Cmd("settings-put"));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal("request body must be a JSON object", result.Error);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SettingsPut_GatewayPatch_ReAppliesTheGatewayLive()
    {
        // A gateway change must take effect immediately, exactly as the Director's route re-applies it.
        SeedConfig("""{ "gateway": { "url": "http://old.example:7878" } }""");
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var reapplied = 0;
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg",
                Cmd("settings-put", PutPayload("""{ "gateway": { "url": "http://new.example:7878" } }""")),
                new SessionCommandServices
                {
                    ReapplyGatewayAsync = () => { reapplied++; return Task.CompletedTask; },
                });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(1, reapplied);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SettingsPut_NonGatewayPatch_DoesNotReApplyTheGateway()
    {
        // Only a gateway patch re-registers; everything else is read on next use. Mirrors the route's guard.
        SeedConfig("""{ "gateway": { "url": "http://gw.example:7878" } }""");
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var reapplied = 0;
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg",
                Cmd("settings-put", PutPayload("""{ "screenshots": { "source_directory": "D:/new" } }""")),
                new SessionCommandServices
                {
                    ReapplyGatewayAsync = () => { reapplied++; return Task.CompletedTask; },
                });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(0, reapplied);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SettingsPut_GatewayPatchWithNoReApplyHook_FailsLoudlyAndWritesNothing()
    {
        // The one place this verb refuses rather than degrading: with no re-apply hook wired, a gateway change
        // would be written to disk but never take effect - the running Director would disagree with its own
        // config and the person would be told it worked. It must fail BEFORE writing, leaving the file untouched.
        var seed = """{ "gateway": { "url": "http://old.example:7878" } }""";
        SeedConfig(seed);
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-cfg",
                Cmd("settings-put", PutPayload("""{ "gateway": { "url": "http://new.example:7878" } }""")),
                new SessionCommandServices()); // no ReapplyGatewayAsync

            Assert.Equal(DirectorCommandStatus.Error, result.Status);
            Assert.Contains("Nothing was written", result.Error ?? "", StringComparison.Ordinal);

            var onDisk = JsonNode.Parse(ReadConfigOnDisk())!.AsObject();
            Assert.Equal("http://old.example:7878", onDisk["gateway"]!["url"]!.GetValue<string>());
        }
        finally { sm.Dispose(); }
    }
}
