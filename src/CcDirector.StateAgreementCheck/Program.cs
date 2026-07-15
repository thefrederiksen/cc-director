using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// THE CROSS-SURFACE AGREEMENT CHECK, run against the LIVE fleet - the mission's proof
/// (specification section 6): "read the live fleet and assert that every session's desktop answer equals
/// its phone answer equals its Cockpit answer." It reported SIX disagreements out of THIRTEEN when it was
/// written. The comparison itself is <see cref="AgreementCheck"/>; this is the live harness around it.
///
/// Run it:  dotnet run --project src/CcDirector.StateAgreementCheck -- [repoRoot]
/// Exit code 0 = zero disagreements. 1 = disagreements. 2 = the check could not run (never a zero).
///
/// READ <see cref="AgreementCheck.ToDesktopInput"/> BEFORE QUOTING ANY NUMBER THIS PRINTS. It states what
/// this check can and cannot see, and why the obvious HTTP-only version of it would have been a
/// fabricated zero.
/// </summary>
public static class Program
{
    /// <summary>Seconds between the two reads. A session flipping Working &lt;-&gt; WaitingForInput between
    /// them is a FALSE disagreement, and one false positive discredits the whole check - so every
    /// disagreement is re-read before it is reported.</summary>
    private const int ReReadDelaySeconds = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var repoRoot = args.FirstOrDefault(a => !a.StartsWith('-')) ?? FindRepoRoot();
            Console.WriteLine("CROSS-SURFACE AGREEMENT CHECK - specification section 6");
            Console.WriteLine("Calls the REAL SessionOrdering fold and the REAL palettes. Re-implements nothing.");
            Console.WriteLine(new string('=', 78));

            var clientPalette = ClientPalette.Read(repoRoot);
            Console.WriteLine($"Client palette: {clientPalette.Count} names read from {ClientPalette.RelativePath}");

            var (url, token) = ReadGatewayConfig();
            Console.WriteLine($"Gateway: {url}  (token read from config.json; never printed)");
            Console.WriteLine();

            var first = await ReadRosterAsync(url, token);
            Console.WriteLine($"Live fleet: {first.Count} session(s).");
            Console.WriteLine();

            var findings = AgreementCheck.Compare(first, clientPalette).ToList();

            if (findings.Count > 0)
            {
                Console.WriteLine($"{findings.Count} candidate disagreement(s) - re-reading in {ReReadDelaySeconds}s to " +
                                  "rule out a session that changed state between reads...");
                await Task.Delay(TimeSpan.FromSeconds(ReReadDelaySeconds));
                var second = await ReadRosterAsync(url, token);
                var confirmed = AgreementCheck.Compare(second, clientPalette)
                    .Where(f => findings.Any(x => x.SessionId == f.SessionId && x.Kind == f.Kind))
                    .ToList();
                var transient = findings.Count - confirmed.Count;
                if (transient > 0)
                    Console.WriteLine($"  {transient} did not survive the re-read (a racing state change) - NOT reported.");
                findings = confirmed;
            }

            Report(first, findings);
            return findings.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            // Fail loudly. A check that swallows its own error and prints zero is the worst outcome
            // available here - it is a fabricated proof.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"AGREEMENT CHECK FAILED TO RUN: {ex.Message}");
            Console.Error.WriteLine("No number is reported. A check that cannot run reports NOTHING, never zero.");
            return 2;
        }
    }

    private static void Report(IReadOnlyList<SessionDto> roster, IReadOnlyList<AgreementCheck.Finding> findings)
    {
        Console.WriteLine(new string('-', 78));
        foreach (var row in roster)
            Console.WriteLine($"  {row.EffectiveColor,-11} {row.StateLabel,-18} {row.SessionRole,-11} " +
                              $"{StatusPalette.HexFor(row.EffectiveColor)}  {row.Name}");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine();

        // THE EXPOSURE COUNT. How many live sessions carry a Gateway-only fold input right now. A zero
        // measured over a fleet where none of them is in play has NOT exercised those arms, and saying so
        // is the difference between a proof and a green light. The arms themselves are exercised
        // deliberately, and watched failing, by AgreementCheckFaultInjectionTests.
        var exposed = roster.Count(r => r.DictationStatus is not null || r.Transcribing || r.VoiceGenerating || r.SnoozeExpired);
        Console.WriteLine($"Sessions carrying a Gateway-only fold input right now: {exposed} of {roster.Count}.");
        if (exposed == 0)
            Console.WriteLine("  So the four inputs the desktop cannot see are NOT in play on this fleet at this instant,");
        Console.WriteLine("  and this run therefore does NOT exercise those arms. See the fault-injection tests.");
        Console.WriteLine();

        foreach (var f in findings)
            Console.WriteLine($"DISAGREEMENT [{f.Kind}] {f.Name}\n    {f.Detail}\n");

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"AGREEMENT NUMBER: {findings.Count} disagreement(s) over {roster.Count} live session(s).");
        Console.WriteLine("  (It was SIX out of THIRTEEN when the specification was written.)");
        Console.WriteLine();
        Console.WriteLine("MEASURED: the stamp is present; the stamped answer IS the shared fold's answer; the law");
        Console.WriteLine("  (working => blue) holds; the desktop's fold agrees with the Gateway's; and every colour");
        Console.WriteLine("  resolves to the SAME HEX in the desktop palette and the shipped client palette.");
        Console.WriteLine("NOT MEASURED, and not to be claimed: the desktop's own SessionRole copy is not externally");
        Console.WriteLine("  observable (the Gateway's fleet pass overwrites the inbound role on every read), so a");
        Console.WriteLine("  STALE role on a Director cannot be seen from here. That push is proved in-process by");
        Console.WriteLine("  DesktopRoleStampWireProofTests, which hand-sets nothing.");
    }

    private static async Task<IReadOnlyList<SessionDto>> ReadRosterAsync(string url, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await http.GetStringAsync($"{url.TrimEnd('/')}/sessions");
        return JsonSerializer.Deserialize<List<SessionDto>>(body, Json)
               ?? throw new InvalidOperationException("The Gateway roster did not deserialize to a session list.");
    }

    /// <summary>
    /// The Gateway's address and device token, from the Director's config. The token is NEVER printed.
    ///
    /// The path comes from <c>CcStorage.Config()</c> and is NOT composed here. This file originally hand-built
    /// GetFolderPath(LocalApplicationData) + "cc-director" + "config", and StorageRootGuardTests failed it
    /// within the hour - correctly. A hand-composed root cannot be redirected by CC_DIRECTOR_ROOT, so anything
    /// reaching it reads and writes the REAL running Director's folders no matter how careful the caller is.
    /// Worth recording that the guard caught the agent auditing everyone else's fitness functions, in the same
    /// pass: care is not the control, the test is - which is this mission's whole thesis, applied to its own QA.
    /// </summary>
    private static (string Url, string Token) ReadGatewayConfig()
    {
        var path = Path.Combine(CcDirector.Core.Storage.CcStorage.Config(), "config.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No Director config at '{path}', so the Gateway cannot be reached.", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("gateway", out var gw))
            throw new InvalidOperationException("config.json has no 'gateway' section.");
        var url = gw.TryGetProperty("url", out var u) ? u.GetString() : null;
        var token = gw.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "config.json's gateway section has no url or no token. Gateway endpoints are device-key " +
                "authenticated; without a token this check cannot read the live fleet, and it will not " +
                "pretend to by measuring something else.");
        return (url, token);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no 'packages' directory above this binary). " +
                   "Pass it as the first argument.");
    }
}
