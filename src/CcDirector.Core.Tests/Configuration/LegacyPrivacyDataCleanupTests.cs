using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

[Collection("CcStorageRoot")]
public sealed class LegacyPrivacyDataCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public LegacyPrivacyDataCleanupTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-privacy-cleanup-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Run_RemovesRetiredKeysAndFilesWithoutReadingOrForwardingQueuedPayloads()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["telemetry"] = new JsonObject { ["enabled"] = true },
            ["telemetry_consent"] = true,
            ["gateway"] = new JsonObject { ["url"] = "http://gateway.example" },
        });

        var retiredFiles = new[]
        {
            Path.Combine(CcStorage.Config(), "director", "telemetry-queue.json"),
            Path.Combine(CcStorage.Config(), "director", "telemetry-consent-cache.json"),
            Path.Combine(CcStorage.Config(), "director", "devthrottle-usage-events.jsonl"),
            Path.Combine(CcStorage.Config(), "director", "devthrottle-auth-events.jsonl"),
            Path.Combine(CcStorage.Config(), "gateway", "devthrottle-auth-events.jsonl"),
            Path.Combine(CcStorage.Root(), "carmode-telemetry.json"),
        };
        foreach (var path in retiredFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"target\":\"http://must-not-be-contacted.invalid\",\"payload\":\"queued\"}");
        }
        var unrelated = Path.Combine(CcStorage.Config(), "gateway", "devthrottle-credential.bin");
        File.WriteAllText(unrelated, "keep");
        var retiredTranscriptionLog = Path.Combine(CcStorage.Root(), "transcription-log");
        Directory.CreateDirectory(retiredTranscriptionLog);
        File.WriteAllText(Path.Combine(retiredTranscriptionLog, "transcription-20260701.jsonl"),
            "{\"rawText\":\"private dictated words\"}");

        var removed = LegacyPrivacyDataCleanup.Run();

        Assert.Equal(retiredFiles.Length + 1, removed);
        Assert.All(retiredFiles, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(unrelated));
        Assert.False(Directory.Exists(retiredTranscriptionLog));
        var config = CcDirectorConfigService.ReadRaw();
        Assert.Null(config["telemetry"]);
        Assert.Null(config["telemetry_consent"]);
        Assert.Equal("http://gateway.example", config["gateway"]?["url"]?.GetValue<string>());
        Assert.Equal(0, LegacyPrivacyDataCleanup.Run());
    }
}
