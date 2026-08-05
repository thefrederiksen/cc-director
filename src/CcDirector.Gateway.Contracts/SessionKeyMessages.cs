namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The Director telling the Gateway about a session key it has just minted for one of its sessions
/// (Remove-the-network-port mission, phase 1b).
///
/// It carries the HASH, never the key. The Director hashes the key on the machine that minted it and
/// sends only that, so the raw key exists in exactly two places - the Director process that minted it
/// and the environment of the one session it belongs to - and never on the wire, never in the
/// Gateway's memory, and never in its database.
///
/// The session's TENANT is deliberately NOT in this message. It is resolved on the Gateway from the
/// tenant this Director's tunnel bound to at Hello, which came from the authenticated device key. A
/// tenant a client can name in a payload is a tenant a client can choose.
/// </summary>
public sealed class SessionKeyRegistration
{
    /// <summary>The session this key belongs to, as a plain GUID string.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// The lower-case hexadecimal SHA-256 of the session key
    /// (<c>CcDirector.Core.Security.GatewaySessionKey.Hash</c>).
    /// </summary>
    public string KeyHash { get; set; } = "";

    /// <summary>
    /// When this key stops being accepted (UTC). Refreshed every time the Director re-registers the
    /// session on a tunnel reseed, so a live session's key never lapses while its Director is
    /// connected, and a key whose Director vanished lapses on its own.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
