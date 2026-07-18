namespace CcDirector.Gateway.Governance;

/// <summary>An append to the governance ledger violates the ledger rules (unknown subject or state, a
/// missing subject key, an oversize reason). Maps to HTTP 400 at the endpoint boundary.</summary>
public sealed class GovernanceValidationException : Exception
{
    public GovernanceValidationException(string message) : base(message) { }
}
