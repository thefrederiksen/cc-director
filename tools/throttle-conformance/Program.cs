using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Throttle;
using Microsoft.EntityFrameworkCore;

// THE LIBRARY'S COMMAND LINE (mission "Clean up Your Throttle"). It runs the Gateway's OWN definition -
// ThrottleDefinition, fed by ThrottleLedgerReader, the code behind GET /stats/data - against a Gateway
// database named on the command line, for one tenant over one window, and prints the figure as the same
// camel-cased JSON the feed serves under "throttle".
//
// TWO CALLERS, and it is a second implementation for neither:
//   1. The conformance check (phase three): conformance.py runs this, computes the same figure through the
//      mentor harness's own reader of the same ledger, and fails when they disagree.
//   2. The mentor report itself (phase five, ruling R3): the report asks THIS tool for its figure rather
//      than computing a ring of its own, which is what makes the report a consumer of the library and the
//      report's number and the page's number one number.
// The project name is historical; do not rename it, both callers know it by this name.
//
// READ-ONLY BY CONSTRUCTION. It opens a plain DbContext over the connection string and never touches
// GatewayDatabase.Open(), which checks for and applies pending migrations - a conformance check must never
// be the thing that migrates the production schema. Nothing here writes.
//
// Usage:
//   throttle-conformance --tenant <id> --from <iso-utc> --to <iso-utc> [--connection <npgsql string>] [--out <file>]
//   The connection string may instead come from CC_GATEWAY_DB_CONNECTION or DEVTHROTTLE_GATEWAY_DB_CONNECTION.
//   Exit 0 with the JSON on stdout (or in --out); exit 2 on a usage error; exit 1 on a failure to read.

var args_ = Parse(args);
if (args_ is null) return 2;

var connection = args_.Connection
    ?? Environment.GetEnvironmentVariable("CC_GATEWAY_DB_CONNECTION")
    ?? Environment.GetEnvironmentVariable("DEVTHROTTLE_GATEWAY_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("No connection string. Pass --connection, or set CC_GATEWAY_DB_CONNECTION.");
    return 2;
}

try
{
    var options = new DbContextOptionsBuilder<GatewayDbContext>()
        .UseNpgsql(connection)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .Options;

    var reader = new ThrottleLedgerReader(tenant =>
    {
        var ctx = new GatewayDbContext(options);
        ctx.ActiveTenant = tenant.Value;
        return ctx;
    });

    var figure = reader.Compute(new TenantId(args_.Tenant), args_.FromUtc, args_.ToUtc);
    // The same window shape GET /stats/data serves for an explicit from/to: kind, label, and the selector's
    // choices, so the JSON this prints is the feed's shape and a consumer reads one shape, not two.
    figure.Window.IsDefault = false;
    figure.Window.Kind = ThrottleWindowKinds.Explicit;
    figure.Window.Label = $"{args_.FromUtc:yyyy-MM-dd HH:mm} to {args_.ToUtc:yyyy-MM-dd HH:mm} UTC";
    figure.Window.Choices = ThrottleWindowChoices.Serve();

    var json = JsonSerializer.Serialize(figure, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    });

    if (args_.Out is { } outPath)
    {
        File.WriteAllText(outPath, json);
        Console.Error.WriteLine($"library figure: tenant={args_.Tenant} window={args_.FromUtc:O}..{args_.ToUtc:O} " +
                                $"turns={figure.Turns} voice={figure.VoiceTurns} typed={figure.TypedTurns} " +
                                $"noOrigin={figure.Excluded.NoInputOrigin} -> {outPath}");
    }
    else
    {
        Console.Out.WriteLine(json);
    }
    return 0;
}
catch (Exception ex)
{
    // The connection string is never echoed: the provider's own message can carry part of it.
    Console.Error.WriteLine($"FAILED to compute the library figure: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static Arguments? Parse(string[] argv)
{
    string? tenant = null, from = null, to = null, connection = null, outPath = null;
    for (var i = 0; i < argv.Length; i++)
    {
        string Next()
        {
            if (i + 1 >= argv.Length) throw new ArgumentException($"{argv[i]} needs a value");
            return argv[++i];
        }
        try
        {
            switch (argv[i])
            {
                case "--tenant": tenant = Next(); break;
                case "--from": from = Next(); break;
                case "--to": to = Next(); break;
                case "--connection": connection = Next(); break;
                case "--out": outPath = Next(); break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {argv[i]}");
                    return null;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }
    if (string.IsNullOrWhiteSpace(tenant) || from is null || to is null)
    {
        Console.Error.WriteLine("Usage: throttle-conformance --tenant <id> --from <iso-utc> --to <iso-utc> [--connection <npgsql>] [--out <file>]");
        return null;
    }
    if (!TryUtc(from, out var fromUtc) || !TryUtc(to, out var toUtc))
    {
        Console.Error.WriteLine("--from and --to must be ISO 8601 instants.");
        return null;
    }
    if (toUtc <= fromUtc)
    {
        Console.Error.WriteLine("--to must be later than --from.");
        return null;
    }
    return new Arguments(tenant!, fromUtc, toUtc, connection, outPath);
}

static bool TryUtc(string text, out DateTime utc)
{
    var ok = DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
        out var parsed);
    utc = ok ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : default;
    return ok;
}

internal sealed record Arguments(string Tenant, DateTime FromUtc, DateTime ToUtc, string? Connection, string? Out);
