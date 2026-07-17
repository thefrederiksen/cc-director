using System;

namespace CcDirector.Core.Tenancy;

/// <summary>
/// The isolation boundary a request, connection, session, or stored record belongs to. A
/// <see cref="TenantId"/> is a validated wrapper around a non-empty string, never a bare string passed
/// around, so a tenant identity cannot be silently confused with an ordinary id.
///
/// The core is single-tenant: everything resolves to <see cref="Local"/> and nothing about behavior
/// changes. Making the core tenancy-aware lets a resolver scope work per connection later without
/// touching any consumer; such a resolver always derives the tenant from the authenticated principal at
/// ingress and never reads a tenant id from client input.
///
/// NOTE on <c>default(TenantId)</c>: like any struct, a default value bypasses the constructor and
/// has a null <see cref="Value"/>. A default TenantId is NOT valid and must never be treated as a
/// tenant. Consumers obtain a TenantId from <see cref="ITenantContext"/> (always valid) or construct
/// one explicitly (validated); they do not fabricate one from <c>default</c>. Use
/// <see cref="IsValid"/> when a value's provenance is uncertain.
/// </summary>
public readonly record struct TenantId
{
    /// <summary>The well-known single-tenant identity. Self-host and the open core resolve to this.</summary>
    public static readonly TenantId Local = new("local");

    /// <summary>The underlying identifier. Null only for an invalid <c>default(TenantId)</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// Construct a validated tenant id. Fails loud on a null, empty, or whitespace value rather than
    /// carrying an unusable identity forward (no fallback - an unresolved tenant is denied, not defaulted).
    /// </summary>
    public TenantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A TenantId cannot be null, empty, or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>True when this value came through the validating constructor and carries an identifier.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Value);

    /// <summary>True when this is the well-known single-tenant identity (the self-host / N=1 case).</summary>
    public bool IsLocal => IsValid && string.Equals(Value, Local.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => IsValid ? Value : "<invalid-tenant>";
}
