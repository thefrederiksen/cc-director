using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.History;

/// <summary>
/// The durable catalog of repositories observed in sessions, grouped by tenant and machine. Session
/// history is intentionally retained for only ninety days; this catalog is not part of that sweep.
/// </summary>
public sealed class KnownRepositoryStore
{
    public const int MaxIdentityChars = 1024;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public KnownRepositoryStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Insert or refresh one observed repository. Older observations never move the timestamp or display
    /// facts backwards. Returns true when the durable row changed.
    /// </summary>
    public bool Observe(TenantId tenant, string machineName, string path, string? name, DateTime lastUsedUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));

        var machine = Required(machineName, nameof(machineName));
        var repositoryPath = Required(path, nameof(path));
        var machineKey = NormalizeMachineKey(machine);
        var pathKey = NormalizePathKey(repositoryPath);
        var used = DateTime.SpecifyKind(lastUsedUtc.ToUniversalTime(), DateTimeKind.Utc);
        var displayName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        if (displayName.Length > MaxIdentityChars)
            throw new ArgumentException($"The repository name exceeds {MaxIdentityChars} characters.", nameof(name));

        var changed = false;
        lock (_gate)
        {
            using var context = _db.CreateContext(tenant);

            // The migration normalizes the machine key but preserves the display path available in history.
            // Narrow in the database by tenant and machine, then compare paths in managed code so imported
            // and newly normalized rows obey the same cross-provider path rules.
            var existing = context.KnownRepositories
                .Where(row => row.MachineKey == machineKey)
                .ToList()
                .FirstOrDefault(row =>
                    string.Equals(NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal)
                    && string.Equals(NormalizePathKey(row.Path), pathKey, StringComparison.Ordinal));

            if (existing is null)
            {
                context.KnownRepositories.Add(new KnownRepositoryEntity
                {
                    TenantId = tenant.Value,
                    MachineKey = machineKey,
                    PathKey = pathKey,
                    MachineName = machine,
                    Path = repositoryPath,
                    Name = displayName,
                    LastUsedUtc = used,
                });
                changed = true;
            }
            else if (used > existing.LastUsedUtc)
            {
                existing.MachineName = machine;
                existing.Path = repositoryPath;
                if (displayName.Length > 0)
                    existing.Name = displayName;
                existing.LastUsedUtc = used;
                changed = true;
            }
            else if (used == existing.LastUsedUtc && existing.Name.Length == 0 && displayName.Length > 0)
            {
                existing.Name = displayName;
                changed = true;
            }

            if (changed)
                context.SaveChanges();
        }

        FileLog.Write($"[KnownRepositoryStore] Observe: tenant={tenant.ToLogString()} machine={machine} path={repositoryPath} changed={changed}");
        return changed;
    }

    /// <summary>
    /// Read every repository ever retained for one machine, newest first. There is deliberately no result
    /// cap: the mobile client needs to search the complete catalog rather than a hidden recent subset.
    /// </summary>
    public IReadOnlyList<KnownRepositoryDto> ReadForMachine(TenantId tenant, string machineName)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        var machineKey = NormalizeMachineKey(Required(machineName, nameof(machineName)));

        List<KnownRepositoryEntity> rows;
        lock (_gate)
        {
            using var context = _db.CreateContext(tenant);
            rows = context.KnownRepositories.AsNoTracking()
                .Where(row => row.MachineKey == machineKey)
                .ToList();
        }

        var result = rows
            .GroupBy(row => NormalizePathKey(row.Path), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.LastUsedUtc).First())
            .OrderByDescending(row => row.LastUsedUtc)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => new KnownRepositoryDto
            {
                Name = row.Name,
                Path = row.Path,
                LastUsed = row.LastUsedUtc,
            })
            .ToList();

        FileLog.Write($"[KnownRepositoryStore] ReadForMachine: tenant={tenant.ToLogString()} machine={machineName.Trim()} count={result.Count}");
        return result;
    }

    internal static string NormalizeMachineKey(string machineName) =>
        machineName.Trim().ToUpperInvariant();

    internal static string NormalizePathKey(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            // Keep a Windows drive root (for example C:/) intact even though a repository is not normally
            // registered at the root.
            if (normalized.Length == 3 && char.IsLetter(normalized[0]) && normalized[1] == ':')
                break;
            normalized = normalized[..^1];
        }

        var isWindowsPath = (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
                            || normalized.StartsWith("//", StringComparison.Ordinal);
        return isWindowsPath ? normalized.ToUpperInvariant() : normalized;
    }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdentityChars)
            throw new ArgumentException($"The value exceeds {MaxIdentityChars} characters.", parameterName);
        return trimmed;
    }
}
