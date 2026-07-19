using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// What the startup credential migration did on this launch (two-step install, Slice A), so the caller
/// can log the outcome without re-deriving the gate decision.
/// </summary>
public enum DirectorCredentialStartupOutcome
{
    /// <summary>No gateway configured: the Director's own credential was KEPT (the Slice A exception).</summary>
    KeptNoGateway,

    /// <summary>A gateway is configured and a stale Director blob was present, so it was deleted (issue #642/#651).</summary>
    DeletedStaleBlob,

    /// <summary>A gateway is configured but there was no stale Director blob to delete.</summary>
    NoBlobToDelete,
}

/// <summary>
/// Migrates a Director that still holds a local DevThrottle credential onto the Gateway-centralized
/// model (Gateway Centralization Phase 2, issue #642). The Gateway is the single account authority now,
/// so the Director must hold NO credential of its own: any pre-existing
/// <c>%LOCALAPPDATA%\cc-director\config\director\devthrottle-credential.bin</c> left by an older build
/// is ignored AND deleted on the first run of the new build, with a log line.
///
/// This is deliberately a one-line, fail-loud deletion (no fallback): if the stale blob exists and
/// cannot be removed, the failure is surfaced to the caller's log rather than silently swallowed. It
/// targets ONLY the Director's per-install blob - never the Gateway's own credential blob
/// (<see cref="CcStorage.GatewayDevThrottleCredentialBlob"/>), which the Gateway legitimately keeps.
/// </summary>
public static class DevThrottleCredentialMigration
{
    /// <summary>
    /// Deletes a pre-existing Director credential blob if one is present, returning true when a blob was
    /// found and deleted and false when there was nothing to delete. The blob path defaults to the
    /// Director's credential location (<see cref="CcStorage.DevThrottleCredentialBlob"/>); tests pass an
    /// explicit path to a temporary file. The credential is never read or decrypted here - the Director
    /// no longer trusts a local credential, so the migration only removes the stale file.
    /// </summary>
    /// <param name="blobPath">
    /// The credential blob to remove. Defaults to the Director's credential path. Tests inject a
    /// temporary path so the migration is provable without touching the real install.
    /// </param>
    /// <returns>True when a stale blob was present and deleted; false when none existed.</returns>
    /// <summary>
    /// Decides whether the startup migration should delete the Director's own credential blob, given the
    /// current gateway configuration (two-step install, Slice A). The deletion is correct ONLY when a
    /// gateway is present: then the Gateway is the single account authority (issue #642/#651) and the
    /// Director copy is genuinely stale. When no gateway is configured the Director blob is the LIVE
    /// credential a gateway-less Director legitimately keeps, so it must NOT be deleted.
    ///
    /// This is the production line the "delete is gated on gateway presence" revert-proof pins:
    /// <c>App.axaml.cs</c> calls exactly this method, and making it return <c>true</c> unconditionally
    /// reds the "blob kept when no gateway" test.
    /// </summary>
    /// <param name="config">The current gateway configuration (config.json). <c>IsEnabled</c> is true when a gateway URL is set.</param>
    /// <returns>True when a gateway is present and the stale Director blob should be deleted; false when no gateway is configured and the credential must be kept.</returns>
    public static bool ShouldDeleteDirectorCredential(GatewayConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        return config.IsEnabled;
    }

    /// <summary>
    /// The complete startup credential-migration WIRING (two-step install, Slice A): the single method
    /// <c>App.axaml.cs</c> calls at boot. It runs the gate and the delete together - deleting the stale
    /// Director blob only when <see cref="ShouldDeleteDirectorCredential"/> says a gateway is present, and
    /// keeping the Director's own credential when no gateway is configured - and returns what it did so
    /// the caller can log it. The <c>if</c> lives HERE, in one production place a test pins directly, so
    /// the wiring is never duplicated in a test: reverting this <c>if</c> to an unconditional delete reds
    /// the "no gateway keeps the blob" test.
    /// </summary>
    /// <param name="config">The current gateway configuration (config.json).</param>
    /// <param name="blobPath">The credential blob to act on. Defaults to the Director's credential path; tests inject a temporary path.</param>
    /// <returns>What the migration did on this launch.</returns>
    public static DirectorCredentialStartupOutcome RunStartupMigration(GatewayConfig config, string? blobPath = null)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (!ShouldDeleteDirectorCredential(config))
        {
            FileLog.Write("[DevThrottleCredentialMigration] RunStartupMigration: no gateway configured -> keeping the Director's own credential (Slice A exception to issue #642)");
            return DirectorCredentialStartupOutcome.KeptNoGateway;
        }

        return DeleteStaleDirectorCredential(blobPath)
            ? DirectorCredentialStartupOutcome.DeletedStaleBlob
            : DirectorCredentialStartupOutcome.NoBlobToDelete;
    }

    public static bool DeleteStaleDirectorCredential(string? blobPath = null)
    {
        var path = blobPath ?? CcStorage.DevThrottleCredentialBlob();
        FileLog.Write($"[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: checking for a stale Director credential blob at {path}");

        if (!File.Exists(path))
        {
            FileLog.Write("[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: no Director credential blob present (nothing to migrate)");
            return false;
        }

        File.Delete(path);
        FileLog.Write($"[DevThrottleCredentialMigration] DeleteStaleDirectorCredential: deleted stale Director credential blob at {path} (the Gateway is the account authority now, issue #642)");
        return true;
    }
}
