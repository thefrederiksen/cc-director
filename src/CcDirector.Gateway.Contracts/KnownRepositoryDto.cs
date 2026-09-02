namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One repository the Gateway has observed in a session on a machine. Unlike the Director's recent
/// repository registry, this record is durable and is not pruned with session history.
/// </summary>
public sealed class KnownRepositoryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime LastUsed { get; set; }
}
