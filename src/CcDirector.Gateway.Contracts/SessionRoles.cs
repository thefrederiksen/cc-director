namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The canonical automatic-session-role values. Roles ride the wire as STRINGS (on
/// <see cref="SessionDto.SessionRole"/> / <see cref="SessionDto.ExplicitRole"/> /
/// <see cref="NewSessionRequest.Role"/> and every TS client), so these named constants are the SINGLE
/// source of truth - use them everywhere instead of bare literals.
///
///  - <see cref="Worker"/> and <see cref="Manager"/> are AUTO-DERIVED from the fleet (Worker = controlled
///    and controller alive; Manager = a non-worker, non-architect controlling a live session).
///  - <see cref="Standalone"/> is the default (human-facing, red allowed).
///  - <see cref="Architect"/> is EXPLICIT-ONLY: it cannot be inferred from the spawn graph, so it is set
///    at spawn (<see cref="NewSessionRequest.Role"/>) or via the set-role verb, and wins over derivation.
/// </summary>
public static class SessionRoles
{
    public const string Standalone = "Standalone";
    public const string Manager = "Manager";
    public const string Worker = "Worker";
    public const string Architect = "Architect";

    /// <summary>The four valid roles - the validation set and the CLI's accepted values.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Standalone, Manager, Worker, Architect };

    /// <summary>
    /// Canonicalize a role string (case-insensitive, trimmed) to one of the four constants, or null when it
    /// is empty or not a known role. Used to validate an explicit role on the way in and to normalize its
    /// casing before it is stored.
    /// </summary>
    public static string? Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var trimmed = role.Trim();
        foreach (var r in All)
        {
            if (string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    /// <summary>True when the value is a known role. Used to REJECT an unknown role as a bad request.</summary>
    public static bool IsValid(string? role) => Normalize(role) is not null;
}
