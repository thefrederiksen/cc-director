namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// A repository the Gateway has observed in a session on a machine. This catalog is deliberately
/// independent of the ninety-day session-history retention window: it answers the mobile repository
/// picker after the source session row has been pruned.
/// </summary>
public sealed class KnownRepositoryEntity : GatewayMintedKeyEntity
{
    /// <summary>Normalized machine identity used by reads and in-process deduplication.</summary>
    public string MachineKey { get; set; } = "";

    /// <summary>Normalized repository path used by the database observation lookup and deduplication.</summary>
    public string PathKey { get; set; } = "";

    /// <summary>The machine name shown to the user.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The repository path shown to the user and sent when a session is created.</summary>
    public string Path { get; set; } = "";

    /// <summary>The most recently observed non-blank repository name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The newest session observation for this machine and repository.</summary>
    public DateTime LastUsedUtc { get; set; }
}
