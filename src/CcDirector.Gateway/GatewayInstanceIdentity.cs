using System.Text.Json;

namespace CcDirector.Gateway;

/// <summary>
/// This Gateway PROCESS's identity, unique per boot (issue #2398, stage 1).
///
/// The commit is not enough to tell two live Gateways apart. A deploy runs two containers at once, and a
/// redeploy of the SAME commit - a rollback, a retried release - puts two processes on the wire carrying
/// identical commit stamps. <see cref="GatewayInstanceRole"/> has to answer "is the public address
/// answering with ME", and only a per-boot value can answer that.
///
/// Not persisted and deliberately not derived from the machine or the container: a restart is a new
/// instance, which is exactly right, because the question being asked is about a running process.
/// </summary>
public static class GatewayInstanceIdentity
{
    /// <summary>The JSON property this is published under on /healthz.</summary>
    public const string HealthFieldName = "instance";

    /// <summary>This process's identity. Stable for the life of the process, new on every boot.</summary>
    public static string Current { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Read the instance id out of a Gateway's /healthz body, or null when it is absent or the body is not
    /// readable JSON.
    ///
    /// Null has a precise meaning to the caller and it is NOT "no instance": it is "this answer cannot be
    /// used", which includes an OLDER Gateway that predates the field. That case must read as "not
    /// confirmed to be me" rather than as "confirmed to be someone else", and both leave a process
    /// passive - the safe direction during a rollout where one side is older than the other.
    /// </summary>
    public static string? ReadInstanceFromHealthJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(HealthFieldName, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            var instance = value.GetString();
            return string.IsNullOrWhiteSpace(instance) ? null : instance;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
