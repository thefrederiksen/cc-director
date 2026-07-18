namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of one Web Push subscription (<see cref="Push.StoredPushSubscription"/>) in the EF data
/// layer: one row in the <c>push_subscriptions</c> table, keyed by <see cref="Endpoint"/>.
///
/// <see cref="Endpoint"/> is the PRIMARY KEY directly - the browser push endpoint URL is unique per
/// subscription and ordinally compared (the legacy store keyed a <c>Dictionary(StringComparer.Ordinal)</c> by
/// it), so it is the natural key and there is no surrogate id. Re-subscribing the same device is an idempotent
/// upsert on this key, exactly as before. The store carries NO per-user field (the legacy shape had none - a
/// subscription is endpoint + keys; the Gateway's host-wide token is the only identity), so none is invented
/// here; <c>tenant_id</c> is inherited from the base and scopes the tenant, nothing more.
///
/// <see cref="CreatedAtUtc"/> is UTC and round-trips through the backbone's UTC DateTime convention.
/// </summary>
public sealed class PushSubscriptionEntity : TenantScopedEntity
{
    /// <summary>The browser push endpoint URL. Primary key (an ordinal, unique-per-subscription string).</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The client P-256 ECDH public key (base64url), from <c>subscription.keys.p256dh</c>.</summary>
    public string P256dh { get; set; } = "";

    /// <summary>The client auth secret (base64url), from <c>subscription.keys.auth</c>.</summary>
    public string Auth { get; set; } = "";

    /// <summary>When the subscription was first recorded (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}
