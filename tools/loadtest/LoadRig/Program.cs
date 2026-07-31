using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.LoadTest.Shared;

// The Gateway load-test rig (devthrottle_internal issue #1173, mission 05).
//
// Boots the REAL GatewayHost in hosted mode against a THROWAWAY Postgres, seeds synthetic tenants and
// device keys, writes the key files the load drivers (k6, DirectorSim) read, then serves as the target
// until stopped. Teardown of the synthetic tenants is the teardown of the throwaway database container
// (tools/loadtest/scripts/stop-postgres.ps1 removes the container AND its volume) - no synthetic tenant
// can survive into a real database because this rig refuses to start against anything but a local (or
// explicitly named non-production) database host.
//
// Environment:
//   CC_GATEWAY_DB_CONNECTION   REQUIRED. The throwaway Postgres connection string. The host inside it
//                              must be local (or named via LOADTEST_ALLOW_HOST); production is refused.
//   LOADTEST_PORT              Port to listen on. Default 7891 (deliberately NOT 7878, the real default,
//                              so a live local Gateway is never mistaken for the rig).
//   LOADTEST_TENANTS           Synthetic tenants to seed. Default 20.
//   LOADTEST_DIRECTORS_PER_TENANT  Synthetic Director identities (and keys) per tenant. Default 5.
//   LOADTEST_OUT_DIR           Where the key files are written. Default ./loadtest-out.
//   CC_DIRECTOR_ROOT           Set by this program to an isolated scratch directory if not already set,
//                              so the rig never touches the machine's real cc-director storage.

var port = ReadInt("LOADTEST_PORT", 7891);
var tenantCount = ReadInt("LOADTEST_TENANTS", 20);
var directorsPerTenant = ReadInt("LOADTEST_DIRECTORS_PER_TENANT", 5);
var outDir = Environment.GetEnvironmentVariable("LOADTEST_OUT_DIR") ?? Path.Combine(Environment.CurrentDirectory, "loadtest-out");

var dbConnection = Environment.GetEnvironmentVariable(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar);
if (string.IsNullOrWhiteSpace(dbConnection))
{
    Console.Error.WriteLine(
        "ERROR: CC_GATEWAY_DB_CONNECTION is not set. The rig runs the hosted path, which is Postgres. " +
        "Start the throwaway database first: powershell -File tools/loadtest/scripts/start-postgres.ps1 " +
        "- it prints the connection string to export.");
    return 2;
}

// The one hard rule: never production. The database host must be local or an explicitly named rig.
LoadTargetGuard.AssertHostAllowed(ReadHostFromConnectionString(dbConnection), "CC_GATEWAY_DB_CONNECTION");

