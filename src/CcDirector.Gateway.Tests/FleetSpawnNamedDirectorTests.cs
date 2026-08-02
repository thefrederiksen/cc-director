using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director floor's routing decision for <c>POST /fleet/spawn</c> when the caller names ONE
/// Director (<c>session spawn --director</c>): is it me, and if not, where does it run?
///
/// Driven through the REAL endpoint against a Kestrel stub standing in for the Gateway, because the
/// decision is made in the endpoint and nowhere else - a test of the pieces would stay green while the
/// route dropped the field, resolved the wrong Director, or reported a spawn that landed elsewhere.
///
/// The stub's Director list deliberately holds TWO Directors on ONE machine, which is the case the
/// machine name cannot answer and this feature exists for.
/// </summary>
public sealed class FleetSpawnNamedDirectorTests
{
    private const string LocalDirectorId = "11111111-1111-1111-1111-111111111111";
    private const string OtherDirectorId = "22222222-2222-2222-2222-222222222222";
    private const string FirstOnMachineId = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// The machine the tests treat as REMOTE. Derived from this host's own name rather than written as
    /// a literal, because a literal is only remote until somebody's machine is called that - and then
    /// the endpoint takes its LOCAL branch, never calls the stub, and the test fails for a reason that
    /// has nothing to do with the code. (A literal "SOREN_NORTH" did exactly that on this repository's
    /// own test host.)
    /// </summary>
    private static readonly string RemoteMachine = Environment.MachineName + "-REMOTE";

    /// <summary>The machine the local Director reports as its own - what the endpoint compares against.</summary>
    private static readonly string LocalMachine = Environment.MachineName;

    private sealed class GatewayStub
    {
        public NewSessionRequest? SpawnBody { get; set; }
        public string? SpawnMachine { get; set; }
        /// <summary>The Director the stub claims took the session - how an OLD Gateway that ignored the
        /// named target is simulated: it answers with a different Director than the one asked for.</summary>
        public string LandsOn { get; set; } = OtherDirectorId;
        /// <summary>Extra rows spliced into the Director list, for the duplicate-name cases.</summary>
        public List<DirectorDto> Extra { get; } = new();
        /// <summary>Make the Director list answer 500 - a Gateway that is present but cannot be read,
        /// which is how an outage and a not-yet-registered Director look from the floor.</summary>
        public bool DirectorsFail { get; set; }
    }

    private static async Task<(WebApplication app, string url, GatewayStub stub)> StartGatewayStubAsync()
    {
        var stub = new GatewayStub();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.MapGet("/directors", () => stub.DirectorsFail
            ? Results.Json(new { error = "roster unavailable" }, statusCode: 500)
            : Results.Json(new List<DirectorDto>
        {
            // First on the remote machine, and NOT the one the tests name: a resolve that ignored the
            // name would land here, which is what makes these assertions meaningful rather than
            // order-lucky.
            new() { DirectorId = FirstOnMachineId, MachineName = RemoteMachine, DisplayName = "North daily" },
            new() { DirectorId = OtherDirectorId, MachineName = RemoteMachine, DisplayName = "North build" },
            new() { DirectorId = LocalDirectorId, MachineName = LocalMachine, DisplayName = "This one" },
        }.Concat(stub.Extra)));

        app.MapPost("/machines/{machine}/sessions", (string machine, NewSessionRequest body) =>
        {
            stub.SpawnMachine = machine;
            stub.SpawnBody = body;
            return Results.Json(new SessionDto
            {
                SessionId = Guid.NewGuid().ToString(),
                DirectorId = stub.LandsOn,
                MachineName = machine,
            }, statusCode: 201);
        });

        await app.StartAsync();
        return (app, app.Urls.First(), stub);
    }

