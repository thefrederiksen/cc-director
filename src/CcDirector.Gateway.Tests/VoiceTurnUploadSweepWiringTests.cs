using System.Net;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The voice-turn upload staging is bounded IN PRODUCTION, not merely bounded in principle.
///
/// WHY THIS TEST EXISTS AND WHY IT BOOTS A REAL GATEWAY. <see cref="VoiceUploadStore.SweepAbandoned"/> has
/// had a direct unit test since it was written, and for that whole time nothing in production called it: the
/// staging directory for a voice turn is deleted on the SUCCESS path only, so every refused, dropped or
/// never-completed upload kept its recorded audio forever. A test that calls the sweep itself cannot notice
/// that - it passes with the timer present and passes with the timer gone. So this test never calls the
/// sweep. It stages an abandoned upload on disk, starts a real <see cref="GatewayHost"/>, and waits for the
/// Gateway to remove that upload on its own. The only thing that can make it pass is the sweep actually
/// running in the host, which is the fact that was missing.
///
/// The schedule is compressed through <see cref="GatewayHost.VoiceTurnUploadSweepScheduleForTests"/>; the
/// AGE cut-off is production's real one, and the staged upload is aged into the past on disk, so the test
/// exercises the deployed retention rule rather than a shortened copy of it.
/// </summary>
[Collection("DirectorRoot")]
public sealed class VoiceTurnUploadSweepWiringTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private GatewayHost? _gateway;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-sweep-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-sweep-instances-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        GatewayHost.VoiceTurnUploadSweepScheduleForTests = null;
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task RunningGateway_SweepsAbandonedVoiceTurnStaging_KeepsFreshAndTenantPartitions()
    {
        var stagingRoot = CcStorage.VoiceTurnUploads();

        // An upload that was abandoned long ago: staged chunks, nothing written for well past the retention
        // window. This is what a size refusal, a dropped connection or a caller that walked away leaves.
        var abandoned = StageUpload(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));

        // An upload that is still in flight. It must survive - a retention sweep that eats live uploads is a
        // worse defect than the leak it replaces.
        var inFlight = StageUpload(stagingRoot, Guid.NewGuid().ToString("N"));

        // The per-tenant partition container, aged the same as the abandoned upload. It is not an upload, and
        // deleting it would take every tenant's staging with it.
        var tenantsDir = Path.Combine(stagingRoot, VoiceUploadStore.TenantPartitionDirectoryName);
        var tenantUpload = StageUpload(Path.Combine(tenantsDir, Guid.NewGuid().ToString("D")),
            Guid.NewGuid().ToString("N"));
        Directory.SetLastWriteTimeUtc(tenantsDir, DateTime.UtcNow.AddDays(-2));

        // Compress only the SCHEDULE. The age cut-off stays production's.
        GatewayHost.VoiceTurnUploadSweepScheduleForTests = TimeSpan.FromMilliseconds(150);
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        var swept = await WaitUntilGone(abandoned, TimeSpan.FromSeconds(20));

        Assert.True(swept, $"the running Gateway never swept the abandoned staging at {abandoned} - " +
            "nothing in production is running the age sweep");
        Assert.True(Directory.Exists(inFlight), "the sweep removed an upload that was still in flight");
        Assert.True(Directory.Exists(tenantsDir), "the sweep deleted the per-tenant partition container");
        Assert.True(File.Exists(Path.Combine(tenantUpload, "00000.part")),
            "the sweep descended into the per-tenant partition container and removed another tenant's staging");
    }

    // ===== helpers =================================================================================

    /// <summary>Write one staged chunk for an upload id under a staging root, as a real upload would.</summary>
    private static string StageUpload(string root, string uploadId)
    {
        var dir = Path.Combine(root, uploadId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "00000.part"), new byte[] { 1, 2, 3, 4 });
        return dir;
    }

    private static async Task<bool> WaitUntilGone(string dir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists(dir)) return true;
            await Task.Delay(100);
        }
        return !Directory.Exists(dir);
    }

}
