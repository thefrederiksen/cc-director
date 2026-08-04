using CcDirector.Core.Instances;
using CcDirector.Core.Lifecycle;

// The lifecycle-signal probe: one end of a cross-process signal test. See the csproj comment for why
// this exists. Each verb drives the PRODUCTION LifecycleSignal under the environment the spawning
// test gave this process - no signal logic is re-implemented here.
//
// "-redirected" verbs first do exactly what a real Director's Program.Main does before it touches any
// signal: capture the shared root into InstanceContext, then point CC_DIRECTOR_ROOT at the instance
// home. That redirect is the production condition the cross-process tests exist to reproduce, because
// it is what made the two ends of a signal resolve different request-file directories.
//
// Protocol: a listener prints LISTENING when armed, then SIGNALLED or TIMED-OUT (exit 0 / 1).
// A raiser prints RAISED delivered=<bool> and exits 0 when the raise reported delivery.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: <listen|listen-redirected|raise|raise-redirected> <signal-name> [seconds]");
    return 2;
}

var verb = args[0];
var name = args[1];
var seconds = args.Length > 2 ? int.Parse(args[2]) : 15;

if (verb.EndsWith("-redirected", StringComparison.Ordinal))
{
    // Program.cs ResolveInstance, reduced to its two effects: SharedRoot captured before the
    // redirect, then the whole data tree pointed at the instance home.
    InstanceContext.Initialize(null, wasExplicit: false);
    Directory.CreateDirectory(InstanceContext.InstanceHome);
    Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", InstanceContext.InstanceHome);
}

switch (verb)
{
    case "listen" or "listen-redirected":
    {
        using var arrived = new ManualResetEventSlim(false);
        using var listener = LifecycleSignal.Listen(name, () => arrived.Set());
        Console.WriteLine("LISTENING");
        Console.Out.Flush();
        var ok = arrived.Wait(TimeSpan.FromSeconds(seconds));
        Console.WriteLine(ok ? "SIGNALLED" : "TIMED-OUT");
        if (!ok)
        {
            var expected = Path.Combine(InstanceContext.SharedRoot, "config", "lifecycle-signals",
                name + ".request");
            var dir = Path.GetDirectoryName(expected)!;
            var listing = Directory.Exists(dir)
                ? string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))
                : "(directory absent)";
            Console.WriteLine($"diagnostic: sharedRoot={InstanceContext.SharedRoot} "
                              + $"env={Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT")} "
                              + $"expectedPath={expected} existsNow={File.Exists(expected)} "
                              + $"dirListing=[{listing}]");
        }
        return ok ? 0 : 1;
    }

    case "raise" or "raise-redirected":
    {
        var delivered = LifecycleSignal.Raise(name);
        Console.WriteLine($"RAISED delivered={delivered}");
        return delivered ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"unknown verb: {verb}");
        return 2;
}