    private static async Task<(WebApplication app, HttpClient http, SessionManager sm)> StartDirectorAsync(
        GatewayClient gateway)
    {
        var sm = new SessionManager(new AgentOptions());
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        ControlEndpoints.Map(app, sm, LocalDirectorId, "1.0.0-test", () => Task.CompletedTask,
            gatewayClientProvider: () => gateway);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, sm);
    }

    private static async Task<(HttpResponseMessage resp, GatewayStub stub)> SpawnAsync(
        object body, Action<GatewayStub>? arrange = null)
    {
        var (gwApp, url, stub) = await StartGatewayStubAsync();
        await using var _ = gwApp;
        arrange?.Invoke(stub);
        using var gateway = new GatewayClient(new GatewayConfig { Url = url }, LocalDirectorId, 7879, "1.0.0");
        var (director, http, sm) = await StartDirectorAsync(gateway);
        await using var __ = director;
        using var ___ = http;
        using var ____ = sm;

        var resp = await http.PostAsJsonAsync("/fleet/spawn", body);
        // Read the body before the stub is disposed - the caller asserts on it.
        await resp.Content.LoadIntoBufferAsync();
        return (resp, stub);
    }

    // Naming THIS Director keeps the spawn local: it is resolved against the fleet, comes back as this
    // Director, and is never relayed. The stub records no spawn - the assertion that separates "handled
    // here" from "went out and came back".
    //
    // This is the plain good path and is NOT by itself a proof of the new flag - a request naming this
    // machine takes the local branch on origin/main too. The proofs that distinguish new from old are
    // the contradiction, duplicate-name and hijack tests below.
    [Fact]
    public async Task NamingThisDirectorById_spawnsLocally()
    {
        var (resp, stub) = await SpawnAsync(new
        {
            repoPath = @"C:\definitely\not\a\repo",
            machine = LocalMachine,
            director = LocalDirectorId,
        });

        Assert.Null(stub.SpawnBody);
        // The repo path is bogus, so the LOCAL create is what rejects it - proof it took the local leg
        // rather than being relayed. A relay would have returned the stub's 201.
        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
    }

    // A Director must be able to answer for its OWN id with no working Gateway. The id is issued by the
    // system and unique, so this process can prove the target is itself - and making that depend on a
    // healthy roster would fail a provably-correct local spawn during a Gateway outage, or during the
    // ordinary lag before a just-started Director appears in the list, with "no Director is registered".
    // That message would be false: it is the Director answering the call.
    [Fact]
    public async Task ItsOwnId_resolvesWithoutTheGateway_evenWhenTheRosterCannotBeRead()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\definitely\not\a\repo", director = LocalDirectorId },
            s => s.DirectorsFail = true);          // the roster call answers 500

        Assert.Null(stub.SpawnBody);
        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
        // Rejected by the LOCAL create for the bogus path, not by a roster failure.
        Assert.DoesNotContain("Gateway", await resp.Content.ReadAsStringAsync());
    }

    // The same outage, with a DISPLAY NAME. This one cannot be answered locally: a sibling Director may
    // hold the same name, and only the roster can say. It must fail loudly rather than assume.
    [Fact]
    public async Task ADisplayName_cannotBeResolvedWhenTheRosterCannotBeRead()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\repo", director = "This one" },
            s => s.DirectorsFail = true);

        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
        Assert.Null(stub.SpawnBody);
    }

    // A named Director and a machine that CONTRADICT each other. The id identifies a Director that runs
    // here; the machine field says somewhere else. Honoring either half silently would put the session
    // somewhere the caller did not fully ask for, so the machine narrows the match to nothing and it
    // fails - the same rule the Gateway resolver applies.
    [Fact]
    public async Task NamingThisDirector_withAMachineItDoesNotRunOn_failsRatherThanPickingAHalf()
    {
        var (resp, stub) = await SpawnAsync(new
        {
            repoPath = @"C:\repo",
            machine = RemoteMachine,
            director = LocalDirectorId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(stub.SpawnBody);
    }

    // THE DUPLICATE-NAME TRAP. This Director and a remote one are BOTH called "Build box". Deciding
    // locally first - "is that name mine? yes" - claims the name, spawns here, and reports success,
    // and the caller is never told there were two. The name must be resolved against the fleet, which
    // sees both and refuses.
    [Fact]
    public async Task ADisplayNameSharedWithThisDirector_isAmbiguous_notQuietlyLocal()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\definitely\not\a\repo", director = "Build box" },
            s =>
            {
                s.Extra.Add(new DirectorDto
                {
                    DirectorId = LocalDirectorId, MachineName = LocalMachine, DisplayName = "Build box",
                });
                s.Extra.Add(new DirectorDto
                {
                    DirectorId = OtherDirectorId, MachineName = RemoteMachine, DisplayName = "Build box",
                });
            });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(LocalDirectorId, body);
        Assert.Contains(OtherDirectorId, body);
        Assert.Null(stub.SpawnBody);
    }

    // The same duplicate name, with --machine naming the REMOTE one. A local-first decision ignores the
    // machine entirely and still spawns here; resolving against the fleet narrows to the remote row and
    // relays there.
    [Fact]
    public async Task ADuplicateDisplayName_isDisambiguatedByTheMachine_andRelays()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\repo", machine = RemoteMachine, director = "Build box" },
            s =>
            {
                s.Extra.Add(new DirectorDto
                {
                    DirectorId = LocalDirectorId, MachineName = LocalMachine, DisplayName = "Build box",
                });
                s.Extra.Add(new DirectorDto
                {
                    DirectorId = OtherDirectorId, MachineName = RemoteMachine, DisplayName = "Build box",
                });
                s.LandsOn = OtherDirectorId;
            });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(RemoteMachine, stub.SpawnMachine);
        // And the relay carries the id of the remote twin, so the Gateway's own resolve cannot pick the
        // local one that shares the name.
        Assert.Equal(OtherDirectorId, stub.SpawnBody!.Director);
    }

    // ID PRECEDENCE at the endpoint, framed so equal-precedence matching gets it WRONG. One remote
    // Director's DISPLAY NAME is set to ANOTHER remote Director's id - free text, so nothing stops it.
    // Naming that id must resolve to its owner and relay there. Matching ids and names at equal rank
    // would find two candidates and refuse a request that is perfectly unambiguous, which is the
    // guarantee the id exists to provide.
    //
    // Both candidates are REMOTE on purpose: a local one would be answered by the own-id shortcut and
    // the precedence rule would never be exercised.
    [Fact]
    public async Task AnIdResolvesToItsOwner_evenWhenAnotherDirectorIsNamedThatId()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\repo", director = OtherDirectorId },
            s =>
            {
                s.Extra.Add(new DirectorDto
                {
                    // The impostor: its display name IS the id being asked for.
                    DirectorId = "44444444-4444-4444-4444-444444444444",
                    MachineName = RemoteMachine,
                    DisplayName = OtherDirectorId,
                });
                s.LandsOn = OtherDirectorId;
            });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(OtherDirectorId, stub.SpawnBody!.Director);   // pinned to the owner, not the impostor
    }

    // Naming ANOTHER Director with no machine: the floor asks the Gateway which machine it runs on and
    // relays there, carrying the RESOLVED ID rather than the display name the caller typed.
    //
    // Relaying the name would resolve it TWICE, against two reads of the registry taken moments apart,
    // and a display name is not a stable handle: if the Director holding it drops out in between and a
    // sibling on that machine carries the same name, the Gateway's own resolve legitimately finds the
    // sibling and the session opens there. The id cannot move between Directors, so the second resolve
    // can only agree with the first.
    [Fact]
    public async Task NamingAnotherDirector_relaysTheResolvedId_notTheDisplayName()
    {
        var (resp, stub) = await SpawnAsync(new
        {
            repoPath = @"C:\repo",
            director = "North build",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(RemoteMachine, stub.SpawnMachine);
        Assert.NotNull(stub.SpawnBody);
        Assert.Equal(OtherDirectorId, stub.SpawnBody!.Director);
    }

    [Fact]
    public async Task NamingADirectorThatIsNotRegistered_failsLoud_andSpawnsNothing()
    {
        var (resp, stub) = await SpawnAsync(new
        {
            repoPath = @"C:\repo",
            director = "North experiments",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("North experiments", await resp.Content.ReadAsStringAsync());
        // The one that matters: no session was started anywhere, on any Director.
        Assert.Null(stub.SpawnBody);
    }

    // THE SILENT FAILURE. A Gateway that predates Director targeting ignores the name and hands the
    // create to the machine's first Director, answering with an ordinary 201. Without this check the
    // caller is told the spawn succeeded and never learns it went somewhere else.
    [Fact]
    public async Task WhenTheGatewayIgnoresTheNamedDirector_theCallerIsTold_notReportedAsSuccess()
    {
        var (resp, _) = await SpawnAsync(
            new { repoPath = @"C:\repo", director = "North build" },
            stub => stub.LandsOn = FirstOnMachineId);   // an old Gateway: first on the machine

        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("North build", body);
        Assert.Contains(FirstOnMachineId, body);        // names where it actually landed
    }

    // THE SAME FAILURE, WEARING NOTHING. A Gateway that ignores the target may also return no Director
    // id at all - the field defaults to empty on the contract, so "no answer" is a real response shape,
    // and it is likeliest from exactly the old Gateway this check exists for. Treating absence as
    // permission would fail OPEN in the one case that matters. Silence is not proof of placement.
    [Fact]
    public async Task WhenTheGatewayReturnsNoDirectorId_thatIsNotProofOfCorrectPlacement()
    {
        var (resp, _) = await SpawnAsync(
            new { repoPath = @"C:\repo", director = "North build" },
            stub => stub.LandsOn = "");                 // an old Gateway: no id in the reply

        Assert.NotEqual(HttpStatusCode.Created, resp.StatusCode);
        Assert.Contains("North build", await resp.Content.ReadAsStringAsync());
    }

    // The same reply, when the Gateway DID honor the target, must not trip the check - a false alarm
    // here would report every correct targeted spawn as a failure.
    [Fact]
    public async Task WhenTheGatewayHonorsTheNamedDirector_theSpawnIsReportedAsSuccess()
    {
        var (resp, _) = await SpawnAsync(
            new { repoPath = @"C:\repo", director = "North build" },
            stub => stub.LandsOn = OtherDirectorId);    // "North build" itself

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    // An ordinary machine spawn must be untouched by all of this: no Director name, no lookup, and the
    // relay carries no name that would pin the Gateway and disable its launch-on-demand. A BLANK
    // returned Director id must not trip the new check either - an untargeted spawn has nothing to
    // check against, and tripping here would break every ordinary remote spawn in the fleet.
    [Fact]
    public async Task AnOrdinaryMachineSpawn_isUnchanged_andNamesNoDirector()
    {
        var (resp, stub) = await SpawnAsync(
            new { repoPath = @"C:\repo", machine = RemoteMachine },
            s => s.LandsOn = "");

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(RemoteMachine, stub.SpawnMachine);
        Assert.True(string.IsNullOrEmpty(stub.SpawnBody!.Director));
    }
}
