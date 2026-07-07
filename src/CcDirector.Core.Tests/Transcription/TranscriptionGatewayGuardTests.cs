using Xunit;

namespace CcDirector.Core.Tests.Transcription;

/// <summary>
/// Architecture guard for the Gateway-owned transcription migration.
///
/// Target rule: production code outside the Gateway must not execute transcription.
/// It may capture audio, preserve audio, upload audio, poll status, and consume the
/// Gateway result. Only Gateway transcription code may call the batch transcription
/// pipeline/provider path.
///
/// This guard is intentionally staged: known current violations are allowlisted with
/// reasons so the build stays green while the migration is underway. The allowlist
/// must shrink to zero as each path is moved to the Gateway job protocol. Any new
/// direct transcription file outside the list fails immediately.
/// </summary>
public sealed class TranscriptionGatewayGuardTests
{
    private static readonly Dictionary<string, string> DirectTranscriptionAllowlist = new()
    {
        ["src/CcDirector.Core/Transcription/BatchTranscriptionPipeline.cs"] =
            "Current shared implementation; target is Gateway-only execution.",
    };

    private static readonly Dictionary<string, string> LegacyClientEndpointAllowlist = new()
    {
        ["src/CcDirector.ControlApi/Web/session-view.html"] =
            "Director session Voice tab still calls /voice/utterance; migrate to Gateway transcription job.",
        ["src/CcDirector.ControlApi/Web/manager.html"] =
            "Director manager Voice panel still calls /voice/command; migrate or remove.",
        ["src/CcDirector.ControlApi/Web/dictation-overlay.js"] =
            "Director browser dictation still opens /dictate; migrate to Gateway transcription job.",
        ["src/CcDirector.ControlApi/Web/dictate.html"] =
            "Standalone Director dictation still opens /dictate; migrate to Gateway transcription job.",
    };

    [Fact]
    public void No_new_production_code_executes_transcription_outside_gateway()
    {
        var root = GetRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateProductionFiles(root, ["src"], "*.cs"))
        {
            var rel = Relative(root, file);
            var text = File.ReadAllText(file);
            if (!ContainsDirectTranscriptionExecution(text)) continue;
            if (IsGatewayTranscriptionFile(rel)) continue;
            if (DirectTranscriptionAllowlist.ContainsKey(rel)) continue;
            offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "Production code outside Gateway may not execute transcription. Upload audio to the Gateway "
            + "transcription job protocol instead. Add only temporary, tracked migration exceptions:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Direct_transcription_allowlist_has_no_stale_entries()
    {
        var root = GetRepoRoot();
        var stale = new List<string>();

        foreach (var (rel, _) in DirectTranscriptionAllowlist)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { stale.Add($"{rel} (file no longer exists)"); continue; }
            if (!ContainsDirectTranscriptionExecution(File.ReadAllText(full)))
                stale.Add($"{rel} (no longer directly executes transcription - remove it)");
        }

        Assert.True(stale.Count == 0,
            "The direct-transcription allowlist has stale entries; remove them so the list shrinks:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void No_new_client_code_calls_legacy_transcription_endpoints()
    {
        var root = GetRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateProductionFiles(root, ["apps", "packages", "phone", "src/CcDirector.ControlApi/Web"], "*.*"))
        {
            var rel = Relative(root, file);
            if (!IsClientSource(rel)) continue;
            var text = File.ReadAllText(file);
            if (!ContainsLegacyTranscriptionEndpoint(text)) continue;
            if (LegacyClientEndpointAllowlist.ContainsKey(rel)) continue;
            offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "Client/UI code may not call legacy transcription endpoints. Use the Gateway /transcription "
            + "job protocol instead. Add only temporary, tracked migration exceptions:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Legacy_endpoint_allowlist_has_no_stale_entries()
    {
        var root = GetRepoRoot();
        var stale = new List<string>();

        foreach (var (rel, _) in LegacyClientEndpointAllowlist)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { stale.Add($"{rel} (file no longer exists)"); continue; }
            if (!ContainsLegacyTranscriptionEndpoint(File.ReadAllText(full)))
                stale.Add($"{rel} (no longer calls a legacy endpoint - remove it)");
        }

        Assert.True(stale.Count == 0,
            "The legacy-endpoint allowlist has stale entries; remove them so the list shrinks:\n  "
            + string.Join("\n  ", stale));
    }

    private static bool ContainsDirectTranscriptionExecution(string text)
        => text.Contains("new BatchTranscriptionPipeline", StringComparison.Ordinal)
           || text.Contains("BatchTranscriptionPipeline(", StringComparison.Ordinal)
           || text.Contains("/audio/transcriptions", StringComparison.Ordinal);

    private static bool ContainsLegacyTranscriptionEndpoint(string text)
        => text.Contains("/wingman/utterance", StringComparison.Ordinal)
           || text.Contains("/voice/utterance", StringComparison.Ordinal)
           || text.Contains("/voice/command", StringComparison.Ordinal)
           || text.Contains("/dictate", StringComparison.Ordinal)
           || text.Contains("/audio/transcriptions", StringComparison.Ordinal);

    private static bool IsGatewayTranscriptionFile(string rel)
        => rel.StartsWith("src/CcDirector.Gateway/", StringComparison.Ordinal)
           || rel.StartsWith("src/CcDirector.GatewayApp/", StringComparison.Ordinal);

    private static bool IsClientSource(string rel)
    {
        if (rel.Contains("/bin/") || rel.Contains("/obj/")) return false;
        if (rel.Contains("/dist/")) return false;
        if (rel.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)) return false;
        if (rel.Equals("packages/client-core/src/api/schema.ts", StringComparison.OrdinalIgnoreCase)) return false;
        if (rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateProductionFiles(string root, string[] roots, string pattern)
    {
        foreach (var dir in roots.Select(r => Path.Combine(root, r)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var rel = Relative(root, file);
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                if (rel.Contains(".Tests/", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "cc-director.sln"))) return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
            dir = parent ?? "";
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