// Isolate ALL file-based Gateway storage from the machine's real cc-director root.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT")))
{
    var scratchRoot = Path.Combine(Path.GetTempPath(), "dt-loadtest-root-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(scratchRoot);
    Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", scratchRoot);
    Console.WriteLine($"[LoadRig] CC_DIRECTOR_ROOT isolated to {scratchRoot}");
}

// Hosted-by-environment, NOT hosted-image: exactly the seam the Gateway's own hosted tests use. With no
// image marker, DeviceRegistry.OnAccountBoundForTest auto-seeds an entitlement per synthetic tenant, so
// the request path's hosted 402 gate does not fire for them.
Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
if (GatewayHostedMode.IsHostedImage)
{
    Console.Error.WriteLine("ERROR: this build carries the hosted-image marker; the rig must never run as the hosted image.");
    return 2;
}

// The PRODUCTION hosted image mirrors every FileLog line synchronously to the console
// (GatewayEntryPoint does this when IsHostedImage). The rig is not the hosted image, so that cost is
// absent by default; set LOADTEST_MIRROR_CONSOLE=1 to reproduce it when measuring how much it matters.
// Either way, record which mode a run used in its baseline notes - it is a real difference.
if (Environment.GetEnvironmentVariable("LOADTEST_MIRROR_CONSOLE") == "1")
{
    CcDirector.Core.Utilities.FileLog.MirrorToConsole = true;
    Console.WriteLine("[LoadRig] FileLog console mirror ON (matching the production hosted image)");
}

const string rigToken = "loadtest-rig-token";
Console.WriteLine($"[LoadRig] starting GatewayHost: port={port} hosted=env postgres={ReadHostFromConnectionString(dbConnection)}");
var gateway = new GatewayHost(port: port, token: rigToken, authEnabled: true, streamMode: true);
await gateway.StartAsync();
Console.WriteLine($"[LoadRig] Gateway up at http://127.0.0.1:{gateway.Port}");

// ---- Seed synthetic tenants and device keys through the real registries. --------------------------
var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var viewers = new List<object>();
var directors = new List<object>();
var seedStart = DateTime.UtcNow;

for (var t = 1; t <= tenantCount; t++)
{
    // A GUID subject, because that is the production shape: account subjects are Supabase uuids, and
    // on Postgres the website-owned gateway.entitlements.subject column IS uuid - a non-uuid synthetic
    // subject could never be entitled there.
    var subject = Guid.NewGuid().ToString();
    var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, $"loadtest-{t:D4}@loadtest.invalid");

    var viewerDeviceId = $"loadtest-viewer-{runId}-{t:D4}";
    var viewerKey = gateway.Devices.Register(viewerDeviceId, $"LOADTEST-VIEWER-{t:D4}").DeviceKey;
    gateway.Devices.SetAccountBinding(viewerDeviceId, subject, tenant.Value);
    viewers.Add(new { tenant = tenant.Value, deviceKey = viewerKey });

    for (var d = 1; d <= directorsPerTenant; d++)
    {
        var directorDeviceId = $"loadtest-dir-{runId}-{t:D4}-{d:D4}";
        var directorKey = gateway.Devices.Register(directorDeviceId, $"LOADTEST-DIR-{t:D4}-{d:D4}").DeviceKey;
        gateway.Devices.SetAccountBinding(directorDeviceId, subject, tenant.Value);
        directors.Add(new
        {
            tenant = tenant.Value,
            directorId = $"loadtest-director-{t:D4}-{d:D4}",
            machineName = $"LOADTEST-DIR-{t:D4}-{d:D4}",
            deviceKey = directorKey,
        });
    }

    if (t % 25 == 0 || t == tenantCount)
        Console.WriteLine($"[LoadRig] seeded {t}/{tenantCount} tenants ({directors.Count} director keys) in {(DateTime.UtcNow - seedStart).TotalSeconds:F0}s");
}

Directory.CreateDirectory(outDir);
var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
var viewersFile = Path.Combine(outDir, "viewers.json");
var directorsFile = Path.Combine(outDir, "directors.json");
var rigFile = Path.Combine(outDir, "rig.json");
File.WriteAllText(viewersFile, JsonSerializer.Serialize(viewers, jsonOptions));
File.WriteAllText(directorsFile, JsonSerializer.Serialize(directors, jsonOptions));
File.WriteAllText(rigFile, JsonSerializer.Serialize(new
{
    runId,
    gatewayUrl = $"http://127.0.0.1:{gateway.Port}",
    port = gateway.Port,
    tenants = tenantCount,
    directorsPerTenant,
    seededAtUtc = DateTime.UtcNow,
}, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"[LoadRig] keys written: {viewersFile} ({viewers.Count} viewers), {directorsFile} ({directors.Count} directors)");
Console.WriteLine($"RIG READY url=http://127.0.0.1:{gateway.Port} tenants={tenantCount} directors={directors.Count} out={outDir}");
Console.WriteLine("[LoadRig] press Ctrl+C to stop. Teardown: stop the rig, then remove the throwaway " +
                  "database (tools/loadtest/scripts/stop-postgres.ps1) so no synthetic tenant survives.");

// Run until Ctrl+C, then stop the Gateway cleanly.
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
Console.WriteLine("[LoadRig] stopping...");
await gateway.StopAsync();
Console.WriteLine("[LoadRig] stopped. Remember: tools/loadtest/scripts/stop-postgres.ps1 removes the synthetic tenants with the database.");
return 0;

static int ReadInt(string variable, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    if (!int.TryParse(raw, out var value) || value <= 0)
        throw new InvalidOperationException($"{variable} must be a positive integer, got '{raw}'.");
    return value;
}

static string ReadHostFromConnectionString(string connectionString)
{
    foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0) continue;
        var key = part[..eq].Trim();
        if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            return part[(eq + 1)..].Trim();
    }
    throw new InvalidOperationException("CC_GATEWAY_DB_CONNECTION has no Host= (or Server=) part; the rig cannot verify the database is local, so it refuses to start.");
}
