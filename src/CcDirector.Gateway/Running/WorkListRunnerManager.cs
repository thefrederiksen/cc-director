using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Enforces the v1 same-machine single-drain guard (issue #274, criterion 8). A machine has exactly
/// ONE slot-5 test Director - one build output, one Control API port, strictly sequential sub-agents
/// (#270 Constraint 1) - so two lists may NOT drain at the same time against the same machine. This
/// manager admits at most one active drain per machine key and REFUSES a second; v1 parallelism is
/// across DIFFERENT machines only (criterion 6 - different machine keys run concurrently here).
///
/// The cross-machine case (criterion 6) needs no coordination at this manager: two different machine
/// keys each get admitted independently, so two runners drain two lists concurrently without
/// interfering. The single-consumer claim (#273) keeps each list to one drainer regardless.
///
/// HOSTED MULTI-TENANCY (audit MED, gap audit-e): the admission slot is PARTITIONED BY TENANT. The
/// machine key is caller-controlled (a body field or a job's target machine), so two tenants can name the
/// SAME key - and the guard's whole job is a hard refusal on that key. Keying the slot by the bare machine
/// key alone let one tenant's drain refuse another tenant's drain on a shared name, and let the 409's
/// <see cref="ActiveList"/> leak the other tenant's list name. Each operation now takes the SERVER-RESOLVED
/// tenant and only ever reads/writes that tenant's own partition, so a caller's admission conflicts only
/// within its own tenant and the 409 never names another tenant's list. Self-host is one tenant
/// (<see cref="TenantId.Local"/>) - one partition, behaviour unchanged.
/// </summary>
public sealed class WorkListRunnerManager
{
    private readonly object _gate = new();

    // Tenant -> (machine key -> the list name currently being drained on that machine for THAT tenant).
    // The inner map keeps the machine key's original case-insensitive comparison; the outer key is the
    // tenant, so one tenant's machine key can never collide with another's.
    private readonly Dictionary<TenantId, Dictionary<string, string>> _activeByTenant = new();

    /// <summary>The outcome of asking to admit a drain on a machine.</summary>
    public enum AdmitResult
    {
        /// <summary>Admitted; the caller now holds the machine's single drain slot.</summary>
        Admitted,

        /// <summary>Refused; the machine is already draining another list (criterion 8).</summary>
        RefusedMachineBusy,
    }

    /// <summary>
    /// Try to admit a drain of <paramref name="listName"/> on <paramref name="machineKey"/> within
    /// <paramref name="tenant"/>'s own partition. Returns <see cref="AdmitResult.Admitted"/> when the machine
    /// is free FOR THAT TENANT, or <see cref="AdmitResult.RefusedMachineBusy"/> when that tenant is already
    /// draining another list on the key. Another tenant draining the same key never refuses this caller. On
    /// admit, the caller MUST call <see cref="Complete"/> with the same tenant when the drain finishes (a
    /// finally block).
    /// </summary>
    public AdmitResult TryAdmit(TenantId tenant, string machineKey, string listName)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("a valid tenant is required", nameof(tenant));
        if (string.IsNullOrWhiteSpace(machineKey))
            throw new ArgumentException("machine key is required", nameof(machineKey));
        if (string.IsNullOrWhiteSpace(listName))
            throw new ArgumentException("list name is required", nameof(listName));

        lock (_gate)
        {
            if (_activeByTenant.TryGetValue(tenant, out var perMachine) &&
                perMachine.TryGetValue(machineKey, out var active))
            {
                FileLog.Write($"[WorkListRunnerManager] TryAdmit REFUSED: tenant={tenant.ToLogString()} machine={machineKey} already draining {active}");
                return AdmitResult.RefusedMachineBusy;
            }

            if (perMachine is null)
            {
                perMachine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _activeByTenant[tenant] = perMachine;
            }

            perMachine[machineKey] = listName;
            FileLog.Write($"[WorkListRunnerManager] TryAdmit: tenant={tenant.ToLogString()} machine={machineKey} admitted list={listName}");
            return AdmitResult.Admitted;
        }
    }

    /// <summary>Release the machine's single drain slot for <paramref name="tenant"/>. A no-op if that tenant
    /// was not active on the machine.</summary>
    public void Complete(TenantId tenant, string machineKey)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(machineKey)) return;
        lock (_gate)
        {
            if (_activeByTenant.TryGetValue(tenant, out var perMachine) && perMachine.Remove(machineKey))
            {
                if (perMachine.Count == 0)
                    _activeByTenant.Remove(tenant);
                FileLog.Write($"[WorkListRunnerManager] Complete: tenant={tenant.ToLogString()} machine={machineKey} drain slot released");
            }
        }
    }

    /// <summary>The list currently draining on the machine FOR <paramref name="tenant"/>, or null if that
    /// tenant is not draining on the machine. Never reveals another tenant's list.</summary>
    public string? ActiveList(TenantId tenant, string machineKey)
    {
        if (!tenant.IsValid) return null;
        lock (_gate)
            return _activeByTenant.TryGetValue(tenant, out var perMachine) &&
                   perMachine.TryGetValue(machineKey, out var name)
                ? name
                : null;
    }
}
