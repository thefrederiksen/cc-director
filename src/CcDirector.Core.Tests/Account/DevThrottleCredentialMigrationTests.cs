using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Gateway Centralization Phase 2 Director credential migration (issue #642): on the first
/// run of the new build, a pre-existing local Director credential blob is deleted (the Gateway is the
/// account authority now), and a run with no blob present is a harmless no-op. Every test points the
/// migration at a temporary path so it never touches the real install.
/// </summary>
public sealed class DevThrottleCredentialMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;

    public DevThrottleCredentialMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-cred-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _blobPath = Path.Combine(_tempDir, "devthrottle-credential.bin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Acceptance criterion: a pre-existing credential blob is present before upgrade and absent after
    // the first run of the new build.
    [Fact]
    public void DeleteStaleDirectorCredential_BlobPresent_DeletesItAndReturnsTrue()
    {
        File.WriteAllBytes(_blobPath, new byte[] { 1, 2, 3, 4 });
        Assert.True(File.Exists(_blobPath));

        var deleted = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);

        Assert.True(deleted);
        Assert.False(File.Exists(_blobPath));
    }

    // Acceptance criterion: a fresh Director with no credential blob is a harmless no-op (nothing to
    // delete), and never creates the file.
    [Fact]
    public void DeleteStaleDirectorCredential_NoBlob_ReturnsFalseAndCreatesNothing()
    {
        Assert.False(File.Exists(_blobPath));

        var deleted = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);

        Assert.False(deleted);
        Assert.False(File.Exists(_blobPath));
    }

    // The migration is safe to run on every launch: a second run after the blob is gone is still a no-op.
    [Fact]
    public void DeleteStaleDirectorCredential_RunTwice_SecondRunIsNoOp()
    {
        File.WriteAllBytes(_blobPath, new byte[] { 9, 9, 9 });

        var first = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);
        var second = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);

        Assert.True(first);
        Assert.False(second);
        Assert.False(File.Exists(_blobPath));
    }

    // --- Two-step install, Slice A: the delete is GATED on gateway presence -------------------------

    // The gate decision itself: with no gateway configured (IsEnabled == false) the Director keeps its
    // own credential, so the migration must NOT delete it. This is the production method App.axaml.cs
    // calls; making it return true unconditionally reds the "blob kept when no gateway" seam test below.
    [Fact]
    public void ShouldDeleteDirectorCredential_NoGatewayConfigured_ReturnsFalse()
    {
        var config = new GatewayConfig { Url = "" };
        Assert.False(config.IsEnabled);

        Assert.False(DevThrottleCredentialMigration.ShouldDeleteDirectorCredential(config));
    }

    // The gate decision: with a gateway configured (IsEnabled == true) the Gateway is the account
    // authority and the stale Director copy must still be deleted (issue #642/#651 unchanged).
    [Fact]
    public void ShouldDeleteDirectorCredential_GatewayConfigured_ReturnsTrue()
    {
        var config = new GatewayConfig { Url = "https://gateway.example.com" };
        Assert.True(config.IsEnabled);

        Assert.True(DevThrottleCredentialMigration.ShouldDeleteDirectorCredential(config));
    }

    // Revert-proof #1 (the seam): reproduce the exact startup decision - "if
    // ShouldDeleteDirectorCredential(config) then DeleteStaleDirectorCredential(blob)" - and prove that
    // with NO gateway a present Director blob is KEPT. Reverting the production
    // ShouldDeleteDirectorCredential to => true reds this test.
    [Fact]
    public void StartupDecision_NoGateway_KeepsPresentDirectorBlob()
    {
        File.WriteAllBytes(_blobPath, new byte[] { 1, 2, 3, 4 });
        var config = new GatewayConfig { Url = "" };

        if (DevThrottleCredentialMigration.ShouldDeleteDirectorCredential(config))
            DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);

        Assert.True(File.Exists(_blobPath));
    }

    // Control (Manager point #3): the gateway-PRESENT path still deletes the stale Director blob, so the
    // #642/#651 authority behavior is provably NOT broken by the gate.
    [Fact]
    public void StartupDecision_GatewayPresent_DeletesStaleDirectorBlob()
    {
        File.WriteAllBytes(_blobPath, new byte[] { 1, 2, 3, 4 });
        var config = new GatewayConfig { Url = "https://gateway.example.com" };

        if (DevThrottleCredentialMigration.ShouldDeleteDirectorCredential(config))
            DevThrottleCredentialMigration.DeleteStaleDirectorCredential(_blobPath);

        Assert.False(File.Exists(_blobPath));
    }
}
